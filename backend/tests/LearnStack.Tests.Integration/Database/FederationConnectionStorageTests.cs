using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.Tools.Seeder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// ADR-0099 Step 1 storage proof for <c>federation_connections</c>, connected as <c>learnstack_app</c>
/// against the nonempty seeded schema. Every case rolls back its transaction.
/// </summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class FederationConnectionStorageTests(SchemaFixture schema)
{
    private static readonly DateTimeOffset At = DateTimeOffset.UtcNow.AddMinutes(-30);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Each_tenant_reads_only_its_own_connections(bool tenantA)
    {
        var (own, foreign) = tenantA ? (SchemaFixture.TenantA, SchemaFixture.TenantB) : (SchemaFixture.TenantB, SchemaFixture.TenantA);
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, own);
        var visible = await ReadIdsAsync(app, transaction, "SELECT id FROM public.federation_connections ORDER BY id");
        visible.Should().BeEquivalentTo(tenantA
            ? [FederationConnectionSchemaSeed.TombstoneA, FederationConnectionSchemaSeed.LiveA]
            : new[] { FederationConnectionSchemaSeed.LiveB });
        (await ScalarAsync<long>(app, transaction, "SELECT count(*) FROM public.federation_connections WHERE tenant_id=@foreign",
            ("foreign", foreign))).Should().Be(0, "the foreign tenant's seeded rows exist but are invisible");
        await using (var clear = new NpgsqlCommand("SELECT set_config('app.tenant_id','',true)", app, transaction))
            await clear.ExecuteNonQueryAsync();
        (await ScalarAsync<long>(app, transaction, "SELECT count(*) FROM public.federation_connections")).Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Each_tenant_cannot_insert_or_update_the_other_tenants_connection(bool tenantA)
    {
        var (own, foreign, foreignRow) = tenantA
            ? (SchemaFixture.TenantA, SchemaFixture.TenantB, FederationConnectionSchemaSeed.LiveB)
            : (SchemaFixture.TenantB, SchemaFixture.TenantA, FederationConnectionSchemaSeed.LiveA);
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await FederationConnectionSchemaSeed.FenceAsync(app, transaction, own);
        await LockAsync(app, transaction, foreign, null, false);
        var insert = async () => await FederationConnectionSchemaSeed.InsertAsync(app, transaction, Guid.CreateVersion7(), foreign, AdminA, At);
        // The guard's tenant fence binds NEW.tenant_id to app.tenant_id before RLS WITH CHECK is reached.
        (await insert.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().BeOneOf("P0301", "42501");
        await transaction.RollbackAsync();

        await using var second = await app.BeginTransactionAsync();
        await FederationConnectionSchemaSeed.FenceAsync(app, second, own);
        (await ExecuteCountAsync(app, second, "UPDATE public.federation_connections SET display_name='Hijacked',row_version=row_version+1,updated_at=clock_timestamp(),updated_by=created_by WHERE id=@id",
            ("id", foreignRow))).Should().Be(0, "USING hides the foreign row from UPDATE");
    }

    [Fact]
    public async Task The_application_role_holds_exactly_select_insert_update_and_RLS_is_enabled_and_forced()
    {
        await using var app = await OpenAppAsync();
        var grants = await ReadStringsAsync(app, """
            SELECT privilege_type FROM information_schema.role_table_grants
            WHERE table_schema='public' AND table_name='federation_connections' AND grantee='learnstack_app' ORDER BY 1
            """);
        grants.Should().Equal("INSERT", "SELECT", "UPDATE");
        (await ScalarAsync<string>(app, null, """
            SELECT relrowsecurity::text||':'||relforcerowsecurity::text FROM pg_class WHERE oid='public.federation_connections'::regclass
            """)).Should().Be("true:true");
        (await ScalarAsync<long>(app, null, """
            SELECT count(*) FROM pg_policies WHERE schemaname='public' AND tablename='federation_connections'
            """)).Should().Be(1, "a tenant-only table carries exactly one permissive isolation policy");
        (await ReadStringsAsync(app, """
            SELECT conname FROM pg_constraint WHERE conrelid='public.federation_connections'::regclass AND contype='c' ORDER BY conname
            """)).Should().Equal("ck_federation_connections_alias", "ck_federation_connections_client", "ck_federation_connections_digests",
            "ck_federation_connections_display_name", "ck_federation_connections_identifiers", "ck_federation_connections_issuer",
            "ck_federation_connections_pending", "ck_federation_connections_protocol", "ck_federation_connections_revision",
            "ck_federation_connections_source_kind", "ck_federation_connections_state", "ck_federation_connections_tenant",
            "ck_federation_connections_test", "ck_federation_connections_tombstone");
        await using var transaction = await app.BeginTransactionAsync();
        await FederationConnectionSchemaSeed.FenceAsync(app, transaction, SchemaFixture.TenantA);
        var delete = async () => await ExecuteAsync(app, transaction, "DELETE FROM public.federation_connections WHERE id=@id",
            ("id", FederationConnectionSchemaSeed.LiveA));
        (await delete.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501", "no DELETE grant exists");
    }

    [Fact]
    public async Task A_write_without_the_Identity_tenant_fence_is_refused()
    {
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantB);
        var update = async () => await ExecuteAsync(app, transaction,
            "UPDATE public.federation_connections SET row_version=row_version+1 WHERE id=@id", ("id", FederationConnectionSchemaSeed.LiveB));
        (await update.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("P0301");
    }

    [Fact]
    public async Task One_live_connection_per_tenant_and_a_tombstone_frees_the_slot_but_keeps_its_alias()
    {
        await using var app = await OpenAppAsync();
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await FederationConnectionSchemaSeed.FenceAsync(app, transaction, SchemaFixture.TenantB);
            var duplicate = async () => await FederationConnectionSchemaSeed.InsertAsync(app, transaction, Guid.CreateVersion7(),
                SchemaFixture.TenantB, AdminB, At);
            var refused = (await duplicate.Should().ThrowAsync<PostgresException>()).Which;
            refused.SqlState.Should().Be("23505");
            refused.ConstraintName.Should().Be("ux_federation_connections_tenant_live");
        }
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await FederationConnectionSchemaSeed.FenceAsync(app, transaction, SchemaFixture.TenantB);
            await FederationConnectionSchemaSeed.TombstoneAsync(app, transaction, FederationConnectionSchemaSeed.LiveB, AdminB, DateTimeOffset.UtcNow);
            var successor = Guid.CreateVersion7();
            await FederationConnectionSchemaSeed.InsertAsync(app, transaction, successor, SchemaFixture.TenantB, AdminB, DateTimeOffset.UtcNow);
            var rows = await ReadStringsAsync(app, """
                SELECT id::text||':'||state||':'||alias||':'||(deleted_at IS NOT NULL)::text FROM public.federation_connections ORDER BY created_at, id
                """, transaction);
            rows.Should().BeEquivalentTo(
            [
                FederationConnectionSchemaSeed.LiveB.ToString("D") + ":Deleted:" + FederationConnectionSchemaSeed.Alias(FederationConnectionSchemaSeed.LiveB) + ":true",
                successor.ToString("D") + ":Draft:" + FederationConnectionSchemaSeed.Alias(successor) + ":false",
            ]);
            await transaction.RollbackAsync();
        }
    }

    public static TheoryData<string, string> CheckViolations => new()
    {
        { "protocol", "ck_federation_connections_protocol" },
        { "alias", "ck_federation_connections_alias" },
        { "display_name_untrimmed", "ck_federation_connections_display_name" },
        { "display_name_control", "ck_federation_connections_display_name" },
        { "saml_client", "ck_federation_connections_client" },
        { "oidc_without_client", "ck_federation_connections_client" },
        { "digest", "ck_federation_connections_digests" },
        { "issuer", "ck_federation_connections_issuer" },
        { "identifier_version", "ck_federation_connections_identifiers" },
        { "source_kind", "ck_federation_connections_source_kind" },
    };

    [Theory]
    [MemberData(nameof(CheckViolations))]
    public async Task Insert_check_constraints_refuse_malformed_rows(string variant, string constraint)
    {
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await FederationConnectionSchemaSeed.FenceAsync(app, transaction, SchemaFixture.TenantB);
        // Free tenant B's slot so only the constraint under test can refuse.
        await FederationConnectionSchemaSeed.TombstoneAsync(app, transaction, FederationConnectionSchemaSeed.LiveB, AdminB, DateTimeOffset.UtcNow.AddMinutes(-20));
        var id = variant == "identifier_version" ? Guid.NewGuid() : Guid.CreateVersion7();
        Func<Task> insert = variant switch
        {
            "protocol" => () => FederationConnectionSchemaSeed.InsertAsync(app, transaction, id, SchemaFixture.TenantB, AdminB, At, protocol: "Ldap"),
            "alias" => () => FederationConnectionSchemaSeed.InsertAsync(app, transaction, id, SchemaFixture.TenantB, AdminB, At, alias: "lsf-" + new string('0', 32)),
            "display_name_untrimmed" => () => FederationConnectionSchemaSeed.InsertAsync(app, transaction, id, SchemaFixture.TenantB, AdminB, At, displayName: " Seed "),
            "display_name_control" => () => FederationConnectionSchemaSeed.InsertAsync(app, transaction, id, SchemaFixture.TenantB, AdminB, At, displayName: "Bad\nName"),
            "saml_client" => () => FederationConnectionSchemaSeed.InsertAsync(app, transaction, id, SchemaFixture.TenantB, AdminB, At, protocol: "Saml"),
            "oidc_without_client" => () => FederationConnectionSchemaSeed.InsertAsync(app, transaction, id, SchemaFixture.TenantB, AdminB, At, clientId: null),
            "identifier_version" => () => FederationConnectionSchemaSeed.InsertAsync(app, transaction, id, SchemaFixture.TenantB, AdminB, At),
            _ => ShapeInsertAsync,
        };
        var refused = (await insert.Should().ThrowAsync<PostgresException>()).Which;
        refused.SqlState.Should().Be("23514");
        refused.ConstraintName.Should().Be(constraint);

        Task ShapeInsertAsync()
        {
            var (digest, issuer, source) = variant switch
            {
                "digest" => ("ABC", "https://idp.example.invalid/", "Manual"),
                "issuer" => (FederationConnectionSchemaSeed.Digest('a'), "has space", "Manual"),
                _ => (FederationConnectionSchemaSeed.Digest('a'), "https://idp.example.invalid/", "Paste"),
            };
            return ExecuteAsync(app, transaction, """
                INSERT INTO public.federation_connections(id,tenant_id,protocol,alias,display_name,issuer_or_entity_id,state,
                    source_kind,client_id,configuration_revision,configuration_digest,desired_digest,
                    pending_operation_kind,pending_operation_id,pending_actor,pending_started_at,pending_quiet_until,
                    pending_desired_digest,created_at,created_by,row_version)
                VALUES(@id,@tenant,'Oidc','lsf-'||replace(@id::text,'-',''),'Shape',@issuer,'Draft',@source,'client',1,@digest,@desired,
                    'Configure',uuidv7(),@actor,@at,@at+interval '90 seconds',@desired,@at,@actor,0)
                """, ("id", id), ("tenant", SchemaFixture.TenantB), ("issuer", issuer), ("source", source), ("digest", digest),
                ("desired", FederationConnectionSchemaSeed.Digest('a')), ("actor", AdminB), ("at", At));
        }
    }

    [Fact]
    public async Task Display_name_over_eighty_characters_is_refused_by_the_column_bound()
    {
        await using var app = await OpenAppAsync();
        foreach (var (length, refused) in new[] { (81, true), (80, false) })
        {
            await using var transaction = await app.BeginTransactionAsync();
            await FederationConnectionSchemaSeed.FenceAsync(app, transaction, SchemaFixture.TenantB);
            var sql = $"UPDATE public.federation_connections SET display_name=repeat('n',{length}),row_version=row_version+1,updated_at=clock_timestamp(),updated_by=created_by WHERE id=@id";
            if (refused)
            {
                var act = async () => await ExecuteAsync(app, transaction, sql, ("id", FederationConnectionSchemaSeed.LiveB));
                (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("22001");
            }
            else (await ExecuteCountAsync(app, transaction, sql, ("id", FederationConnectionSchemaSeed.LiveB))).Should().Be(1);
            await transaction.RollbackAsync();
        }
    }

    [Fact]
    public async Task Pending_operation_fields_are_all_null_or_all_set_and_the_quiet_window_is_ninety_seconds()
    {
        foreach (var update in new[]
                 {
                     "pending_operation_id=NULL",
                     "pending_actor=NULL",
                     "pending_quiet_until=pending_started_at+interval '30 seconds'",
                     "pending_operation_kind='Resume'",
                 })
        {
            await using var app = await OpenAppAsync();
            await using var transaction = await app.BeginTransactionAsync();
            await FederationConnectionSchemaSeed.FenceAsync(app, transaction, SchemaFixture.TenantB);
            var act = async () => await ExecuteAsync(app, transaction,
                $"UPDATE public.federation_connections SET {update},row_version=row_version+1,updated_at=clock_timestamp(),updated_by=created_by WHERE id=@id",
                ("id", FederationConnectionSchemaSeed.LiveB));
            var refused = (await act.Should().ThrowAsync<PostgresException>()).Which;
            refused.SqlState.Should().Be("23514", update);
            refused.ConstraintName.Should().Be("ck_federation_connections_pending", update);
        }
    }

    [Theory]
    [InlineData("test_attempt_id=uuidv7(),test_actor=created_by,test_revision=1,test_expires_at=clock_timestamp()+interval '10 minutes',test_result='Pending'")]
    [InlineData("test_result='Pending'")]
    [InlineData("deleted_at=clock_timestamp(),deleted_by=created_by")]
    public async Task State_test_revision_and_tombstone_constraints_refuse_inconsistent_rows(string update)
    {
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await FederationConnectionSchemaSeed.FenceAsync(app, transaction, SchemaFixture.TenantB);
        var act = async () => await ExecuteAsync(app, transaction,
            $"UPDATE public.federation_connections SET {update},row_version=row_version+1,updated_at=clock_timestamp(),updated_by=created_by WHERE id=@id",
            ("id", FederationConnectionSchemaSeed.LiveB));
        var refused = (await act.Should().ThrowAsync<PostgresException>()).Which;
        refused.SqlState.Should().Be("23514");
        refused.ConstraintName.Should().BeOneOf("ck_federation_connections_test", "ck_federation_connections_revision",
            "ck_federation_connections_tombstone");
    }

    [Theory]
    [InlineData("state='Active'", "Federation connection transition refused.")]
    [InlineData("state='TestPassed'", "Federation connection transition refused.")]
    [InlineData("state='Disabled'", "Federation connection transition refused.")]
    [InlineData("alias='lsf-'||replace(uuidv7()::text,'-','')", "Federation connection immutable state refused.")]
    [InlineData("issuer_or_entity_id='https://other.example.invalid/'", "Federation connection immutable state refused.")]
    [InlineData("protocol='Saml',client_id=NULL", "Federation connection immutable state refused.")]
    [InlineData("pending_operation_id=uuidv7()", "Federation provider write is already pending.")]
    [InlineData("configuration_revision=configuration_revision+5,state='Draft'", "Federation configuration revision refused.")]
    public async Task Mutation_guard_refuses_illegal_transitions_and_immutable_changes(string update, string message)
    {
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await FederationConnectionSchemaSeed.FenceAsync(app, transaction, SchemaFixture.TenantB);
        var act = async () => await ExecuteAsync(app, transaction,
            $"UPDATE public.federation_connections SET {update},row_version=row_version+1,updated_at=clock_timestamp(),updated_by=created_by WHERE id=@id",
            ("id", FederationConnectionSchemaSeed.LiveB));
        var refused = (await act.Should().ThrowAsync<PostgresException>()).Which;
        refused.SqlState.Should().Be("23514");
        refused.MessageText.Should().Be(message);
    }

    [Fact]
    public async Task The_tombstone_is_terminal_even_for_an_otherwise_legal_update()
    {
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await FederationConnectionSchemaSeed.FenceAsync(app, transaction, SchemaFixture.TenantA);
        var act = async () => await ExecuteAsync(app, transaction,
            "UPDATE public.federation_connections SET display_name='Revived',row_version=row_version+1,updated_at=clock_timestamp(),updated_by=created_by WHERE id=@id",
            ("id", FederationConnectionSchemaSeed.TombstoneA));
        var refused = (await act.Should().ThrowAsync<PostgresException>()).Which;
        refused.SqlState.Should().Be("23514");
        refused.MessageText.Should().Be("Federation connection tombstone is terminal.");
    }

    [Fact]
    public async Task Insert_must_start_as_a_Draft_with_its_pending_Configure()
    {
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await FederationConnectionSchemaSeed.FenceAsync(app, transaction, SchemaFixture.TenantB);
        await FederationConnectionSchemaSeed.TombstoneAsync(app, transaction, FederationConnectionSchemaSeed.LiveB, AdminB, DateTimeOffset.UtcNow.AddMinutes(-20));
        var id = Guid.CreateVersion7();
        var act = async () => await ExecuteAsync(app, transaction, """
            INSERT INTO public.federation_connections(id,tenant_id,protocol,alias,display_name,issuer_or_entity_id,state,
                source_kind,client_id,configuration_revision,configuration_digest,desired_digest,created_at,created_by,row_version)
            VALUES(@id,@tenant,'Oidc','lsf-'||replace(@id::text,'-',''),'Direct','https://idp.example.invalid/','Draft',
                'Manual','client',1,@digest,@digest,clock_timestamp(),@actor,0)
            """, ("id", id), ("tenant", SchemaFixture.TenantB), ("digest", FederationConnectionSchemaSeed.Digest('a')), ("actor", AdminB));
        var refused = (await act.Should().ThrowAsync<PostgresException>()).Which;
        refused.SqlState.Should().Be("23514");
        refused.MessageText.Should().Be("Federation connection initial state refused.");
    }

    [Fact]
    public async Task The_legal_lifecycle_round_trips_through_the_guard_and_constraints()
    {
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await FederationConnectionSchemaSeed.FenceAsync(app, transaction, SchemaFixture.TenantB);
        var id = FederationConnectionSchemaSeed.LiveB;
        var attempt = Guid.CreateVersion7();
        string[] steps =
        [
            // Settle Configure, begin and apply the test start.
            "pending_operation_kind=NULL,pending_operation_id=NULL,pending_actor=NULL,pending_started_at=NULL,pending_quiet_until=NULL,pending_desired_digest=NULL,last_provider_digest=desired_digest",
            "pending_operation_kind='TestStart',pending_operation_id=uuidv7(),pending_actor=created_by,pending_started_at=now(),pending_quiet_until=now()+interval '90 seconds',pending_desired_digest=desired_digest",
            "state='TestOpen',pending_operation_kind=NULL,pending_operation_id=NULL,pending_actor=NULL,pending_started_at=NULL,pending_quiet_until=NULL,pending_desired_digest=NULL,test_attempt_id=@attempt,test_actor=created_by,test_revision=configuration_revision,test_expires_at=now()+interval '10 minutes',test_result='Pending'",
            "state='TestPassed',test_result='Passed',test_passed_at=now(),test_expires_at=now()+interval '10 minutes'",
            "state='Active',test_attempt_id=NULL,test_actor=NULL,test_revision=NULL,test_expires_at=NULL,test_result=NULL,test_passed_at=NULL",
            "state='Disabled',pending_operation_kind='Disable',pending_operation_id=uuidv7(),pending_actor=created_by,pending_started_at=now(),pending_quiet_until=now()+interval '90 seconds',pending_desired_digest=desired_digest",
            "pending_operation_kind=NULL,pending_operation_id=NULL,pending_actor=NULL,pending_started_at=NULL,pending_quiet_until=NULL,pending_desired_digest=NULL",
            "state='Draft'",
            "configuration_revision=configuration_revision+1,pending_operation_kind='RotateSecret',pending_operation_id=uuidv7(),pending_actor=created_by,pending_started_at=now(),pending_quiet_until=now()+interval '90 seconds',pending_desired_digest=desired_digest",
        ];
        foreach (var step in steps)
            (await ExecuteCountAsync(app, transaction,
                $"UPDATE public.federation_connections SET {step},row_version=row_version+1,updated_at=clock_timestamp(),updated_by=created_by WHERE id=@id",
                ("id", id), ("attempt", attempt))).Should().Be(1, step);
        (await ScalarAsync<string>(app, transaction,
            "SELECT state||':'||configuration_revision||':'||pending_operation_kind FROM public.federation_connections WHERE id=@id", ("id", id)))
            .Should().Be("Draft:2:RotateSecret");
    }

    [Fact]
    public async Task The_aggregate_round_trips_through_EF_as_the_application_role_and_settles_to_a_tombstone()
    {
        await using var source = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        await using var unit = new NpgsqlUnitOfWork(source, NullLogger<NpgsqlUnitOfWork>.Instance);
        await using var frame = await unit.BeginTransactionAsync();
        var tenant = TenantId.From(SchemaFixture.TenantB);
        await unit.SetProvisioningTenantContextAsync(tenant);
        await new IdentityMutationLocks(unit).AcquireAsync(new([], tenant, IdentityTenantLockMode.Shared));
        await using var db = new IdentityDbContext(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(unit.Connection).Options,
            new StaticTenantContextAccessor(new SeedTenantContext(tenant, null)), EmptyIdentityScope.Instance);
        await db.Database.UseTransactionAsync(unit.Transaction);
        var actor = UserId.From(AdminB);
        var now = DateTimeOffset.UtcNow;
        var live = await db.FederationConnections.SingleAsync();
        live.Id.Value.Should().Be(FederationConnectionSchemaSeed.LiveB, "the tenant EF filter and RLS expose exactly the seeded live row");
        live.Alias.Should().Be(FederationConnectionSchemaSeed.Alias(FederationConnectionSchemaSeed.LiveB));
        live.Complete(live.PendingOperationId!.Value, FederationOperationOutcome.Applied, live.PendingDesiredDigest, null, now, actor).Value.Should().BeTrue();
        // Each settled step is its own save: the guard admits exactly one row version per UPDATE.
        await db.SaveChangesAsync();
        var delete = FederationOperationId.From(Guid.CreateVersion7());
        live.BeginDelete(FederationConnectionSchemaSeed.Digest('d'), delete, now.AddSeconds(1), actor).IsSuccess.Should().BeTrue();
        await db.SaveChangesAsync();
        live.Complete(delete, FederationOperationOutcome.Applied, FederationConnectionSchemaSeed.Digest('d'), null, now.AddSeconds(2), actor).Value.Should().BeTrue();
        await db.SaveChangesAsync();
        var id = FederationConnectionId.From(Guid.CreateVersion7());
        var created = FederationConnection.Create(id, tenant, FederationProtocol.Saml, "  Okul SSO ",
            new FederationConfiguration(FederationSourceKind.MetadataUrl, "urn:example:idp", null,
                FederationConnectionSchemaSeed.Digest('a'), FederationConnectionSchemaSeed.Digest('b')),
            FederationOperationId.From(Guid.CreateVersion7()), now.AddSeconds(3), actor);
        created.IsSuccess.Should().BeTrue();
        db.FederationConnections.Add(created.Value!);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var reloaded = await db.FederationConnections.IgnoreQueryFilters().Where(row => row.TenantId == tenant)
            .OrderBy(row => row.CreatedAt).ToListAsync();
        reloaded.Select(row => (row.Id.Value, row.State, row.IsDeleted, row.Version)).Should().Equal(
            (FederationConnectionSchemaSeed.LiveB, FederationConnectionState.Deleted, true, 3L),
            (id.Value, FederationConnectionState.Draft, false, 0L));
        var fresh = reloaded[1];
        fresh.DisplayName.Should().Be("Okul SSO");
        fresh.Alias.Should().Be(FederationConnection.DeriveAlias(id));
        fresh.PendingOperationKind.Should().Be(FederationOperationKind.Configure);
        fresh.PendingQuietUntil.Should().Be(fresh.PendingStartedAt + FederationConnection.QuietWindow);
        fresh.ClientId.Should().BeNull();
        await frame.FailAsync();
    }

    private async Task<NpgsqlConnection> OpenAppAsync()
    {
        var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(schema.Postgres.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        return app;
    }

    private static async Task<int> ExecuteCountAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql,
        params (string Name, object? Value)[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<Guid>> ReadIdsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await using var rows = await command.ExecuteReaderAsync();
        var ids = new List<Guid>();
        while (await rows.ReadAsync()) ids.Add(rows.GetGuid(0));
        return ids;
    }

    private static async Task<List<string>> ReadStringsAsync(NpgsqlConnection connection, string sql, NpgsqlTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await using var rows = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await rows.ReadAsync()) values.Add(rows.GetString(0));
        return values;
    }
}
