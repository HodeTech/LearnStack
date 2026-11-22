using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Application.Contracts.DataRequests;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// ADR-0106 § 9 and ADR-0107 § 8: <c>fn_identity_own_data_request_scopes</c> and its two owner arms, connected as
/// <c>learnstack_app</c> (and, for the refusals, as every other role). The function returns exactly the subject's own
/// groups in the host's tenant — Memberships of any status and requests — and nothing of another person, tenant or
/// branch; the arms admit only the state the function itself requires.
/// </summary>
/// <remarks>
/// Seeded by <see cref="IdentityMembershipFixture"/>: in tenant A, PersonA holds the tenant-wide Membership and one in
/// each organization, with an export request in each of those three groups, and AdminA holds only the tenant-wide
/// administrator Membership; in tenant B, AdminB holds the tenant-wide Membership and PersonB the organization's, each
/// with a request. The tests that change a lifecycle run on their own disposable database.
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class OwnDataRequestScopeTests(IdentityMembershipFixture fixture) : IClassFixture<IdentityMembershipFixture>
{
    private const string Signature = "public.fn_identity_own_data_request_scopes(uuid,uuid,uuid,text,uuid,uuid,integer)";
    private static readonly (Guid Tenant, Guid? Organization) TenantWideA = (SchemaFixture.TenantA, null);
    private static readonly (Guid Tenant, Guid? Organization) BranchA1 = (SchemaFixture.TenantA, SchemaFixture.OrgA1);
    private static readonly (Guid Tenant, Guid? Organization) BranchA2 = (SchemaFixture.TenantA, SchemaFixture.OrgA2);

    [Fact]
    public async Task The_subject_sees_exactly_their_own_groups_in_the_host_and_nothing_else()
    {
        var app = fixture.Postgres.AppConnectionString;
        (await ListAsync(app, PersonA, SchemaFixture.TenantA, null)).Should().Equal(TenantWideA, BranchA1, BranchA2);
        (await ListAsync(app, AdminA, SchemaFixture.TenantA, null)).Should().Equal(TenantWideA);
        // An organization host discovers only its own branch, never the tenant-wide group or another branch.
        (await ListAsync(app, PersonA, SchemaFixture.TenantA, SchemaFixture.OrgA1)).Should().Equal(BranchA1);
        (await ListAsync(app, PersonA, SchemaFixture.TenantA, SchemaFixture.OrgA2)).Should().Equal(BranchA2);
        (await ListAsync(app, AdminA, SchemaFixture.TenantA, SchemaFixture.OrgA1)).Should().BeEmpty();
        // Two tenants: the actor argument selects that subject alone, in the host's tenant alone.
        (await ListAsync(app, PersonA, SchemaFixture.TenantB, null)).Should().BeEmpty();
        (await ListAsync(app, PersonB, SchemaFixture.TenantA, null)).Should().BeEmpty();
        (await ListAsync(app, PersonB, SchemaFixture.TenantB, null)).Should().Equal((SchemaFixture.TenantB, (Guid?)SchemaFixture.OrgB1));
        (await ListAsync(app, AdminB, SchemaFixture.TenantB, null)).Should().Equal((SchemaFixture.TenantB, (Guid?)null));
        (await ListAsync(app, AdminB, SchemaFixture.TenantB, SchemaFixture.OrgB1)).Should().BeEmpty();
        (await ListAsync(app, Guid.CreateVersion7(), SchemaFixture.TenantA, null)).Should().BeEmpty("an unknown person has no group");
    }

    [Fact]
    public async Task List_pages_are_ordered_tenant_wide_first_and_selection_proves_one_exact_group()
    {
        var app = fixture.Postgres.AppConnectionString;
        (await CallAsync(app, PersonA, SchemaFixture.TenantA, null, "list", null, null, 1)).Should().Equal(TenantWideA);
        (await CallAsync(app, PersonA, SchemaFixture.TenantA, null, "list", null, Guid.Empty, 1)).Should().Equal(new[] { BranchA1 },
            "the nil key is the cursor that follows the tenant-wide group");
        (await CallAsync(app, PersonA, SchemaFixture.TenantA, null, "list", null, SchemaFixture.OrgA1, 1)).Should().Equal(BranchA2);
        (await CallAsync(app, PersonA, SchemaFixture.TenantA, null, "list", null, SchemaFixture.OrgA2, 20)).Should().BeEmpty();

        (await CallAsync(app, PersonA, SchemaFixture.TenantA, null, "select", null, null, 1)).Should().Equal(TenantWideA);
        (await CallAsync(app, PersonA, SchemaFixture.TenantA, null, "select", SchemaFixture.OrgA2, null, 1)).Should().Equal(BranchA2);
        (await CallAsync(app, PersonA, SchemaFixture.TenantA, SchemaFixture.OrgA1, "select", null, null, 1)).Should().BeEmpty(
            "an organization host cannot select the tenant-wide group");
        (await CallAsync(app, PersonA, SchemaFixture.TenantA, SchemaFixture.OrgA1, "select", SchemaFixture.OrgA2, null, 1)).Should().BeEmpty(
            "an organization host cannot select another branch");
        (await CallAsync(app, PersonA, SchemaFixture.TenantA, null, "select", SchemaFixture.OrgB1, null, 1)).Should().BeEmpty();
        (await CallAsync(app, AdminA, SchemaFixture.TenantA, null, "select", SchemaFixture.OrgA1, null, 1)).Should().BeEmpty(
            "another person's group is not this subject's");
    }

    public static TheoryData<string> Refusals() => new()
    {
        "read-write", "repeatable-read", "tenant-announced", "organization-announced", "scope-announced",
        "identity-announced", "null-actor", "nil-actor", "system-actor", "null-host", "nil-host", "sentinel-host",
        "nil-host-organization", "nil-organization", "null-mode", "unknown-mode", "null-limit", "zero-limit", "over-limit",
        "list-with-organization", "select-with-after", "select-limit-two",
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task Every_state_and_argument_outside_the_two_modes_is_refused(string label)
    {
        var start = label switch
        {
            "read-write" => "SET TRANSACTION READ WRITE",
            "repeatable-read" => "SET TRANSACTION READ ONLY, ISOLATION LEVEL REPEATABLE READ",
            _ => "SET TRANSACTION READ ONLY",
        };
        var setup = label switch
        {
            "tenant-announced" => $"SELECT set_config('app.tenant_id','{SchemaFixture.TenantA:D}',true)",
            "organization-announced" => $"SELECT set_config('app.organization_id','{SchemaFixture.OrgA1:D}',true)",
            "scope-announced" => "SELECT set_config('app.scope','tenant',true)",
            "identity-announced" => $"SELECT set_config('app.identity_authority_id','{Authority.AuthorityId.Value:D}',true)",
            _ => null,
        };
        Guid? actor = label switch
        {
            "null-actor" => null,
            "nil-actor" => Guid.Empty,
            "system-actor" => UserId.SystemActor.Value,
            _ => PersonA,
        };
        Guid? host = label switch
        {
            "null-host" => null,
            "nil-host" => Guid.Empty,
            "sentinel-host" => TenantId.PlatformSentinel.Value,
            _ => SchemaFixture.TenantA,
        };
        var mode = label switch { "null-mode" => null, "unknown-mode" => "selectall", "select-with-after" or "select-limit-two" => "select", _ => "list" };
        int? limit = label switch { "null-limit" => null, "zero-limit" => 0, "over-limit" => 21, "select-limit-two" => 2, _ => 1 };
        var call = () => CallAsync(fixture.Postgres.AppConnectionString, actor, host,
            label == "nil-host-organization" ? Guid.Empty : null, mode,
            label switch { "nil-organization" => Guid.Empty, "list-with-organization" => SchemaFixture.OrgA1, _ => null },
            label == "select-with-after" ? SchemaFixture.OrgA1 : null, limit, start, setup);
        (await call.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
    }

    [Theory]
    [InlineData("platform")]
    [InlineData("outbox")]
    public async Task No_other_role_executes_the_function_or_reads_the_tables_it_reads(string role)
    {
        var connection = role == "platform" ? fixture.Postgres.PlatformConnectionString : fixture.Postgres.OutboxConnectionString;
        var call = () => CallAsync(connection, PersonA, SchemaFixture.TenantA, null, "list", null, null, 20);
        (await call.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");
        await using var other = (NpgsqlConnection)await PostgresFixture.OpenAsync(connection);
        var read = () => ScalarAsync<long>(other, null, "SELECT count(*) FROM public.memberships");
        (await read.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");
        if (role == "outbox")
        {
            var requests = () => ScalarAsync<long>(other, null, "SELECT count(*) FROM public.data_subject_requests");
            (await requests.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");
        }
    }

    [Fact]
    public async Task The_function_acl_is_exactly_the_application_role_and_the_arms_open_nothing_to_a_session()
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Postgres.MigrationConnectionString);
        (await SchemaQueries.ReadStringsAsync(owner, $"""
            SELECT CASE WHEN a.grantee=0 THEN 'PUBLIC' ELSE pg_get_userbyid(a.grantee)::text END || ':' || a.privilege_type
            FROM pg_proc p, aclexplode(p.proacl) a
            WHERE p.oid='{Signature}'::regprocedure AND a.grantee<>p.proowner ORDER BY 1
            """, null)).Should().Equal("learnstack_app:EXECUTE");
        (await ScalarAsync<string>(owner, null, $"SELECT pg_get_userbyid(proowner)::text FROM pg_proc WHERE oid='{Signature}'::regprocedure"))
            .Should().Be("learnstack_migration");
        foreach (var role in new[] { "learnstack_platform", "learnstack_outbox_admin", "learnstack_identity_ingress", "learnstack_identity_rebind" })
            (await ScalarAsync<bool>(owner, null, $"SELECT has_function_privilege('{role}','{Signature}','EXECUTE')"))
                .Should().BeFalse(role + " has no path to the retained scope");

        // In exactly the function's own state, but as a session rather than a definer body: both arms stay shut,
        // for the application login (current_user is not the owner) and for the owner login (session_user is not
        // the application role) alike.
        foreach (var connection in new[] { fixture.Postgres.AppConnectionString, fixture.Postgres.MigrationConnectionString })
        {
            await using var session = (NpgsqlConnection)await PostgresFixture.OpenAsync(connection);
            await using var transaction = await session.BeginTransactionAsync();
            await ExecuteAsync(session, transaction, "SET TRANSACTION READ ONLY");
            (await ScalarAsync<long>(session, transaction, "SELECT count(*) FROM public.memberships")).Should().Be(0);
            (await ScalarAsync<long>(session, transaction, "SELECT count(*) FROM public.data_subject_requests")).Should().Be(0);
        }
    }

    /// <summary>
    /// The arms are state-bound, not caller-bound, so this dirty probe states exactly what they admit: a second
    /// owner-run definer sees every Membership and request in the function's own pre-context read-only state and
    /// nothing in any other. That breadth is why the definer census pins every body that may run as the owner.
    /// </summary>
    [Fact]
    public async Task The_owner_arms_admit_only_the_functions_own_state_and_restore_cleanly()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(fixture.Postgres);
        await SeedSchemaAsync(database.MigrationConnectionString, database.AppConnectionString);
        await using (var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString))
            await ExecuteAsync(owner, null, """
                CREATE FUNCTION public.own_data_request_arm_probe() RETURNS TABLE (members bigint, requests bigint)
                    LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $probe$
                    SELECT (SELECT count(*) FROM public.memberships), (SELECT count(*) FROM public.data_subject_requests) $probe$;
                REVOKE ALL ON FUNCTION public.own_data_request_arm_probe() FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION public.own_data_request_arm_probe() TO learnstack_app;
                """);
        async Task<(long Members, long Requests)> ProbeAsync(string start, string? setup)
        {
            await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
            await using var transaction = await app.BeginTransactionAsync();
            await ExecuteAsync(app, transaction, start);
            if (setup is not null) await ExecuteAsync(app, transaction, setup);
            await using var command = new NpgsqlCommand("SELECT members, requests FROM public.own_data_request_arm_probe()", app, transaction);
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            return (reader.GetInt64(0), reader.GetInt64(1));
        }
        (await ProbeAsync("SET TRANSACTION READ ONLY", null)).Should().Be((6L, 5L), "the function's own state sees every row it filters");
        (await ProbeAsync("SET TRANSACTION READ WRITE", null)).Should().Be((0L, 0L));
        (await ProbeAsync("SET TRANSACTION READ ONLY", $"SELECT set_config('app.tenant_id','{SchemaFixture.TenantB:D}',true)"))
            .Should().Be((1L, 1L), "an announced tenant leaves only the application arm: tenant B's tenant-wide rows");
        (await ProbeAsync("SET TRANSACTION READ ONLY", "SELECT set_config('app.scope','tenant',true)")).Should().Be((0L, 0L));
        (await ProbeAsync("SET TRANSACTION READ ONLY", "SELECT set_config('app.organization_id','" + SchemaFixture.OrgA1.ToString("D") + "',true)"))
            .Should().Be((0L, 0L));
        (await ProbeAsync("SET TRANSACTION READ ONLY", "SELECT set_config('app.identity_subject_key',repeat('0',64),true)"))
            .Should().Be((0L, 0L));
    }

    /// <summary>The policy is the independent oracle, canonicalized by PostgreSQL itself, not loaded from the migration.</summary>
    [Fact]
    public async Task The_request_policy_is_the_canonical_template_plus_exactly_the_one_owner_arm()
    {
        const string ExpectedAppUsing = """
            tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
            AND (organization_id IS NULL
                OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
                OR NULLIF(current_setting('app.scope', true), '') = 'tenant')
            """;
        const string ExpectedOwnerUsing = """
            current_user = 'learnstack_migration' AND session_user = 'learnstack_app'
            AND NULLIF(current_setting('app.tenant_id', true), '') IS NULL
            AND NULLIF(current_setting('app.organization_id', true), '') IS NULL
            AND NULLIF(current_setting('app.scope', true), '') IS NULL
            AND NULLIF(current_setting('app.identity_authority_id', true), '') IS NULL
            AND NULLIF(current_setting('app.identity_subject_key', true), '') IS NULL
            AND current_setting('transaction_read_only') = 'on'
            """;
        const string ExpectedCheck = """
            tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
            AND ((organization_id IS NULL AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid)
            """;
        await using var database = await DisposableSchemaDatabase.CreateAsync(fixture.Postgres);
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await ExecuteAsync(owner, null, $"""
            CREATE TEMP TABLE request_policy_expected (tenant_id uuid, organization_id uuid);
            CREATE POLICY request_policy_expected ON request_policy_expected
                USING (({ExpectedAppUsing}) OR ({ExpectedOwnerUsing})) WITH CHECK ({ExpectedCheck});
            """);
        const string Read = """
            SELECT pg_get_expr(polqual,polrelid) || ' | ' || pg_get_expr(polwithcheck,polrelid) || ' | ' || polcmd::text
                || ' | ' || polpermissive::text
            FROM pg_policy WHERE polrelid=to_regclass(@relation) AND polname=@policy
            """;
        var expected = await ScalarAsync<string>(owner, null, Read, ("relation", "pg_temp.request_policy_expected"), ("policy", "request_policy_expected"));
        var actual = await ScalarAsync<string>(owner, null, Read, ("relation", "public.data_subject_requests"), ("policy", "data_subject_requests_isolation"));
        actual.Should().Be(expected);
        (await SchemaQueries.ReadStringsAsync(owner, """
            SELECT polname::text FROM pg_policy WHERE polrelid='public.data_subject_requests'::regclass ORDER BY polname
            """, null)).Should().Equal("data_subject_requests_isolation", "data_subject_requests_org_delete_guard", "data_subject_requests_org_write_guard");
        foreach (var table in new[] { "data_export_artifacts", "data_export_tickets", "data_export_chunks" })
            (await ScalarAsync<string>(owner, null, "SELECT pg_get_expr(polqual,polrelid) FROM pg_policy WHERE polrelid=to_regclass(@relation) AND polname=@policy",
                ("relation", "public." + table), ("policy", table + "_isolation"))).Should().NotContain("learnstack_migration",
                "only data_subject_requests gains an owner arm; the artifact, ticket and chunk tables keep the bare template");
    }

    [Fact]
    public async Task Suspended_and_revoked_memberships_and_a_request_only_group_are_retained()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(fixture.Postgres);
        await SeedSchemaAsync(database.MigrationConnectionString, database.AppConnectionString);
        await using (var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString))
        {
            await SchemaQueries.AssertApplicationRoleAsync(app);
            foreach (var (organization, member, status) in new[]
            {
                (SchemaFixture.OrgA2, BranchMembershipA2, "Suspended"),
                (SchemaFixture.OrgA1, BranchMembershipA1, "Revoked"),
            })
            {
                await using var transaction = await app.BeginTransactionAsync();
                await ScopeAsync(app, transaction, SchemaFixture.TenantA, organization);
                await LockAsync(app, transaction, SchemaFixture.TenantA, PersonA, false);
                await ExecuteAsync(app, transaction, status == "Revoked"
                    ? """
                      UPDATE public.memberships SET status='Revoked',deleted_at=clock_timestamp(),deleted_by=@actor,
                          row_version=row_version+1 WHERE id=@member;
                      SET CONSTRAINTS ALL IMMEDIATE;
                      """
                    : "UPDATE public.memberships SET status='Suspended',row_version=row_version+1 WHERE id=@member; SET CONSTRAINTS ALL IMMEDIATE;",
                    ("member", member), ("actor", AdminA));
                await transaction.CommitAsync();
            }
        }
        // A request whose group holds no Membership row: the application guard refuses to write one, so the owner
        // writes it with that guard disabled, the way a group could otherwise only be reached by retained evidence.
        var requestOnly = Guid.CreateVersion7();
        await using (var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString))
        {
            await using var transaction = await owner.BeginTransactionAsync();
            await ExecuteAsync(owner, transaction, "ALTER TABLE public.data_subject_requests DISABLE TRIGGER tg_data_subject_requests_mutation_guard");
            await ScopeAsync(owner, transaction, SchemaFixture.TenantA, SchemaFixture.OrgA1);
            await ExecuteAsync(owner, transaction, """
                INSERT INTO public.data_subject_requests(id,tenant_id,organization_id,kind,origin,subject_user_id,requester_user_id,
                    status,membership_snapshot,execute_at,created_at,created_by)
                VALUES(@id,@tenant,@organization,'export','self',@subject,@subject,'requested',@snapshot::jsonb,NULL,now(),@subject)
                """, ("id", requestOnly), ("tenant", SchemaFixture.TenantA), ("organization", SchemaFixture.OrgA1),
                ("subject", PersonB), ("snapshot", DataRequestSchemaSeed.Snapshot(Guid.CreateVersion7())));
            await ExecuteAsync(owner, transaction, "ALTER TABLE public.data_subject_requests ENABLE TRIGGER tg_data_subject_requests_mutation_guard");
            await transaction.CommitAsync();
        }

        (await ListAsync(database.AppConnectionString, PersonA, SchemaFixture.TenantA, null)).Should().Equal(new[] { TenantWideA, BranchA1, BranchA2 },
            "a Revoked tombstone and a Suspended Membership keep their groups");
        (await ListAsync(database.AppConnectionString, PersonB, SchemaFixture.TenantA, null)).Should().Equal(new[] { BranchA1 },
            "a request alone keeps its group");
        (await ListAsync(database.AppConnectionString, PersonB, SchemaFixture.TenantA, SchemaFixture.OrgA2)).Should().BeEmpty();
        (await ListAsync(database.AppConnectionString, AdminA, SchemaFixture.TenantA, null)).Should().Equal(new[] { TenantWideA },
            "another person's request in a group never lends that group to a bystander");

    }

    [Fact]
    public async Task The_confined_lookup_rechecks_the_function_and_runs_on_its_own_read_only_owner()
    {
        await using var dataSource = ApplicationDataSource.Build(fixture.Postgres.AppConnectionString);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(dataSource);
        services.AddScoped<IUnitOfWork, NpgsqlUnitOfWork>();
        services.AddScoped<IOwnDataRequestScopeLookup, OwnDataRequestScopeLookup>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var lookup = scope.ServiceProvider.GetRequiredService<IOwnDataRequestScopeLookup>();
        var ambient = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var actor = UserId.From(PersonA);
        var tenant = TenantId.From(SchemaFixture.TenantA);
        var branch = OrganizationId.From(SchemaFixture.OrgA2);

        var listed = await lookup.LookupAsync(actor, tenant, null, OwnDataRequestLookupMode.List, null, null, 20);
        listed.Should().Equal(new DataRequestScopeGroup(tenant, null), new DataRequestScopeGroup(tenant, OrganizationId.From(SchemaFixture.OrgA1)),
            new DataRequestScopeGroup(tenant, branch));
        ambient.HasActiveTransaction.Should().BeFalse("the preliminary owner is disposed before any observation escapes");
        (await lookup.LookupAsync(actor, tenant, null, OwnDataRequestLookupMode.List, null, listed[0], 1))
            .Should().Equal(listed[1]);
        (await lookup.LookupAsync(actor, tenant, branch, OwnDataRequestLookupMode.Select, new DataRequestScopeGroup(tenant, branch), null, 1))
            .Should().Equal(new DataRequestScopeGroup(tenant, branch));
        (await lookup.LookupAsync(UserId.From(PersonB), tenant, null, OwnDataRequestLookupMode.List, null, null, 20)).Should().BeEmpty();

        var foreignSelection = () => lookup.LookupAsync(actor, tenant, null, OwnDataRequestLookupMode.Select,
            new DataRequestScopeGroup(TenantId.From(SchemaFixture.TenantB), null), null, 1);
        await foreignSelection.Should().ThrowAsync<ArgumentException>();
        var wideList = () => lookup.LookupAsync(actor, tenant, null, OwnDataRequestLookupMode.List, null, null, 21);
        await wideList.Should().ThrowAsync<ArgumentException>();
        var system = () => lookup.LookupAsync(UserId.SystemActor, tenant, null, OwnDataRequestLookupMode.List, null, null, 20);
        await system.Should().ThrowAsync<ArgumentException>();

        await using (var frame = await ambient.BeginTransactionAsync(TransactionMode.ReadOnly))
        {
            var joined = () => lookup.LookupAsync(actor, tenant, null, OwnDataRequestLookupMode.List, null, null, 20);
            await joined.Should().ThrowAsync<InfrastructureException>("the preliminary read never joins an ambient owner");
            await frame.FailAsync();
        }
    }

    /// <summary>
    /// The fifteenth logical tenant setter: the witness the function's result produced announces exactly its group on a
    /// fresh physical owner in the witness's mode, and nothing else. Test-only captured provenance; this claims no HTTP
    /// credential admission.
    /// </summary>
    [Theory]
    [InlineData("read")]
    [InlineData("create")]
    public async Task The_captured_group_is_announced_on_its_own_fresh_owner(string label)
    {
        var purpose = label == "read" ? OwnDataRequestPurpose.Read : OwnDataRequestPurpose.Create;
        var actor = UserId.From(PersonA);
        var tenant = TenantId.From(SchemaFixture.TenantA);
        var group = new DataRequestScopeGroup(tenant, OrganizationId.From(SchemaFixture.OrgA2));
        await using var dataSource = ApplicationDataSource.Build(fixture.Postgres.AppConnectionString);
        using var owner = new CapturedOwner(purpose, group);

        await using var unit = new NpgsqlUnitOfWork(dataSource, Microsoft.Extensions.Logging.Abstractions.NullLogger<NpgsqlUnitOfWork>.Instance);
        await using (var frame = await unit.BeginOwnDataRequestTransactionAsync(owner.Witness, owner.Context))
        {
            unit.IsOwnDataRequestOwner(owner.Witness).Should().BeTrue();
            unit.Mode.Should().Be(owner.Witness.Mode);
            (await UnitScalarAsync<string>(unit, "SELECT current_setting('transaction_read_only')")).Should().Be(label == "read" ? "on" : "off");
            (await UnitScalarAsync<string>(unit, "SELECT current_setting('app.tenant_id')")).Should().Be(SchemaFixture.TenantA.ToString("D"));
            (await UnitScalarAsync<string>(unit, "SELECT current_setting('app.organization_id')")).Should().Be(SchemaFixture.OrgA2.ToString("D"));
            (await UnitScalarAsync<string>(unit, "SELECT COALESCE(NULLIF(current_setting('app.scope',true),''),'none')")).Should().Be("none");
            // The canonical policy shows the group's rows and the tenant-wide group's, never a sibling branch or another
            // tenant: a handler still filters to the exact group.
            (await UnitScalarAsync<Guid[]>(unit, "SELECT array_agg(id ORDER BY id) FROM public.data_subject_requests"))
                .Should().BeEquivalentTo([DataRequestSchemaSeed.TenantWideA.Request, DataRequestSchemaSeed.BranchA2.Request]);
            var nested = () => unit.BeginTransactionAsync(TransactionMode.ReadOnly);
            await nested.Should().ThrowAsync<InvalidOperationException>();
            unit.IsOwnDataRequestOwner(owner.Witness).Should().BeFalse("a refused join poisons the owner");
            await frame.FailAsync();
        }
        unit.IsOwnDataRequestOwner(owner.Witness).Should().BeFalse();
        await using (var fresh = new NpgsqlUnitOfWork(dataSource, Microsoft.Extensions.Logging.Abstractions.NullLogger<NpgsqlUnitOfWork>.Instance))
        {
            var reuse = () => fresh.BeginOwnDataRequestTransactionAsync(owner.Witness, owner.Context);
            await reuse.Should().ThrowAsync<InvalidOperationException>("a witness binds to one physical transaction once");
            fresh.IsOwnDataRequestOwner(owner.Witness).Should().BeFalse();
        }

        await using var pooled = await dataSource.OpenConnectionAsync();
        await using var check = new NpgsqlCommand("""
            SELECT NULLIF(current_setting('app.tenant_id',true), '') IS NULL
               AND NULLIF(current_setting('app.organization_id',true), '') IS NULL
               AND NULLIF(current_setting('app.scope',true), '') IS NULL
            """, pooled);
        (await check.ExecuteScalarAsync()).Should().Be(true, "a returned pooled connector keeps no group authority");
    }

    [Theory]
    [InlineData("http-entry")]
    [InlineData("other-context")]
    [InlineData("closed-source")]
    [InlineData("ambient-owner")]
    public async Task The_setter_refuses_any_witness_that_is_not_its_own_fresh_application_owner(string label)
    {
        var tenant = TenantId.From(SchemaFixture.TenantA);
        var group = new DataRequestScopeGroup(tenant, OrganizationId.From(SchemaFixture.OrgA1));
        await using var dataSource = ApplicationDataSource.Build(fixture.Postgres.AppConnectionString);
        using var owner = new CapturedOwner(OwnDataRequestPurpose.Read, group, http: label == "http-entry");
        var context = label == "other-context"
            ? LearnStack.SharedKernel.Tenancy.TenantContextFactory.CreateForOwnDataRequestScope(tenant, null, UserId.From(PersonA), group, null).Value!
            : owner.Context;
        if (label == "closed-source") owner.Close();
        await using var unit = new NpgsqlUnitOfWork(dataSource, Microsoft.Extensions.Logging.Abstractions.NullLogger<NpgsqlUnitOfWork>.Instance);
        IUnitOfWorkScope? ambient = label == "ambient-owner" ? await unit.BeginTransactionAsync(TransactionMode.ReadOnly) : null;
        var begin = () => unit.BeginOwnDataRequestTransactionAsync(owner.Witness, context);
        await begin.Should().ThrowAsync<InvalidOperationException>();
        unit.IsOwnDataRequestOwner(owner.Witness).Should().BeFalse();
        unit.IsRollbackOnly.Should().BeTrue();
        if (ambient is not null) { await ambient.FailAsync(); await ambient.DisposeAsync(); }
    }

    private static async Task<T> UnitScalarAsync<T>(NpgsqlUnitOfWork unit, string sql)
    {
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private sealed record ReadProbe;

    /// <summary>One captured invocation, its context and its witness, built exactly as the source would.</summary>
    private sealed class CapturedOwner : IDisposable
    {
        private readonly CandidateSource _candidates;
        private readonly InvocationSource _invocations = new();
        private readonly WitnessSource _witnesses = new();

        public CapturedOwner(OwnDataRequestPurpose purpose, DataRequestScopeGroup group, bool http = false)
        {
            var actor = UserId.From(PersonA);
            Context = LearnStack.SharedKernel.Tenancy.TenantContextFactory.CreateForOwnDataRequestScope(group.TenantId, null, actor, group, null).Value!;
            _candidates = new CandidateSource(Context);
            var policy = LearnStack.SharedKernel.Permissions.OperationPolicy.ForOwnDataRequestEntry<ReadProbe>("identity.data_request.read");
            var invocation = _invocations.Open(new ReadProbe(), policy, Context, _candidates.Create(),
                http ? LearnStack.SharedKernel.Permissions.OperationEntryKind.Http : LearnStack.SharedKernel.Permissions.OperationEntryKind.Application);
            Witness = _witnesses.Capture(invocation, actor, group.TenantId, purpose, group);
        }

        public LearnStack.SharedKernel.Tenancy.TenantContext Context { get; }
        public OwnDataRequestScopeWitness Witness { get; }
        public void Close() => _witnesses.Dispose();
        public void Dispose() { _witnesses.Dispose(); _candidates.Dispose(); }
    }

    private sealed class CandidateSource(LearnStack.SharedKernel.Tenancy.TenantContext context)
        : LearnStack.SharedKernel.Permissions.AuthorizationCandidate.MintingSource
    {
        internal LearnStack.SharedKernel.Permissions.AuthorizationCandidate Create() =>
            ForCandidate(LearnStack.SharedKernel.Permissions.AuthorizationSourceKind.Http, context.UserId!.Value, context.TenantId, context.OrganizationId);
    }

    private sealed class InvocationSource : LearnStack.SharedKernel.Permissions.OperationInvocation.MintingSource
    {
        private LearnStack.SharedKernel.Permissions.OperationInvocation? _current;
        internal LearnStack.SharedKernel.Permissions.OperationInvocation Open(object operation,
            LearnStack.SharedKernel.Permissions.OperationPolicy policy, LearnStack.SharedKernel.Tenancy.TenantContext context,
            LearnStack.SharedKernel.Permissions.AuthorizationCandidate candidate, LearnStack.SharedKernel.Permissions.OperationEntryKind kind) =>
            _current = ForInvocation(operation, policy, context, candidate, null, CancellationToken.None, kind);
        protected override bool IsCurrent(LearnStack.SharedKernel.Permissions.OperationInvocation invocation) => ReferenceEquals(_current, invocation);
    }

    private sealed class WitnessSource : OwnDataRequestScopeWitness.MintingSource
    {
        private static readonly GlobalIdentitySubject Subject = new(Authority.AuthorityId, Authority.Issuer,
            "fixture-subject-" + PersonA.ToString("N"));
        private readonly object _evidence = new();
        private LearnStack.SharedKernel.Permissions.OperationInvocation? _current;

        internal OwnDataRequestScopeWitness Capture(LearnStack.SharedKernel.Permissions.OperationInvocation invocation, UserId actor,
            TenantId tenant, OwnDataRequestPurpose purpose, DataRequestScopeGroup group)
        {
            _current = invocation;
            return Capture(_evidence, invocation, Subject, actor, tenant, null, "data-request-fixture.invalid", purpose, group, null);
        }

        protected override bool IsEvidenceCurrent(object evidence, LearnStack.SharedKernel.Permissions.OperationInvocation invocation) =>
            ReferenceEquals(evidence, _evidence) && ReferenceEquals(invocation, _current);
    }

    private static Task<(Guid Tenant, Guid? Organization)[]> ListAsync(string connection, Guid actor, Guid hostTenant, Guid? hostOrganization) =>
        CallAsync(connection, actor, hostTenant, hostOrganization, "list", null, null, 20);

    internal static async Task<(Guid Tenant, Guid? Organization)[]> CallAsync(string connectionString, Guid? actor, Guid? hostTenant,
        Guid? hostOrganization, string? mode, Guid? organization, Guid? after, int? limit,
        string start = "SET TRANSACTION READ ONLY", string? setup = null)
    {
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, start);
        if (setup is not null) await ExecuteAsync(connection, transaction, setup);
        await using var command = new NpgsqlCommand("""
            SELECT tenant_id, organization_id
            FROM public.fn_identity_own_data_request_scopes(@actor,@tenant,@host_organization,@mode,@organization,@after,@limit)
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("actor", NpgsqlDbType.Uuid) { Value = (object?)actor ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("tenant", NpgsqlDbType.Uuid) { Value = (object?)hostTenant ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("host_organization", NpgsqlDbType.Uuid) { Value = (object?)hostOrganization ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("mode", NpgsqlDbType.Text) { Value = (object?)mode ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("organization", NpgsqlDbType.Uuid) { Value = (object?)organization ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("after", NpgsqlDbType.Uuid) { Value = (object?)after ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = (object?)limit ?? DBNull.Value });
        var rows = new List<(Guid, Guid?)>();
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) rows.Add((reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1)));
        await transaction.RollbackAsync();
        return [.. rows];
    }
}
