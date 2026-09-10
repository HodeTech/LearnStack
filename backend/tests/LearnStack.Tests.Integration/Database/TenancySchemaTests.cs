using FluentAssertions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The applied schema — both chains — its Row Level Security policies and its
/// grants, asserted against a real PostgreSQL with the four roles provisioned.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every data case connects as <c>learnstack_app</c>.</b> A test that connected
/// as the owner — or as either <c>BYPASSRLS</c> role — would pass with every policy
/// inert and prove nothing, which is the failure mode
/// <see href="../../../../docs/decisions/0003-tenant-isolation-defense-in-depth.md">ADR-0003
/// Amendment 3</see> names by hand. The cases that connect as
/// <c>learnstack_migration</c> read <c>pg_catalog</c> / <c>information_schema</c>
/// rather than data; the one exception,
/// <see cref="TheOwnerIsDeniedOnThePlatformScopedTable"/>, asserts a denial only
/// the owner can experience and reads the truth through the platform role first.
/// </para>
/// <para>
/// <b>Every table carries rows for both tenants, and the sweeps enumerate the
/// catalogue.</b> A count assertion against an empty table passes whether or not
/// the policy exists, and a sweep over a hand-written list of names fails open for
/// the table nobody added to it — both were shipped, and both are recorded in
/// <see cref="SchemaFixture"/>'s remarks.
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
[Collection(SharedSchema.Name)]
public sealed class TenancySchemaTests
{
    private readonly SchemaFixture _schema;

    public TenancySchemaTests(SchemaFixture schema) => _schema = schema;

    [Fact]
    public async Task EveryTableEnablesAndForcesRowLevelSecurity()
    {
        // The catalogue itself, not a list of names kept beside it. An inclusion
        // list fails open for the table somebody adds and forgets to add here —
        // reproduced twice: a table created outside the array was invisible to
        // this sweep and to the snake-case sweep at once, and the sweeps
        // themselves ran on a fixture carrying only one of the two chains, which
        // narrowed every one of them to eight of the ten tables.
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.MigrationConnectionString);

        var scanned = await SchemaQueries.ReadStringsAsync(connection, SchemaQueries.TableNames);
        scanned.Should().Contain(SchemaFixture.KnownTables,
            "the sweep must be reading the whole applied schema, not an empty result set");

        var unprotected = await SchemaQueries.ReadStringsAsync(connection,
            $"""
             SELECT relname FROM pg_class
             WHERE oid IN ({SchemaQueries.TableOids}) AND NOT (relrowsecurity AND relforcerowsecurity)
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

        // The sweep proves it read the catalogue before it reports nothing wrong:
        // a schema with no policies at all satisfies "no two permissive policies".
        (await SchemaQueries.CountAsync(connection,
                "SELECT count(*) FROM pg_policies WHERE schemaname = 'public'"))
            .Should().BeGreaterThan(0, "a policy sweep over no policies passes vacuously");

        var offenders = await SchemaQueries.ReadStringsAsync(connection,
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
        // the ten holds rows for both tenants, so a table whose policy lost its
        // predicate answers with a non-zero count here.
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);

        var counts = await SchemaQueries.CountEveryTableAsync(connection, transaction: null);

        // ONE declared exception, and the sweep still enumerates the catalogue. It is not
        // taught with a hand-written inclusion list of names: such a list fails open, which
        // is how a second permissive policy on outbox_messages once passed the whole suite.
        // What changes is the EXPECTATION, not the reach.
        counts.Where(entry => entry.Key != KillswitchTable)
            .Should().OnlyContain(entry => entry.Value == 0,
                "with no app.tenant_id every policy predicate is NULL, which is false");

        counts.Keys.Should().Contain(SchemaFixture.KnownTables);

        // The positive half, which nothing asserted before: a killswitch is one
        // platform-wide switch with no tenant term to isolate, so USING (true) exists to
        // guarantee learnstack_app reads ALL of it with no context. A sweep that merely
        // excused the table would pass against a policy that returned nothing — and a
        // killswitch nobody can read is a killswitch that fails open.
        counts[KillswitchTable].Should().BeGreaterThan(0,
            "the switch is global by construction, so hiding it from the role that must "
            + "honour it would only fail open");
    }

    /// <summary>The one table for which "no tenant context implies zero rows" is false.</summary>
    private const string KillswitchTable = "platform_killswitches";

    [Fact]
    public async Task Exactly_One_Policy_In_The_Schema_Reads_Unconditionally()
    {
        // The companion that stops the exception above from widening. The sweep excuses
        // one table by name; this pins the SET of unconditional policies to exactly that
        // one, read from pg_policies rather than from this file's opinion — so a second
        // USING (true) landing anywhere in `public` fails here even though the sweep would
        // now excuse nothing extra.
        //
        // This is the assertion Database Standards § platform_killswitches asks for, and
        // it is the half a name-based inclusion list can never provide.
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);

        await using var command = new NpgsqlCommand(
            """
            SELECT tablename || '.' || policyname
            FROM pg_policies
            WHERE schemaname = 'public' AND qual = 'true'
            ORDER BY 1
            """,
            (NpgsqlConnection)connection);

        var unconditional = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            unconditional.Add(reader.GetString(0));
        }

        unconditional.Should().BeEquivalentTo(
            ["platform_killswitches.platform_killswitches_read"],
            "exactly one policy reads unconditionally, and ADR-0045 § 5 is where that was "
            + "decided");
    }

    [Fact]
    public async Task Tenant_A_cannot_read_Tenant_B_data()
    {
        // Both tenants hold rows in all ten tables, and tenant A holds a second
        // organization, so every count here is a number only the correct policy
        // produces. A policy widened to USING (true) — the mutation that survived
        // the first version of this suite — shows up as A seeing B's rows.
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);
            var seenByA = await SchemaQueries.CountEveryTableAsync(connection, transaction);

            seenByA.Should().BeEquivalentTo(SchemaFixture.RowsVisibleToTenantA);
        }

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantB);
            var seenByB = await SchemaQueries.CountEveryTableAsync(connection, transaction);

            seenByB.Should().BeEquivalentTo(SchemaFixture.RowsVisibleToTenantB);
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
        await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);

        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM tenant_settings WHERE key = 'beta-only' AND organization_id IS NULL",
            (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);

        (await command.ExecuteScalarAsync()).Should().Be(0L,
            "tenant B's tenant-wide setting must not be visible to tenant A");
    }

    /// <summary>
    /// One foreign-tenant write per table whose policy carries a
    /// <c>WITH CHECK</c>, keyed by table name.
    /// </summary>
    /// <remarks>
    /// Hand-written because each table needs its own column list, and audited
    /// against the database by
    /// <see cref="Every_WithCheck_Policy_Has_A_Foreign_Write_Case"/> — which reads
    /// `pg_policies` and fails when a table has a `WITH CHECK` and no case here.
    /// The version this replaced named three tables and claimed in a comment to
    /// cover "every table whose policy carries a WITH CHECK". It covered three of
    /// nine: `WITH CHECK (true)` on `tenant_locales` passed the entire suite.
    /// </remarks>
    public static readonly Dictionary<string, string> ForeignTenantWrites = new(StringComparer.Ordinal)
    {
        // Self-keyed: the tenant term is on `id`, so a new id is already foreign.
        ["tenants"] =
            """
            INSERT INTO tenants (id, slug, display_name, status, created_at, created_by, row_version)
            VALUES (uuidv7(), 'sneak', 'Sneak', 'Active', now(), @actor, 0)
            """,
        ["organizations"] =
            """
            INSERT INTO organizations (id, tenant_id, slug, display_name, status, created_at, created_by, row_version)
            VALUES (uuidv7(), @foreign, 'sneak', 'Sneak', 'Active', now(), @actor, 0)
            """,
        ["tenant_domains"] =
            """
            INSERT INTO tenant_domains (id, tenant_id, host, kind, status, created_at, created_by, row_version)
            VALUES (uuidv7(), @foreign, 'sneak.example.test', 'Custom', 'Requested', now(), @actor, 0)
            """,
        ["tenant_locales"] =
            """
            INSERT INTO tenant_locales (tenant_id, locale, is_default)
            VALUES (@foreign, 'zz-Sneak', false)
            """,
        ["tenant_feature_flags"] =
            """
            INSERT INTO tenant_feature_flags (tenant_id, key, value, updated_by)
            VALUES (@foreign, 'sneak', '{}', @actor)
            """,
        ["tenant_settings"] =
            """
            INSERT INTO tenant_settings (id, tenant_id, key, value, created_at, created_by, row_version)
            VALUES (uuidv7(), @foreign, 'sneak', '{}', now(), @actor, 0)
            """,
        ["platform_entitlement_cache"] =
            """
            INSERT INTO platform_entitlement_cache
                (tenant_id, plan_code, features, limits, compliance, valid_until, source)
            VALUES (@foreign, 'sneak', '{}', '{}', '{}', now() + interval '1 day', 'null-provider')
            """,
        // Platform-scoped: the read is widened by app.resolving_host, the write is
        // not. This is the statement that proves the widening did not leak.
        ["platform_host_to_tenant"] =
            """
            INSERT INTO platform_host_to_tenant (host, tenant_id, is_active, is_publicly_live)
            VALUES ('sneak.example.test', @foreign, true, true)
            """,
        // Where the consequence is loudest: an event enqueued into another
        // tenant's stream is delivered under that tenant's context by the
        // dispatcher.
        ["outbox_messages"] =
            """
            INSERT INTO outbox_messages (tenant_id, correlation_id, type, topic, partition_key, payload)
            VALUES (@foreign, '00-sneak-span-01', 'Sneak', 'learnstack.tenancy.tenant', 'k', '{}')
            """,
        ["idempotency_keys"] =
            """
            INSERT INTO idempotency_keys (tenant_id, key, fingerprint, claim_token, state, expires_at)
            VALUES (@foreign, 'sneak-key-01', 'fp', uuidv7(), 'in_flight', now() + interval '5 minutes')
            """,
        // The Customization chain. A content type or a taxonomy written into
        // another tenant is a shape that tenant's own renderers will read and
        // that no page can question — the content is the tenant's declaration of
        // what its data means.
        ["tenant_content_types"] =
            """
            INSERT INTO tenant_content_types
                (id, tenant_id, key, schema_version, schema_revision, status, display_name,
                 json_schema, renderer_key, created_at, created_by, row_version)
            VALUES (uuidv7(), @foreign, 'sneak', 1, 0, 'Draft', '{"en":"Sneak"}',
                    '{"type":"object"}', 'default-card', now(), @actor, 0)
            """,
        ["tenant_level_taxonomies"] =
            """
            INSERT INTO tenant_level_taxonomies
                (id, tenant_id, key, schema_version, schema_revision, status, display_name,
                 created_at, created_by, row_version)
            VALUES (uuidv7(), @foreign, 'sneak', 1, 0, 'Draft', '{"en":"Sneak"}',
                    now(), @actor, 0)
            """,
        // Named against the seeded parent in the *foreign* tenant, so the statement
        // is a genuine cross-tenant write rather than one the foreign key would
        // have refused on its own.
        ["tenant_level_taxonomy_items"] =
            """
            INSERT INTO tenant_level_taxonomy_items
                (tenant_id, taxonomy_key, schema_version, key, display_name, sort)
            VALUES (@foreign, 'proficiency', 1, 'sneak', '{"en":"Sneak"}', 99)
            """,
        // The counter every cache key embeds: advancing another tenant's
        // generation invalidates its caches, and holding it back serves them stale.
        ["customization_generations"] =
            """
            INSERT INTO customization_generations (tenant_id, generation)
            VALUES (@foreign, 99)
            """,
        // The Audit chain. A row written into another tenant's log is worse than a
        // missing one: it is a false record of what that tenant did, in the table an
        // investigator treats as authoritative, and no later write can correct it —
        // tenant_id sits outside learnstack_platform's column-restricted UPDATE grant.
        ["audit_log"] =
            """
            INSERT INTO audit_log
                (id, tenant_id, module, operation, operation_type, operation_class,
                 outcome, timestamp)
            VALUES (uuidv7(), @foreign, 'tenancy', 'tenancy.tenant.provision',
                    'Create', 'Must', 'success', now())
            """,
    };

    /// <summary>
    /// The same, for the one table no runtime role may write at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>audit_config</c> carries a <c>WITH CHECK</c> and, in this packet, no writer:
    /// both runtime roles hold <c>SELECT</c> and nothing more, because the editor that
    /// authors an override lands with the Studio in Phase 06
    /// (<see href="../../../../docs/standards/05-database.md">Database Standards
    /// § GRANT matrix</see>). Running its foreign write as <c>learnstack_app</c> would
    /// pass on the absent privilege — also <c>42501</c> — and prove nothing about the
    /// policy, which is the shape of test this suite has already caught twice.
    /// </para>
    /// <para>
    /// <b>So it runs as the owner, and that is sound here rather than a precedent.</b>
    /// The standing rule — never run an isolation test as the owner or a
    /// <c>BYPASSRLS</c> role — exists because such a test passes when the policies are
    /// inert. This one is the opposite shape: it asserts a <b>refusal</b>, and under
    /// <c>FORCE ROW LEVEL SECURITY</c> the owner is bound by the policy like anyone
    /// else (measured on PostgreSQL 18.6). Drop <c>FORCE</c> and the write succeeds and
    /// this case fails. A <c>SELECT</c>-count assertion run as the owner would have the
    /// vacuous shape the rule is about; this one cannot.
    /// </para>
    /// </remarks>
    public static readonly Dictionary<string, string> OwnerForeignTenantWrites =
        new(StringComparer.Ordinal)
        {
            ["audit_config"] =
                """
                INSERT INTO audit_config
                    (id, tenant_id, module, operation, is_enabled, created_at, created_by,
                     row_version)
                VALUES (uuidv7(), @foreign, 'tenancy', 'tenancy.tenant.provision', false,
                        now(), @actor, 0)
                """,
        };

    public static TheoryData<string> TablesWithAWithCheck()
    {
        var data = new TheoryData<string>();

        foreach (var table in ForeignTenantWrites.Keys)
        {
            data.Add(table);
        }

        return data;
    }

    public static TheoryData<string> TablesWithAWithCheckAndNoRuntimeWriter()
    {
        var data = new TheoryData<string>();

        foreach (var table in OwnerForeignTenantWrites.Keys)
        {
            data.Add(table);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(TablesWithAWithCheck))]
    public async Task Write_With_Foreign_TenantId_Is_Rejected_By_WithCheck(string table)
    {
        await ExpectForeignWriteRefusedAsync(
            ForeignTenantWrites[table], _schema.Postgres.AppConnectionString);
    }

    [Theory]
    [MemberData(nameof(TablesWithAWithCheckAndNoRuntimeWriter))]
    public async Task Owner_Write_With_Foreign_TenantId_Is_Rejected_By_WithCheck(string table)
    {
        await ExpectForeignWriteRefusedAsync(
            OwnerForeignTenantWrites[table], _schema.Postgres.MigrationConnectionString);
    }

    [Theory]
    // The other half of WITH CHECK: not inserting a foreign row, but handing an
    // owned one away. USING admits the row; WITH CHECK refuses the new value.
    [InlineData("UPDATE organizations SET tenant_id = @foreign WHERE tenant_id <> @foreign")]
    [InlineData("UPDATE tenant_settings SET tenant_id = @foreign WHERE tenant_id <> @foreign")]
    public async Task Reassigning_An_Owned_Row_To_Another_Tenant_Is_Rejected(string statement)
    {
        await ExpectForeignWriteRefusedAsync(statement, _schema.Postgres.AppConnectionString);
    }

    [Fact]
    public async Task Every_WithCheck_Policy_Has_A_Foreign_Write_Case()
    {
        // The catalogue is the authority, not this file. A table that gains a
        // WITH CHECK and no case above fails here rather than passing silently for
        // as long as nobody notices — which is exactly how six of the ten came to
        // be unexercised.
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.PlatformConnectionString);
        await using var command = new NpgsqlCommand(
            """
            SELECT DISTINCT tablename
            FROM pg_policies
            WHERE schemaname = 'public' AND with_check IS NOT NULL
            """, (NpgsqlConnection)connection);

        var guarded = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                guarded.Add(reader.GetString(0));
            }
        }

        guarded.Should().NotBeEmpty("the sweep must be reading the applied schema, not an empty result set");
        guarded.Should().BeEquivalentTo(
            ForeignTenantWrites.Keys.Concat(OwnerForeignTenantWrites.Keys),
            "every table whose policy constrains a write has a write that proves it, "
            + "and every case here names a table that still has one");
    }

    /// <summary>
    /// Runs a statement under tenant B's context, with tenant A as <c>@foreign</c>,
    /// and asserts the policy refuses it.
    /// </summary>
    /// <remarks>
    /// <c>learnstack_app</c> for every table it may write; the owner for the one it may
    /// not, where an absent privilege would answer <c>42501</c> before the policy did.
    /// See <see cref="OwnerForeignTenantWrites"/> for why that is not the vacuous shape
    /// the owner-role rule forbids.
    /// </remarks>
    private static async Task ExpectForeignWriteRefusedAsync(
        string statement, string connectionString)
    {
        await using var connection = await PostgresFixture.OpenAsync(connectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantB);

        await using var command = new NpgsqlCommand(
            statement, (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        command.Parameters.AddWithValue("foreign", SchemaFixture.TenantA);

        if (statement.Contains("@actor", StringComparison.Ordinal))
        {
            command.Parameters.AddWithValue("actor", SchemaFixture.Actor);
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
        // organizations; a session scoped to the first must not see the second's.
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);
        await SchemaQueries.SetSettingAsync(connection, transaction,
            "app.organization_id", SchemaFixture.OrgA1.ToString());

        await using var read = new NpgsqlCommand(
            "SELECT count(*) FROM tenant_settings WHERE organization_id = @other",
            (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        read.Parameters.AddWithValue("other", SchemaFixture.OrgA2);

        (await read.ExecuteScalarAsync()).Should().Be(0L,
            "organization A1 must not read organization A2's rows");
    }

    [Fact]
    public async Task TheTenantScopeHatchWidensReadsAndNeitherWrite()
    {
        // `app.scope = 'tenant'` is the cross-organization READ hatch, and the two
        // AS RESTRICTIVE guards are what stop it widening writes. Without this
        // case both guards could be deleted with the whole suite green: under an
        // ordinary organization-scoped session the base policy's own organization
        // term already refuses the sibling row, so the guards are never the reason
        // anything fails. Measured — with the hatch set and the delete guard
        // dropped, DELETE removed organization A2's row; with the write guard
        // dropped, an UPDATE reassigned it into the caller's own organization.
        //
        // No caller sets app.scope yet (ITenantContext has no scope member; Packet
        // 7 decides how it arrives), which is exactly why the guards need a test
        // now rather than when the first one does.
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);
        await SchemaQueries.SetSettingAsync(connection, transaction,
            "app.organization_id", SchemaFixture.OrgA1.ToString());
        await SchemaQueries.SetSettingAsync(connection, transaction, "app.scope", "tenant");

        await using (var read = new NpgsqlCommand(
            "SELECT count(*) FROM tenant_settings WHERE organization_id = @other",
            (NpgsqlConnection)connection, (NpgsqlTransaction)transaction))
        {
            read.Parameters.AddWithValue("other", SchemaFixture.OrgA2);
            (await read.ExecuteScalarAsync()).Should().Be(1L,
                "the hatch is what makes cross-organization reporting possible");
        }

        // Three writes, because the three gates fail differently. An in-place
        // UPDATE and a DELETE are refused by the restrictive guards; a re-parenting
        // UPDATE — stealing a sibling's row into the caller's own organization —
        // satisfies the base policy's WITH CHECK and is refused only by the write
        // guard's USING.
        await using (var update = new NpgsqlCommand(
            "UPDATE tenant_settings SET value = '\"hijacked\"' WHERE organization_id = @other",
            (NpgsqlConnection)connection, (NpgsqlTransaction)transaction))
        {
            update.Parameters.AddWithValue("other", SchemaFixture.OrgA2);
            (await update.ExecuteNonQueryAsync()).Should().Be(0,
                "tenant_settings_org_write_guard is restrictive and admits no sibling row");
        }

        await using (var steal = new NpgsqlCommand(
            "UPDATE tenant_settings SET organization_id = @mine WHERE organization_id = @other",
            (NpgsqlConnection)connection, (NpgsqlTransaction)transaction))
        {
            steal.Parameters.AddWithValue("mine", SchemaFixture.OrgA1);
            steal.Parameters.AddWithValue("other", SchemaFixture.OrgA2);
            (await steal.ExecuteNonQueryAsync()).Should().Be(0,
                "and the guard's USING is what refuses a row the WITH CHECK would have accepted");
        }

        await using (var delete = new NpgsqlCommand(
            "DELETE FROM tenant_settings WHERE organization_id = @other",
            (NpgsqlConnection)connection, (NpgsqlTransaction)transaction))
        {
            delete.Parameters.AddWithValue("other", SchemaFixture.OrgA2);
            (await delete.ExecuteNonQueryAsync()).Should().Be(0,
                "tenant_settings_org_delete_guard is the only gate DELETE has");
        }
    }

    [Fact]
    public async Task OrganizationIdIsImmutableAfterInsert()
    {
        // Tenant-wide to org-scoped: the NULL -> value move, which `<>` would miss
        // because `<>` is NULL when either side is null. The trigger is what refuses it,
        // and this case exists because no policy does.
        //
        // Run as a TENANT-scope session — no app.organization_id. Since ADR-0003
        // Amendment 4 the restrictive UPDATE guard refuses an organization-scoped session
        // the tenant-wide row outright, so attempting the move from one filters to zero
        // rows and the trigger never fires: the case would pass while testing nothing. A
        // tenant-scope session is the one that can still reach the row, which makes it
        // the one that can still attempt the move.
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);

        await using var command = new NpgsqlCommand(
            "UPDATE tenant_settings SET organization_id = @org WHERE key = 'tz'",
            (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        command.Parameters.AddWithValue("org", SchemaFixture.OrgA1);

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
        await SchemaQueries.SetSettingAsync(connection, transaction, "app.resolving_host", SchemaFixture.HostA);

        await using var command = new NpgsqlCommand(
            "SELECT string_agg(host, ',' ORDER BY host) FROM platform_host_to_tenant",
            (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);

        (await command.ExecuteScalarAsync()).Should().Be(SchemaFixture.HostA);
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

        await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);
        await SchemaQueries.ExecuteAsync(connection, transaction,
            """
            INSERT INTO tenant_domains
                (id, tenant_id, host, kind, status, verification_attempts, created_at, created_by, row_version)
            VALUES (uuidv7(), @tenant, @host, 'Custom', 'Requested', 0, now(), @actor, 0)
            """,
            ("tenant", SchemaFixture.TenantA), ("host", Host), ("actor", SchemaFixture.Actor));

        await SchemaQueries.ExecuteAsync(connection, transaction,
            "UPDATE tenant_domains SET deleted_at = now(), deleted_by = @actor WHERE host = @host",
            ("actor", SchemaFixture.Actor), ("host", Host));

        await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantB);
        var reclaim = async () => await SchemaQueries.ExecuteAsync(connection, transaction,
            """
            INSERT INTO tenant_domains
                (id, tenant_id, host, kind, status, verification_attempts, created_at, created_by, row_version)
            VALUES (uuidv7(), @tenant, @host, 'Custom', 'Requested', 0, now(), @actor, 0)
            """,
            ("tenant", SchemaFixture.TenantB), ("host", Host), ("actor", SchemaFixture.Actor));

        await reclaim.Should().NotThrowAsync();

        // The other half, and without it this case passes with the uniqueness dropped
        // entirely: `unique: false` on ux_tenant_domains_host leaves everything above
        // green, because nothing here ever asks for a CONFLICT. A live claim must still
        // block a second one — that index is the schema's only guarantee that two tenants
        // cannot hold the same hostname at once, and RLS hides the collision from both
        // sides, so nothing else would notice it was gone.
        const string Contested = "contested.example.com";

        await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);
        await SchemaQueries.ExecuteAsync(connection, transaction,
            """
            INSERT INTO tenant_domains
                (id, tenant_id, host, kind, status, verification_attempts, created_at, created_by, row_version)
            VALUES (uuidv7(), @tenant, @host, 'Custom', 'Requested', 0, now(), @actor, 0)
            """,
            ("tenant", SchemaFixture.TenantA), ("host", Contested), ("actor", SchemaFixture.Actor));

        await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantB);
        var contest = async () => await SchemaQueries.ExecuteAsync(connection, transaction,
            """
            INSERT INTO tenant_domains
                (id, tenant_id, host, kind, status, verification_attempts, created_at, created_by, row_version)
            VALUES (uuidv7(), @tenant, @host, 'Custom', 'Requested', 0, now(), @actor, 0)
            """,
            ("tenant", SchemaFixture.TenantB), ("host", Contested), ("actor", SchemaFixture.Actor));

        (await contest.Should().ThrowAsync<PostgresException>(
            "a hostname that is live for one tenant is not available to another"))
            .Which.SqlState.Should().Be("23505");
    }

    [Fact]
    public async Task An_Organization_Scoped_Session_Cannot_Write_A_Tenant_Wide_Row()
    {
        // Database Standards § Tenant-Owned and Organization-Scoped Tables: "a
        // tenant-scope reporting query may read across organizations, but NOTHING may
        // write outside its organization." A tenant-wide row belongs to no organization,
        // so an organization-scoped session writing one is writing outside its own.
        //
        // Both AS RESTRICTIVE guards used a bare `organization_id IS NULL` first arm,
        // which exists so a TENANT-scope session can write those rows — and admitted an
        // org-scoped one to them as well. Measured before ADR-0003 Amendment 5: a session
        // announcing tenant A and organization A1 rewrote tenant A's tenant-wide row.
        //
        // The refusal is silent by construction: a RESTRICTIVE USING clause on UPDATE
        // FILTERS the rows the statement may target rather than raising, so what this
        // asserts is zero rows affected and the value unchanged — not an exception.
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();

        await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);
        await SchemaQueries.ExecuteAsync(connection, transaction,
            "SELECT set_config('app.organization_id', @org, true)",
            ("org", SchemaFixture.OrgA1.ToString()));

        await using (var update = new NpgsqlCommand(
            """
            UPDATE tenant_settings SET value = '"hijacked"'
            WHERE tenant_id = @tenant AND organization_id IS NULL
            """,
            (NpgsqlConnection)connection,
            (NpgsqlTransaction)transaction))
        {
            update.Parameters.AddWithValue("tenant", SchemaFixture.TenantA);

            (await update.ExecuteNonQueryAsync()).Should().Be(0,
                "the tenant-wide row is outside this session's organization, so the "
                + "restrictive guard does not let the statement target it");
        }

        // And the row is still there, unchanged — so the zero above is the guard
        // filtering rather than the row being absent.
        await using (var read = new NpgsqlCommand(
            """
            SELECT count(*) FROM tenant_settings
            WHERE tenant_id = @tenant AND organization_id IS NULL AND value <> '"hijacked"'
            """,
            (NpgsqlConnection)connection,
            (NpgsqlTransaction)transaction))
        {
            read.Parameters.AddWithValue("tenant", SchemaFixture.TenantA);

            // Read under the same org-scoped session: the main policy's USING admits a
            // tenant-wide row to a reader, which is the asymmetry the standard states —
            // read across, write only your own.
            (await read.ExecuteScalarAsync()).Should().Be(1L);
        }

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task An_Entitlement_With_No_Scheduled_Expiry_Persists_As_Null()
    {
        // Packet 6 declared `valid_until` NOT NULL against a wire contract that makes
        // `expires_at` required AND nullable — so the only sanctioned writer would have
        // been structurally unable to persist what its own source sends. The Hub sends
        // null for every trial and perpetual licence, which is the cohort it creates
        // first, so this is the common row rather than an edge one
        // (ADR-0045 Amendment 1 § 2).
        //
        // A real INSERT as learnstack_app, not a read of information_schema: the column's
        // declared nullability is only half the claim, and the half that bites is whether
        // the row survives the policy's WITH CHECK on the way in.
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();

        await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantB);

        await using (var write = new NpgsqlCommand(
            """
            INSERT INTO platform_entitlement_cache
                (tenant_id, plan_code, features, limits, compliance, valid_until, source)
            VALUES (@tenant, 'perpetual', '{}', '{}', '{}', NULL, 'null-provider')
            ON CONFLICT (tenant_id) DO UPDATE SET valid_until = NULL, plan_code = 'perpetual'
            """,
            (NpgsqlConnection)connection,
            (NpgsqlTransaction)transaction))
        {
            write.Parameters.AddWithValue("tenant", SchemaFixture.TenantB);
            await write.ExecuteNonQueryAsync();
        }

        await using var read = new NpgsqlCommand(
            "SELECT valid_until FROM platform_entitlement_cache WHERE tenant_id = @tenant",
            (NpgsqlConnection)connection,
            (NpgsqlTransaction)transaction);
        read.Parameters.AddWithValue("tenant", SchemaFixture.TenantB);

        (await read.ExecuteScalarAsync()).Should().Be(DBNull.Value,
            "null is 'no scheduled expiry' and is never coerced to a sentinel — a "
            + "far-future date would silently become an expiry somebody has to explain");

        // Rolled back: this suite shares a schema with cases that compare exact row
        // counts, and the fixture's seeded row for this tenant is not this one.
        await transaction.RollbackAsync();
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

        var scanned = await SchemaQueries.ReadStringsAsync(connection, SchemaQueries.TableNames);
        scanned.Should().Contain(SchemaFixture.KnownTables);

        (await SchemaQueries.CountAsync(connection,
                $"""
                 SELECT count(*) FROM pg_attribute a
                 WHERE a.attrelid IN ({SchemaQueries.TableOids}) AND a.attnum > 0
                 """))
            .Should().BeGreaterThan(0, "a column sweep over no columns passes vacuously");

        var offenders = await SchemaQueries.ReadStringsAsync(connection,
            $"""
             SELECT c.relname || '.' || a.attname
             FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid
             WHERE c.oid IN ({SchemaQueries.TableOids})
               AND a.attnum > 0 AND NOT a.attisdropped
               AND a.attname <> lower(a.attname)
             """);

        offenders.Should().BeEmpty();
    }

    [Fact]
    public async Task Every_Foreign_Key_Has_A_Supporting_Index()
    {
        // Database Standards § Indexes: index every foreign key. Every foreign key
        // in this schema is ON DELETE RESTRICT except the one cascade the standard
        // sanctions — a child inside an aggregate boundary — so a parent delete
        // pays the child scan either way, to refuse or to cascade. Swept rather
        // than listed: the one that shipped without an index —
        // fk_organizations_reporting_parent — was missed precisely because nothing
        // swept.
        //
        // "Supporting" means one of two things, and both bound the scan:
        //
        //   an index whose LEADING columns are the constraint's columns, in order
        //   — a trailing match does not serve the scan, which is why the
        //   comparison is a prefix slice rather than a containment test; or
        //
        //   a UNIQUE index over a leading prefix of them. tenants' primary key is
        //   the case: fk_tenants_default_organization is composite on
        //   (id, default_organization_id), and a unique index on `id` alone
        //   already yields at most one candidate row, so the second column adds
        //   nothing an index could.
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.MigrationConnectionString);

        (await SchemaQueries.CountAsync(connection,
                $"""
                 SELECT count(*) FROM pg_constraint
                 WHERE contype = 'f' AND conrelid IN ({SchemaQueries.TableOids})
                 """))
            .Should().BeGreaterThan(0, "an index sweep over no foreign keys passes vacuously");

        var unindexed = await SchemaQueries.ReadStringsAsync(connection,
            """
            SELECT c.conname || ' on ' || c.conrelid::regclass::text
            FROM pg_constraint c
            WHERE c.contype = 'f'
              AND c.connamespace = 'public'::regnamespace
              AND NOT EXISTS (
                  SELECT 1 FROM pg_index i
                  WHERE i.indrelid = c.conrelid
                    AND (
                        (i.indkey::int2[])[0:cardinality(c.conkey) - 1] = c.conkey
                        OR (i.indisunique
                            AND i.indnkeyatts <= cardinality(c.conkey)
                            AND (i.indkey::int2[])[0:i.indnkeyatts - 1] = c.conkey[1:i.indnkeyatts])
                    )
              )
            ORDER BY 1
            """);

        unindexed.Should().BeEmpty("Standards 05 § Indexes: index every foreign key");
    }

    [Fact]
    public async Task TheGrantMatrixIsExactlyWhatTheMigrationsWrote()
    {
        // There is no ALTER DEFAULT PRIVILEGES, so every grant is one a migration
        // wrote. All three non-owner grantees are asserted, across every chain:
        // BYPASSRLS bypasses policies and not GRANTs, so for learnstack_platform
        // and learnstack_outbox_admin this matrix is the whole of the bound, and a
        // widened grant on either is invisible in any other assertion. Measured:
        // granting learnstack_app table-wide UPDATE on outbox_messages let a
        // handler mark every pending event processed — making them unpublishable —
        // while every other assertion in the suite stayed green.
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.MigrationConnectionString);

        var grants = await SchemaQueries.ReadStringsAsync(connection,
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
            // SELECT and nothing else. The read policy is USING (true), so the GRANT is
            // what bounds the role instead — a write privilege here would make the policy
            // the only thing standing between the application role and a platform-wide
            // switch, and there is no write policy for it to be.
            "learnstack_app platform_killswitches SELECT",
            "learnstack_app platform_host_to_tenant DELETE,INSERT,SELECT,UPDATE",
            "learnstack_app outbox_messages INSERT,SELECT",
            "learnstack_app idempotency_keys INSERT,SELECT,UPDATE",
            "learnstack_platform tenants DELETE,INSERT,SELECT,UPDATE",
            "learnstack_platform organizations DELETE,INSERT,SELECT,UPDATE",
            "learnstack_platform tenant_domains SELECT",
            "learnstack_platform tenant_locales SELECT",
            "learnstack_platform tenant_settings SELECT",
            "learnstack_platform tenant_feature_flags DELETE,INSERT,SELECT,UPDATE",
            "learnstack_platform platform_entitlement_cache DELETE,SELECT",
            // Written ahead of its caller: no runtime path puts a row in
            // platform_killswitches until Phase 03 ships the Platform-scope permission
            // that lets anything enter EnterPlatformAdminScope at all.
            "learnstack_platform platform_killswitches DELETE,INSERT,SELECT,UPDATE",
            "learnstack_platform platform_host_to_tenant DELETE,INSERT,SELECT,UPDATE",
            "learnstack_platform outbox_messages DELETE,SELECT",
            "learnstack_platform idempotency_keys DELETE,SELECT",
            "learnstack_app tenant_content_types DELETE,INSERT,SELECT,UPDATE",
            "learnstack_app tenant_level_taxonomies DELETE,INSERT,SELECT,UPDATE",
            "learnstack_app tenant_level_taxonomy_items DELETE,INSERT,SELECT,UPDATE",
            "learnstack_app customization_generations INSERT,SELECT,UPDATE",
            "learnstack_platform tenant_content_types SELECT",
            "learnstack_platform tenant_level_taxonomies SELECT",
            "learnstack_platform tenant_level_taxonomy_items SELECT",
            "learnstack_platform customization_generations SELECT",
            "learnstack_outbox_admin outbox_messages SELECT",

            // The Audit chain. learnstack_platform's column-restricted UPDATE on
            // audit_log does NOT appear here — information_schema.role_table_grants
            // reports table-level privileges only — so the six-column list has its own
            // assertion in AuditSchemaTests, the way the dispatcher's four-column grant
            // on outbox_messages does in PlatformSchemaTests. A matrix row missing its
            // column half reads as a narrower grant than the one that shipped.
            "learnstack_app audit_log INSERT,SELECT",
            "learnstack_platform audit_log DELETE,INSERT,SELECT",
            "learnstack_app audit_config SELECT",
            "learnstack_platform audit_config SELECT",
        ],
        "every line is one line of the three migrations' grant matrices, and "
        + "learnstack_outbox_admin holds nothing beyond the outbox");
    }
}
