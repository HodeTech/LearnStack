using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Application.Contracts.Entry;
using LearnStack.Modules.Identity.Application.Federation;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.Entitlements;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Time;
using LearnStack.Tests.Fixtures;
using LearnStack.Tools.Seeder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityAuthorizationHttpProof;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// ADR-0099 Step 2 through the actual Program, real authorization and PostgreSQL connected as <c>learnstack_app</c>:
/// Begin and Complete transactions with their MUST rows, uncertain and revoked-actor settlement, audit-failure
/// rollback, two-tenant isolation, save ordering and the anonymous host-bound sign-in read. The provider and the
/// egress fetcher are the in-memory test fakes; the provider asserts no transaction is open when it is called.
/// </summary>
[Collection(PublicReadTestGroup.Name)]
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class FederationHttpTests(PublicReadFixture parent)
{
    private const string Secret = "integration-s3cret-value";
    private static readonly SeedTenant English = SeedData.English;
    private static readonly SeedTenant Yoga = SeedData.Yoga;
    /// <summary>An orgless Yoga host: the seed host maps to Studio One and therefore announces an organization (C-4).</summary>
    private const string YogaTenantHost = "federation-yoga.example.invalid";
    /// <summary>
    /// ADR-0099 Amendment 6: the test record needs the optional <c>federation.test_record</c> scope, which only the BFF's
    /// federation_test authorization requests. These administrator tokens model that test-purpose token.
    /// </summary>
    private const string RecorderScope = "openid profile email federation.test_record";

    [Fact]
    public async Task The_test_record_refuses_a_token_without_the_test_record_scope_before_reading_the_body()
    {
        await using var proof = await CreatePreparedAsync();
        var harness = new Harness();
        using var client = proof.Client(harness.Configure);
        var admin = proof.Token(EnglishAdmin, English);
        var recorder = proof.Token(EnglishAdmin, English, scope: RecorderScope);
        await AdoptFederationReleaseAsync(client, admin, English);
        using var configured = await SendAsync(client, admin, "/api/v1/identity/federation/configuration", "PUT", ConfigureBody());
        var version = (await JsonAsync(configured)).GetProperty("version").GetString()!;
        using var started = await SendAsync(client, admin, "/api/v1/identity/federation/test-starts", "POST", "{}", version);
        var start = await JsonAsync(started);
        var attempt = start.GetProperty("testAttemptId").GetString()!;
        version = start.GetProperty("version").GetString()!;
        var body = JsonSerializer.Serialize(new { testAttemptId = attempt, passed = true });

        // An ordinary Studio token (the same administrator, no test-record scope) is refused with the closed 403, before the
        // body is read: a malformed body is refused the same way, and nothing is audited or changed.
        foreach (var scope in new[] { "openid profile email", "openid federation.test_record_extra", "federation.test_recorder" })
        {
            var ordinary = proof.Token(EnglishAdmin, English, scope: scope);
            using (var refused = await SendAsync(client, ordinary, "/api/v1/identity/federation/test-results", "POST", body, version))
            {
                await ProblemAsync(refused, HttpStatusCode.Forbidden, "forbidden");
                Assert.DoesNotContain(await proof.AuditAsync(refused, English), row => row.Operation == "identity.federation.test_record");
            }
            using (var malformed = await SendAsync(client, ordinary, "/api/v1/identity/federation/test-results", "POST", "{\"x\":1}", version))
                await ProblemAsync(malformed, HttpStatusCode.Forbidden, "forbidden");
        }
        Assert.Equal("TestOpen", (await RowAsync(proof, English)).State);

        // The scope grants nothing on its own: a learner holding it is still refused by the Tenant policy.
        var learner = proof.Token(EnglishLearner, English, scope: RecorderScope);
        using (var learned = await SendAsync(client, learner, "/api/v1/identity/federation/test-results", "POST", body, version))
            Assert.Equal(HttpStatusCode.Forbidden, learned.StatusCode);
        Assert.Equal("TestOpen", (await RowAsync(proof, English)).State);

        // The positive control: the same administrator with the scope records the result.
        using var passed = await SendAsync(client, recorder, "/api/v1/identity/federation/test-results", "POST", body, version);
        Assert.Equal("recorded", (await JsonAsync(passed)).GetProperty("outcome").GetString());
        Assert.Equal("TestPassed", (await RowAsync(proof, English)).State);
    }

    /// <summary>
    /// ADR-0099 Amendment 8 (S4-2b): a token carrying the test-record scope reaches only the test record. The Studio
    /// detail read, configure write, test start and capability read refuse it with the test record's own closed 403
    /// before any handler, provider or row is touched; the same administrator without the scope is admitted on each.
    /// </summary>
    [Fact]
    public async Task A_test_record_token_reaches_only_the_test_record()
    {
        await using var proof = await CreatePreparedAsync();
        var harness = new Harness();
        using var client = proof.Client(harness.Configure);
        var admin = proof.Token(EnglishAdmin, English);
        var recorder = proof.Token(EnglishAdmin, English, scope: RecorderScope);
        await AdoptFederationReleaseAsync(client, admin, English);

        using (var read = await SendAsync(client, recorder, "/api/v1/identity/federation"))
            await ProblemAsync(read, HttpStatusCode.Forbidden, "forbidden");
        using (var write = await SendAsync(client, recorder, "/api/v1/identity/federation/configuration", "PUT", ConfigureBody()))
            await ProblemAsync(write, HttpStatusCode.Forbidden, "forbidden");
        using (var capabilities = await SendAsync(client, recorder, "/api/v1/identity/me/capabilities"))
            await ProblemAsync(capabilities, HttpStatusCode.Forbidden, "forbidden");
        Assert.Empty(harness.Provider.Calls);
        Assert.Null(await RowOrNullAsync(proof, English));

        // The unscoped administrator is admitted on the same routes.
        using (var capabilities = await SendAsync(client, admin, "/api/v1/identity/me/capabilities"))
            Assert.Equal(HttpStatusCode.OK, capabilities.StatusCode);
        using var configured = await SendAsync(client, admin, "/api/v1/identity/federation/configuration", "PUT", ConfigureBody());
        var version = (await JsonAsync(configured)).GetProperty("version").GetString()!;
        using (var read = await SendAsync(client, admin, "/api/v1/identity/federation"))
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using (var start = await SendAsync(client, recorder, "/api/v1/identity/federation/test-starts", "POST", "{}", version))
            await ProblemAsync(start, HttpStatusCode.Forbidden, "forbidden");
        Assert.Equal("Draft", (await RowAsync(proof, English)).State);
        using var started = await SendAsync(client, admin, "/api/v1/identity/federation/test-starts", "POST", "{}", version);
        var attempt = await JsonAsync(started);

        // The test record admits the scoped token, and only it.
        using var passed = await SendAsync(client, recorder, "/api/v1/identity/federation/test-results", "POST",
            JsonSerializer.Serialize(new { testAttemptId = attempt.GetProperty("testAttemptId").GetString(), passed = true }),
            attempt.GetProperty("version").GetString());
        Assert.Equal("recorded", (await JsonAsync(passed)).GetProperty("outcome").GetString());
        Assert.Equal("TestPassed", (await RowAsync(proof, English)).State);
    }

    /// <summary>
    /// ADR-0099 Amendment 6 (H): the native self read's host-only setter, against PostgreSQL as learnstack_app. It reads only
    /// the host tenant's Active, entitled alias: nothing before Active, the alias while Active and entitled, nothing for
    /// another host tenant, nothing after an entitlement lapse or Disable.
    /// </summary>
    [Fact]
    public async Task The_host_alias_setter_reads_only_the_host_tenants_Active_entitled_alias()
    {
        await using var proof = await CreatePreparedAsync();
        var harness = new Harness();
        using var client = proof.Client(harness.Configure);
        var token = proof.Token(EnglishAdmin, English);
        var recorder = proof.Token(EnglishAdmin, English, scope: RecorderScope);
        await AdoptFederationReleaseAsync(client, token, English);
        Assert.Null(await HostAliasAsync(proof, English));

        using var configured = await SendAsync(client, token, "/api/v1/identity/federation/configuration", "PUT", ConfigureBody());
        var version = (await JsonAsync(configured)).GetProperty("version").GetString()!;
        Assert.Null(await HostAliasAsync(proof, English));
        version = await ActivateAsync(client, token, recorder, version);
        var alias = (await RowAsync(proof, English)).Alias;
        Assert.Equal(alias, await HostAliasAsync(proof, English));
        Assert.Null(await HostAliasAsync(proof, Yoga));

        harness.Entitlement.SsoOff = true;
        Assert.Null(await HostAliasAsync(proof, English));
        harness.Entitlement.SsoOff = false;
        Assert.Equal(alias, await HostAliasAsync(proof, English));

        using var disabled = await SendAsync(client, token, "/api/v1/identity/federation/disablement", "POST", "{}", version);
        Assert.Equal("applied", (await JsonAsync(disabled)).GetProperty("outcome").GetString());
        Assert.Null(await HostAliasAsync(proof, English));

        async Task<string?> HostAliasAsync(IdentityAuthorizationHttpProof owner, SeedTenant tenant)
        {
            // The production setter on its own learnstack_app connection; the plan half is the gated default provider.
            await using var source = ApplicationDataSource.Build(owner.Database.AppConnectionString);
            var reader = new FederationHostAliasReader(new Lazy<NpgsqlDataSource>(() => source),
                new GatedEntitlementProvider(new NullEntitlementProvider(), harness.Entitlement));
            return await reader.ReadActiveAliasAsync(tenant.TenantId);
        }
    }

    [Fact]
    public async Task The_lifecycle_commits_each_step_with_its_MUST_row_and_exposes_only_the_host_tenants_Active_alias()
    {
        await using var proof = await CreatePreparedAsync();
        var harness = new Harness();
        using var client = proof.Client(harness.Configure);
        var token = proof.Token(EnglishAdmin, English);
        var recorder = proof.Token(EnglishAdmin, English, scope: RecorderScope);
        await AdoptFederationReleaseAsync(client, token, English);

        using (var empty = await SendAsync(client, token, "/api/v1/identity/federation"))
        {
            var body = await JsonAsync(empty);
            Assert.Equal(JsonValueKind.Null, body.GetProperty("connection").ValueKind);
            Assert.Null(empty.Headers.ETag);
        }
        Assert.Equal(JsonValueKind.Null, (await SignInAsync(client, English)).GetProperty("federation").ValueKind);

        using var configured = await SendAsync(client, token, "/api/v1/identity/federation/configuration", "PUT", ConfigureBody());
        var receipt = await JsonAsync(configured);
        Assert.Equal("applied", receipt.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, receipt.GetProperty("testAttemptId").ValueKind);
        Assert.Equal(["identity.federation.apply", "identity.federation.configure"],
            (await proof.AuditAsync(configured, English)).Select(row => row.Operation + ":" + row.Outcome).Order(StringComparer.Ordinal)
                .Select(value => value.Split(':')[0]).ToArray());
        Assert.All(await proof.AuditAsync(configured, English), row => Assert.Equal("success", row.Outcome));
        var version = receipt.GetProperty("version").GetString()!;
        Assert.Equal("Draft", (await RowAsync(proof, English)).State);

        using var started = await SendAsync(client, token, "/api/v1/identity/federation/test-starts", "POST", "{}", version);
        var start = await JsonAsync(started);
        Assert.Equal("applied", start.GetProperty("outcome").GetString());
        var attempt = start.GetProperty("testAttemptId").GetString()!;
        Assert.Equal(JsonValueKind.Null, (await SignInAsync(client, English)).GetProperty("federation").ValueKind);

        using var passed = await SendAsync(client, recorder, "/api/v1/identity/federation/test-results", "POST",
            JsonSerializer.Serialize(new { testAttemptId = attempt, passed = true }), start.GetProperty("version").GetString());
        var pass = await JsonAsync(passed);
        Assert.Equal("recorded", pass.GetProperty("outcome").GetString());
        Assert.Equal("identity.federation.test_record", Assert.Single(await proof.AuditAsync(passed, English)).Operation);
        Assert.Equal("TestPassed", (await RowAsync(proof, English)).State);
        Assert.Equal(JsonValueKind.Null, (await SignInAsync(client, English)).GetProperty("federation").ValueKind);

        using var activated = await SendAsync(client, token, "/api/v1/identity/federation/activation", "POST", "{}", pass.GetProperty("version").GetString());
        var active = await JsonAsync(activated);
        Assert.Equal("recorded", active.GetProperty("outcome").GetString());
        Assert.Equal("Active", (await RowAsync(proof, English)).State);

        var alias = (await RowAsync(proof, English)).Alias;
        var federation = (await SignInAsync(client, English)).GetProperty("federation");
        Assert.Equal(alias, federation.GetProperty("alias").GetString());
        Assert.Equal("Acme SSO", federation.GetProperty("displayName").GetString());
        Assert.Equal(JsonValueKind.Null, (await SignInAsync(client, Yoga)).GetProperty("federation").ValueKind);
        using (var head = await SendAsync(client, null, "/api/v1/public/sign-in", "HEAD"))
        {
            Assert.Equal(HttpStatusCode.OK, head.StatusCode);
            Assert.NotNull(head.Headers.CacheControl);
            Assert.True(head.Headers.CacheControl!.NoStore);
            Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        }
        // ADR-0099 § Lifecycle and test: an entitlement lapse hides the public option at once, though the row stays Active.
        harness.Entitlement.SsoOff = true;
        Assert.Equal(JsonValueKind.Null, (await SignInAsync(client, English)).GetProperty("federation").ValueKind);
        Assert.Equal("Active", (await RowAsync(proof, English)).State);
        harness.Entitlement.SsoOff = false;
        Assert.Equal(alias, (await SignInAsync(client, English)).GetProperty("federation").GetProperty("alias").GetString());

        using var disabled = await SendAsync(client, token, "/api/v1/identity/federation/disablement", "POST", "{}", active.GetProperty("version").GetString());
        Assert.Equal("applied", (await JsonAsync(disabled)).GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, (await SignInAsync(client, English)).GetProperty("federation").ValueKind);
        Assert.Equal(0, await AuditCountAsync(proof, English, Secret));
        Assert.Equal(Secret, harness.Provider.LastSecret);
        Assert.Equal(0, harness.TransactionalProviderCalls);
    }

    [Fact]
    public async Task An_uncertain_Complete_stays_pending_and_a_different_administrator_settles_it_after_the_revoked_actor()
    {
        await using var proof = await CreatePreparedAsync();
        var harness = new Harness();
        using var client = proof.Client(harness.Configure);
        var admin = proof.Token(EnglishAdmin, English);
        await AdoptFederationReleaseAsync(client, admin, English);
        await PromoteLearnerAsync(client, admin);
        var second = proof.Token(EnglishLearner, English);

        harness.Provider.OnApply = (_, _) => Task.FromResult<FederationProviderResult?>(FederationProviderResult.Unavailable);
        using var uncertain = await SendAsync(client, admin, "/api/v1/identity/federation/configuration", "PUT", ConfigureBody());
        var receipt = await JsonAsync(uncertain);
        Assert.Equal("uncertain", receipt.GetProperty("outcome").GetString());
        var row = await RowAsync(proof, English);
        Assert.Equal("Configure", row.PendingKind);
        using (var early = await SendAsync(client, second, "/api/v1/identity/federation/resumption", "POST", "{}", receipt.GetProperty("version").GetString()))
            await ProblemAsync(early, HttpStatusCode.Conflict, "concurrency_conflict");

        // Nothing landed: after the quiet window another administrator's Resume reads the provider and settles the
        // Configure as resubmit-required; the absent broker is drift from the desired state, so the row is Draft.
        harness.Clock.Advance(TimeSpan.FromSeconds(95));
        using var resumedBySecond = await SendAsync(client, second, "/api/v1/identity/federation/resumption", "POST", "{}",
            receipt.GetProperty("version").GetString());
        var resumed = await JsonAsync(resumedBySecond);
        Assert.Equal("drift", resumed.GetProperty("outcome").GetString());
        Assert.Null((await RowAsync(proof, English)).PendingKind);
        Assert.Contains(await proof.AuditAsync(resumedBySecond, English), audit => audit.Operation == "identity.federation.resume" && audit.Outcome == "success");

        // The write lands but the first actor loses authority meanwhile: Complete is refused and the operation stays pending.
        harness.Provider.OnApply = async (_, _) =>
        {
            await proof.SetMembershipStatusAsync(English, EnglishAdmin, SeedData.IdentityMemberships[0].MembershipId, "Suspended");
            return null;
        };
        // The provider never confirmed this configuration (the earlier write did not land), so it needs a fresh secret.
        using (var keep = await SendAsync(client, admin, "/api/v1/identity/federation/configuration", "PUT",
            ConfigureBody(displayName: null, secret: null), resumed.GetProperty("version").GetString()))
            await ProblemAsync(keep, HttpStatusCode.BadRequest, "validation_failed");
        using var revoked = await SendAsync(client, admin, "/api/v1/identity/federation/configuration", "PUT",
            ConfigureBody(displayName: null), resumed.GetProperty("version").GetString());
        var pending = await JsonAsync(revoked);
        Assert.Equal("pending", pending.GetProperty("outcome").GetString());
        Assert.Equal("Configure", (await RowAsync(proof, English)).PendingKind);
        harness.Provider.OnApply = null;
        harness.Clock.Advance(TimeSpan.FromSeconds(95));
        using var settledBySecond = await SendAsync(client, second, "/api/v1/identity/federation/resumption", "POST", "{}",
            (await RowAsync(proof, English)).Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("applied", (await JsonAsync(settledBySecond)).GetProperty("outcome").GetString());
        var settled = await RowAsync(proof, English);
        Assert.Null(settled.PendingKind);
        Assert.Equal("Draft", settled.State);
    }

    [Fact]
    public async Task A_forced_audit_failure_rolls_back_the_Begin_and_a_refused_operation_keeps_its_own_refusal()
    {
        await using var proof = await CreatePreparedAsync();
        var harness = new Harness();
        using var client = proof.Client(harness.Configure);
        var token = proof.Token(EnglishAdmin, English);
        var recorder = proof.Token(EnglishAdmin, English, scope: RecorderScope);
        await AdoptFederationReleaseAsync(client, token, English);
        harness.Audit.FailWrites = true;
        using (var failed = await SendAsync(client, token, "/api/v1/identity/federation/configuration", "PUT", ConfigureBody()))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Null(await RowOrNullAsync(proof, English));
        Assert.Empty(harness.Provider.Calls);
        using (var refused = await SendAsync(client, token, "/api/v1/identity/federation/test-starts", "POST", "{}", "0"))
            await ProblemAsync(refused, HttpStatusCode.NotFound, "not_found");
        harness.Audit.FailWrites = false;
        using var configured = await SendAsync(client, token, "/api/v1/identity/federation/configuration", "PUT", ConfigureBody());
        var receipt = await JsonAsync(configured);
        Assert.Equal("applied", receipt.GetProperty("outcome").GetString());
        var version = receipt.GetProperty("version").GetString()!;

        // Step 2 review C-11: Activate and the test record begin with the audit-Off detail read, which writes no MUST row,
        // so a failing MUST store cannot turn their own refusals into 503 (ADR-0033 Amendment 1).
        harness.Audit.FailWrites = true;
        using (var detail = await SendAsync(client, token, "/api/v1/identity/federation"))
            Assert.Equal("draft", (await JsonAsync(detail)).GetProperty("connection").GetProperty("status").GetString());
        using (var activation = await SendAsync(client, token, "/api/v1/identity/federation/activation", "POST", "{}", version))
            await ProblemAsync(activation, HttpStatusCode.Conflict, "business_rule_violation");
        using (var result = await SendAsync(client, recorder, "/api/v1/identity/federation/test-results", "POST",
            JsonSerializer.Serialize(new { testAttemptId = Guid.CreateVersion7().ToString("D"), passed = true }), version))
            await ProblemAsync(result, HttpStatusCode.Conflict, "business_rule_violation");
        // A mutation that would succeed still fails closed when its MUST row cannot be written.
        using (var start = await SendAsync(client, token, "/api/v1/identity/federation/test-starts", "POST", "{}", version))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, start.StatusCode);
        harness.Audit.FailWrites = false;
        Assert.Equal("Draft", (await RowAsync(proof, English)).State);
    }

    /// <summary>
    /// Step 2 review C-4. The Yoga seed host maps to Studio One (<c>MapHostToDefaultOrganization</c>), so every request
    /// on it announces an organization context, and both role-release adoption and federation administration are
    /// orgless Tenant operations: the Yoga administrator was refused there for that reason, not for tenancy. The proof
    /// therefore gives Yoga an orgless host, adopts v1→v4 through the real chain, shows that the Yoga administrator
    /// can act on its own tenant, and only then shows that neither administrator reaches the other tenant.
    /// </summary>
    [Fact]
    public async Task Two_tenants_neither_read_nor_write_each_others_connection()
    {
        await using var proof = await CreatePreparedAsync();
        await AddOrglessHostAsync(proof, Yoga, YogaTenantHost);
        var harness = new Harness();
        using var client = proof.Client(harness.Configure);
        var english = proof.Token(EnglishAdmin, English);
        var yoga = proof.Token(YogaAdmin, Yoga);

        // The organization-mapped seed host refuses the orgless operation for the Yoga administrator.
        using (var mapped = await SendAsync(client, yoga, "/api/v1/identity/me/capabilities", host: Yoga.Host))
        {
            var capabilities = await JsonAsync(mapped);
            Assert.False(capabilities.GetProperty("canAdoptRoleRelease").GetBoolean());
            Assert.True(capabilities.GetProperty("canReadOrganizationMembers").GetBoolean(), "the mapped host announces an organization");
        }
        await AdoptFederationReleaseAsync(client, english, English);
        await AdoptFederationReleaseAsync(client, yoga, Yoga, YogaTenantHost);

        // Dirty control: each administrator configures and reads its own tenant's connection.
        using var englishConfigured = await SendAsync(client, english, "/api/v1/identity/federation/configuration", "PUT", ConfigureBody());
        var englishVersion = (await JsonAsync(englishConfigured)).GetProperty("version").GetString()!;
        using var yogaConfigured = await SendAsync(client, yoga, "/api/v1/identity/federation/configuration", "PUT",
            ConfigureBody("Yoga SSO"), host: YogaTenantHost);
        var yogaVersion = (await JsonAsync(yogaConfigured)).GetProperty("version").GetString()!;
        var englishRow = await RowAsync(proof, English);
        var yogaRow = await RowAsync(proof, Yoga);
        Assert.NotEqual(englishRow.Alias, yogaRow.Alias);
        using (var own = await SendAsync(client, yoga, "/api/v1/identity/federation", host: YogaTenantHost))
            Assert.Equal(yogaRow.Alias, (await JsonAsync(own)).GetProperty("connection").GetProperty("alias").GetString());
        using (var own = await SendAsync(client, english, "/api/v1/identity/federation"))
            Assert.Equal(englishRow.Alias, (await JsonAsync(own)).GetProperty("connection").GetProperty("alias").GetString());
        var calls = harness.Provider.Calls.Count;

        // Cross-tenant: each administrator's credential on the other tenant's host reaches nothing there.
        foreach (var (token, host, version, foreign) in new[]
        {
            (yoga, English.Host, englishVersion, englishRow.Alias),
            (english, YogaTenantHost, yogaVersion, yogaRow.Alias),
        })
        {
            using var read = await SendAsync(client, token, "/api/v1/identity/federation", host: host);
            Assert.Contains(read.StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
            Assert.DoesNotContain(foreign, await read.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            using var write = await SendAsync(client, token, "/api/v1/identity/federation/test-starts", "POST", "{}", version, host);
            Assert.Contains(write.StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
            using var rename = await SendAsync(client, token, "/api/v1/identity/federation/display-name", "PUT", "{\"displayName\":\"Taken\"}", version, host);
            Assert.Contains(rename.StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
        }
        Assert.Equal(englishRow, await RowAsync(proof, English));
        Assert.Equal(yogaRow, await RowAsync(proof, Yoga));
        Assert.Equal(calls, harness.Provider.Calls.Count);
        Assert.Equal(JsonValueKind.Null, (await SignInAsync(client, Yoga)).GetProperty("federation").ValueKind);
    }

    [Fact]
    public async Task An_underprivileged_principal_and_an_organization_context_are_refused_before_any_provider_or_egress_call()
    {
        await using var proof = await CreatePreparedAsync();
        await proof.Database.AddMembershipAsync(English, EnglishLearner, English.DefaultOrganization.OrganizationId, BuiltInRoleKey.OrgAdmin);
        var harness = new Harness();
        using var client = proof.Client(harness.Configure);
        var admin = proof.Token(EnglishAdmin, English);
        var recorder = proof.Token(EnglishAdmin, English, scope: RecorderScope);
        await AdoptFederationReleaseAsync(client, admin, English);
        var learner = proof.Token(EnglishLearner, English);
        var branch = proof.Token(EnglishLearner, English, organization: true, claimedOrganization: English.DefaultOrganization.OrganizationId);
        var adminBranch = proof.Token(EnglishAdmin, English, organization: true, claimedOrganization: English.DefaultOrganization.OrganizationId);
        foreach (var refusedToken in new[] { learner, branch, adminBranch })
        {
            using var read = await SendAsync(client, refusedToken, "/api/v1/identity/federation");
            Assert.Contains(read.StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
            using var write = await SendAsync(client, refusedToken, "/api/v1/identity/federation/configuration", "PUT", ConfigureBody());
            Assert.Contains(write.StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
        }
        using (var missing = await SendAsync(client, null, "/api/v1/identity/federation/configuration", "PUT", ConfigureBody()))
            await ProblemAsync(missing, HttpStatusCode.NotFound, "not_found");
        // ADR-0099: no mutation accepts an Idempotency-Key; each refuses it as Standard 04's validation_failed.
        foreach (var (method, path, body) in Mutations(Guid.CreateVersion7()))
        {
            using var idempotent = await SendAsync(client, path.EndsWith("/test-results", StringComparison.Ordinal) ? recorder : admin,
                path, method, body, "0", key: "federation-key-0001");
            await ProblemAsync(idempotent, HttpStatusCode.BadRequest, "validation_failed");
        }
        Assert.Empty(harness.Provider.Calls);
        Assert.Equal(0, harness.Egress.Calls);
        Assert.Null(await RowOrNullAsync(proof, English));
    }

    [Fact]
    public async Task A_failed_Complete_with_drift_saves_Complete_and_drift_as_two_row_versions()
    {
        await using var proof = await CreatePreparedAsync();
        var harness = new Harness();
        using var client = proof.Client(harness.Configure);
        var token = proof.Token(EnglishAdmin, English);
        await AdoptFederationReleaseAsync(client, token, English);
        using var configured = await SendAsync(client, token, "/api/v1/identity/federation/configuration", "PUT", ConfigureBody());
        var version = long.Parse((await JsonAsync(configured)).GetProperty("version").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        harness.Provider.OnRead = (owner, _) =>
        {
            var broker = harness.Provider.Find(owner.Alias)!;
            return Task.FromResult<FederationBrokerReadback?>(FederationBrokerReadback.Present(broker.State, 1, true));
        };
        using var renamed = await SendAsync(client, token, "/api/v1/identity/federation/display-name", "PUT",
            "{\"displayName\":\"Renamed\"}", version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var drift = await JsonAsync(renamed);
        Assert.Equal("drift", drift.GetProperty("outcome").GetString());
        // Begin (+1), Complete (+1) and RecordDrift (+1): each domain step is its own UPDATE.
        var row = await RowAsync(proof, English);
        Assert.Equal(version + 3, row.Version);
        Assert.Equal("Draft", row.State);
        Assert.Null(row.PendingKind);
    }

    [Fact]
    public async Task Rotate_enable_and_delete_commit_with_their_MUST_rows_and_a_deleted_connection_is_never_offered()
    {
        await using var proof = await CreatePreparedAsync();
        var harness = new Harness();
        using var client = proof.Client(harness.Configure);
        var token = proof.Token(EnglishAdmin, English);
        var recorder = proof.Token(EnglishAdmin, English, scope: RecorderScope);
        await AdoptFederationReleaseAsync(client, token, English);
        using var configured = await SendAsync(client, token, "/api/v1/identity/federation/configuration", "PUT", ConfigureBody());
        var version = (await JsonAsync(configured)).GetProperty("version").GetString()!;

        using (var rotated = await SendAsync(client, token, "/api/v1/identity/federation/secret-rotations", "POST",
            "{\"clientSecret\":\"rotated-s3cret-value\"}", version))
        {
            var receipt = await JsonAsync(rotated);
            Assert.Equal("applied", receipt.GetProperty("outcome").GetString());
            version = receipt.GetProperty("version").GetString()!;
            Assert.Equal(["identity.federation.apply", "identity.federation.rotate_secret"],
                (await proof.AuditAsync(rotated, English)).Select(row => row.Operation).Order(StringComparer.Ordinal));
        }
        Assert.Equal("rotated-s3cret-value", harness.Provider.LastSecret);
        Assert.Equal(0, await AuditCountAsync(proof, English, "rotated-s3cret-value"));

        version = await ActivateAsync(client, token, recorder, version);
        Assert.NotEqual(JsonValueKind.Null, (await SignInAsync(client, English)).GetProperty("federation").ValueKind);
        using (var disabled = await SendAsync(client, token, "/api/v1/identity/federation/disablement", "POST", "{}", version))
            version = (await JsonAsync(disabled)).GetProperty("version").GetString()!;
        using (var enabled = await SendAsync(client, token, "/api/v1/identity/federation/enablement", "POST", "{}", version))
        {
            var receipt = await JsonAsync(enabled);
            Assert.Equal("recorded", receipt.GetProperty("outcome").GetString());
            version = receipt.GetProperty("version").GetString()!;
            Assert.Equal("identity.federation.enable", Assert.Single(await proof.AuditAsync(enabled, English)).Operation);
        }
        Assert.Equal("Draft", (await RowAsync(proof, English)).State);
        Assert.Equal(JsonValueKind.Null, (await SignInAsync(client, English)).GetProperty("federation").ValueKind);

        // Active, then deleted: the tombstone is never offered and frees no other tenant's read.
        version = await ActivateAsync(client, token, recorder, version);
        var alias = (await RowAsync(proof, English)).Alias;
        Assert.Equal(alias, (await SignInAsync(client, English)).GetProperty("federation").GetProperty("alias").GetString());
        using (var deleted = await SendAsync(client, token, "/api/v1/identity/federation/deletion", "POST", "{}", version))
        {
            Assert.Equal("applied", (await JsonAsync(deleted)).GetProperty("outcome").GetString());
            Assert.Equal(["identity.federation.apply", "identity.federation.delete"],
                (await proof.AuditAsync(deleted, English)).Select(row => row.Operation).Order(StringComparer.Ordinal));
        }
        Assert.Null(await RowOrNullAsync(proof, English));
        Assert.Null(harness.Provider.Find(alias));
        Assert.Equal(JsonValueKind.Null, (await SignInAsync(client, English)).GetProperty("federation").ValueKind);
        using (var empty = await SendAsync(client, token, "/api/v1/identity/federation"))
            Assert.Equal(JsonValueKind.Null, (await JsonAsync(empty)).GetProperty("connection").ValueKind);
        Assert.Equal(0, harness.TransactionalProviderCalls);
    }

    /// <summary>
    /// Step 2 review C-2: every mutation refuses a stale or missing <c>If-Match</c> and a state its operation does not
    /// admit, and the four entitlement-gated operations refuse an unentitled protocol, each before any provider call.
    /// Rename, Disable, Enable, Delete, Resume and the test record are not entitlement-gated: a lapse must not block
    /// stopping or removing a broker (ADR-0099 § Lifecycle and test, Entitlement lapse).
    /// </summary>
    [Fact]
    public async Task Every_operation_refuses_stale_and_missing_If_Match_wrong_state_and_the_gated_ones_an_unentitled_protocol()
    {
        await using var proof = await CreatePreparedAsync();
        var harness = new Harness();
        using var client = proof.Client(harness.Configure);
        var token = proof.Token(EnglishAdmin, English);
        var recorder = proof.Token(EnglishAdmin, English, scope: RecorderScope);
        await AdoptFederationReleaseAsync(client, token, English);
        using var configured = await SendAsync(client, token, "/api/v1/identity/federation/configuration", "PUT", ConfigureBody());
        var version = (await JsonAsync(configured)).GetProperty("version").GetString()!;
        var calls = harness.Provider.Calls.Count;

        foreach (var (method, path, body) in Mutations(Guid.CreateVersion7()))
        {
            // ADR-0099 Amendment 8: only the test record admits, and requires, the test-record token.
            var sender = path.EndsWith("/test-results", StringComparison.Ordinal) ? recorder : token;
            using var stale = await SendAsync(client, sender, path, method, body, "999");
            await ProblemAsync(stale, HttpStatusCode.Conflict, "concurrency_conflict");
            using var missing = await SendAsync(client, sender, path, method, body);
            // Configure without If-Match is a create; on an existing connection that is a recoverable conflict.
            if (path.EndsWith("/configuration", StringComparison.Ordinal)) await ProblemAsync(missing, HttpStatusCode.Conflict, "concurrency_conflict");
            else await ProblemAsync(missing, HttpStatusCode.BadRequest, "validation_failed");
        }
        Assert.Equal(calls, harness.Provider.Calls.Count);

        // A changed OIDC configuration without a fresh secret (ADR-0099 Amendment 3), before any provider call.
        using (var changed = await SendAsync(client, token, "/api/v1/identity/federation/configuration", "PUT",
            ManualConfigureBody(InMemoryFederationEgressFetcher.Issuer + "/protocol/openid-connect/token-v2", secret: null), version))
            await ProblemAsync(changed, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal(calls, harness.Provider.Calls.Count);

        // Draft admits no Activate, Disable, Enable, Resume or test record.
        foreach (var path in new[] { "activation", "disablement", "enablement", "resumption" })
        {
            using var refused = await SendAsync(client, token, "/api/v1/identity/federation/" + path, "POST", "{}", version);
            await ProblemAsync(refused, HttpStatusCode.Conflict, "business_rule_violation");
        }
        using (var record = await SendAsync(client, recorder, "/api/v1/identity/federation/test-results", "POST",
            JsonSerializer.Serialize(new { testAttemptId = Guid.CreateVersion7().ToString("D"), passed = true }), version))
            await ProblemAsync(record, HttpStatusCode.Conflict, "business_rule_violation");
        using (var started = await SendAsync(client, token, "/api/v1/identity/federation/test-starts", "POST", "{}", version))
            version = (await JsonAsync(started)).GetProperty("version").GetString()!;
        using (var again = await SendAsync(client, token, "/api/v1/identity/federation/test-starts", "POST", "{}", version))
            await ProblemAsync(again, HttpStatusCode.Conflict, "business_rule_violation");

        // A pending write admits no second Configure, Rotate, Rename or Delete.
        harness.Provider.OnApply = (_, _) => Task.FromResult<FederationProviderResult?>(FederationProviderResult.Unavailable);
        using (var uncertain = await SendAsync(client, token, "/api/v1/identity/federation/display-name", "PUT", "{\"displayName\":\"Pending\"}", version))
        {
            var receipt = await JsonAsync(uncertain);
            Assert.Equal("uncertain", receipt.GetProperty("outcome").GetString());
            version = receipt.GetProperty("version").GetString()!;
        }
        harness.Provider.OnApply = null;
        calls = harness.Provider.Calls.Count;
        foreach (var (method, path, body) in Mutations(Guid.CreateVersion7()).Where(mutation => mutation.Path.EndsWith("/configuration", StringComparison.Ordinal)
            || mutation.Path.EndsWith("/secret-rotations", StringComparison.Ordinal) || mutation.Path.EndsWith("/display-name", StringComparison.Ordinal)
            || mutation.Path.EndsWith("/deletion", StringComparison.Ordinal)))
        {
            // Configure may not change the display name, which the pending Rename already did; it omits it.
            using var pending = await SendAsync(client, token, path, method,
                path.EndsWith("/configuration", StringComparison.Ordinal) ? ConfigureBody(displayName: null) : body, version);
            await ProblemAsync(pending, HttpStatusCode.Conflict, "concurrency_conflict");
        }

        // Configure, Rotate secret, test start and Activate are gated on the protocol's entitlement.
        harness.Entitlement.SsoOff = true;
        foreach (var (method, path, body) in Mutations(Guid.CreateVersion7()).Where(mutation => mutation.Path.EndsWith("/configuration", StringComparison.Ordinal)
            || mutation.Path.EndsWith("/secret-rotations", StringComparison.Ordinal) || mutation.Path.EndsWith("/test-starts", StringComparison.Ordinal)
            || mutation.Path.EndsWith("/activation", StringComparison.Ordinal)))
        {
            using var unentitled = await SendAsync(client, token, path, method, body, version);
            await ProblemAsync(unentitled, HttpStatusCode.Forbidden, "feature_disabled");
        }
        harness.Entitlement.SsoOff = false;
        // Only the Activate readback reached the provider; no write was attempted.
        Assert.DoesNotContain("apply", harness.Provider.Calls.Skip(calls));
        Assert.Equal("Rename", (await RowAsync(proof, English)).PendingKind);
    }

    [Fact]
    public async Task A_provider_fault_after_Begin_settles_uncertain_with_its_apply_row()
    {
        await using var proof = await CreatePreparedAsync();
        var harness = new Harness();
        using var client = proof.Client(harness.Configure);
        var token = proof.Token(EnglishAdmin, English);
        await AdoptFederationReleaseAsync(client, token, English);
        harness.Provider.OnApply = (_, _) => throw new HttpRequestException("connection reset");
        using var faulted = await SendAsync(client, token, "/api/v1/identity/federation/configuration", "PUT", ConfigureBody());
        Assert.Equal("uncertain", (await JsonAsync(faulted)).GetProperty("outcome").GetString());
        var rows = await proof.AuditAsync(faulted, English);
        Assert.Equal(["identity.federation.apply", "identity.federation.configure"], rows.Select(row => row.Operation).Order(StringComparer.Ordinal));
        Assert.All(rows, row => Assert.Equal("success", row.Outcome));
        Assert.Equal("Configure", (await RowAsync(proof, English)).PendingKind);
    }

    /// <summary>
    /// The two corrective-write carriers through the actual pipeline (Step 2 review round 2, M-1 and M-3): a failed test
    /// result, and a detail load after a refused Configure of a changed configuration from Active. Each applies its
    /// disabling write with no transaction open and records its own <c>apply</c> MUST row; the second keeps the
    /// configuration the provider holds and changes only the enabled flag.
    /// </summary>
    [Fact]
    public async Task A_failed_test_result_and_a_detail_reconcile_apply_their_disabling_writes_outside_any_transaction()
    {
        await using var proof = await CreatePreparedAsync();
        var harness = new Harness();
        using var client = proof.Client(harness.Configure);
        var token = proof.Token(EnglishAdmin, English);
        var recorder = proof.Token(EnglishAdmin, English, scope: RecorderScope);
        await AdoptFederationReleaseAsync(client, token, English);
        using var configured = await SendAsync(client, token, "/api/v1/identity/federation/configuration", "PUT", ConfigureBody());
        var version = (await JsonAsync(configured)).GetProperty("version").GetString()!;
        var alias = (await RowAsync(proof, English)).Alias;

        using var started = await SendAsync(client, token, "/api/v1/identity/federation/test-starts", "POST", "{}", version);
        var start = await JsonAsync(started);
        Assert.True(harness.Provider.Find(alias)!.State.Enabled);
        using (var failed = await SendAsync(client, recorder, "/api/v1/identity/federation/test-results", "POST",
            JsonSerializer.Serialize(new { testAttemptId = start.GetProperty("testAttemptId").GetString(), passed = false }),
            start.GetProperty("version").GetString()))
        {
            var receipt = await JsonAsync(failed);
            Assert.Equal("failed", receipt.GetProperty("outcome").GetString());
            version = receipt.GetProperty("version").GetString()!;
            Assert.Equal(["identity.federation.apply", "identity.federation.test_record"],
                (await proof.AuditAsync(failed, English)).Select(row => row.Operation).Order(StringComparer.Ordinal));
        }
        Assert.False(harness.Provider.Find(alias)!.State.Enabled);
        var draft = await RowAsync(proof, English);
        Assert.Equal("Draft", draft.State);
        Assert.Null(draft.PendingKind);
        Assert.Equal(0, harness.TransactionalProviderCalls);

        version = await ActivateAsync(client, token, recorder, version);
        var held = harness.Provider.Find(alias)!.State;
        Assert.True(held.Enabled);
        harness.Provider.OnApply = (_, _) => Task.FromResult<FederationProviderResult?>(FederationProviderResult.Refused);
        using (var refused = await SendAsync(client, token, "/api/v1/identity/federation/configuration", "PUT",
            ManualConfigureBody(InMemoryFederationEgressFetcher.Issuer + "/protocol/openid-connect/token-v2", Secret), version))
            Assert.Equal("failed", (await JsonAsync(refused)).GetProperty("outcome").GetString());
        harness.Provider.OnApply = null;
        Assert.Equal(held, harness.Provider.Find(alias)!.State);
        var writes = harness.Provider.Calls.Count(call => call == "apply");
        using (var detail = await SendAsync(client, token, "/api/v1/identity/federation"))
        {
            var body = await JsonAsync(detail);
            Assert.Equal("draft", body.GetProperty("connection").GetProperty("status").GetString());
            Assert.Equal(["identity.federation.apply", "identity.federation.apply"],
                (await proof.AuditAsync(detail, English)).Select(row => row.Operation).ToArray());
        }
        Assert.Equal(writes + 1, harness.Provider.Calls.Count(call => call == "apply"));
        Assert.Equal(held with { Enabled = false }, harness.Provider.Find(alias)!.State);
        Assert.Null((await RowAsync(proof, English)).PendingKind);
        Assert.Equal(0, harness.TransactionalProviderCalls);
    }

    private static string ConfigureBody(string? displayName = "Acme SSO", string? secret = Secret) => JsonSerializer.Serialize(new
    {
        protocol = "oidc",
        displayName,
        source = "discovery_url",
        documentUrl = InMemoryFederationEgressFetcher.Discovery.AbsoluteUri,
        issuerOrEntityId = (string?)null,
        authorizationEndpoint = (string?)null,
        tokenEndpoint = (string?)null,
        jwksEndpoint = (string?)null,
        userInfoEndpoint = (string?)null,
        singleSignOnEndpoint = (string?)null,
        signingCertificates = Array.Empty<string>(),
        clientId = "learnstack",
        clientAuthentication = "client_secret_basic",
        clientSecret = secret,
    });

    private static string ManualConfigureBody(string tokenEndpoint, string? secret) => JsonSerializer.Serialize(new
    {
        protocol = "oidc",
        displayName = (string?)null,
        source = "manual",
        documentUrl = (string?)null,
        issuerOrEntityId = InMemoryFederationEgressFetcher.Issuer,
        authorizationEndpoint = InMemoryFederationEgressFetcher.Issuer + "/protocol/openid-connect/auth",
        tokenEndpoint,
        jwksEndpoint = InMemoryFederationEgressFetcher.Issuer + "/protocol/openid-connect/certs",
        userInfoEndpoint = InMemoryFederationEgressFetcher.Issuer + "/protocol/openid-connect/userinfo",
        singleSignOnEndpoint = (string?)null,
        signingCertificates = Array.Empty<string>(),
        clientId = "learnstack",
        clientAuthentication = "client_secret_basic",
        clientSecret = secret,
    });

    /// <summary>The ten mutation routes with a well-formed body each; the eleventh action is the detail GET.</summary>
    private static IEnumerable<(string Method, string Path, string Body)> Mutations(Guid attempt) =>
    [
        ("PUT", "/api/v1/identity/federation/configuration", ConfigureBody()),
        ("POST", "/api/v1/identity/federation/secret-rotations", "{\"clientSecret\":\"another-s3cret-value\"}"),
        ("PUT", "/api/v1/identity/federation/display-name", "{\"displayName\":\"Renamed\"}"),
        ("POST", "/api/v1/identity/federation/test-starts", "{}"),
        ("POST", "/api/v1/identity/federation/test-results", JsonSerializer.Serialize(new { testAttemptId = attempt.ToString("D"), passed = true })),
        ("POST", "/api/v1/identity/federation/activation", "{}"),
        ("POST", "/api/v1/identity/federation/disablement", "{}"),
        ("POST", "/api/v1/identity/federation/enablement", "{}"),
        ("POST", "/api/v1/identity/federation/deletion", "{}"),
        ("POST", "/api/v1/identity/federation/resumption", "{}"),
    ];

    /// <summary>
    /// Test start, a passed result and Activate from Draft; returns the Active version. The result is recorded with the
    /// test-record token, which no other endpoint admits (ADR-0099 Amendment 8).
    /// </summary>
    private static async Task<string> ActivateAsync(HttpClient client, string token, string recorder, string version)
    {
        using var started = await SendAsync(client, token, "/api/v1/identity/federation/test-starts", "POST", "{}", version);
        var start = await JsonAsync(started);
        using var passed = await SendAsync(client, recorder, "/api/v1/identity/federation/test-results", "POST",
            JsonSerializer.Serialize(new { testAttemptId = start.GetProperty("testAttemptId").GetString(), passed = true }),
            start.GetProperty("version").GetString());
        using var activated = await SendAsync(client, token, "/api/v1/identity/federation/activation", "POST", "{}",
            (await JsonAsync(passed)).GetProperty("version").GetString());
        var active = await JsonAsync(activated);
        Assert.Equal("recorded", active.GetProperty("outcome").GetString());
        return active.GetProperty("version").GetString()!;
    }

    /// <summary>An active, resolvable orgless host for a tenant, written as learnstack_app under that tenant's scope.</summary>
    private static async Task AddOrglessHostAsync(IdentityAuthorizationHttpProof proof, SeedTenant tenant, string host)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(app, transaction, tenant.TenantId.Value);
        await IdentityMembershipFixture.ExecuteAsync(app, transaction, """
            INSERT INTO public.platform_host_to_tenant(host,tenant_id,organization_id,is_active,is_publicly_live)
            VALUES(@host,@tenant,NULL,true,true)
            """, ("host", host), ("tenant", tenant.TenantId.Value));
        await transaction.CommitAsync();
    }

    private sealed class Harness
    {
        internal InMemoryFederationProvider Provider { get; } = new();
        internal InMemoryFederationEgressFetcher Egress { get; } = new();
        internal MovableClock Clock { get; } = new();
        internal AuditGate Audit { get; } = new();
        internal EntitlementGate Entitlement { get; } = new();
        internal int TransactionalProviderCalls;

        internal void Configure(IServiceCollection services)
        {
            services.AddHttpContextAccessor();
            services.Replace(ServiceDescriptor.Singleton<IFederationProvider>(provider =>
            {
                var accessor = provider.GetRequiredService<IHttpContextAccessor>();
                Provider.BeforeCall = _ =>
                {
                    if (accessor.HttpContext?.RequestServices.GetRequiredService<IUnitOfWork>() is { HasActiveTransaction: true })
                        Interlocked.Increment(ref TransactionalProviderCalls);
                };
                return Provider;
            }));
            services.Replace(ServiceDescriptor.Singleton<IFederationEgressFetcher>(Egress));
            // Generous step budgets: the ADR values are proven in the unit suite; a cold container must not flake here.
            services.Replace(ServiceDescriptor.Singleton(new FederationOperationBudgets(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10))));
            services.Replace(ServiceDescriptor.Singleton<IClock>(Clock));
            var original = services.Last(descriptor => descriptor.ServiceType == typeof(IAuditStore));
            services.Remove(original);
            services.Add(ServiceDescriptor.Describe(typeof(IAuditStore), provider => new GatedAuditStore(Create<IAuditStore>(provider, original), Audit,
                provider.GetRequiredService<IAuditStateCapture>()), original.Lifetime));
            var flags = services.Last(descriptor => descriptor.ServiceType == typeof(IFeatureFlags));
            services.Remove(flags);
            services.Add(ServiceDescriptor.Describe(typeof(IFeatureFlags), provider => new GatedFeatureFlags(Create<IFeatureFlags>(provider, flags), Entitlement),
                flags.Lifetime));
            // The self read's host alias setter takes entitlement through the port IFeatureFlags composes over; gate it the same way.
            var entitlements = services.Last(descriptor => descriptor.ServiceType == typeof(IEntitlementProvider));
            services.Remove(entitlements);
            services.Add(ServiceDescriptor.Describe(typeof(IEntitlementProvider),
                provider => new GatedEntitlementProvider(Create<IEntitlementProvider>(provider, entitlements), Entitlement), entitlements.Lifetime));
        }

        private static T Create<T>(IServiceProvider provider, ServiceDescriptor descriptor) where T : class =>
            descriptor.ImplementationInstance as T
            ?? (descriptor.ImplementationFactory is { } factory ? (T)factory(provider)
                : (T)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!));
    }

    /// <summary>Turns both SSO features off for the tenant, as a Hub plan change would; every other key is untouched.</summary>
    private sealed class EntitlementGate { internal volatile bool SsoOff; }

    private sealed class GatedFeatureFlags(IFeatureFlags inner, EntitlementGate gate) : IFeatureFlags
    {
        public Task<bool> IsEnabledAsync(FeatureKey key, CancellationToken ct = default) =>
            gate.SsoOff && (key == FeatureKeys.SsoOidc || key == FeatureKeys.SsoSaml) ? Task.FromResult(false) : inner.IsEnabledAsync(key, ct);
        public Task<long> GetLimitAsync(LimitKey key, CancellationToken ct = default) => inner.GetLimitAsync(key, ct);
    }

    private sealed class GatedEntitlementProvider(IEntitlementProvider inner, EntitlementGate gate) : IEntitlementProvider
    {
        public async Task<EntitlementProjection> GetAsync(TenantId tenantId, CancellationToken ct = default)
        {
            var projection = await inner.GetAsync(tenantId, ct);
            if (!gate.SsoOff) return projection;
            var features = projection.Features.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            features[FeatureKeys.SsoOidc.Value] = false;
            features[FeatureKeys.SsoSaml.Value] = false;
            return projection with { Features = features };
        }
        public Task<EntitlementRefreshOutcome> RefreshAsync(EntitlementProjection projection, CancellationToken ct = default) =>
            inner.RefreshAsync(projection, ct);
    }

    private sealed class MovableClock : IClock
    {
        private long _offset;
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow.AddTicks(Interlocked.Read(ref _offset));
        public void Advance(TimeSpan by) => Interlocked.Add(ref _offset, by.Ticks);
    }

    private sealed class AuditGate { internal volatile bool FailWrites; }

    /// <summary>
    /// Fails the in-transaction MUST write exactly where the production store would write one: when the request has a
    /// MUST intent pending. A commit with none (an audit-Off read) writes nothing in production, so it cannot fail here
    /// either (Step 2 review C-11).
    /// </summary>
    private sealed class GatedAuditStore(IAuditStore inner, AuditGate gate, IAuditStateCapture capture) : IAuditStore
    {
        public Task WriteAuthorizationPendingAsync(IUnitOfWork unitOfWork, AuthorizationEvidenceOccurrence occurrence,
            CancellationToken cancellationToken = default) => inner.WriteAuthorizationPendingAsync(unitOfWork, occurrence, cancellationToken);
        public Task WriteAuthorizationStandaloneAsync(AuthorizationEvidenceOccurrence occurrence, CancellationToken cancellationToken = default) =>
            inner.WriteAuthorizationStandaloneAsync(occurrence, cancellationToken);
        public Task WriteGlobalIdentityPendingAsync(IUnitOfWork unit, GlobalIdentityCreationReference creation,
            CancellationToken cancellationToken = default) => inner.WriteGlobalIdentityPendingAsync(unit, creation, cancellationToken);
        public Task ReconcileGlobalIdentityAsync(AuditIntent intent, IReadOnlyList<CapturedEntityChange> changes,
            AuditIntentState state, DateTimeOffset timestamp, CancellationToken cancellationToken = default) =>
            inner.ReconcileGlobalIdentityAsync(intent, changes, state, timestamp, cancellationToken);
        public Task WritePendingAsync(IUnitOfWork unit, CancellationToken cancellationToken = default) =>
            gate.FailWrites && capture.Intents.Any(intent => intent.OperationClass == OperationClass.Must)
                ? throw new AuditWriteFailedException("Deliberate federation MUST refusal.") : inner.WritePendingAsync(unit, cancellationToken);
        public Task WriteStandaloneAsync(AuditEntryDraft entry, CancellationToken cancellationToken = default) => inner.WriteStandaloneAsync(entry, cancellationToken);
        public Task WriteBestEffortAsync(AuditEntryDraft entry, CancellationToken cancellationToken = default) => inner.WriteBestEffortAsync(entry, cancellationToken);
        public Task WritePlatformScopeAsync(AuditEntryDraft entry, DbConnection connection, DbTransaction transaction,
            CancellationToken cancellationToken = default) => inner.WritePlatformScopeAsync(entry, connection, transaction, cancellationToken);
        public Task WriteEventTerminalAsync(AuditEntryDraft entry, EventTerminalTransition transition, DbConnection connection,
            DbTransaction transaction, CancellationToken cancellationToken = default) => inner.WriteEventTerminalAsync(entry, transition, connection, transaction, cancellationToken);
        public Task WriteJobTerminalAsync(AuditEntryDraft entry, JobTerminalTransition transition, DbConnection connection,
            DbTransaction transaction, CancellationToken cancellationToken = default) => inner.WriteJobTerminalAsync(entry, transition, connection, transaction, cancellationToken);
    }

    private sealed record ConnectionRow(Guid Id, string Alias, string State, string? PendingKind, long Version);

    private static async Task<ConnectionRow> RowAsync(IdentityAuthorizationHttpProof proof, SeedTenant tenant) =>
        await RowOrNullAsync(proof, tenant) ?? throw new Xunit.Sdk.XunitException("No live federation connection.");

    /// <summary>Reads as learnstack_app under the tenant's RLS scope, through the live (non-tombstone) predicate.</summary>
    private static async Task<ConnectionRow?> RowOrNullAsync(IdentityAuthorizationHttpProof proof, SeedTenant tenant)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(app, transaction, tenant.TenantId.Value);
        await using var command = new NpgsqlCommand($"""
            SELECT id,alias,state,pending_operation_kind,row_version FROM public.federation_connections
            WHERE {FederationConnectionReads.LiveSql}
            """, app, transaction);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        var row = new ConnectionRow(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt64(4));
        Assert.False(await reader.ReadAsync());
        return row;
    }

    /// <summary>
    /// Counts the tenant's audit rows that mention a needle, as learnstack_app under that tenant's scope. audit_log is
    /// FORCE row level security, so the table owner without app.tenant_id announced sees no row at all and a zero count would
    /// prove nothing: the helper first asserts that its own scope can see the tenant's rows.
    /// </summary>
    private static async Task<long> AuditCountAsync(IdentityAuthorizationHttpProof proof, SeedTenant tenant, string needle)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(app, transaction, tenant.TenantId.Value);
        await using (var visible = new NpgsqlCommand("SELECT count(*) FROM public.audit_log", app, transaction))
            Assert.True((long)(await visible.ExecuteScalarAsync())! > 0, "the reading scope sees no audit row, so a zero count is vacuous");
        await using var command = new NpgsqlCommand("""
            SELECT count(*) FROM public.audit_log audit
            WHERE audit::text LIKE '%' || @needle || '%'
            """, app, transaction);
        command.Parameters.AddWithValue("needle", needle);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<IdentityAuthorizationHttpProof> CreatePreparedAsync()
    {
        var proof = await CreateAsync(parent);
        try
        {
            await using var source = ApplicationDataSource.Build(proof.Database.AppConnectionString);
            var runner = new SeedAdministrationOriginRunner(context => SeedComposition.Build(source, context,
                NullLoggerFactory.Instance, identityAuthority: proof.Database.Registration,
                seedOriginAdmission: new FixedSeedAdministrationOriginAdmission()));
            Assert.Equal(2, await runner.RunAsync(SeedData.All.Select(tenant => tenant.TenantId)));
            return proof;
        }
        catch { await proof.DisposeAsync(); throw; }
    }

    /// <summary>The built-in Tenant Admin adopts v2, v3 and v4 in order, each by the existing explicit adoption.</summary>
    private static async Task AdoptFederationReleaseAsync(HttpClient client, string token, SeedTenant tenant, string? tenantHost = null)
    {
        var host = tenantHost ?? tenant.Host;
        var role = SeedData.IdentityRoles.Single(value => value.Tenant == tenant && value.BuiltInKey == BuiltInRoleKey.TenantAdmin).RoleId.ToString("D");
        while (true)
        {
            string version;
            int adopted;
            using (var detail = await SendAsync(client, token, "/api/v1/identity/roles/" + role, host: host))
            {
                var body = await JsonAsync(detail);
                version = body.GetProperty("version").GetString()!;
                adopted = body.GetProperty("adoptedReleaseVersion").GetInt32();
            }
            if (adopted >= 4) break;
            JsonElement release;
            using (var registry = await SendAsync(client, token, "/api/v1/identity/permissions?sourceReleaseVersion=" + adopted, host: host))
                release = (await JsonAsync(registry)).GetProperty("release");
            using var adoption = await SendAsync(client, token, "/api/v1/identity/roles/" + role + "/manifest-adoptions", "POST",
                JsonSerializer.Serialize(new { manifestVersion = release.GetProperty("version").GetInt32(), manifestFingerprint = release.GetProperty("fingerprint").GetString() }),
                version, host, "federation-adopt-" + Guid.NewGuid().ToString("N"));
            await JsonAsync(adoption);
        }
        using var capabilities = await SendAsync(client, token, "/api/v1/identity/me/capabilities", host: host);
        Assert.True((await JsonAsync(capabilities)).GetProperty("canManageFederation").GetBoolean());
    }

    /// <summary>The learner becomes a second current Tenant Admin, so another administrator can settle a pending operation.</summary>
    private static async Task PromoteLearnerAsync(HttpClient client, string adminToken)
    {
        var member = "/api/v1/identity/memberships/" + SeedData.IdentityMemberships[1].MembershipId.ToString("D");
        string version;
        using (var detail = await SendAsync(client, adminToken, member))
            version = (await JsonAsync(detail)).GetProperty("version").GetString()!;
        var adminRole = SeedData.IdentityRoles.Single(value => value.Tenant == English && value.BuiltInKey == BuiltInRoleKey.TenantAdmin).RoleId;
        using var assigned = await SendAsync(client, adminToken, member + "/role-assignments", "PUT",
            JsonSerializer.Serialize(new { roleIds = new[] { adminRole } }), version, key: "federation-promote-" + Guid.NewGuid().ToString("N"));
        await JsonAsync(assigned);
    }

    private static async Task<JsonElement> SignInAsync(HttpClient client, SeedTenant tenant)
    {
        using var response = await SendAsync(client, null, "/api/v1/public/sign-in", host: tenant.Host);
        return await JsonAsync(response);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string? token, string path,
        string method = "GET", string? body = null, string? version = null, string? host = null, string? key = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Host = host ?? English.Host;
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (version is not null) request.Headers.TryAddWithoutValidation("If-Match", "\"" + version + "\"");
        if (key is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.True(expected == response.StatusCode, response.StatusCode + ": " + await response.Content.ReadAsStringAsync());
        Assert.NotNull(response.Headers.CacheControl);
        Assert.True(response.Headers.CacheControl!.NoStore);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}
