using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Audit.Infrastructure.Persistence;
using LearnStack.Modules.Customization.Infrastructure.Persistence;
using LearnStack.Modules.Education.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// Reversible chain prefixes return to an empty schema; current forward-only heads preserve their installed state.
/// </summary>
/// <remarks>
/// <para>
/// Database Standards § Migrations says reversal is expected for a non-destructive
/// migration. These chains create schema and tighten controls. Neither of the first two reversed
/// as shipped:
/// the tenancy <c>Down()</c> aborted on its first statement, because
/// <c>DROP FUNCTION fn_organization_id_immutable()</c> fails while the trigger on
/// <c>tenant_settings</c> depends on it, and would have aborted again on
/// <c>DROP TABLE organizations</c>, which three foreign keys reference.
/// <c>DropTable</c> emits a bare <c>DROP TABLE</c> with no <c>CASCADE</c>, so the
/// alphabetical order EF scaffolds is not a working order.
/// </para>
/// <para>
/// The point of the case is that it is measured rather than reasoned about. A
/// comment asserting the ordering is correct is exactly what shipped the broken
/// one.
/// </para>
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class MigrationRollbackTests : IClassFixture<MigrationRollbackFixture>
{
    private readonly MigrationRollbackFixture _fixture;

    public MigrationRollbackTests(MigrationRollbackFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Reversible_chain_prefixes_reverse_to_empty_and_current_heads_refuse_reversal()
    {
        await using var connection = await PostgresFixture.OpenAsync(
            _fixture.Postgres.MigrationConnectionString);

        // Applied state first, so a rollback that reversed nothing because nothing
        // was there cannot pass.
        (await CountAsync(connection, TablesQuery)).Should().Be(56L,
            "nine tenancy tables, twenty-three platform tables, four customization tables, "
            + "two audit tables, four Education tables, eight Identity tables including creation capacity, and six history tables");
        (await CountAsync(connection, "SELECT count(*) FROM pg_class WHERE relnamespace='public'::regnamespace AND relkind='r' AND relname='tenant_custom_field_defs'"))
            .Should().Be(0, "the historical Customization prefix predates retained definitions");
        (await CountAsync(connection, "SELECT count(*) FROM pg_class WHERE relnamespace='public'::regnamespace AND relkind='r' AND relname='membership_field_consents'"))
            .Should().Be(0, "the historical Tenancy prefix predates retained consent evidence");
        (await CountAsync(connection, FunctionQuery)).Should().Be(1L,
            "fn_organization_id_immutable backs the tenant_settings trigger");
        (await CountAsync(connection, AuditFunctionQuery)).Should().Be(2L,
            "fn_audit_log_append_only and fn_audit_log_no_truncate back the two "
            + "append-only triggers");
        (await CountAsync(connection, EducationFunctionQuery)).Should().Be(3L,
            "each Education parent relation has its own invoker scope check");
        (await CountAsync(connection, InboxFunctionQuery)).Should().Be(1L,
            "fn_inbox_messages_immutable backs the shared inbox append-only trigger");
        (await CountAsync(connection, IdentityFunctionQuery)).Should().Be(15L,
            "the reversible Identity prefix includes global/contextual functions and all three self-entry functions");

        (await CountAsync(connection, JobFunctionQuery)).Should().BeGreaterThan(0,
            "the new job guards must be installed before their reversal is tested");
        (await CountAsync(connection, VendorSequenceQuery)).Should().Be(9L);

        await _fixture.RollBackAsync();

        // The history tables survive: `database update 0` empties them, it does not
        // drop them. Everything the six chains created is gone.
        (await CountAsync(connection, TablesQuery)).Should().Be(6L);
        (await CountAsync(connection, FunctionQuery)).Should().Be(0L);

        // The audit triggers went with their table; the FUNCTIONS did not, and a
        // Down() that dropped only the tables would leave both behind. Re-applying Up
        // would then keep whichever body happened to be there, because CREATE FUNCTION
        // with a matching signature is not an error the migration surfaces.
        (await CountAsync(connection, AuditFunctionQuery)).Should().Be(0L);
        (await CountAsync(connection, EducationFunctionQuery)).Should().Be(0L);
        (await CountAsync(connection, InboxFunctionQuery)).Should().Be(0L);
        (await CountAsync(connection, IdentityFunctionQuery)).Should().Be(0L);
        (await CountAsync(connection, PolicyQuery)).Should().Be(0L);

        (await CountAsync(connection, JobFunctionQuery)).Should().Be(0L);
        (await CountAsync(connection, VendorSequenceQuery)).Should().Be(0L);

        // Reinstall the complete current chains, including forward-only retained-evidence heads.
        await MigrationChains.ApplyAllAsync(_fixture.Postgres.MigrationConnectionString);
        (await CountAsync(connection, TablesQuery)).Should().Be(65L);
        (await CountAsync(connection, "SELECT count(*) FROM pg_class WHERE relnamespace='public'::regnamespace AND relkind='r' AND relname IN ('tenant_custom_field_defs','membership_field_consents')"))
            .Should().Be(2, "both current custom-field tables were restored");
        await SchemaTableClasses.AssertCompleteAsync(connection);
        (await CountAsync(connection, FunctionQuery)).Should().Be(1L);
        (await CountAsync(connection, AuditFunctionQuery)).Should().Be(2L);
        (await CountAsync(connection, EducationFunctionQuery)).Should().Be(3L);
        (await CountAsync(connection, InboxFunctionQuery)).Should().Be(1L);
        (await CountAsync(connection, IdentityFunctionQuery)).Should().Be(22L,
            "current Identity also installs three provider definers and one target transition guard");
        await _fixture.AssertCurrentHeadsRefuseReversalAsync();
        await EducationSchemaSeed.SeedAsync(_fixture.Postgres.AppConnectionString);
        await using var app = await PostgresFixture.OpenAsync(_fixture.Postgres.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
        foreach (var table in EducationSchemaSeed.Tables)
        {
            (await SchemaQueries.CountAsync(app, $"SELECT count(*) FROM {table}", transaction))
                .Should().Be(1L, "reapplied constraints and policies must admit a real app write and scoped read");
        }
    }

    private const string JobFunctionQuery =
        "SELECT count(*) FROM pg_proc WHERE pronamespace = 'public'::regnamespace "
        + "AND (proname LIKE 'fn_background_job%' OR proname LIKE 'fn_hangfire%')";

    private const string VendorSequenceQuery =
        "SELECT count(*) FROM pg_class WHERE relnamespace = 'public'::regnamespace AND relkind = 'S' "
        + "AND relname IN ('aggregatedcounter_id_seq','counter_id_seq','hash_id_seq','job_id_seq',"
        + "'jobparameter_id_seq','jobqueue_id_seq','list_id_seq','set_id_seq','state_id_seq')";

    private const string TablesQuery =
        "SELECT count(*) FROM pg_class WHERE relnamespace = 'public'::regnamespace AND relkind = 'r'";

    private const string FunctionQuery =
        "SELECT count(*) FROM pg_proc WHERE proname = 'fn_organization_id_immutable'";

    private const string AuditFunctionQuery =
        "SELECT count(*) FROM pg_proc WHERE proname IN "
        + "('fn_audit_log_append_only', 'fn_audit_log_no_truncate')";

    private const string PolicyQuery =
        "SELECT count(*) FROM pg_policies WHERE schemaname = 'public'";

    private const string InboxFunctionQuery =
        "SELECT count(*) FROM pg_proc WHERE pronamespace = 'public'::regnamespace "
        + "AND proname = 'fn_inbox_messages_immutable'";

    private const string IdentityFunctionQuery =
        "SELECT count(*) FROM pg_proc WHERE pronamespace = 'public'::regnamespace AND proname LIKE 'fn_identity_%'";

    private const string EducationFunctionQuery =
        "SELECT count(*) FROM pg_proc WHERE pronamespace = 'public'::regnamespace "
        + "AND proname IN ('fn_lessons_parent_scope', 'fn_course_translations_parent_scope', "
        + "'fn_lesson_translations_parent_scope')";

    private static async Task<long> CountAsync(System.Data.Common.DbConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}

/// <summary>
/// Its own container: this fixture destroys the schema, so it cannot share one
/// with the cases that read it.
/// </summary>
public sealed class MigrationRollbackFixture : IAsyncLifetime
{
    public PostgresFixture Postgres { get; } = new();

    public async Task InitializeAsync()
    {
        await Postgres.InitializeAsync();

        // Explicit historical prefixes are the reversal subjects. Applying latest
        // here would ask retained-evidence migrations to erase their authority.
        await using (var tenancy = CreateTenancy())
            await tenancy.GetService<IMigrator>().MigrateAsync(TenancyReversibleHead);
        await using (var platform = CreatePlatform())
            await platform.GetService<IMigrator>().MigrateAsync(PlatformReversibleHead);
        await using (var audit = CreateAudit()) await audit.Database.MigrateAsync();
        await using (var identity = CreateIdentity())
            await identity.GetService<IMigrator>().MigrateAsync(IdentityReversibleHead);
        await using (var customization = CreateCustomization())
            await customization.GetService<IMigrator>().MigrateAsync(CustomizationReversibleHead);
        await using (var education = CreateEducation()) await education.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await Postgres.DisposeAsync();

    private const string TenancyReversibleHead = "20261017210806_grant_job_lifecycle_projection";
    private const string PlatformReversibleHead = "20261017231821_index_background_job_control_quarantines";
    private const string IdentityReversibleHead = "20261028155736_add_identity_self_entry";
    private const string CustomizationReversibleHead = "20260906100809_create_customization_schema";

    public async Task AssertCurrentHeadsRefuseReversalAsync()
    {
        await using var tenancy = CreateTenancy();
        await using var platform = CreatePlatform();
        await using var identity = CreateIdentity();
        await using var customization = CreateCustomization();
        // Historical refusal contracts remain explicit even when newer reversible
        // migrations now precede these guards in the chain.
        Action retainedDefaults = () => _ = new LearnStack.Modules.Tenancy.Infrastructure.Persistence.Migrations.guard_retained_tenancy_defaults().DownOperations;
        retainedDefaults.Should().Throw<NotSupportedException>().WithMessage("Retained default fencing requires a reviewed forward migration.");
        Action invitationParent = () => _ = new LearnStack.Modules.Identity.Infrastructure.Persistence.Migrations.lock_invitation_result_parent().DownOperations;
        invitationParent.Should().Throw<NotSupportedException>().WithMessage("Parent scope locking requires a reviewed forward migration.");
        Action envelopeParent = () => _ = new LearnStack.Infrastructure.Persistence.Migrations.lock_invitation_envelope_parent().DownOperations;
        envelopeParent.Should().Throw<NotSupportedException>().WithMessage("Parent scope locking requires a reviewed forward migration.");
        Action retainedFields = () => _ = new LearnStack.Modules.Identity.Infrastructure.Persistence.Migrations.membership_revision_pinned_custom_fields().DownOperations;
        retainedFields.Should().Throw<NotSupportedException>().WithMessage("Retained membership custom fields require a reviewed forward migration.");
        Action recoveryAnchor = () => _ = new LearnStack.Modules.Identity.Infrastructure.Persistence.Migrations.preserve_provider_recovery_anchor().DownOperations;
        recoveryAnchor.Should().Throw<NotSupportedException>().WithMessage("Provider recovery continuity requires a forward repair.");
        foreach (var (context, immediate, historical, head, reason) in new (DbContext, string, string, string, string)[]
        {
            (tenancy, "20261029112940_guard_retained_tenancy_defaults", TenancyReversibleHead,
                "20261109230237_membership_field_consent_evidence", "Retained consent evidence requires a reviewed forward migration."),
            (platform, "20261105020229_lock_invitation_envelope_parent", PlatformReversibleHead,
                "20261110013140_add_custom_field_protected_receipts", "Retained custom field receipts require a reviewed forward migration."),
            (identity, "20261109230238_role_registry_release_v3", IdentityReversibleHead,
                "20261111180000_classify_retained_provider_collision", "Provider receipt collision classification requires a forward repair."),
            (customization, CustomizationReversibleHead, CustomizationReversibleHead,
                "20261109230218_membership_custom_field_definitions", "Retained custom field definitions require a reviewed forward migration."),
        })
        {
            (await context.Database.GetAppliedMigrationsAsync()).Last().Should().Be(head);
            foreach (var target in new[] { immediate, historical }.Distinct(StringComparer.Ordinal))
            {
                var before = await SchemaStateAsync();
                Func<Task> reverse = () => context.GetService<IMigrator>().MigrateAsync(target);
                (await reverse.Should().ThrowAsync<NotSupportedException>()).Which.Message.Should().Be(reason);
                (await SchemaStateAsync()).Should().Be(before, "a refused reversal must preserve definitions, grants, policies, triggers and every migration history");
            }
        }
    }

    private async Task<string> SchemaStateAsync()
    {
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(Postgres.MigrationConnectionString);
        await using var command = new NpgsqlCommand("""
            SELECT jsonb_build_object(
                'relations',(SELECT jsonb_agg(jsonb_build_array(c.relname,c.relkind,c.relowner,c.relacl,c.relrowsecurity,c.relforcerowsecurity) ORDER BY c.relname)
                    FROM pg_class c WHERE c.relnamespace='public'::regnamespace),
                'columns',(SELECT jsonb_agg(jsonb_build_array(c.relname,a.attnum,a.attname,a.atttypid,a.atttypmod,a.attnotnull,a.attidentity,
                        pg_get_expr(d.adbin,d.adrelid)) ORDER BY c.relname,a.attnum)
                    FROM pg_attribute a JOIN pg_class c ON c.oid=a.attrelid LEFT JOIN pg_attrdef d ON d.adrelid=a.attrelid AND d.adnum=a.attnum
                    WHERE c.relnamespace='public'::regnamespace AND a.attnum>0 AND NOT a.attisdropped),
                'constraints',(SELECT jsonb_agg(jsonb_build_array(c.conname,c.conrelid,pg_get_constraintdef(c.oid)) ORDER BY c.conrelid,c.conname)
                    FROM pg_constraint c WHERE c.connamespace='public'::regnamespace),
                'indexes',(SELECT jsonb_agg(to_jsonb(i) ORDER BY i.indexname) FROM pg_indexes i WHERE i.schemaname='public'),
                'functions',(SELECT jsonb_agg(jsonb_build_array(p.oid,p.proowner,p.proacl,pg_get_functiondef(p.oid)) ORDER BY p.oid)
                    FROM pg_proc p WHERE p.pronamespace='public'::regnamespace),
                'triggers',(SELECT jsonb_agg(jsonb_build_array(t.tgrelid,t.tgname,t.tgenabled,pg_get_triggerdef(t.oid)) ORDER BY t.tgrelid,t.tgname)
                    FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid WHERE c.relnamespace='public'::regnamespace AND NOT t.tgisinternal),
                'policies',(SELECT jsonb_agg(to_jsonb(p) ORDER BY p.tablename,p.policyname) FROM pg_policies p WHERE p.schemaname='public'))::text
            """, connection);
        var state = new List<string> { (string)(await command.ExecuteScalarAsync())! };
        foreach (var history in MigrationChains.HistoryTables)
        {
            await using var rows = new NpgsqlCommand($"SELECT COALESCE(jsonb_agg(to_jsonb(h) ORDER BY h.\"MigrationId\"),'[]'::jsonb)::text FROM \"{history}\" h", connection);
            state.Add((string)(await rows.ExecuteScalarAsync())!);
        }
        return string.Join('\n', state);
    }

    /// <summary>
    /// Reverses the installed historical prefixes in dependency order. Dependents
    /// release references before the chain owning a shared table or function reverses.
    /// </summary>
    public async Task RollBackAsync()
    {
        // Disposable-schema reversal only: normal deployment never retires permanent
        // bindings by rolling this chain back. Release Audit witness dependencies first.
        await using (var identity = CreateIdentity())
        {
            await identity.GetService<IMigrator>().MigrateAsync(Migration.InitialDatabase);
        }

        // Education first: its four immutability triggers call Tenancy's function.
        await using (var education = CreateEducation())
        {
            await education.GetService<IMigrator>().MigrateAsync(Migration.InitialDatabase);
        }

        // Audit next, because it depends on another chain's tables:
        // fk_audit_config_tenant references `tenants` with ON DELETE RESTRICT, and a
        // DROP TABLE tenants while audit_config still exists fails on the dependency
        // rather than on any row. Reversal order is application order reversed, which
        // is what makes that fall out rather than have to be remembered.
        await using (var audit = CreateAudit())
        {
            await audit.GetService<IMigrator>().MigrateAsync(Migration.InitialDatabase);
        }

        await using (var customization = CreateCustomization())
        {
            await customization.GetService<IMigrator>().MigrateAsync(Migration.InitialDatabase);
        }

        await using (var platform = CreatePlatform())
        {
            await platform.GetService<IMigrator>().MigrateAsync(Migration.InitialDatabase);
        }

        await using var tenancy = CreateTenancy();
        await tenancy.GetService<IMigrator>().MigrateAsync(Migration.InitialDatabase);
    }

    private TenancyDbContext CreateTenancy() =>
        new(
            new DbContextOptionsBuilder<TenancyDbContext>()
                .UseNpgsql(Postgres.MigrationConnectionString, npgsql =>
                    npgsql.MigrationsHistoryTable(TenancyDbContextFactory.HistoryTable))
                .Options,
            StaticTenantContextAccessor.Unresolved);

    private IdentityDbContext CreateIdentity() =>
        new(new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(Postgres.MigrationConnectionString, provider =>
                provider.MigrationsHistoryTable(IdentityDbContextFactory.HistoryTable)).Options,
            StaticTenantContextAccessor.Unresolved, EmptyIdentityScope.Instance);

    private EducationDbContext CreateEducation() =>
        new(
            new DbContextOptionsBuilder<EducationDbContext>()
                .UseNpgsql(Postgres.MigrationConnectionString, npgsql =>
                    npgsql.MigrationsHistoryTable(EducationDbContextFactory.HistoryTable))
                .Options,
            StaticTenantContextAccessor.Unresolved);

    private PlatformDbContext CreatePlatform() =>
        new(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(Postgres.MigrationConnectionString, npgsql =>
                npgsql.MigrationsHistoryTable(PlatformDbContextFactory.HistoryTable))
            .Options);

    private AuditDbContext CreateAudit() =>
        new(
            new DbContextOptionsBuilder<AuditDbContext>()
                .UseNpgsql(Postgres.MigrationConnectionString, npgsql =>
                    npgsql.MigrationsHistoryTable(AuditDbContextFactory.HistoryTable))
                .Options,
            StaticTenantContextAccessor.Unresolved);

    private CustomizationDbContext CreateCustomization() =>
        new(
            new DbContextOptionsBuilder<CustomizationDbContext>()
                .UseNpgsql(Postgres.MigrationConnectionString, npgsql =>
                    npgsql.MigrationsHistoryTable(CustomizationDbContextFactory.HistoryTable))
                .Options,
            StaticTenantContextAccessor.Unresolved);
}
