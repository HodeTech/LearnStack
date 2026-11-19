using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Platform;
using Microsoft.AspNetCore.Hosting;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The five operator onboarding acts through the actual Program, the operator bearer scheme and the
/// ordinary pipeline, against PostgreSQL as the application role (ADR-0102).
/// </summary>
/// <remarks>
/// <para>
/// Every state assertion reads as <c>learnstack_app</c> under the tenant's own announcement, so what is
/// observed is what row security shows that tenant. The only owner-credential reads are of
/// <c>platform_host_to_tenant</c>, which has no tenant to announce, and the fixture's one deliberate
/// corruption (an administrator revoked outside the last-admin guard so the recovery has something to
/// recover).
/// </para>
/// <para>
/// An act is a <c>PUT</c> on operator-supplied identifiers, so each case that proves an act also
/// sends it again: a reply lost on the wire must cost nothing and write nothing twice.
/// </para>
/// </remarks>
[Collection(PublicReadTestGroup.Name)]
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class PlatformOperatorOnboardingTests(PublicReadFixture parent)
{
    private const string Full = "operator-full";
    private const string TenantOnly = "operator-tenant-only";
    private const string BootstrapOnly = "operator-bootstrap-only";

    [Fact]
    public async Task An_Operator_Onboards_A_Tenant_Step_By_Step_And_Every_Act_Is_Audited_And_Repeatable()
    {
        await using var proof = await Proof.CreateAsync(parent);
        var tenant = Ids.Next();
        var organization = Ids.Next();
        var role = Ids.Next();
        var membership = Ids.Next();
        var slug = "onboard-" + Ids.Short();
        var host = slug + ".example.test";

        // Provision, and send the same act again.
        var provision = await proof.PutAsync(Full, $"/tenants/{tenant}", ProvisionBody(slug, organization));
        await AssertOkAsync(provision);
        await AssertOkAsync(await proof.PutAsync(Full, $"/tenants/{tenant}", ProvisionBody(slug, organization)));
        (await proof.CountAsync(tenant, "tenants")).Should().Be(1);
        (await proof.CountAsync(tenant, "organizations")).Should().Be(1);

        // The first administrator needs the role and the person first; each refusal writes nothing.
        await AssertRuleAsync(
            await proof.PutAsync(Full, FirstAdminPath(tenant, membership), AdminBody(role, proof.PersonSubject)),
            HttpStatusCode.Conflict, "lockey_role_not_initialized");
        await AssertOkAsync(await proof.PutAsync(Full, $"/tenants/{tenant}/roles/{role}", RoleBody()));
        await AssertOkAsync(await proof.PutAsync(Full, $"/tenants/{tenant}/roles/{role}", RoleBody()));
        (await proof.CountAsync(tenant, "roles")).Should().Be(1);
        await AssertRuleAsync(
            await proof.PutAsync(Full, FirstAdminPath(tenant, membership), AdminBody(role, "nobody-has-signed-in")),
            HttpStatusCode.Conflict, "lockey_bootstrap_required");
        (await proof.CountAsync(tenant, "memberships")).Should().Be(0);

        await AssertOkAsync(await proof.PutAsync(Full, FirstAdminPath(tenant, membership), AdminBody(role, proof.PersonSubject)));
        await AssertOkAsync(await proof.PutAsync(Full, FirstAdminPath(tenant, membership), AdminBody(role, proof.PersonSubject)));
        (await proof.CountAsync(tenant, "memberships")).Should().Be(1);
        (await proof.CountAsync(tenant, "membership_role_assignments")).Should().Be(1);

        // The host mapping, then again.
        var mapped = await proof.PutAsync(Full, $"/tenants/{tenant}/host-mappings/{host}", """{"isActive":true,"isPubliclyLive":true}""");
        await AssertOkAsync(mapped);
        await AssertOkAsync(await proof.PutAsync(Full, $"/tenants/{tenant}/host-mappings/{host}", """{"isActive":true,"isPubliclyLive":true}"""));
        (await proof.HostMappingsAsync(host)).Should().Equal(tenant);

        // Who the business columns name, and what the audit rows say.
        (await proof.ScalarAsync<Guid>(tenant, "SELECT created_by FROM public.memberships")).Should().Be(UserId.SystemActor.Value);
        (await proof.ScalarAsync<Guid>(tenant, "SELECT user_id FROM public.memberships")).Should().Be(proof.Person.Value);
        var audit = await proof.AuditAsync(tenant);
        audit.Where(row => row.Outcome == "success").Select(row => row.Operation).Distinct().Should().BeEquivalentTo(
            ["tenancy.tenant.create", "tenancy.organization.create", "identity.role.initialize",
             "identity.membership.create_first_admin", "tenancy.hostmapping.write"]);
        audit.Should().OnlyContain(row => row.ActorUserId == null, "an operator act names no tenant user; the operator is in metadata");
        audit.Where(row => row.Outcome == "failed").Select(row => row.Operation).Should().Equal(
            ["identity.membership.create_first_admin", "identity.membership.create_first_admin"],
            "the two refused first-administrator attempts are recorded as the failures they were, and nothing else failed");
        audit.Should().OnlyContain(row => row.Metadata.Contains(proof.Operator.Issuer, StringComparison.Ordinal),
            "every row carries the admitted operator's issuer");
        audit.Should().OnlyContain(row => !row.Metadata.Contains(proof.PersonSubject, StringComparison.Ordinal)
            && !row.Metadata.Contains(Full, StringComparison.Ordinal), "neither the person's subject nor the operator's raw subject is stored");
        audit.Should().OnlyContain(row => !row.Metadata.Contains("INC-77", StringComparison.Ordinal), "the ticket stays out of rows a tenant can read");
        audit.Select(row => row.Reason).Distinct().Should().BeEquivalentTo(
            ["tenant_provision", "role_initialize", "first_admin_create", "host_map"]);
    }

    [Fact]
    public async Task A_Grant_Is_Evaluated_Per_Act_And_A_Refused_Act_Writes_Nothing()
    {
        await using var proof = await Proof.CreateAsync(parent);
        var tenant = Ids.Next();
        var organization = Ids.Next();
        var slug = "grants-" + Ids.Short();

        // Holding the tenant grant is not holding the bootstrap grant, and the reverse.
        (await proof.PutAsync(BootstrapOnly, $"/tenants/{tenant}", ProvisionBody(slug, organization))).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
        await AssertOkAsync(await proof.PutAsync(TenantOnly, $"/tenants/{tenant}", ProvisionBody(slug, organization)));

        var role = Ids.Next();
        (await proof.PutAsync(TenantOnly, $"/tenants/{tenant}/roles/{role}", RoleBody())).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await proof.PutAsync(TenantOnly, FirstAdminPath(tenant, Ids.Next()), AdminBody(role, proof.PersonSubject))).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
        (await proof.PutAsync(TenantOnly, RecoveryPath(tenant, Ids.Next()), AdminBody(role, proof.PersonSubject))).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
        (await proof.PutAsync("somebody-without-a-grant", $"/tenants/{Ids.Next()}", ProvisionBody("x-" + Ids.Short(), Ids.Next()))).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);

        (await proof.CountAsync(tenant, "roles")).Should().Be(0, "a refused act reaches no handler");
        (await proof.CountAsync(tenant, "memberships")).Should().Be(0);
    }

    [Fact]
    public async Task An_Act_On_A_Tenant_That_Does_Not_Exist_Is_Not_Found_And_A_Taken_Name_Is_Refused()
    {
        await using var proof = await Proof.CreateAsync(parent);
        var missing = Ids.Next();

        (await proof.PutAsync(Full, $"/tenants/{missing}/host-mappings/missing.example.test", "{}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await proof.PutAsync(Full, $"/tenants/{missing}/roles/{Ids.Next()}", RoleBody())).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await proof.PutAsync(Full, FirstAdminPath(missing, Ids.Next()), AdminBody(Ids.Next(), proof.PersonSubject))).StatusCode
            .Should().Be(HttpStatusCode.NotFound);

        var first = Ids.Next();
        var slug = "taken-" + Ids.Short();
        await AssertOkAsync(await proof.PutAsync(Full, $"/tenants/{first}", ProvisionBody(slug, Ids.Next())));
        await AssertRuleAsync(
            await proof.PutAsync(Full, $"/tenants/{Ids.Next()}", ProvisionBody(slug, Ids.Next())),
            HttpStatusCode.Conflict, "lockey_slug_taken");
        await AssertRuleAsync(
            await proof.PutAsync(Full, $"/tenants/{first}", ProvisionBody("another-" + Ids.Short(), Ids.Next())),
            HttpStatusCode.Conflict, "lockey_identifier_taken");
    }

    [Fact]
    public async Task Identifiers_That_Are_Not_UuidV7_Are_Refused_Before_Any_Write()
    {
        await using var proof = await Proof.CreateAsync(parent);
        var v4 = Guid.NewGuid();
        var tenant = Ids.Next();

        (await proof.PutAsync(Full, $"/tenants/{v4}", ProvisionBody("v4-" + Ids.Short(), Ids.Next()))).StatusCode
            .Should().Be(HttpStatusCode.BadRequest, "the tenant id is the act's identity and must be a UUIDv7");
        (await proof.PutAsync(Full, $"/tenants/{tenant}", ProvisionBody("v4-" + Ids.Short(), v4))).StatusCode
            .Should().Be(HttpStatusCode.BadRequest, "nor may the default organization's");
        (await proof.PutAsync(Full, $"/tenants/{tenant}/roles/{v4}", RoleBody())).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await proof.PutAsync(Full, FirstAdminPath(tenant, v4), AdminBody(Ids.Next(), proof.PersonSubject))).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
        (await proof.PutAsync(Full, FirstAdminPath(tenant, Ids.Next()), AdminBody(v4, proof.PersonSubject))).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
        (await proof.PutAsync(Full, RecoveryPath(tenant, v4), AdminBody(Ids.Next(), proof.PersonSubject))).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
        (await proof.CountAsync(Ids.Next(), "tenants")).Should().Be(0);
    }

    [Fact]
    public async Task A_Person_Whose_Binding_Is_No_Longer_Active_Cannot_Be_Named_An_Administrator()
    {
        await using var proof = await Proof.CreateAsync(parent);
        var tenant = Ids.Next();
        var role = Ids.Next();
        await AssertOkAsync(await proof.PutAsync(Full, $"/tenants/{tenant}", ProvisionBody("closing-" + Ids.Short(), Ids.Next())));
        await AssertOkAsync(await proof.PutAsync(Full, $"/tenants/{tenant}/roles/{role}", RoleBody()));
        await proof.CloseUserAsync(proof.Person);

        await AssertRuleAsync(
            await proof.PutAsync(Full, FirstAdminPath(tenant, Ids.Next()), AdminBody(role, proof.PersonSubject)),
            HttpStatusCode.Conflict, "lockey_bootstrap_required");
        (await proof.CountAsync(tenant, "memberships")).Should().Be(0);
    }

    [Fact]
    public async Task The_First_Administrator_Is_Refused_Once_A_Tenant_Has_Had_Any_Member()
    {
        await using var proof = await Proof.CreateAsync(parent);
        var (tenant, role, _) = await proof.OnboardAsync(Full);
        var (_, second) = await proof.AnotherPersonAsync();

        await AssertRuleAsync(
            await proof.PutAsync(Full, FirstAdminPath(tenant, Ids.Next()), AdminBody(role, second)),
            HttpStatusCode.Conflict, "lockey_tenant_has_memberships");
        // Nor may a Role template be created once a Membership exists.
        await AssertRuleAsync(
            await proof.PutAsync(Full, $"/tenants/{tenant}/roles/{Ids.Next()}", RoleBody()),
            HttpStatusCode.Conflict, "lockey_tenant_has_memberships");
        (await proof.CountAsync(tenant, "memberships")).Should().Be(1);
    }

    [Fact]
    public async Task A_Recovery_Is_Refused_While_An_Administrator_Is_Active_And_Succeeds_Once_None_Is()
    {
        await using var proof = await Proof.CreateAsync(parent);
        var (tenant, role, firstMembership) = await proof.OnboardAsync(Full);
        var (secondUser, second) = await proof.AnotherPersonAsync();
        var replacement = Ids.Next();

        await AssertRuleAsync(
            await proof.PutAsync(Full, RecoveryPath(tenant, replacement), AdminBody(role, second)),
            HttpStatusCode.Conflict, "lockey_administrator_exists");
        (await proof.CountAsync(tenant, "memberships")).Should().Be(1);

        // The database never lets the last administrator be revoked, so the state a recovery exists for is
        // reached only by an owner-level repair that steps outside the guard; the fixture does exactly that.
        await proof.SuspendOutsideTheGuardAsync(tenant, firstMembership);

        await AssertOkAsync(await proof.PutAsync(Full, RecoveryPath(tenant, replacement), AdminBody(role, second)));
        await AssertOkAsync(await proof.PutAsync(Full, RecoveryPath(tenant, replacement), AdminBody(role, second)));
        (await proof.ScalarAsync<long>(tenant, "SELECT count(*) FROM public.memberships WHERE status='Active'")).Should().Be(1);
        (await proof.AuditAsync(tenant)).Should().Contain(row =>
            row.Operation == "identity.membership.recover_admin" && row.Reason == "admin_recover" && row.Outcome == "success");

        // The act is repeatable but not additive: with an administrator active again a third person is refused.
        await AssertRuleAsync(
            await proof.PutAsync(Full, RecoveryPath(tenant, Ids.Next()), AdminBody(role, (await proof.AnotherPersonAsync()).Subject)),
            HttpStatusCode.Conflict, "lockey_administrator_exists");

        // The last-administrator invariant is the database's and the recovery leaves it as it found it: the
        // replacement, now the only administrator, cannot be revoked by the application role.
        var revoke = () => proof.RevokeAsApplicationRoleAsync(tenant, replacement, secondUser);
        (await revoke.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("P0303");
        (await proof.ScalarAsync<long>(tenant, "SELECT count(*) FROM public.memberships WHERE status='Active'")).Should().Be(1);
    }

    [Fact]
    public async Task A_Recovery_Names_An_Initialized_Administrator_Role_Of_The_Tenant_And_A_Person_Who_Has_Signed_In()
    {
        await using var proof = await Proof.CreateAsync(parent);
        var (tenant, _, firstMembership) = await proof.OnboardAsync(Full);
        await proof.SuspendOutsideTheGuardAsync(tenant, firstMembership);
        var (_, second) = await proof.AnotherPersonAsync();

        await AssertRuleAsync(
            await proof.PutAsync(Full, RecoveryPath(tenant, Ids.Next()), AdminBody(Ids.Next(), second)),
            HttpStatusCode.Conflict, "lockey_role_not_initialized");
        await AssertRuleAsync(
            await proof.PutAsync(Full, RecoveryPath(tenant, Ids.Next()), AdminBody(Ids.Next(), "nobody-has-signed-in")),
            HttpStatusCode.Conflict, "lockey_bootstrap_required");
        (await proof.ScalarAsync<long>(tenant, "SELECT count(*) FROM public.memberships WHERE status='Active'")).Should().Be(0);
    }

    [Fact]
    public async Task A_Recovery_Is_Refused_For_A_Role_That_Is_Not_An_Administrator_Role_Of_The_Tenant()
    {
        await using var proof = await Proof.CreateAsync(parent);
        var tenant = Ids.Next();
        var adminRole = Ids.Next();
        var learnerRole = Ids.Next();
        var membership = Ids.Next();
        await AssertOkAsync(await proof.PutAsync(Full, $"/tenants/{tenant}", ProvisionBody("roles-" + Ids.Short(), Ids.Next())));
        await AssertOkAsync(await proof.PutAsync(Full, $"/tenants/{tenant}/roles/{adminRole}", RoleBody()));
        await AssertOkAsync(await proof.PutAsync(
            Full, $"/tenants/{tenant}/roles/{learnerRole}", """{"builtInKey":"Learner","capabilityScope":"Tenant"}"""));
        await AssertOkAsync(await proof.PutAsync(Full, FirstAdminPath(tenant, membership), AdminBody(adminRole, proof.PersonSubject)));
        await proof.SuspendOutsideTheGuardAsync(tenant, membership);
        var (_, second) = await proof.AnotherPersonAsync();

        await AssertRuleAsync(
            await proof.PutAsync(Full, RecoveryPath(tenant, Ids.Next()), AdminBody(learnerRole, second)),
            HttpStatusCode.Conflict, "lockey_role_not_initialized");
        (await proof.ScalarAsync<long>(tenant, "SELECT count(*) FROM public.memberships WHERE status='Active'")).Should().Be(0);
    }

    private static string ProvisionBody(string slug, Guid organization) => JsonSerializer.Serialize(new
    {
        slug,
        displayName = "Onboarded " + slug,
        defaultOrganization = new { id = organization, slug = "main", displayName = "Main" },
        ticket = "INC-77",
    });

    private static string RoleBody() => """{"builtInKey":"TenantAdmin","capabilityScope":"Tenant","ticket":"INC-77"}""";

    private static string AdminBody(Guid role, string subject) => JsonSerializer.Serialize(new { roleId = role, providerSubject = subject, ticket = "INC-77" });

    private static string FirstAdminPath(Guid tenant, Guid membership) => $"/tenants/{tenant}/first-administrator/{membership}";

    private static string RecoveryPath(Guid tenant, Guid membership) => $"/tenants/{tenant}/administrator-recovery/{membership}";

    private static async Task AssertOkAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        using var document = JsonDocument.Parse(text);
        document.RootElement.GetProperty("correlationId").GetString().Should().NotBeNullOrWhiteSpace();
    }

    private static async Task AssertRuleAsync(HttpResponseMessage response, HttpStatusCode status, string reason)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(status, text);
        text.Should().Contain(reason);
    }

    private static class Ids
    {
        internal static Guid Next() => Guid.CreateVersion7();

        internal static string Short() => Guid.NewGuid().ToString("N")[..10];
    }

    private sealed record AuditRow(string Operation, string Outcome, Guid? ActorUserId, string? Reason, string Metadata);

    private sealed class Proof : IAsyncDisposable
    {
        private readonly HttpClient _client;
        private readonly Dictionary<string, string> _tokens = [];

        private Proof(TenantAuthenticationIssuer tenantIssuer, TenantAuthenticationIssuer @operator,
            IdentityPublicReadFixture database, UserId person, string personSubject, HttpClient client)
        {
            TenantIssuer = tenantIssuer;
            Operator = @operator;
            Database = database;
            Person = person;
            PersonSubject = personSubject;
            _client = client;
        }

        internal TenantAuthenticationIssuer TenantIssuer { get; }

        internal TenantAuthenticationIssuer Operator { get; }

        internal IdentityPublicReadFixture Database { get; }

        internal UserId Person { get; }

        internal string PersonSubject { get; }

        internal static async Task<Proof> CreateAsync(PublicReadFixture parent)
        {
            var tenantIssuer = await TenantAuthenticationIssuer.StartAsync();
            var @operator = await TenantAuthenticationIssuer.StartAsync();
            var person = UserId.From(Guid.CreateVersion7());
            var subject = "onboarding-person-" + person.Value.ToString("N");
            IdentityPublicReadFixture? database = null;
            HttpClient? client = null;
            try
            {
                database = await IdentityPublicReadFixture.CreateAsync(
                    parent, tenantIssuer.Issuer, new Dictionary<UserId, string> { [person] = subject });
                client = database.ClientFor("localhost", configureHost: ConfigureOperator(@operator));
                return new Proof(tenantIssuer, @operator, database, person, subject, client);
            }
            catch
            {
                client?.Dispose();
                if (database is not null) await database.DisposeAsync();
                await @operator.DisposeAsync();
                await tenantIssuer.DisposeAsync();
                throw;
            }
        }

        private static Action<IWebHostBuilder> ConfigureOperator(TenantAuthenticationIssuer @operator) => builder =>
        {
            builder.UseSetting("PlatformOperator:Issuer", @operator.Issuer);
            builder.UseSetting("PlatformOperator:AcceptedAcr:0", "mfa");
            builder.UseSetting("PlatformOperator:AuthorizedParties:0", "learnstack-operator");
            var grants = new (string Subject, string[] Keys)[]
            {
                (Full, [PlatformPermissions.TenantWrite.Key, PlatformPermissions.TenantBootstrapWrite.Key]),
                (TenantOnly, [PlatformPermissions.TenantWrite.Key]),
                (BootstrapOnly, [PlatformPermissions.TenantBootstrapWrite.Key]),
            };
            for (var index = 0; index < grants.Length; index++)
            {
                builder.UseSetting($"PlatformOperator:Grants:{index}:Subject", grants[index].Subject);
                for (var key = 0; key < grants[index].Keys.Length; key++)
                {
                    builder.UseSetting($"PlatformOperator:Grants:{index}:Permissions:{key}", grants[index].Keys[key]);
                }
            }
        };

        internal async Task<HttpResponseMessage> PutAsync(string operatorSubject, string path, string json)
        {
            if (!_tokens.TryGetValue(operatorSubject, out var token))
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                token = Operator.Token(JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["iss"] = Operator.Issuer,
                    ["sub"] = operatorSubject,
                    ["aud"] = "learnstack-platform",
                    ["iat"] = now - 5,
                    ["exp"] = now + 600,
                    ["azp"] = "learnstack-operator",
                    ["acr"] = "mfa",
                    ["auth_time"] = now - 30,
                }));
                _tokens[operatorSubject] = token;
            }

            using var request = new HttpRequestMessage(HttpMethod.Put, "/api/platform/v1" + path)
            {
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) },
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            return await _client.SendAsync(request);
        }

        /// <summary>A person who has signed in, so the first-sign-in bootstrap has bound them.</summary>
        internal async Task<(UserId Person, string Subject)> AnotherPersonAsync()
        {
            var person = UserId.From(Guid.CreateVersion7());
            var subject = "onboarding-person-" + person.Value.ToString("N");
            await Database.AdoptAsync(person, subject);
            return (person, subject);
        }

        /// <summary>
        /// A user closed, with the immutability trigger and the insert check stepped around exactly as the
        /// administration-eligibility cases do, and restored before the next act runs.
        /// </summary>
        internal async Task CloseUserAsync(UserId user)
        {
            await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(Database.MigrationConnectionString);
            var check = await IdentityMembershipFixture.ScalarAsync<string>(owner, null,
                "SELECT pg_get_expr(polwithcheck,polrelid) FROM pg_policy WHERE polrelid='public.users'::regclass AND polname='users_isolation'");
            await using var transaction = await owner.BeginTransactionAsync();
            await IdentityMembershipFixture.ExecuteAsync(owner, transaction, $"""
                ALTER TABLE public.users DISABLE TRIGGER tg_users_no_update;
                ALTER POLICY users_isolation ON public.users WITH CHECK (({check}) OR
                    (current_user='learnstack_migration' AND id='{user.Value:D}'::uuid AND status='Closing'
                     AND deleted_at IS NULL AND deleted_by IS NULL));
                UPDATE public.users SET status='Closing' WHERE id='{user.Value:D}'::uuid;
                ALTER POLICY users_isolation ON public.users WITH CHECK ({check});
                ALTER TABLE public.users ENABLE TRIGGER tg_users_no_update;
                """);
            await transaction.CommitAsync();
        }

        /// <summary>The whole onboarding through the real routes; returns the tenant, its role and its first membership.</summary>
        internal async Task<(Guid Tenant, Guid Role, Guid Membership)> OnboardAsync(string operatorSubject)
        {
            var tenant = Ids.Next();
            var role = Ids.Next();
            var membership = Ids.Next();
            await AssertOkAsync(await PutAsync(operatorSubject, $"/tenants/{tenant}", ProvisionBody("full-" + Ids.Short(), Ids.Next())));
            await AssertOkAsync(await PutAsync(operatorSubject, $"/tenants/{tenant}/roles/{role}", RoleBody()));
            await AssertOkAsync(await PutAsync(operatorSubject, FirstAdminPath(tenant, membership), AdminBody(role, PersonSubject)));
            return (tenant, role, membership);
        }

        internal Task<long> CountAsync(Guid tenant, string table) =>
            ScalarAsync<long>(tenant, $"SELECT count(*) FROM public.{table}");

        internal async Task<T> ScalarAsync<T>(Guid tenant, string sql)
        {
            await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(Database.AppConnectionString);
            await SchemaQueries.AssertApplicationRoleAsync(app);
            await using var transaction = await app.BeginTransactionAsync();
            await IdentityMembershipFixture.ScopeAsync(app, transaction, tenant);
            await using var command = new NpgsqlCommand(sql, app, transaction);
            return (T)(await command.ExecuteScalarAsync())!;
        }

        internal async Task<List<AuditRow>> AuditAsync(Guid tenant)
        {
            await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(Database.AppConnectionString);
            await SchemaQueries.AssertApplicationRoleAsync(app);
            await using var transaction = await app.BeginTransactionAsync();
            await IdentityMembershipFixture.ScopeAsync(app, transaction, tenant);
            await using var command = new NpgsqlCommand(
                "SELECT operation,outcome,actor_user_id,reason,metadata::text FROM public.audit_log ORDER BY timestamp", app, transaction);
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<AuditRow>();
            while (await reader.ReadAsync())
            {
                rows.Add(new(
                    reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetGuid(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4)));
            }

            return rows;
        }

        /// <summary>
        /// What the resolver itself would read for the host: the application role, announcing only the host
        /// it is resolving, which is the one thing row security lets it see of the host index.
        /// </summary>
        internal async Task<List<Guid>> HostMappingsAsync(string host)
        {
            await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(Database.AppConnectionString);
            await SchemaQueries.AssertApplicationRoleAsync(app);
            await using var transaction = await app.BeginTransactionAsync();
            await IdentityMembershipFixture.ExecuteAsync(
                app, transaction, "SELECT set_config('app.resolving_host', @host, true)", ("host", host));
            await using var command = new NpgsqlCommand(
                "SELECT tenant_id FROM public.platform_host_to_tenant WHERE host=@host", app, transaction);
            command.Parameters.AddWithValue("host", host);
            await using var reader = await command.ExecuteReaderAsync();
            var tenants = new List<Guid>();
            while (await reader.ReadAsync())
            {
                tenants.Add(reader.GetGuid(0));
            }

            return tenants;
        }

        /// <summary>
        /// The fixture's one deliberate corruption: the only administrator suspended with the deferred
        /// last-administrator guard stepped around, which is the state a recovery exists for.
        /// </summary>
        /// <remarks>
        /// The owner is subject to forced row security, so it announces the tenant like any other connection and
        /// takes the same locks; what it skips is the commit-time guard that refuses a tenant with no administrator.
        /// </remarks>
        internal async Task SuspendOutsideTheGuardAsync(Guid tenant, Guid membership)
        {
            await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(Database.MigrationConnectionString);
            await using var transaction = await owner.BeginTransactionAsync();
            await IdentityMembershipFixture.ScopeAsync(owner, transaction, tenant);
            await IdentityMembershipFixture.LockAsync(owner, transaction, tenant, Person.Value, exclusive: true);
            await IdentityMembershipFixture.ExecuteAsync(owner, transaction, "ALTER TABLE public.memberships DISABLE TRIGGER tg_memberships_complete_graph");
            await IdentityMembershipFixture.ExecuteAsync(
                owner, transaction, "UPDATE public.memberships SET status='Suspended' WHERE id=@membership", ("membership", membership));
            await IdentityMembershipFixture.ExecuteAsync(owner, transaction, "ALTER TABLE public.memberships ENABLE TRIGGER tg_memberships_complete_graph");
            await transaction.CommitAsync();
        }

        /// <summary>An ordinary revocation by the application role, under the locks a handler would take.</summary>
        internal async Task RevokeAsApplicationRoleAsync(Guid tenant, Guid membership, UserId user)
        {
            await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(Database.AppConnectionString);
            await SchemaQueries.AssertApplicationRoleAsync(app);
            await using var transaction = await app.BeginTransactionAsync();
            await IdentityMembershipFixture.ScopeAsync(app, transaction, tenant);
            await IdentityMembershipFixture.LockAsync(app, transaction, tenant, user.Value, exclusive: true);
            await IdentityMembershipFixture.ExecuteAsync(app, transaction, """
                UPDATE public.memberships SET status='Revoked', deleted_at=clock_timestamp(), deleted_by=@actor WHERE id=@membership
                """, ("actor", UserId.SystemActor.Value), ("membership", membership));
            await transaction.CommitAsync();
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await Database.DisposeAsync();
            await Operator.DisposeAsync();
            await TenantIssuer.DisposeAsync();
        }
    }
}
