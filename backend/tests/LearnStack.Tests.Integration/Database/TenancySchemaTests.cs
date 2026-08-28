using FluentAssertions;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The tenancy schema, its Row Level Security policies and its grants, asserted
/// against a real PostgreSQL with the four roles provisioned.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every data case connects as <c>learnstack_app</c>.</b> A test that connected
/// as the owner — or as either <c>BYPASSRLS</c> role — would pass with every policy
/// inert and prove nothing, which is the failure mode
/// <see href="../../../../docs/decisions/0003-tenant-isolation-defense-in-depth.md">ADR-0003
/// Amendment 3</see> names by hand. Four cases connect as
/// <c>learnstack_migration</c>: three read <c>pg_catalog</c> /
/// <c>information_schema</c> rather than data, and the fourth
/// (<see cref="TheOwnerIsDeniedOnThePlatformScopedTable"/>) asserts a denial that
/// only the owner can experience.
/// </para>
/// <para>
/// <b>Every table carries rows for both tenants.</b> A count assertion against an
/// empty table passes whether or not the policy exists — that is how the first
/// version of the owner-denial case shipped proving nothing. The seed therefore
/// fills all eight, tenant A with a second organization so the organization half
/// of the template has something to hide, and every read case sweeps the whole
/// catalogue rather than a hand-written list.
/// </para>
/// <para>
/// Three method names are not house style —
/// <c>TenantWide_Row_Of_TenantB_Is_Invisible_To_TenantA</c>,
/// <c>Write_With_Foreign_TenantId_Is_Rejected_By_WithCheck</c> and
/// <c>Org_X_cannot_read_Org_Y_within_TenantA</c> — because they are the canonical
/// identifiers in
/// <see href="../../../../docs/standards/21-architecture-tests-catalogue.md">the
/// architecture-test catalogue</see> and the Phase 02a document. One rule, one
/// spelling.
/// </para>
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class TenancySchemaTests : IClassFixture<TenancySchemaFixture>
{
    private readonly TenancySchemaFixture _schema;

    public TenancySchemaTests(TenancySchemaFixture schema) => _schema = schema;

    [Fact]
    public async Task EveryTableEnablesAndForcesRowLevelSecurity()
    {
        // The catalogue itself, not a list of names kept beside it. An inclusion
        // list fails open for the table somebody adds and forgets to add here —
        // reproduced: a table created outside the array was invisible to this
        // sweep and to the snake-case sweep at once, with row security off and a
        // PascalCase column.
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.MigrationConnectionString);

        var scanned = await ReadStringsAsync(connection, TableCatalogueQuery);
        scanned.Should().Contain(TenancySchemaFixture.KnownTables,
            "the sweep must be reading the tenancy schema, not an empty result set");

        var unprotected = await ReadStringsAsync(connection,
            $"""
             SELECT relname FROM pg_class
             WHERE oid IN ({TableCatalogueOids}) AND NOT (relrowsecurity AND relforcerowsecurity)
             """);

        unprotected.Should().BeEmpty("ENABLE without FORCE lets the owner bypass its own policies");
    }

    [Fact]
    public async Task NoTableCarriesTwoPermissivePoliciesForOneCommand()
    {
        // The defect ADR-0003 Amendment 3 corrects. CREATE POLICY is permissive by
        // default and PostgreSQL combines permissive policies with OR, so a second
        // one WIDENS access — it made every tenant-wide row visible to every
        // tenant.
        //
        // Grouped by (table, command) rather than excluding platform_host_to_tenant
        // by name. That table legitimately carries four permissive policies, one
        // per command, and naming it here made the one table whose SELECT policy is
        // deliberately wide the one table this guard could not see: a second
        // permissive SELECT on it passed the excluded form while leaking every
        // tenant's host mappings to any session with any tenant context.
        //
        // `FOR ALL` is caught separately because its cmd is 'ALL' — it overlaps
        // every per-command policy, so a table holding one alongside any other
        // permissive policy has two policies for at least one command.
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.MigrationConnectionString);

        var offenders = await ReadStringsAsync(connection,
            """
            SELECT tablename || ' (' || string_agg(policyname, ', ' ORDER BY policyname) || ')'
            FROM pg_policies
            WHERE schemaname = 'public' AND permissive = 'PERMISSIVE'
            GROUP BY tablename
            HAVING count(*) > 1
               AND (bool_or(cmd = 'ALL') OR count(*) <> count(DISTINCT cmd))
            """);

        offenders.Should().BeEmpty(
            "permissive policies covering the same command are OR-ed, and widen access");
    }

    [Fact]
    public async Task Unsetting_tenant_context_returns_zero_rows_through_RLS()
    {
        // Fail-closed, and it is the NULLIF that delivers it: an unset dotted GUC
        // reads as the empty string on a pooled connection, and ''::uuid RAISES
        // rather than filtering. NULLIF makes it NULL, and a NULL predicate is
        // false for USING and WITH CHECK alike.
        //
        // Swept over every table rather than asserted on `tenants` alone. Each of
        // the eight holds rows for both tenants, so a table whose policy lost its
        // predicate answers with a non-zero count here.
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);

        var counts = await CountEveryTableAsync(connection, transaction: null);

        counts.Should().OnlyContain(entry => entry.Value == 0,
            "with no app.tenant_id every policy predicate is NULL, which is false");
        counts.Keys.Should().Contain(TenancySchemaFixture.KnownTables);
    }

    [Fact]
    public async Task Tenant_A_cannot_read_Tenant_B_data()
    {
        // Both tenants hold rows in all eight tables, and tenant A holds a second
        // organization, so every count here is a number only the correct policy
        // produces. A policy widened to USING (true) — the mutation that survived
        // the first version of this suite — shows up as A seeing B's rows.
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await SetTenantAsync(connection, transaction, TenancySchemaFixture.TenantA);
            var seenByA = await CountEveryTableAsync(connection, transaction);

            seenByA.Should().BeEquivalentTo(TenancySchemaFixture.RowsVisibleToTenantA);
        }

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await SetTenantAsync(connection, transaction, TenancySchemaFixture.TenantB);
            var seenByB = await CountEveryTableAsync(connection, transaction);

            seenByB.Should().BeEquivalentTo(TenancySchemaFixture.RowsVisibleToTenantB);
        }
    }

    [Fact]
    public async Task TenantWide_Row_Of_TenantB_Is_Invisible_To_TenantA()
    {
        // THE case the superseded template leaked. Its two permissive policies
        // were OR-ed, so a row with organization_id IS NULL satisfied the
        // organization half on its own — visible to every tenant. Asserted on the
        // key rather than on a count, so it names the row it is looking for.
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenantAsync(connection, transaction, TenancySchemaFixture.TenantA);

        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM tenant_settings WHERE key = 'beta-only' AND organization_id IS NULL",
            (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);

        (await command.ExecuteScalarAsync()).Should().Be(0L,
            "tenant B's tenant-wide setting must not be visible to tenant A");
    }

    [Theory]
    [InlineData(
        """
        INSERT INTO organizations (id, tenant_id, slug, display_name, status, created_at, created_by, row_version)
        VALUES (uuidv7(), @foreign, 'sneak', 'Sneak', 'Active', now(), @actor, 0)
        """)]
    [InlineData("UPDATE organizations SET tenant_id = @foreign WHERE tenant_id <> @foreign")]
    public async Task Write_With_Foreign_TenantId_Is_Rejected_By_WithCheck(string statement)
    {
        // Both halves, because WITH CHECK guards both and a USING-only policy
        // would pass the read-side cases while leaving either write open. The
        // UPDATE runs under tenant B's context against tenant B's own row and
        // tries to hand it to tenant A: USING admits the row, WITH CHECK refuses
        // the new value.
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenantAsync(connection, transaction, TenancySchemaFixture.TenantB);

        await using var command = new NpgsqlCommand(
            statement, (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        command.Parameters.AddWithValue("foreign", TenancySchemaFixture.TenantA);

        if (statement.Contains("@actor", StringComparison.Ordinal))
        {
            command.Parameters.AddWithValue("actor", TenancySchemaFixture.Actor);
        }

        var act = async () => await command.ExecuteNonQueryAsync();

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be("42501", "WITH CHECK refuses a row outside the caller's tenant");
    }

    [Fact]
    public async Task Org_X_cannot_read_Org_Y_within_TenantA()
    {
        // The organization half of the template, on the one org-scoped table this
        // packet ships. Tenant A holds a setting under each of its two
        // organizations; a session scoped to the first must see its own and the
        // tenant-wide row, and neither see nor touch the second's.
        //
        // The three assertions are the three gates: USING for the read, the
        // restrictive UPDATE guard, and the restrictive DELETE guard — DELETE has
        // no WITH CHECK in PostgreSQL, so USING is its only gate and a widened one
        // would let a sibling organization's row be removed.
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenantAsync(connection, transaction, TenancySchemaFixture.TenantA);
        await SetSettingAsync(connection, transaction,
            "app.organization_id", TenancySchemaFixture.OrgA1.ToString());

        await using (var read = new NpgsqlCommand(
            "SELECT count(*) FROM tenant_settings WHERE organization_id = @other",
            (NpgsqlConnection)connection, (NpgsqlTransaction)transaction))
        {
            read.Parameters.AddWithValue("other", TenancySchemaFixture.OrgA2);
            (await read.ExecuteScalarAsync()).Should().Be(0L,
                "organization A1 must not read organization A2's rows");
        }

        await using (var update = new NpgsqlCommand(
            "UPDATE tenant_settings SET value = '\"hijacked\"' WHERE organization_id = @other",
            (NpgsqlConnection)connection, (NpgsqlTransaction)transaction))
        {
            update.Parameters.AddWithValue("other", TenancySchemaFixture.OrgA2);
            (await update.ExecuteNonQueryAsync()).Should().Be(0,
                "tenant_settings_org_write_guard is restrictive and admits no sibling row");
        }

        await using (var delete = new NpgsqlCommand(
            "DELETE FROM tenant_settings WHERE organization_id = @other",
            (NpgsqlConnection)connection, (NpgsqlTransaction)transaction))
        {
            delete.Parameters.AddWithValue("other", TenancySchemaFixture.OrgA2);
            (await delete.ExecuteNonQueryAsync()).Should().Be(0,
                "tenant_settings_org_delete_guard is the only gate DELETE has");
        }
    }

    [Fact]
    public async Task OrganizationIdIsImmutableAfterInsert()
    {
        // Tenant-wide to org-scoped: the NULL -> value move, which `<>` would miss
        // because `<>` is NULL when either side is null. The restrictive UPDATE
        // guard does not cover it either — it admits the row when the NEW
        // organization_id is the caller's own, which is exactly this move.
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenantAsync(connection, transaction, TenancySchemaFixture.TenantA);
        await SetSettingAsync(connection, transaction,
            "app.organization_id", TenancySchemaFixture.OrgA1.ToString());

        await using var command = new NpgsqlCommand(
            "UPDATE tenant_settings SET organization_id = @org WHERE key = 'tz'",
            (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        command.Parameters.AddWithValue("org", TenancySchemaFixture.OrgA1);

        var act = async () => await command.ExecuteNonQueryAsync();

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.MessageText.Should().Contain("immutable after insert");
    }

    [Fact]
    public async Task TheOwnerIsDeniedOnThePlatformScopedTable()
    {
        // platform_host_to_tenant's policies are role-qualified TO learnstack_app,
        // so NO policy applies to the owner — and under FORCE that is a denial
        // rather than a bypass. Rows arrive through learnstack_app under tenant
        // context, or through learnstack_platform. Stated as a test because it
        // reads like a mistake and is the design.
        //
        // The BYPASSRLS role is read first, and that is what makes this an
        // assertion rather than a tautology: the earlier version of this case read
        // only the owner, on a table the fixture never populated, and passed with
        // every policy dropped and row security disabled.
        await using var platform = await PostgresFixture.OpenAsync(_schema.Postgres.PlatformConnectionString);
        await using var truth = new NpgsqlCommand(
            "SELECT count(*) FROM platform_host_to_tenant", (NpgsqlConnection)platform);

        (await truth.ExecuteScalarAsync()).Should().Be(2L, "the fixture seeds one mapping per tenant");

        await using var owner = await PostgresFixture.OpenAsync(_schema.Postgres.MigrationConnectionString);
        await using var denied = new NpgsqlCommand(
            "SELECT count(*) FROM platform_host_to_tenant", (NpgsqlConnection)owner);

        (await denied.ExecuteScalarAsync()).Should().Be(0L,
            "no policy is qualified to the owner, and FORCE turns that into a denial");
    }

    [Fact]
    public async Task TheResolvingHostAdmitsExactlyItsOwnRowBeforeAnyTenantContext()
    {
        // The read IHostToTenantResolver actually performs: no tenant context yet,
        // because reading this row is how the tenant is determined. The policy
        // admits the announced host and nothing else, so the wide SELECT clause is
        // wide by exactly one row.
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SetSettingAsync(connection, transaction, "app.resolving_host", TenancySchemaFixture.HostA);

        await using var command = new NpgsqlCommand(
            "SELECT string_agg(host, ',' ORDER BY host) FROM platform_host_to_tenant",
            (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);

        (await command.ExecuteScalarAsync()).Should().Be(TenancySchemaFixture.HostA);
    }

    [Fact]
    public async Task ASoftDeletedDomainReleasesItsHostToAnotherTenant()
    {
        // ux_tenant_domains_host is partial on `deleted_at IS NULL`. Without the
        // predicate a released claim keeps the hostname for every other tenant
        // forever — and the error crosses a tenant boundary, because PostgreSQL
        // enforces unique indexes with row security bypassed, so it doubles as an
        // oracle for a row the second tenant cannot see.
        //
        // One transaction, rolled back: the two GUC assignments are transaction
        // local, so switching tenants inside it is legal and the fixture's row
        // counts are left as the other cases expect them.
        const string Host = "released.example.com";

        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();

        await SetTenantAsync(connection, transaction, TenancySchemaFixture.TenantA);
        await ExecuteAsync(connection, transaction,
            """
            INSERT INTO tenant_domains
                (id, tenant_id, host, kind, status, verification_attempts, created_at, created_by, row_version)
            VALUES (uuidv7(), @tenant, @host, 'Custom', 'Requested', 0, now(), @actor, 0)
            """,
            ("tenant", TenancySchemaFixture.TenantA), ("host", Host), ("actor", TenancySchemaFixture.Actor));

        await ExecuteAsync(connection, transaction,
            "UPDATE tenant_domains SET deleted_at = now(), deleted_by = @actor WHERE host = @host",
            ("actor", TenancySchemaFixture.Actor), ("host", Host));

        await SetTenantAsync(connection, transaction, TenancySchemaFixture.TenantB);
        var reclaim = async () => await ExecuteAsync(connection, transaction,
            """
            INSERT INTO tenant_domains
                (id, tenant_id, host, kind, status, verification_attempts, created_at, created_by, row_version)
            VALUES (uuidv7(), @tenant, @host, 'Custom', 'Requested', 0, now(), @actor, 0)
            """,
            ("tenant", TenancySchemaFixture.TenantB), ("host", Host), ("actor", TenancySchemaFixture.Actor));

        await reclaim.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EveryMappedIdentifierIsSnakeCase()
    {
        // Every policy predicate, every GRANT and every index name in Database
        // Standards is written against snake_case identifiers, so one PascalCase
        // column is a column the policy does not mention and the grant does not
        // cover. Swept over the catalogue for the same reason the row-security
        // sweep is: a table missing from a hand-written list is a table nobody
        // checks.
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.MigrationConnectionString);

        var scanned = await ReadStringsAsync(connection, TableCatalogueQuery);
        scanned.Should().Contain(TenancySchemaFixture.KnownTables);

        var offenders = await ReadStringsAsync(connection,
            $"""
             SELECT c.relname || '.' || a.attname
             FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid
             WHERE c.oid IN ({TableCatalogueOids})
               AND a.attnum > 0 AND NOT a.attisdropped
               AND a.attname <> lower(a.attname)
             """);

        offenders.Should().BeEmpty();
    }

    [Fact]
    public async Task TheGrantMatrixIsExactlyWhatTheMigrationWrote()
    {
        // There is no ALTER DEFAULT PRIVILEGES, so every grant is one the migration
        // wrote. All three grantees are asserted, not just the application role:
        // BYPASSRLS bypasses policies and not GRANTs, so for learnstack_platform
        // and learnstack_outbox_admin this matrix is the whole of the bound, and a
        // widened grant on either is invisible in any other assertion.
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.MigrationConnectionString);

        var grants = await ReadStringsAsync(connection,
            """
            SELECT grantee || ' ' || table_name || ' ' || string_agg(privilege_type, ',' ORDER BY privilege_type)
            FROM information_schema.role_table_grants
            WHERE table_schema = 'public'
              AND grantee IN ('learnstack_app', 'learnstack_platform', 'learnstack_outbox_admin')
            GROUP BY grantee, table_name
            """);

        grants.Should().BeEquivalentTo(
        [
            "learnstack_app tenants INSERT,SELECT,UPDATE",
            "learnstack_app organizations DELETE,INSERT,SELECT,UPDATE",
            "learnstack_app tenant_domains DELETE,INSERT,SELECT,UPDATE",
            "learnstack_app tenant_locales DELETE,INSERT,SELECT,UPDATE",
            "learnstack_app tenant_settings DELETE,INSERT,SELECT,UPDATE",
            "learnstack_app tenant_feature_flags DELETE,INSERT,SELECT,UPDATE",
            "learnstack_app platform_entitlement_cache INSERT,SELECT,UPDATE",
            "learnstack_app platform_host_to_tenant DELETE,INSERT,SELECT,UPDATE",
            "learnstack_platform tenants DELETE,INSERT,SELECT,UPDATE",
            "learnstack_platform organizations DELETE,INSERT,SELECT,UPDATE",
            "learnstack_platform tenant_domains SELECT",
            "learnstack_platform tenant_locales SELECT",
            "learnstack_platform tenant_settings SELECT",
            "learnstack_platform tenant_feature_flags DELETE,INSERT,SELECT,UPDATE",
            "learnstack_platform platform_entitlement_cache DELETE,SELECT",
            "learnstack_platform platform_host_to_tenant DELETE,INSERT,SELECT,UPDATE",
        ],
        "learnstack_outbox_admin owns nothing in the tenancy schema, and every other "
        + "grant is one line of the migration's matrix");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Every ordinary table in schema <c>public</c>, minus EF's history table.
    /// </summary>
    /// <remarks>
    /// The only name written down, and it is written down because it is the one
    /// table that is <b>not</b> part of the schema under test: it carries
    /// <c>MigrationId</c> and <c>ProductVersion</c>, both PascalCase, and no row
    /// security by design.
    /// </remarks>
    private const string TableCatalogueOids =
        """
        SELECT oid FROM pg_class
        WHERE relnamespace = 'public'::regnamespace AND relkind = 'r'
          AND relname NOT LIKE '\_\_ef%'
        """;

    private const string TableCatalogueQuery =
        """
        SELECT relname FROM pg_class
        WHERE relnamespace = 'public'::regnamespace AND relkind = 'r'
          AND relname NOT LIKE '\_\_ef%'
        ORDER BY relname
        """;

    private static async Task<Dictionary<string, long>> CountEveryTableAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction? transaction)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var table in await ReadStringsAsync(connection, TableCatalogueQuery, transaction))
        {
            // The table name is a pg_class.relname read from the same connection
            // moments earlier, not caller input; there is no bind parameter for an
            // identifier, and quote_ident is what makes the interpolation safe.
            await using var command = new NpgsqlCommand(
                $"SELECT count(*) FROM {Quote(table)}",
                (NpgsqlConnection)connection, (NpgsqlTransaction?)transaction);

            counts[table] = (long)(await command.ExecuteScalarAsync())!;
        }

        return counts;
    }

    private static string Quote(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static async Task<List<string>> ReadStringsAsync(
        System.Data.Common.DbConnection connection,
        string sql,
        System.Data.Common.DbTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand(
            sql, (NpgsqlConnection)connection, (NpgsqlTransaction?)transaction);

        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static async Task ExecuteAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(
            sql, (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }


        await command.ExecuteNonQueryAsync();
    }

    private static Task SetTenantAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        Guid tenantId)
        => SetSettingAsync(connection, transaction, "app.tenant_id", tenantId.ToString());

    private static async Task SetSettingAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        string name,
        string value)
    {
        // set_config(..., true) rather than SET LOCAL: PostgreSQL's SET takes no
        // bind parameter — `SET LOCAL x = $1` is a syntax error, measured — and the
        // third argument `true` is what makes it transaction-local. The transaction
        // is passed explicitly because that locality is the whole point: a setting
        // applied to the wrong transaction is applied to nothing.
        await using var command = new NpgsqlCommand(
            "SELECT set_config(@name, @value, true)",
            (NpgsqlConnection)connection,
            (NpgsqlTransaction)transaction);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("value", value);
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// Applies the tenancy migration once and seeds <b>every</b> table for both
/// tenants.
/// </summary>
/// <remarks>
/// <para>
/// A count assertion against an empty table passes whether or not the policy that
/// should have emptied it exists, so a partial seed silently turns isolation cases
/// into tautologies. Every one of the eight tables therefore carries rows for
/// tenant A and tenant B, and tenant A carries a second organization so the
/// organization half of the template has a sibling row to hide.
/// </para>
/// <para>
/// Most of the seed runs as <c>learnstack_migration</c> inside a transaction that
/// sets <c>app.tenant_id</c> — the only way to insert, since every table's
/// <c>WITH CHECK</c> is live from the moment the migration finishes.
/// <c>platform_host_to_tenant</c> is the exception: its policies are qualified
/// <c>TO learnstack_app</c>, so the owner is denied on it and its rows go in
/// through the application role.
/// </para>
/// </remarks>
public sealed class TenancySchemaFixture : IAsyncLifetime
{
    public static readonly Guid TenantA = Guid.Parse("11111111-1111-7111-8111-111111111111");
    public static readonly Guid TenantB = Guid.Parse("22222222-2222-7222-8222-222222222222");
    public static readonly Guid OrgA1 = Guid.Parse("aaaaaaaa-1111-7111-8111-111111111111");
    public static readonly Guid OrgA2 = Guid.Parse("aaaaaaaa-2222-7222-8222-222222222222");
    public static readonly Guid OrgB1 = Guid.Parse("bbbbbbbb-1111-7111-8111-111111111111");
    public static readonly Guid Actor = Guid.Parse("00000000-0000-7000-8000-000000000001");

    public const string HostA = "alpha.example.com";
    public const string HostB = "beta.example.com";

    /// <summary>
    /// The eight tables this migration creates, used only to prove that a
    /// catalogue sweep read something.
    /// </summary>
    /// <remarks>
    /// Not an inclusion list: no query filters on it. It exists so a sweep that
    /// silently matched nothing fails instead of passing, which is the other way a
    /// structural assertion can prove nothing.
    /// </remarks>
    public static readonly string[] KnownTables =
    [
        "tenants", "organizations", "tenant_domains", "tenant_locales",
        "tenant_settings", "tenant_feature_flags",
        "platform_entitlement_cache", "platform_host_to_tenant",
    ];

    /// <summary>What tenant A sees with its tenant context set and no organization scope.</summary>
    public static readonly Dictionary<string, long> RowsVisibleToTenantA = new(StringComparer.Ordinal)
    {
        ["tenants"] = 1,
        ["organizations"] = 2,
        ["tenant_domains"] = 1,
        ["tenant_locales"] = 1,
        // Three rows exist; two are organization-scoped and invisible without
        // app.organization_id. Org_X_cannot_read_Org_Y_within_TenantA reads those.
        ["tenant_settings"] = 1,
        ["tenant_feature_flags"] = 1,
        ["platform_entitlement_cache"] = 1,
        ["platform_host_to_tenant"] = 1,
    };

    /// <summary>What tenant B sees with its tenant context set.</summary>
    public static readonly Dictionary<string, long> RowsVisibleToTenantB = new(StringComparer.Ordinal)
    {
        ["tenants"] = 1,
        ["organizations"] = 1,
        ["tenant_domains"] = 1,
        ["tenant_locales"] = 1,
        ["tenant_settings"] = 1,
        ["tenant_feature_flags"] = 1,
        ["platform_entitlement_cache"] = 1,
        ["platform_host_to_tenant"] = 1,
    };

    public PostgresFixture Postgres { get; } = new();

    public async Task InitializeAsync()
    {
        await Postgres.InitializeAsync();

        var options = new DbContextOptionsBuilder<TenancyDbContext>()
            .UseNpgsql(Postgres.MigrationConnectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__ef_migrations_history_tenancy"))
            .Options;

        await using (var context = new TenancyDbContext(options))
        {
            await context.Database.MigrateAsync();
        }

        await SeedAsync();
    }

    public async Task DisposeAsync() => await Postgres.DisposeAsync();

    private async Task SeedAsync()
    {
        await using (var owner = await PostgresFixture.OpenAsync(Postgres.MigrationConnectionString))
        {
            await using var command = new NpgsqlCommand(TenantRowsSql, (NpgsqlConnection)owner);
            await command.ExecuteNonQueryAsync();
        }

        // platform_host_to_tenant only: its four policies are role-qualified TO
        // learnstack_app, so under FORCE the owner is denied on it.
        await using var app = await PostgresFixture.OpenAsync(Postgres.AppConnectionString);
        await using var mappings = new NpgsqlCommand(HostMappingsSql, (NpgsqlConnection)app);
        await mappings.ExecuteNonQueryAsync();
    }

    private const string TenantRowsSql =
        """
        BEGIN;
        SET LOCAL app.tenant_id = '11111111-1111-7111-8111-111111111111';

        INSERT INTO tenants (id, slug, display_name, status, created_at, created_by, row_version)
        VALUES ('11111111-1111-7111-8111-111111111111','alpha','Alpha','Trial', now(),
                '00000000-0000-7000-8000-000000000001', 0);

        INSERT INTO organizations (id, tenant_id, slug, display_name, status, created_at, created_by, row_version)
        VALUES ('aaaaaaaa-1111-7111-8111-111111111111','11111111-1111-7111-8111-111111111111',
                'main','Main','Active', now(), '00000000-0000-7000-8000-000000000001', 0),
               ('aaaaaaaa-2222-7222-8222-222222222222','11111111-1111-7111-8111-111111111111',
                'branch','Branch','Active', now(), '00000000-0000-7000-8000-000000000001', 0);

        UPDATE tenants SET default_organization_id = 'aaaaaaaa-1111-7111-8111-111111111111'
        WHERE id = '11111111-1111-7111-8111-111111111111';

        INSERT INTO tenant_domains
            (id, tenant_id, host, kind, status, verification_attempts, created_at, created_by, row_version)
        VALUES (uuidv7(),'11111111-1111-7111-8111-111111111111','alpha.example.com','Subdomain','Verified',
                0, now(), '00000000-0000-7000-8000-000000000001', 0);

        INSERT INTO tenant_locales (tenant_id, locale, is_default, is_enabled, sort)
        VALUES ('11111111-1111-7111-8111-111111111111','tr-TR', true, true, 0);

        INSERT INTO tenant_settings (id, tenant_id, organization_id, key, value, created_at, created_by, row_version)
        VALUES (uuidv7(),'11111111-1111-7111-8111-111111111111', NULL,
                'tz', '"Europe/Istanbul"', now(), '00000000-0000-7000-8000-000000000001', 0);

        -- One organization at a time, because the org-scoped WITH CHECK admits a
        -- row only under its own organization's context. Writing both in one
        -- statement is exactly what the guard refuses, and the seed is the first
        -- place that shows it.
        SET LOCAL app.organization_id = 'aaaaaaaa-1111-7111-8111-111111111111';
        INSERT INTO tenant_settings (id, tenant_id, organization_id, key, value, created_at, created_by, row_version)
        VALUES (uuidv7(),'11111111-1111-7111-8111-111111111111','aaaaaaaa-1111-7111-8111-111111111111',
                'theme', '"main"', now(), '00000000-0000-7000-8000-000000000001', 0);

        SET LOCAL app.organization_id = 'aaaaaaaa-2222-7222-8222-222222222222';
        INSERT INTO tenant_settings (id, tenant_id, organization_id, key, value, created_at, created_by, row_version)
        VALUES (uuidv7(),'11111111-1111-7111-8111-111111111111','aaaaaaaa-2222-7222-8222-222222222222',
                'theme', '"branch"', now(), '00000000-0000-7000-8000-000000000001', 0);

        INSERT INTO tenant_feature_flags (tenant_id, key, value, updated_by)
        VALUES ('11111111-1111-7111-8111-111111111111','live-classroom','true',
                '00000000-0000-7000-8000-000000000001');

        INSERT INTO platform_entitlement_cache
            (tenant_id, plan_code, features, limits, compliance, valid_until, source)
        VALUES ('11111111-1111-7111-8111-111111111111','pro','{}','{}','{}',
                now() + interval '30 days','null-provider');
        COMMIT;

        BEGIN;
        SET LOCAL app.tenant_id = '22222222-2222-7222-8222-222222222222';

        INSERT INTO tenants (id, slug, display_name, status, created_at, created_by, row_version)
        VALUES ('22222222-2222-7222-8222-222222222222','beta','Beta','Trial', now(),
                '00000000-0000-7000-8000-000000000001', 0);

        INSERT INTO organizations (id, tenant_id, slug, display_name, status, created_at, created_by, row_version)
        VALUES ('bbbbbbbb-1111-7111-8111-111111111111','22222222-2222-7222-8222-222222222222',
                'main','Main','Active', now(), '00000000-0000-7000-8000-000000000001', 0);

        UPDATE tenants SET default_organization_id = 'bbbbbbbb-1111-7111-8111-111111111111'
        WHERE id = '22222222-2222-7222-8222-222222222222';

        INSERT INTO tenant_domains
            (id, tenant_id, host, kind, status, verification_attempts, created_at, created_by, row_version)
        VALUES (uuidv7(),'22222222-2222-7222-8222-222222222222','beta.example.com','Subdomain','Verified',
                0, now(), '00000000-0000-7000-8000-000000000001', 0);

        INSERT INTO tenant_locales (tenant_id, locale, is_default, is_enabled, sort)
        VALUES ('22222222-2222-7222-8222-222222222222','en-US', true, true, 0);

        INSERT INTO tenant_settings (id, tenant_id, organization_id, key, value, created_at, created_by, row_version)
        VALUES (uuidv7(),'22222222-2222-7222-8222-222222222222', NULL,
                'beta-only', '"visible to beta alone"', now(),
                '00000000-0000-7000-8000-000000000001', 0);

        INSERT INTO tenant_feature_flags (tenant_id, key, value, updated_by)
        VALUES ('22222222-2222-7222-8222-222222222222','live-classroom','false',
                '00000000-0000-7000-8000-000000000001');

        INSERT INTO platform_entitlement_cache
            (tenant_id, plan_code, features, limits, compliance, valid_until, source)
        VALUES ('22222222-2222-7222-8222-222222222222','free','{}','{}','{}',
                now() + interval '30 days','null-provider');
        COMMIT;
        """;

    private const string HostMappingsSql =
        """
        BEGIN;
        SET LOCAL app.tenant_id = '11111111-1111-7111-8111-111111111111';
        INSERT INTO platform_host_to_tenant (host, tenant_id, organization_id, is_active, is_publicly_live)
        VALUES ('alpha.example.com','11111111-1111-7111-8111-111111111111',
                'aaaaaaaa-1111-7111-8111-111111111111', true, true);
        COMMIT;

        BEGIN;
        SET LOCAL app.tenant_id = '22222222-2222-7222-8222-222222222222';
        INSERT INTO platform_host_to_tenant (host, tenant_id, organization_id, is_active, is_publicly_live)
        VALUES ('beta.example.com','22222222-2222-7222-8222-222222222222',
                'bbbbbbbb-1111-7111-8111-111111111111', true, true);
        COMMIT;
        """;
}
