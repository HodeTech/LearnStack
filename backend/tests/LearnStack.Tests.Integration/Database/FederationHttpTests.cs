using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Application.Federation;
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

    [Fact]
    public async Task The_lifecycle_commits_each_step_with_its_MUST_row_and_exposes_only_the_host_tenants_Active_alias()
    {
        await using var proof = await CreatePreparedAsync();
        var harness = new Harness();
        using var client = proof.Client(harness.Configure);
        var token = proof.Token(EnglishAdmin, English);
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

        using var passed = await SendAsync(client, token, "/api/v1/identity/federation/test-results", "POST",
            JsonSerializer.Serialize(new { testAttemptId = attempt, passed = true }), start.GetProperty("version").GetString());
        var pass = await JsonAsync(passed);
        Assert.Equal("recorded", pass.GetProperty("outcome").GetString());
        Assert.Equal("identity.federation.test_record", Assert.Single(await proof.AuditAsync(passed, English)).Operation);

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
            Assert.True(head.Headers.CacheControl?.NoStore);
            Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        }

        using var disabled = await SendAsync(client, token, "/api/v1/identity/federation/disablement", "POST", "{}", active.GetProperty("version").GetString());
        Assert.Equal("applied", (await JsonAsync(disabled)).GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, (await SignInAsync(client, English)).GetProperty("federation").ValueKind);
        Assert.Equal(0, await AuditCountAsync(proof, Secret));
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
        Assert.Equal("applied", (await JsonAsync(configured)).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Two_tenants_neither_read_nor_write_each_others_connection()
    {
        await using var proof = await CreatePreparedAsync();
        var harness = new Harness();
        using var client = proof.Client(harness.Configure);
        var english = proof.Token(EnglishAdmin, English);
        await AdoptFederationReleaseAsync(client, english, English);
        using var configured = await SendAsync(client, english, "/api/v1/identity/federation/configuration", "PUT", ConfigureBody());
        var englishVersion = (await JsonAsync(configured)).GetProperty("version").GetString()!;
        var englishRow = await RowAsync(proof, English);
        Assert.Null(await RowOrNullAsync(proof, Yoga));

        // The English administrator's credential on the Yoga host, and the Yoga administrator on the Yoga host, reach
        // nothing of English's: every read and write is refused before the connection registry is touched.
        foreach (var token in new[] { proof.Token(EnglishAdmin, Yoga), proof.Token(YogaAdmin, Yoga) })
        {
            using var read = await SendAsync(client, token, "/api/v1/identity/federation", host: Yoga.Host);
            Assert.Contains(read.StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
            Assert.DoesNotContain(englishRow.Alias, await read.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            using var write = await SendAsync(client, token, "/api/v1/identity/federation/test-starts", "POST", "{}", englishVersion, Yoga.Host);
            Assert.Contains(write.StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
        }
        using (var crossHost = await SendAsync(client, english, "/api/v1/identity/federation/test-starts", "POST", "{}", englishVersion, Yoga.Host))
            Assert.NotEqual(HttpStatusCode.OK, crossHost.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await SignInAsync(client, Yoga)).GetProperty("federation").ValueKind);
        Assert.Equal(englishRow, await RowAsync(proof, English));
        Assert.Null(await RowOrNullAsync(proof, Yoga));
        Assert.Equal(["apply", "read"], harness.Provider.Calls);
    }

    [Fact]
    public async Task An_underprivileged_principal_and_an_organization_context_are_refused_before_any_provider_or_egress_call()
    {
        await using var proof = await CreatePreparedAsync();
        await proof.Database.AddMembershipAsync(English, EnglishLearner, English.DefaultOrganization.OrganizationId, BuiltInRoleKey.OrgAdmin);
        var harness = new Harness();
        using var client = proof.Client(harness.Configure);
        var admin = proof.Token(EnglishAdmin, English);
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
        Assert.Empty(harness.Provider.Calls);
        Assert.Equal(0, harness.Egress.Calls);
        Assert.Null(await RowOrNullAsync(proof, English));
        using (var idempotent = await SendAsync(client, admin, "/api/v1/identity/federation/configuration", "PUT", ConfigureBody(), key: "federation-key-0001"))
            Assert.Equal(HttpStatusCode.BadRequest, idempotent.StatusCode);
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

    private static string ConfigureBody(string? displayName = "Acme SSO") => JsonSerializer.Serialize(new
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
        clientSecret = displayName is null ? null : Secret,
    });

    private sealed class Harness
    {
        internal InMemoryFederationProvider Provider { get; } = new();
        internal InMemoryFederationEgressFetcher Egress { get; } = new();
        internal MovableClock Clock { get; } = new();
        internal AuditGate Audit { get; } = new();
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
            services.Add(ServiceDescriptor.Describe(typeof(IAuditStore), provider => new GatedAuditStore(Create(provider, original), Audit), original.Lifetime));
        }

        private static IAuditStore Create(IServiceProvider provider, ServiceDescriptor descriptor) =>
            descriptor.ImplementationInstance as IAuditStore
            ?? (descriptor.ImplementationFactory is { } factory ? (IAuditStore)factory(provider)
                : (IAuditStore)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!));
    }

    private sealed class MovableClock : IClock
    {
        private long _offset;
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow.AddTicks(Interlocked.Read(ref _offset));
        public void Advance(TimeSpan by) => Interlocked.Add(ref _offset, by.Ticks);
    }

    private sealed class AuditGate { internal volatile bool FailWrites; }

    private sealed class GatedAuditStore(IAuditStore inner, AuditGate gate) : IAuditStore
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
        public Task WritePendingAsync(IUnitOfWork unit, CancellationToken cancellationToken = default) => gate.FailWrites
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
        await using var command = new NpgsqlCommand("""
            SELECT id,alias,state,pending_operation_kind,row_version FROM public.federation_connections
            WHERE state <> 'Deleted' AND deleted_at IS NULL
            """, app, transaction);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        var row = new ConnectionRow(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt64(4));
        Assert.False(await reader.ReadAsync());
        return row;
    }

    private static async Task<long> AuditCountAsync(IdentityAuthorizationHttpProof proof, string needle)
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.MigrationConnectionString);
        await using var command = new NpgsqlCommand("""
            SELECT count(*) FROM public.audit_log audit
            WHERE audit::text LIKE '%' || @needle || '%'
            """, owner);
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
    private static async Task AdoptFederationReleaseAsync(HttpClient client, string token, SeedTenant tenant)
    {
        var role = SeedData.IdentityRoles.Single(value => value.Tenant == tenant && value.BuiltInKey == BuiltInRoleKey.TenantAdmin).RoleId.ToString("D");
        while (true)
        {
            string version;
            int adopted;
            using (var detail = await SendAsync(client, token, "/api/v1/identity/roles/" + role, host: tenant.Host))
            {
                var body = await JsonAsync(detail);
                version = body.GetProperty("version").GetString()!;
                adopted = body.GetProperty("adoptedReleaseVersion").GetInt32();
            }
            if (adopted >= 4) break;
            JsonElement release;
            using (var registry = await SendAsync(client, token, "/api/v1/identity/permissions?sourceReleaseVersion=" + adopted, host: tenant.Host))
                release = (await JsonAsync(registry)).GetProperty("release");
            using var adoption = await SendAsync(client, token, "/api/v1/identity/roles/" + role + "/manifest-adoptions", "POST",
                JsonSerializer.Serialize(new { manifestVersion = release.GetProperty("version").GetInt32(), manifestFingerprint = release.GetProperty("fingerprint").GetString() }),
                version, tenant.Host, "federation-adopt-" + Guid.NewGuid().ToString("N"));
            await JsonAsync(adoption);
        }
        using var capabilities = await SendAsync(client, token, "/api/v1/identity/me/capabilities", host: tenant.Host);
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
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}
