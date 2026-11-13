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
        // The BEFORE guard's tenant fence binds NEW.tenant_id to app.tenant_id, so it refuses before RLS is
        // reached. WITH CHECK itself is proven with the guard isolated, by
        // IdentityMembershipTests.Contextual_WITH_CHECK_rejects_foreign_writes_and_its_dirty_twin_admits_them.
        var fenced = (await insert.Should().ThrowAsync<PostgresException>()).Which;
        fenced.SqlState.Should().Be("P0301");
        fenced.ConstraintName.Should().Be("identity_tenant_fence");
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
        { "display_name_c1_control", "ck_federation_connections_display_name" },
        { "display_name_zero_width", "ck_federation_connections_display_name" },
        { "display_name_bidi_override", "ck_federation_connections_display_name" },
        { "display_name_bidi_isolate", "ck_federation_connections_display_name" },
        { "display_name_byte_order_mark", "ck_federation_connections_display_name" },
        { "display_name_line_separator", "ck_federation_connections_display_name" },
        { "display_name_no_break_space", "ck_federation_connections_display_name" },
        { "display_name_private_use", "ck_federation_connections_display_name" },
        { "display_name_not_nfc", "ck_federation_connections_display_name" },
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
            "display_name_c1_control" => () => FederationConnectionSchemaSeed.InsertAsync(app, transaction, id, SchemaFixture.TenantB, AdminB, At, displayName: "Bad\u0085Name"),
            "display_name_zero_width" => () => FederationConnectionSchemaSeed.InsertAsync(app, transaction, id, SchemaFixture.TenantB, AdminB, At, displayName: "Bad\u200bName"),
            "display_name_bidi_override" => () => FederationConnectionSchemaSeed.InsertAsync(app, transaction, id, SchemaFixture.TenantB, AdminB, At, displayName: "\u202eemaN daB"),
            "display_name_bidi_isolate" => () => FederationConnectionSchemaSeed.InsertAsync(app, transaction, id, SchemaFixture.TenantB, AdminB, At, displayName: "Bad\u2066Name\u2069"),
            "display_name_byte_order_mark" => () => FederationConnectionSchemaSeed.InsertAsync(app, transaction, id, SchemaFixture.TenantB, AdminB, At, displayName: "Bad\ufeffName"),
            "display_name_line_separator" => () => FederationConnectionSchemaSeed.InsertAsync(app, transaction, id, SchemaFixture.TenantB, AdminB, At, displayName: "Bad\u2028Name"),
            "display_name_no_break_space" => () => FederationConnectionSchemaSeed.InsertAsync(app, transaction, id, SchemaFixture.TenantB, AdminB, At, displayName: "Bad\u00a0Name"),
            "display_name_private_use" => () => FederationConnectionSchemaSeed.InsertAsync(app, transaction, id, SchemaFixture.TenantB, AdminB, At, displayName: "Bad\ue000Name"),
            "display_name_not_nfc" => () => FederationConnectionSchemaSeed.InsertAsync(app, transaction, id, SchemaFixture.TenantB, AdminB, At, displayName: "Cafe\u0301"),
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

    [Theory]
    [InlineData("Pending", "pending_operation_id=NULL")]
    [InlineData("Draft", "pending_operation_kind='Rename',pending_operation_id=uuidv7(),pending_actor=created_by,pending_started_at=@at,pending_quiet_until=@at+interval '30 seconds',pending_desired_digest=desired_digest")]
    public async Task Pending_operation_fields_are_all_null_or_all_set_and_the_quiet_window_is_ninety_seconds(string from, string update)
    {
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await FederationConnectionSchemaSeed.FenceAsync(app, transaction, SchemaFixture.TenantB);
        var walk = new Walk(app, transaction, FederationConnectionSchemaSeed.LiveB);
        await walk.ToAsync(from);
        var refused = (await walk.Awaiting(step => step.StepAsync(update)).Should().ThrowAsync<PostgresException>()).Which;
        refused.SqlState.Should().Be("23514", update);
        refused.ConstraintName.Should().Be("ck_federation_connections_pending", update);
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
        var walk = new Walk(app, transaction, FederationConnectionSchemaSeed.LiveB);
        await walk.ToAsync("Disabled");
        foreach (var step in new[]
                 {
                     "state='Draft'",
                     "configuration_revision=configuration_revision+1," + Walk.Begin("RotateSecret"),
                     Walk.Settle,
                     Walk.Begin("Rename"),
                     Walk.Settle,
                     Walk.Begin("Delete"),
                     "state='Deleted'," + Walk.Settle + ",deleted_at=@at,deleted_by=created_by",
                 })
            (await walk.StepAsync(step)).Should().Be(1, step);
        (await ScalarAsync<string>(app, transaction,
            "SELECT state||':'||configuration_revision||':'||(deleted_at IS NOT NULL)::text FROM public.federation_connections WHERE id=@id",
            ("id", FederationConnectionSchemaSeed.LiveB))).Should().Be("Deleted:2:true");
    }

    private const string TransitionRefused = "Federation connection transition refused.";
    private const string RevisionRefused = "Federation configuration revision refused.";
    private const string PendingRefused = "Federation pending operation refused.";
    private const string TestRefused = "Federation test attempt refused.";

    /// <summary>
    /// Fail-closed refusals: each starts from a row whose nullable pending or test columns are NULL where that
    /// matters, the path on which a NULL comparison used to let the guard admit the write.
    /// </summary>
    public static TheoryData<string, string, string> FailClosedRefusals => new()
    {
        // Lifecycle (no provider write behind the transition).
        { "Draft", "state='TestOpen',test_attempt_id=@attempt,test_actor=created_by,test_revision=configuration_revision,test_expires_at=@at+interval '10 minutes',test_result='Pending'", TransitionRefused },
        { "Draft", "state='Deleted',deleted_at=@at,deleted_by=created_by", TransitionRefused },
        { "Active", "state='Deleted',deleted_at=@at,deleted_by=created_by", TransitionRefused },
        { "Active", "state='Disabled'", TransitionRefused },
        { "TestOpen", "state='Active'," + Walk.ClearTest, TransitionRefused },
        { "Disabled", "state='Active'", TransitionRefused },
        { "TestPassedPending", "state='Active'," + Walk.ClearTest, TransitionRefused },
        // Revision.
        { "Draft", "configuration_revision=configuration_revision+1", RevisionRefused },
        { "Active", "configuration_revision=configuration_revision+1,state='Draft'", RevisionRefused },
        { "Draft", "configuration_revision=configuration_revision+1," + Walk.Begin("Rename"), RevisionRefused },
        // The same pending operation is pinned.
        { "Pending", "pending_operation_kind='Rename'", PendingRefused },
        { "Pending", "pending_operation_kind='Resume'", PendingRefused },
        { "Pending", "pending_actor=NULL", PendingRefused },
        { "Pending", "pending_actor=uuidv7()", PendingRefused },
        { "Pending", "pending_started_at=pending_started_at+interval '1 second',pending_quiet_until=pending_quiet_until+interval '1 second'", PendingRefused },
        { "Pending", "pending_desired_digest=repeat('e',64)", PendingRefused },
        // A new pending operation starts now, by its actor, for the desired digest, from a state its kind admits.
        { "Draft", "pending_operation_kind='Rename',pending_operation_id=uuidv7(),pending_actor=created_by,pending_started_at=@at-interval '1 second',pending_quiet_until=@at+interval '89 seconds',pending_desired_digest=desired_digest", PendingRefused },
        { "Draft", Walk.Begin("Rename").Replace("pending_actor=created_by", "pending_actor=uuidv7()", StringComparison.Ordinal), PendingRefused },
        { "Draft", Walk.Begin("Rename").Replace("pending_desired_digest=desired_digest", "pending_desired_digest=repeat('e',64)", StringComparison.Ordinal), PendingRefused },
        { "Active", Walk.Begin("TestStart"), PendingRefused },
        { "Draft", Walk.Begin("Disable"), PendingRefused },
        { "Draft", "state='Disabled'," + Walk.Begin("Disable"), PendingRefused },
        { "Active", Walk.Begin("Delete"), PendingRefused },
        { "Active", Walk.Begin("Configure"), PendingRefused },
        // The test attempt is pinned, and its window is opened and closed only by its own transitions.
        { "TestStart", Walk.OpenTest.Replace("test_actor=created_by", "test_actor=uuidv7()", StringComparison.Ordinal), TestRefused },
        { "TestStart", Walk.OpenTest.Replace("@at+interval '10 minutes'", "@at+interval '1 hour'", StringComparison.Ordinal), TestRefused },
        { "TestOpen", "test_attempt_id=uuidv7()", TestRefused },
        { "TestOpen", "test_expires_at=test_expires_at+interval '1 hour'", TestRefused },
        { "TestOpen", Walk.Pass + ",test_attempt_id=uuidv7()", TestRefused },
        { "TestOpen", "state='TestPassed',test_result='Passed',test_passed_at=@at-interval '1 second',test_expires_at=@at+interval '599 seconds'", TestRefused },
        { "TestPassed", "test_actor=uuidv7()", TestRefused },
        { "TestPassed", "test_passed_at=test_passed_at+interval '1 second',test_expires_at=test_expires_at+interval '1 second'", TestRefused },
    };

    [Theory]
    [MemberData(nameof(FailClosedRefusals))]
    public async Task Mutation_guard_fails_closed_from_settled_and_pending_rows(string from, string update, string message)
    {
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await FederationConnectionSchemaSeed.FenceAsync(app, transaction, SchemaFixture.TenantB);
        var walk = new Walk(app, transaction, FederationConnectionSchemaSeed.LiveB);
        await walk.ToAsync(from);
        var refused = (await walk.Awaiting(step => step.StepAsync(update)).Should().ThrowAsync<PostgresException>()).Which;
        refused.SqlState.Should().Be("23514", update);
        refused.MessageText.Should().Be(message, update);
    }

    [Theory]
    [InlineData("TestOpen", Walk.Pass)]
    [InlineData("TestPassed", "state='Active'," + Walk.ClearTest)]
    public async Task A_pass_or_activation_lands_strictly_inside_its_window(string from, string transition)
    {
        foreach (var (offset, admitted) in new[] { (TimeSpan.FromMinutes(10), false), (TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1), true) })
        {
            await using var app = await OpenAppAsync();
            await using var transaction = await app.BeginTransactionAsync();
            await FederationConnectionSchemaSeed.FenceAsync(app, transaction, SchemaFixture.TenantB);
            var walk = new Walk(app, transaction, FederationConnectionSchemaSeed.LiveB);
            await walk.ToAsync(from);
            var at = walk.Current + offset;
            if (admitted) (await walk.StepAsync(transition, at)).Should().Be(1, "one second before expiry is inside the window");
            else
            {
                var refused = (await walk.Awaiting(step => step.StepAsync(transition, at)).Should().ThrowAsync<PostgresException>()).Which;
                refused.SqlState.Should().Be("23514");
                refused.MessageText.Should().Be(TestRefused, "the window is half-open: at expiry it has closed");
            }
        }
    }

    [Fact]
    public async Task Insert_directly_as_a_tombstone_is_refused()
    {
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await FederationConnectionSchemaSeed.FenceAsync(app, transaction, SchemaFixture.TenantB);
        await FederationConnectionSchemaSeed.TombstoneAsync(app, transaction, FederationConnectionSchemaSeed.LiveB, AdminB, DateTimeOffset.UtcNow.AddMinutes(-20));
        var id = Guid.CreateVersion7();
        var act = async () => await ExecuteAsync(app, transaction, """
            INSERT INTO public.federation_connections(id,tenant_id,protocol,alias,display_name,issuer_or_entity_id,state,
                source_kind,client_id,configuration_revision,configuration_digest,desired_digest,created_at,created_by,
                deleted_at,deleted_by,row_version)
            VALUES(@id,@tenant,'Oidc','lsf-'||replace(@id::text,'-',''),'Direct','https://idp.example.invalid/','Deleted',
                'Manual','client',1,@digest,@digest,@at,@actor,@at,@actor,0)
            """, ("id", id), ("tenant", SchemaFixture.TenantB), ("digest", FederationConnectionSchemaSeed.Digest('a')),
            ("actor", AdminB), ("at", At));
        var refused = (await act.Should().ThrowAsync<PostgresException>()).Which;
        refused.SqlState.Should().Be("23514");
        refused.MessageText.Should().Be("Federation connection initial state refused.");
    }

    /// <summary>
    /// Canonical rule <c>Federation_Issuer_Or_Entity_Id_Cannot_Change_In_Place</c> (Standard 21, ADR-0099
    /// § Architecture Tests): the aggregate refuses a changed issuer as a business rule, and the storage guard
    /// refuses it for every writer, alone or riding a legal Configure Begin, from a settled Draft and from Active.
    /// The same-issuer Begin is the positive control that keeps the refusals from being vacuous.
    /// </summary>
    [Fact]
    public async Task Federation_Issuer_Or_Entity_Id_Cannot_Change_In_Place()
    {
        foreach (var from in new[] { "Draft", "Active" })
        {
            foreach (var (update, admitted) in new[]
                     {
                         ("issuer_or_entity_id='https://other.example.invalid/'", false),
                         ("issuer_or_entity_id='https://other.example.invalid/',state='Draft',configuration_revision=configuration_revision+1," + Walk.Begin("Configure"), false),
                         ("state='Draft',configuration_revision=configuration_revision+1," + Walk.Begin("Configure"), true),
                     })
            {
                await using var app = await OpenAppAsync();
                await using var transaction = await app.BeginTransactionAsync();
                await FederationConnectionSchemaSeed.FenceAsync(app, transaction, SchemaFixture.TenantB);
                var walk = new Walk(app, transaction, FederationConnectionSchemaSeed.LiveB);
                await walk.ToAsync(from);
                if (admitted)
                {
                    (await walk.StepAsync(update)).Should().Be(1, "a same-issuer Configure Begin is legal from " + from);
                    continue;
                }
                var refused = (await walk.Awaiting(step => step.StepAsync(update)).Should().ThrowAsync<PostgresException>()).Which;
                refused.SqlState.Should().Be("23514", update);
                refused.MessageText.Should().Be("Federation connection immutable state refused.", update);
            }
        }

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
        live.Complete(live.PendingOperationId!.Value, FederationOperationOutcome.Applied, live.PendingDesiredDigest, null, now, actor).Value.Should().BeTrue();
        await db.SaveChangesAsync();
        var version = live.Version;
        var changed = live.BeginConfigure(FederationOperationKind.Configure,
            new FederationConfiguration(FederationSourceKind.Manual, "https://other.example.invalid/", "learnstack",
                FederationConnectionSchemaSeed.Digest('b'), FederationConnectionSchemaSeed.Digest('b')),
            FederationOperationId.From(Guid.CreateVersion7()), now.AddSeconds(1), actor);
        changed.IsFailure.Should().BeTrue("the aggregate refuses a changed issuer before storage is reached");
        changed.Error!.Message.Key.Should().Be("lockey_business_rule_violation");
        live.Version.Should().Be(version);
        db.ChangeTracker.HasChanges().Should().BeFalse();
        (await db.FederationConnections.AsNoTracking().Select(row => row.IssuerOrEntityId).SingleAsync())
            .Should().Be("https://idp.example.invalid/issuer");
        await frame.FailAsync();
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

    /// <summary>
    /// Drives the seeded tenant-B row through legal guarded updates, one second apart, as the application role.
    /// </summary>
    private sealed class Walk(NpgsqlConnection app, NpgsqlTransaction transaction, Guid id)
    {
        internal const string Settle =
            "pending_operation_kind=NULL,pending_operation_id=NULL,pending_actor=NULL,pending_started_at=NULL,pending_quiet_until=NULL,pending_desired_digest=NULL";
        internal const string ClearTest =
            "test_attempt_id=NULL,test_actor=NULL,test_revision=NULL,test_expires_at=NULL,test_result=NULL,test_passed_at=NULL";
        internal const string OpenTest = "state='TestOpen'," + Settle
            + ",test_attempt_id=@attempt,test_actor=created_by,test_revision=configuration_revision,test_expires_at=@at+interval '10 minutes',test_result='Pending'";
        internal const string Pass = "state='TestPassed',test_result='Passed',test_passed_at=@at,test_expires_at=@at+interval '10 minutes'";

        private readonly Guid _attempt = Guid.CreateVersion7();

        internal DateTimeOffset Current { get; private set; } = At;

        internal static string Begin(string kind) =>
            $"pending_operation_kind='{kind}',pending_operation_id=uuidv7(),pending_actor=created_by,pending_started_at=@at,"
            + "pending_quiet_until=@at+interval '90 seconds',pending_desired_digest=desired_digest";

        internal Task<int> StepAsync(string set) => StepAsync(set, Current.AddSeconds(1));

        internal async Task<int> StepAsync(string set, DateTimeOffset at)
        {
            await using var command = new NpgsqlCommand(
                $"UPDATE public.federation_connections SET {set},row_version=row_version+1,updated_at=@at,updated_by=created_by WHERE id=@id",
                app, transaction);
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("at", at);
            command.Parameters.AddWithValue("attempt", _attempt);
            var count = await command.ExecuteNonQueryAsync();
            Current = at;
            return count;
        }

        /// <summary>"Pending" is the seeded row with its unsettled Configure; every other name settles it first.</summary>
        internal async Task ToAsync(string state)
        {
            string[] path = state switch
            {
                "Pending" => [],
                "Draft" => [Settled],
                "TestStart" => [Settled, Begin("TestStart")],
                "TestOpen" => [Settled, Begin("TestStart"), OpenTest],
                "TestPassed" => [Settled, Begin("TestStart"), OpenTest, Pass],
                "TestPassedPending" => [Settled, Begin("TestStart"), OpenTest, Pass, Begin("Rename")],
                "Active" => [Settled, Begin("TestStart"), OpenTest, Pass, "state='Active'," + ClearTest],
                "Disabled" => [Settled, Begin("TestStart"), OpenTest, Pass, "state='Active'," + ClearTest,
                    "state='Disabled'," + Begin("Disable"), Settle],
                _ => throw new ArgumentOutOfRangeException(nameof(state)),
            };
            foreach (var step in path) (await StepAsync(step)).Should().Be(1, step);
        }

        private const string Settled = Settle + ",last_provider_digest=desired_digest";
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
