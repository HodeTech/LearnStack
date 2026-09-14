using System.Data.Common;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// Applied-schema proofs for ADR-0003 Amendment 6. Catalogue inspections may use
/// the owner; every behavioral assertion authenticates as learnstack_app.
/// </summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class OrgScopeSchemaTests
{
    private readonly SchemaFixture _schema;

    public OrgScopeSchemaTests(SchemaFixture schema) => _schema = schema;

    /// <summary>
    /// <see href="../../../../docs/standards/05-database.md">Standards 05 § Foreign
    /// keys between tenant-owned tables</see>, catalogued under this exact name in
    /// <see href="../../../../docs/standards/21-architecture-tests-catalogue.md">Standards 21</see>.
    /// Presence in both column lists is not enough:
    /// the tenant columns must be the same positional pair in the constraint.
    /// </summary>
    [Fact]
    public async Task Every_TenantOwned_Foreign_Key_Includes_TenantId()
    {
        await using var owner = await PostgresFixture.OpenAsync(_schema.Postgres.MigrationConnectionString);

        var scanned = await SchemaQueries.ReadStringsAsync(owner, TenantForeignKeys +
            "SELECT conname FROM tenant_foreign_keys ORDER BY conname");
        scanned.Should().Contain([
            "fk_organizations_tenant", "fk_tenants_default_organization",
            "fk_tenant_level_taxonomy_items_taxonomy",
        ], "the self-keyed parent, self-keyed child and independent module must all be scanned");

        (await TenantForeignKeyOffendersAsync(owner)).Should().BeEmpty(
            "Fix: pair the child's tenant key with the parent's tenant key in the same FK; "
            + "tenants.id is the self-keyed exception in either direction (Standards 05)");
    }

    [Theory]
    [InlineData("FOREIGN KEY (parent_id) REFERENCES scope_fk_parent(id)")]
    [InlineData("FOREIGN KEY (tenant_id, parent_id) REFERENCES scope_fk_parent(id, tenant_id)")]
    public async Task The_Tenant_Foreign_Key_Guard_Detects_Missing_And_Mispaired_Tenant_Columns(
        string foreignKey)
    {
        // Metadata only, so the planted schema can remain inside an owner transaction.
        // In particular the second offender carries tenant_id on BOTH sides and still
        // maps it to the parent's id, which a set-membership assertion cannot detect.
        await using var owner = await PostgresFixture.OpenAsync(_schema.Postgres.MigrationConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();
        await SchemaQueries.ExecuteAsync(owner, transaction,
            $"""
             CREATE TABLE scope_fk_parent (
                 id uuid PRIMARY KEY, tenant_id uuid NOT NULL,
                 UNIQUE (id, tenant_id), UNIQUE (tenant_id, id));
             CREATE TABLE scope_fk_child (
                 id uuid PRIMARY KEY, tenant_id uuid NOT NULL, parent_id uuid NOT NULL,
                 CONSTRAINT fk_scope_probe {foreignKey});
             """);

        (await TenantForeignKeyOffendersAsync(owner, transaction))
            .Should().ContainSingle().Which.Should().Be("scope_fk_child.fk_scope_probe");

        await SchemaQueries.ExecuteAsync(owner, transaction,
            """
            ALTER TABLE scope_fk_child DROP CONSTRAINT fk_scope_probe;
            ALTER TABLE scope_fk_child ADD CONSTRAINT fk_scope_probe
                FOREIGN KEY (tenant_id, parent_id) REFERENCES scope_fk_parent(tenant_id, id);
            """);
        (await TenantForeignKeyOffendersAsync(owner, transaction)).Should().BeEmpty(
            "the identical detector must accept the repaired positional tenant pair");
    }

    [Theory]
    [InlineData("organizations", "fk_organizations_tenant",
        "FOREIGN KEY (id) REFERENCES tenants(id)")]
    [InlineData("tenants", "fk_tenants_default_organization",
        "FOREIGN KEY (default_organization_id) REFERENCES organizations(id)")]
    public async Task The_Tenant_Foreign_Key_Guard_Does_Not_Exempt_The_Self_Keyed_Directions(
        string table, string constraint, string replacement)
    {
        await using var owner = await PostgresFixture.OpenAsync(_schema.Postgres.MigrationConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();
        // NOT VALID avoids asking the planted constraint to validate unrelated seed
        // rows. It is still a constraint any new row must obey, and the sweep must see it.
        await SchemaQueries.ExecuteAsync(owner, transaction,
            $"ALTER TABLE {table} DROP CONSTRAINT {constraint}; "
            + $"ALTER TABLE {table} ADD CONSTRAINT {constraint} {replacement} NOT VALID;");

        (await TenantForeignKeyOffendersAsync(owner, transaction))
            .Should().ContainSingle().Which.Should().Be($"{table}.{constraint}");
    }

    /// <summary>
    /// <see href="../../../../docs/decisions/0003-tenant-isolation-defense-in-depth.md">ADR-0003
    /// Amendment 6</see> and
    /// <see href="../../../../docs/standards/21-architecture-tests-catalogue.md">Standards 21</see>:
    /// organization scope is a table class.
    /// The host projection and outbox metadata are the two explicit exclusions;
    /// audit_log's append-only trigger qualifies only with its behavioral proof below.
    /// </summary>
    [Fact]
    public async Task Every_OrgScoped_Table_Has_An_Organization_Immutability_Guard()
    {
        await using var owner = await PostgresFixture.OpenAsync(_schema.Postgres.MigrationConnectionString);
        var all = await SchemaQueries.ReadStringsAsync(owner, OrganizationTables +
            "SELECT relname FROM organization_columns ORDER BY relname");
        var scoped = await SchemaQueries.ReadStringsAsync(owner, OrganizationTables +
            "SELECT relname FROM organization_scoped ORDER BY relname");

        scoped.Should().Contain(["tenant_settings", "audit_log"],
            "a guard over no classified tables proves nothing");
        all.Except(scoped).Should().BeEquivalentTo(["platform_host_to_tenant", "outbox_messages"],
            "these are the two documented table-class exceptions, not an extensible exclusion list");

        (await OrganizationGuardOffendersAsync(owner)).Should().BeEmpty(
            "Fix: attach an enabled, unconditional BEFORE UPDATE row trigger calling "
            + "fn_organization_id_immutable; audit_log retains its proven append-only guard");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("conditional")]
    [InlineData("wrong-column")]
    [InlineData("org-column-only")]
    [InlineData("wrong-function")]
    public async Task The_Organization_Guard_Rejects_A_New_Unguarded_Table_And_Inert_Triggers(string defect)
    {
        await using var owner = await PostgresFixture.OpenAsync(_schema.Postgres.MigrationConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();
        await SchemaQueries.ExecuteAsync(owner, transaction,
            """
            CREATE TABLE scope_guard_probe (
                tenant_id uuid NOT NULL, organization_id uuid NULL, locale text NOT NULL,
                PRIMARY KEY (tenant_id, locale));
            """);

        if (defect != "missing")
        {
            var columns = defect switch
            {
                "wrong-column" => " OF locale",
                "org-column-only" => " OF organization_id",
                _ => "",
            };
            var condition = defect == "conditional" ? " WHEN (OLD.organization_id IS NOT NULL)" : "";
            var function = defect == "wrong-function" ? "fn_audit_log_append_only" : "fn_organization_id_immutable";
            await SchemaQueries.ExecuteAsync(owner, transaction,
                $"CREATE TRIGGER tg_scope_guard_probe BEFORE UPDATE{columns} ON scope_guard_probe "
                + $"FOR EACH ROW{condition} EXECUTE FUNCTION {function}();");
            if (defect == "disabled")
            {
                await SchemaQueries.ExecuteAsync(owner, transaction,
                    "ALTER TABLE scope_guard_probe DISABLE TRIGGER tg_scope_guard_probe;");
            }
        }

        (await OrganizationGuardOffendersAsync(owner, transaction))
            .Should().ContainSingle().Which.Should().Be("scope_guard_probe");

        await SchemaQueries.ExecuteAsync(owner, transaction,
            """
            DROP TRIGGER IF EXISTS tg_scope_guard_probe ON scope_guard_probe;
            CREATE TRIGGER tg_scope_guard_probe BEFORE UPDATE ON scope_guard_probe
                FOR EACH ROW EXECUTE FUNCTION fn_organization_id_immutable();
            """);
        (await OrganizationGuardOffendersAsync(owner, transaction)).Should().BeEmpty(
            "the detector must see an unknown table, refuse its defect and accept its repair");
    }

    [Theory]
    [InlineData("tenant_settings", "tg_tenant_settings_organization_id_immutable")]
    [InlineData("audit_log", "audit_log_append_only_guard")]
    public async Task The_Organization_Guard_Rejects_A_Missing_Guard_On_Each_Existing_Table(
        string table, string trigger)
    {
        await using var owner = await PostgresFixture.OpenAsync(_schema.Postgres.MigrationConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();
        await SchemaQueries.ExecuteAsync(owner, transaction, $"DROP TRIGGER {trigger} ON {table};");

        (await OrganizationGuardOffendersAsync(owner, transaction))
            .Should().ContainSingle().Which.Should().Be(table);
    }

    public static IEnumerable<object[]> OrganizationInsertCases()
    {
        foreach (var table in new[] { "tenant_settings", "audit_log" })
        {
            yield return [table, "none", "none", false, true];
            yield return [table, "own", "own", false, true];
            yield return [table, "own", "none", false, false];
            yield return [table, "none", "own", false, false];
            yield return [table, "own", "other", false, false];
            yield return [table, "own", "foreign", false, false];
            yield return [table, "own", "none", true, false];
            yield return [table, "own", "other", true, false];
            yield return [table, "own", "own", true, true];
        }
    }

    [Theory]
    [MemberData(nameof(OrganizationInsertCases))]
    public async Task An_Insert_Requires_The_Exact_Organization_Scope(
        string table, string sessionOrganization, string rowOrganization, bool tenantReadHatch, bool accepted)
    {
        await using var app = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(app, transaction, SchemaFixture.TenantA);
        await SchemaQueries.SetSettingAsync(app, transaction, "app.organization_id",
            Organization(sessionOrganization)?.ToString() ?? "");
        if (tenantReadHatch)
        {
            await SchemaQueries.SetSettingAsync(app, transaction, "app.scope", "tenant");
        }

        var id = Guid.CreateVersion7();
        var insert = async () => await InsertAsync(app, transaction, table, id, Organization(rowOrganization));
        if (accepted)
        {
            await insert.Should().NotThrowAsync("the exact organization scope must remain writable");
            await using var read = new NpgsqlCommand($"SELECT count(*) FROM {table} WHERE id = @id",
                (NpgsqlConnection)app, (NpgsqlTransaction)transaction);
            read.Parameters.AddWithValue("id", id);
            (await read.ExecuteScalarAsync()).Should().Be(1L,
                "the successful insert must have persisted a visible row");
        }
        else
        {
            var refused = (await insert.Should().ThrowAsync<PostgresException>()).Which;
            refused.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
            refused.MessageText.Should().Contain("row-level security",
                "the WITH CHECK policy must refuse the write, not a missing grant or foreign key");
        }
    }

    [Theory]
    [InlineData("tenant_settings", false)]
    [InlineData("tenant_settings", true)]
    [InlineData("audit_log", false)]
    [InlineData("audit_log", true)]
    public async Task A_Reused_Connection_Does_Not_Keep_The_Previous_Organization(
        string table, bool commitAnnouncement)
    {
        // A dedicated one-connection pool makes physical reuse observable and keeps
        // unrelated fixture connections from satisfying the second checkout.
        var builder = new NpgsqlConnectionStringBuilder(_schema.Postgres.AppConnectionString)
        {
            MaxPoolSize = 1,
            ApplicationName = $"scope-reuse-{Guid.NewGuid():N}",
        };
        await using var source = NpgsqlDataSource.Create(builder.ConnectionString);
        int firstBackend;
        await using (var first = await source.OpenConnectionAsync())
        {
            firstBackend = first.ProcessID;
            await using var announcement = await first.BeginTransactionAsync();
            await SchemaQueries.SetTenantAsync(first, announcement, SchemaFixture.TenantA);
            await SchemaQueries.SetSettingAsync(first, announcement,
                "app.organization_id", SchemaFixture.OrgA1.ToString());
            if (commitAnnouncement)
            {
                await announcement.CommitAsync();
            }
            else
            {
                await announcement.RollbackAsync();
            }
        }

        await using var reused = await source.OpenConnectionAsync();
        reused.ProcessID.Should().Be(firstBackend, "this must exercise a reused physical session");
        await using var transaction = await reused.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(reused, transaction, SchemaFixture.TenantA);
        await using (var scope = new NpgsqlCommand(
            "SELECT NULLIF(current_setting('app.organization_id', true), '') IS NULL", reused, transaction))
        {
            (await scope.ExecuteScalarAsync()).Should().Be(true,
                "an unset dotted GUC can be empty after pooling and must still mean no organization");
        }

        await InsertAsync(reused, transaction, table, Guid.CreateVersion7(), organizationId: null);
        var staleScope = async () => await InsertAsync(
            reused, transaction, table, Guid.CreateVersion7(), SchemaFixture.OrgA1);
        (await staleScope.Should().ThrowAsync<PostgresException>()).Which.SqlState
            .Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "the previous request's organization must confer no authority on this request");
    }

    [Fact]
    public async Task The_Shared_Immutability_Function_Works_On_A_Table_With_No_Id_Column()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(_schema.Postgres);
        await using (var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString))
        {
            // Setup commits before the independently authenticated app connection.
            // The table has a natural key and no id; the original error expression
            // read OLD.id and raised undefined_column instead of its intended 23514.
            await SchemaQueries.ExecuteAsync(owner, null,
                """
                CREATE TABLE scope_natural_key_probe (
                    tenant_id uuid NOT NULL, locale text NOT NULL, organization_id uuid NULL,
                    payload text NOT NULL, PRIMARY KEY (tenant_id, locale));
                CREATE TRIGGER tg_scope_natural_key_probe BEFORE UPDATE ON scope_natural_key_probe
                    FOR EACH ROW EXECUTE FUNCTION public.fn_organization_id_immutable();
                GRANT SELECT, INSERT, UPDATE ON scope_natural_key_probe TO learnstack_app;
                """);
        }

        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        foreach (var (original, replacement) in new (Guid?, Guid?)[]
                 { (null, SchemaFixture.OrgA1), (SchemaFixture.OrgA1, null), (SchemaFixture.OrgA1, SchemaFixture.OrgA2) })
        {
            await using var transaction = await app.BeginTransactionAsync();
            await SchemaQueries.ExecuteAsync(app, transaction,
                """
                INSERT INTO scope_natural_key_probe (tenant_id, locale, organization_id, payload)
                VALUES (@tenant, 'en', @organization, 'original');
                """, ("tenant", SchemaFixture.TenantA), ("organization", (object?)original ?? DBNull.Value));
            await using (var unchanged = new NpgsqlCommand(
                "UPDATE scope_natural_key_probe SET payload = 'changed', organization_id = organization_id",
                (NpgsqlConnection)app, (NpgsqlTransaction)transaction))
            {
                (await unchanged.ExecuteNonQueryAsync()).Should().Be(1,
                    "an unchanged organization, including null, must allow a normal update");
            }

            var move = async () => await SchemaQueries.ExecuteAsync(app, transaction,
                "UPDATE scope_natural_key_probe SET organization_id = @organization",
                ("organization", (object?)replacement ?? DBNull.Value));
            var refused = (await move.Should().ThrowAsync<PostgresException>()).Which;
            refused.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
            refused.MessageText.Should().Contain("organization_id is immutable after insert")
                .And.Contain("scope_natural_key_probe");
        }
    }

    [Fact]
    public async Task The_Audit_Append_Only_Guard_Independently_Refuses_An_Organization_Change()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(_schema.Postgres);
        await using (var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString))
        {
            // Without this committed grant the statement never reaches the trigger.
            // Real privileges remain covered on the untouched shared schema.
            await SchemaQueries.ExecuteAsync(owner, null,
                "GRANT UPDATE (organization_id) ON audit_log TO learnstack_app;");
        }

        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(app, transaction, SchemaFixture.TenantA);
        var id = Guid.CreateVersion7();
        await InsertAsync(app, transaction, "audit_log", id, organizationId: null);

        var move = async () => await SchemaQueries.ExecuteAsync(app, transaction,
            "UPDATE audit_log SET organization_id = @organization WHERE id = @id",
            ("organization", SchemaFixture.OrgA1), ("id", id));
        var refused = (await move.Should().ThrowAsync<PostgresException>()).Which;
        refused.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        refused.MessageText.Should().Contain("audit_log is append-only")
            .And.Contain("learnstack_app",
                "BEFORE UPDATE must reach the append-only guard; a later WITH CHECK denial is insufficient");
    }

    private static Guid? Organization(string scope) => scope switch
    {
        "none" => null,
        "own" => SchemaFixture.OrgA1,
        "other" => SchemaFixture.OrgA2,
        "foreign" => SchemaFixture.OrgB1,
        _ => throw new ArgumentOutOfRangeException(nameof(scope)),
    };

    private static Task InsertAsync(
        DbConnection connection, DbTransaction transaction, string table, Guid id, Guid? organizationId) =>
        SchemaQueries.ExecuteAsync(connection, transaction, table switch
        {
            "tenant_settings" =>
                """
                INSERT INTO tenant_settings
                    (id, tenant_id, organization_id, key, value, created_at, created_by, row_version)
                VALUES (@id, @tenant, @organization, @id::text, '{}', now(), @actor, 0)
                """,
            "audit_log" =>
                """
                INSERT INTO audit_log
                    (id, tenant_id, organization_id, module, operation, operation_type,
                     operation_class, outcome, timestamp)
                VALUES (@id, @tenant, @organization, 'audit', 'audit.event.read',
                        'ReadSensitive', 'Should', 'success', now())
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(table)),
        }, ("id", id), ("tenant", SchemaFixture.TenantA),
        ("organization", (object?)organizationId ?? DBNull.Value), ("actor", SchemaFixture.Actor));

    private static Task<List<string>> TenantForeignKeyOffendersAsync(
        DbConnection connection, DbTransaction? transaction = null) =>
        SchemaQueries.ReadStringsAsync(connection, TenantForeignKeys +
            """
            SELECT relname || '.' || conname FROM tenant_foreign_keys f
            WHERE NOT EXISTS (
                SELECT 1 FROM generate_subscripts(f.conkey, 1) position
                WHERE f.conkey[position] = f.child_tenant_key
                  AND f.confkey[position] = f.parent_tenant_key)
            ORDER BY 1
            """, transaction);

    private static readonly string TenantForeignKeys =
        $"""
         WITH tenant_tables AS (
             SELECT c.oid, c.relname, a.attnum AS tenant_key
             FROM pg_class c JOIN pg_attribute a ON a.attrelid = c.oid
             WHERE c.oid IN ({SchemaQueries.TableOids})
               AND NOT a.attisdropped AND a.attnum > 0
               AND a.attname = CASE WHEN c.relname = 'tenants' THEN 'id' ELSE 'tenant_id' END
               -- A tenant column does not make the host projection tenant-owned.
               AND c.relname <> 'platform_host_to_tenant'
         ), tenant_foreign_keys AS (
             SELECT f.conname, child.relname, f.conkey, f.confkey,
                    child.tenant_key AS child_tenant_key, parent.tenant_key AS parent_tenant_key
             FROM pg_constraint f
             JOIN tenant_tables child ON child.oid = f.conrelid
             JOIN tenant_tables parent ON parent.oid = f.confrelid
             WHERE f.contype = 'f'
         )
         """;

    private static Task<List<string>> OrganizationGuardOffendersAsync(
        DbConnection connection, DbTransaction? transaction = null) =>
        SchemaQueries.ReadStringsAsync(connection, OrganizationTables +
            """
            SELECT scoped.relname FROM organization_scoped scoped
            WHERE NOT EXISTS (
                SELECT 1 FROM pg_trigger t
                JOIN pg_proc f ON f.oid = t.tgfoid
                JOIN pg_namespace n ON n.oid = f.pronamespace
                WHERE t.tgrelid = scoped.oid AND NOT t.tgisinternal
                  AND t.tgenabled IN ('O', 'A')
                  AND (t.tgtype & 19) = 19 -- BEFORE (2), ROW (1), UPDATE (16)
                  AND t.tgqual IS NULL AND t.tgnargs = 0
                  -- UPDATE OF tests the SET list, not NEW: an earlier trigger can
                  -- change organization_id while the statement updates another column.
                  AND cardinality(t.tgattr::smallint[]) = 0
                  AND n.nspname = 'public' AND f.pronargs = 0
                  AND (f.proname = 'fn_organization_id_immutable'
                       OR (scoped.relname = 'audit_log' AND f.proname = 'fn_audit_log_append_only')))
            ORDER BY scoped.relname
            """, transaction);

    private static readonly string OrganizationTables =
        $"""
         WITH organization_columns AS (
             SELECT c.oid, c.relname
             FROM pg_class c JOIN pg_attribute a ON a.attrelid = c.oid
             WHERE c.oid IN ({SchemaQueries.TableOids})
               AND a.attnum > 0 AND NOT a.attisdropped AND a.attname = 'organization_id'
         ), organization_scoped AS (
             SELECT * FROM organization_columns
             WHERE relname NOT IN ('platform_host_to_tenant', 'outbox_messages')
         )
         """;
}
