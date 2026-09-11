using FluentAssertions;
using LearnStack.SharedKernel.Identifiers;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// What makes <c>audit_log</c> append-only, and what makes it tenant-isolated,
/// measured against a real database rather than asserted in a comment.
/// </summary>
/// <remarks>
/// <para>
/// Append-only is three layers and each stops a different actor, so each needs its own
/// case: <c>learnstack_app</c> is stopped by the <b>absent privilege</b>, a
/// <c>learnstack_platform</c> <c>UPDATE</c> touching anything but the six redactable
/// columns by the <b>column-level GRANT</b> before any trigger runs, and the table
/// <b>owner</b> — which holds every privilege implicitly — only by
/// <c>audit_log_append_only_guard</c>. A suite that exercised one layer would go green
/// with the other two deleted.
/// </para>
/// <para>
/// The isolation half is in <see cref="TenancySchemaTests"/>, where the sweeps that read
/// the whole catalogue live: <c>audit_log</c> and <c>audit_config</c> are two of the
/// sixteen tables those cases count, and the seed gives tenant A the same tenant-wide /
/// organization-scoped pair <c>tenant_settings</c> has. What is here is what is specific
/// to this table.
/// </para>
/// <para>
/// Every mutating case writes its own row under a tenant of its own and removes it
/// again, so nothing here moves a number the shared sweeps assert. The tenant is not in
/// <c>tenants</c> and does not need to be: <c>audit_log</c> deliberately carries no
/// foreign key to it.
/// </para>
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class AuditSchemaTests
{
    private readonly SchemaFixture _schema;

    public AuditSchemaTests(SchemaFixture schema) => _schema = schema;

    /// <summary>A tenant of this class's own, so no shared count moves.</summary>
    private static readonly Guid ProbeTenant = Guid.Parse("dddddddd-1111-7111-8111-111111111111");

    [Fact]
    public async Task ThePlatformRoleHoldsExactlyTheSixColumnUpdateGrant()
    {
        // The second layer, and the one no table-level assertion can see:
        // information_schema.role_table_grants reports table privileges only, so a
        // seventh column added here is invisible to the grant matrix in
        // TenancySchemaTests. actor_user_id is the absence that matters — once the users
        // row is erased it is an orphan surrogate with no path back to a natural person,
        // which is what keeps the row's existence auditable after erasure, and redacting
        // it would collapse every erased user's history into one bucket.
        await using var connection = await PostgresFixture.OpenAsync(
            _schema.Postgres.MigrationConnectionString);

        await using var command = new NpgsqlCommand(
            """
            SELECT string_agg(column_name, ',' ORDER BY column_name)
            FROM information_schema.column_privileges
            WHERE table_name = 'audit_log'
              AND grantee = 'learnstack_platform'
              AND privilege_type = 'UPDATE'
            """, (NpgsqlConnection)connection);

        (await command.ExecuteScalarAsync()).Should()
            .Be("actor_email,after_state,before_state,changes,ip_address,user_agent");
    }

    [Theory]
    // Layer one. The runtime role may add rows and read them back and holds nothing
    // else, so the ordinary path fails before any trigger runs — which is what keeps
    // the trigger's cost off every request.
    [InlineData("UPDATE audit_log SET actor_email = 'x@y.z'")]
    [InlineData("DELETE FROM audit_log")]
    [InlineData("TRUNCATE audit_log")]
    public async Task TheRuntimeRoleCannotMutateTheLog(string statement)
    {
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);

        await using var command = new NpgsqlCommand(
            statement, (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);

        var act = async () => await command.ExecuteNonQueryAsync();

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(
                PostgresErrorCodes.InsufficientPrivilege,
                "learnstack_app holds SELECT, INSERT and nothing else on audit_log");
    }

    [Fact]
    public async Task ARedactingUpdateByThePlatformRoleSucceeds()
    {
        // The accepting half, without which every refusal above could be produced by a
        // table nobody can write at all. This is the GDPR path of Audit Subsystem § 10.
        var id = await InsertProbeRowAsync(organizationId: null);

        await using var connection = await PostgresFixture.OpenAsync(
            _schema.Postgres.PlatformConnectionString);

        await using var command = new NpgsqlCommand(
            """
            UPDATE audit_log
            SET actor_email = @redacted, ip_address = NULL, user_agent = @redacted
            WHERE id = @id
            """, (NpgsqlConnection)connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("redacted", "***REDACTED***");

        (await command.ExecuteNonQueryAsync()).Should().Be(1);

        await DeleteProbeRowAsync(id);
    }

    [Theory]
    // Layer two. The column grant refuses the statement before the trigger sees it, so
    // both of these are `permission denied for table`, not the trigger's message. The
    // mixed case is the one that matters: an UPDATE naming a redactable column AND
    // another is refused whole, not applied in part.
    [InlineData("UPDATE audit_log SET operation = 'tampered' WHERE id = @id")]
    [InlineData("UPDATE audit_log SET actor_email = 'a@b.c', outcome = 'failed' WHERE id = @id")]
    [InlineData("UPDATE audit_log SET tenant_id = @id WHERE id = @id")]
    public async Task ThePlatformRoleCannotUpdateAnythingElse(string statement)
    {
        var id = await InsertProbeRowAsync(organizationId: null);

        try
        {
            await using var connection = await PostgresFixture.OpenAsync(
                _schema.Postgres.PlatformConnectionString);
            await using var command = new NpgsqlCommand(statement, (NpgsqlConnection)connection);
            command.Parameters.AddWithValue("id", id);

            var act = async () => await command.ExecuteNonQueryAsync();

            (await act.Should().ThrowAsync<PostgresException>())
                .Which.SqlState.Should().Be(
                    PostgresErrorCodes.InsufficientPrivilege,
                    "the column-level GRANT bounds learnstack_platform before the trigger runs");
        }
        finally
        {
            await DeleteProbeRowAsync(id);
        }
    }

    [Fact]
    public async Task TheRetentionDeleteByThePlatformRoleSucceeds()
    {
        // The second of the two sanctioned mutating paths (Audit Subsystem § 9). The
        // trigger returns OLD for a DELETE by this role; returning NULL would cancel the
        // purge silently, which is the failure mode a guard like this usually has.
        var id = await InsertProbeRowAsync(organizationId: null);

        await using var connection = await PostgresFixture.OpenAsync(
            _schema.Postgres.PlatformConnectionString);
        await using var command = new NpgsqlCommand(
            "DELETE FROM audit_log WHERE id = @id", (NpgsqlConnection)connection);
        command.Parameters.AddWithValue("id", id);

        (await command.ExecuteNonQueryAsync()).Should().Be(1);
    }

    [Theory]
    // Layer three, and the only layer that binds the table owner: ownership carries
    // every privilege implicitly, and while FORCE ROW LEVEL SECURITY does subject the
    // owner to the policy, what the policy constrains it by is the TENANT rather than
    // immutability. Measured on PostgreSQL 18.6: the same UPDATE returns `UPDATE 0` with
    // no tenant announced and reaches the trigger with one. Both statements below
    // announce the row's tenant, so the policy admits them and only the trigger does not.
    [InlineData("UPDATE audit_log SET actor_email = 'owner@x' WHERE id = @id")]
    [InlineData("DELETE FROM audit_log WHERE id = @id")]
    public async Task TheOwnerIsStoppedOnlyByTheTrigger(string statement)
    {
        var id = await InsertProbeRowAsync(organizationId: null);

        try
        {
            await using var connection = await PostgresFixture.OpenAsync(
                _schema.Postgres.MigrationConnectionString);
            await using var transaction = await connection.BeginTransactionAsync();
            await SchemaQueries.SetTenantAsync(connection, transaction, ProbeTenant);

            await using var command = new NpgsqlCommand(
                statement, (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
            command.Parameters.AddWithValue("id", id);

            var act = async () => await command.ExecuteNonQueryAsync();

            (await act.Should().ThrowAsync<PostgresException>())
                .Which.MessageText.Should().Contain(
                    "audit_log is append-only",
                    "the trigger is what binds the owner, and no grant does");
        }
        finally
        {
            await DeleteProbeRowAsync(id);
        }
    }

    [Fact]
    public async Task TruncateIsRefusedEvenForTheOwner()
    {
        // A row trigger cannot see the one statement that empties a table without
        // touching a row, and row security does not apply to TRUNCATE at all. The second
        // trigger takes no current_user test and admits no exception: no role in the
        // GRANT matrix holds TRUNCATE on this table, learnstack_platform included.
        await using var connection = await PostgresFixture.OpenAsync(
            _schema.Postgres.MigrationConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();

        await using var command = new NpgsqlCommand(
            "TRUNCATE audit_log", (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);

        var act = async () => await command.ExecuteNonQueryAsync();

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.MessageText.Should().Contain("TRUNCATE attempted");
    }

    [Fact]
    public async Task AnOrganizationScopedRowNeedsBothAnnouncements()
    {
        // The consequence of the org-scoped class that binds the audit store itself: a
        // row whose organization_id is non-null while app.organization_id is unset fails
        // WITH CHECK, because the unset GUC reads as the empty string, NULLIF makes it
        // NULL, and the comparison is NULL. Every `denied` row for an org-scoped
        // resource travels the standalone path, which is why both writers announce the
        // pair from the draft rather than only the tenant (ADR-0044 § 9).
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);

        await using (var refused = await connection.BeginTransactionAsync())
        {
            await SchemaQueries.SetTenantAsync(connection, refused, SchemaFixture.TenantA);

            var act = async () => await InsertAsync(
                connection, refused, Guid.NewGuid(), SchemaFixture.TenantA, SchemaFixture.OrgA1);

            (await act.Should().ThrowAsync<PostgresException>())
                .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        }

        await using var accepted = await connection.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(connection, accepted, SchemaFixture.TenantA);
        await SchemaQueries.SetSettingAsync(
            connection, accepted, "app.organization_id", SchemaFixture.OrgA1.ToString());

        await InsertAsync(
            connection, accepted, Guid.NewGuid(), SchemaFixture.TenantA, SchemaFixture.OrgA1);

        // Rolled back rather than committed: the shared sweeps count this table.
        await accepted.RollbackAsync();
    }

    [Fact]
    public async Task AnOrganizationScopedSessionReadsItsOwnRowsAndTheTenantWideOnes()
    {
        // The organization arm of the read predicate, in the only direction a count can
        // constrain it. The suite already fails if the arm is WIDENED — Tenant A would see
        // Tenant B's rows — but every mutation that silently NARROWS the read stayed green
        // until this case: delete `OR organization_id = app.organization_id` and an
        // organization-scoped audit screen shows the tenant-wide rows only, reporting
        // success while omitting exactly the rows the caller's own organization produced.
        //
        // The seed gives Tenant A the pair this needs: one tenant-wide row and one under
        // OrgA1 (SchemaFixture), the same shape tenant_settings carries.
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);

        (await CountUnderAsync(connection, SchemaFixture.OrgA1, scope: null)).Should().Be(2L,
            "an OrgA1 session sees the tenant-wide row and its own");

        (await CountUnderAsync(connection, SchemaFixture.OrgA2, scope: null)).Should().Be(1L,
            "OrgA2 has no rows of its own, and OrgA1's are not its to read");
    }

    [Fact]
    public async Task TheTenantScopeHatchWidensTheReadAcrossOrganizations()
    {
        // `app.scope = 'tenant'` is the cross-organization READ hatch, and this is the
        // case that kills its arm: an OrgA2 session sees one row without it and two with
        // it. Deleting the arm leaves the first number unchanged and the second wrong,
        // which no other assertion in the suite observes.
        //
        // Reads only. The two AS RESTRICTIVE guards are what stop the hatch widening
        // writes, and TheOwnerIsStoppedOnlyByTheTrigger plus the runtime role's absent
        // privilege already make every write path on this table refuse.
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);

        (await CountUnderAsync(connection, SchemaFixture.OrgA2, scope: null)).Should().Be(1L);
        (await CountUnderAsync(connection, SchemaFixture.OrgA2, scope: "tenant")).Should().Be(2L,
            "the tenant-scope hatch is what a cross-organization audit read travels");
    }

    [Fact]
    public async Task TheCompositeKeyAdmitsTheCommitInDoubtPairAndNothingElse()
    {
        // The composite primary key (id, timestamp) is load-bearing twice over, and until
        // this case nothing observed it: reducing the key to `id` alone left all 215
        // integration and 91 architecture cases green.
        //
        // What it buys, measured here rather than restated: the ADR-0033 Indeterminate
        // pair is TWO rows carrying ONE AuditEntryId, because the in-transaction row and
        // its standalone re-write have to be recognisable as the same operation across two
        // connections. A single-column key rejects the second with 23505 — which
        // PostgresAuditStore reads as positive evidence the first row is durable, so under
        // a reduced key every commit-in-doubt would report the wrong thing.
        //
        // The second half is what keeps the key from being merely wider than it needs to
        // be: the same id at the same instant is still a duplicate.
        var id = Guid.CreateVersion7();
        var first = DateTimeOffset.UtcNow;

        await using var connection = await PostgresFixture.OpenAsync(
            _schema.Postgres.PlatformConnectionString);

        try
        {
            await InsertAtAsync(connection, id, first);
            await InsertAtAsync(connection, id, first.AddMilliseconds(1));

            await using var count = new NpgsqlCommand(
                "SELECT count(*) FROM audit_log WHERE id = @id", (NpgsqlConnection)connection);
            count.Parameters.AddWithValue("id", id);

            (await count.ExecuteScalarAsync()).Should().Be(2L,
                "one AuditEntryId, two timestamps — the Indeterminate pair");

            var act = async () => await InsertAtAsync(connection, id, first);

            (await act.Should().ThrowAsync<PostgresException>())
                .Which.SqlState.Should().Be(
                    PostgresErrorCodes.UniqueViolation,
                    "the key is (id, timestamp), so the same instant is still a duplicate");
        }
        finally
        {
            await DeleteProbeRowAsync(id);
        }
    }

    [Fact]
    public async Task ThePlatformSentinelRowIsInvisibleToEveryTenant()
    {
        // What the sentinel is for. It names no tenant — `tenants` carries
        // ck_tenants_not_platform_sentinel — so no tenant-keyed policy matches its rows
        // and only learnstack_platform reads them. The row is written by the platform
        // role because that is the role WritePlatformScopeAsync uses; learnstack_app
        // could not write it under any announcement, which the second half asserts.
        var id = Guid.CreateVersion7();

        await using (var platform = await PostgresFixture.OpenAsync(
            _schema.Postgres.PlatformConnectionString))
        {
            await InsertAsync(platform, null, id, TenantId.PlatformSentinel.Value, organizationId: null);
        }

        try
        {
            await using var app = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);

            foreach (var tenant in new[] { SchemaFixture.TenantA, SchemaFixture.TenantB })
            {
                await using var transaction = await app.BeginTransactionAsync();
                await SchemaQueries.SetTenantAsync(app, transaction, tenant);

                await using var read = new NpgsqlCommand(
                    "SELECT count(*) FROM audit_log WHERE id = @id",
                    (NpgsqlConnection)app, (NpgsqlTransaction)transaction);
                read.Parameters.AddWithValue("id", id);

                (await read.ExecuteScalarAsync()).Should().Be(0L,
                    "a platform-scope row belongs to no tenant and is readable only "
                    + "through learnstack_platform");
            }

            // And announcing the sentinel does not help: the row is visible under it,
            // but no runtime path can announce it — SetTenantContextAsync and
            // SetProvisioningTenantContextAsync both refuse the value, which is the
            // control the CHECK on `tenants` only backstops.
            await using var announced = await app.BeginTransactionAsync();
            await SchemaQueries.SetTenantAsync(app, announced, TenantId.PlatformSentinel.Value);

            await using var sentinelRead = new NpgsqlCommand(
                "SELECT count(*) FROM audit_log WHERE id = @id",
                (NpgsqlConnection)app, (NpgsqlTransaction)announced);
            sentinelRead.Parameters.AddWithValue("id", id);

            (await sentinelRead.ExecuteScalarAsync()).Should().Be(1L,
                "the policy is keyed on the announcement, so the guards that refuse to "
                + "announce the sentinel are the control and this is why they exist");
        }
        finally
        {
            await DeleteProbeRowAsync(id);
        }
    }

    [Fact]
    public async Task TheServerDefaultsFillARowTheStoreDidNotWrite()
    {
        // Neither default is the path a row normally takes — PostgresAuditStore supplies
        // both, the id because the commit-in-doubt pair needs one identity across two
        // connections and the timestamp because that pair has to differ under the
        // composite key. They exist so a row inserted by something other than the store
        // is well-formed rather than rejected, and uuidv7() is a PostgreSQL 18 built-in
        // that needs no extension.
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO audit_log
                (tenant_id, module, operation, operation_type, operation_class, outcome)
            VALUES (@tenant, 'audit', 'audit.event.read', 'ReadSensitive', 'Should', 'success')
            RETURNING id, timestamp
            """, (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        command.Parameters.AddWithValue("tenant", SchemaFixture.TenantA);

        await using (var reader = await command.ExecuteReaderAsync())
        {
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetGuid(0).Should().NotBe(Guid.Empty);
            reader.GetDateTime(1).Should().NotBe(default);
        }

        await transaction.RollbackAsync();
    }

    [Theory]
    // The closed sets, each rejected by its own CHECK. `outcome` is lowercase because
    // two Accepted ADRs write it that way; the other two store the C# enum member name
    // unchanged, on the ck_tenants_status precedent. Passing the wrong casing is the
    // mistake the asymmetry invites, so both directions are here.
    [InlineData("outcome", "Success")]
    [InlineData("outcome", "unknown")]
    [InlineData("operation_type", "create")]
    [InlineData("operation_class", "MUST")]
    public async Task TheClosedSetColumnsRejectAValueOutsideTheirCheck(string column, string value)
    {
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);

        await using var command = new NpgsqlCommand(
            $"""
             INSERT INTO audit_log
                 (id, tenant_id, module, operation, operation_type, operation_class,
                  outcome, timestamp)
             VALUES (uuidv7(), @tenant, 'audit', 'audit.event.read',
                     {(column == "operation_type" ? "@value" : "'ReadSensitive'")},
                     {(column == "operation_class" ? "@value" : "'Should'")},
                     {(column == "outcome" ? "@value" : "'success'")}, now())
             """, (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        command.Parameters.AddWithValue("tenant", SchemaFixture.TenantA);
        command.Parameters.AddWithValue("value", value);

        var act = async () => await command.ExecuteNonQueryAsync();

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.ConstraintName.Should().Be($"ck_audit_log_{column}");
    }

    [Fact]
    public async Task TheSentinelCannotBeProvisionedAsATenant()
    {
        // The backstop, and it is only the backstop: a constraint on `tenants` cannot
        // stop the sentinel from being announced on app.tenant_id. The guards that can
        // sit where the value enters — TenantOwnership.EnsureRealTenant, which
        // Tenant.Create calls, and both announcement paths on NpgsqlUnitOfWork — and
        // have their own cases in the unit suite. This is what still holds when a
        // hand-written statement bypasses all of them.
        await using var connection = await PostgresFixture.OpenAsync(
            _schema.Postgres.MigrationConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(connection, transaction, TenantId.PlatformSentinel.Value);

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO tenants (id, slug, display_name, status, created_at, created_by, row_version)
            VALUES (@id, 'sentinel', 'Sentinel', 'Active', now(), @actor, 0)
            """, (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        command.Parameters.AddWithValue("id", TenantId.PlatformSentinel.Value);
        command.Parameters.AddWithValue("actor", SchemaFixture.Actor);

        var act = async () => await command.ExecuteNonQueryAsync();

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.ConstraintName.Should().Be("ck_tenants_not_platform_sentinel");
    }

    [Fact]
    public async Task AuditConfigKeepsItsForeignKeyToTenants()
    {
        // The schema's only foreign key crossing two migration chains, and the reason
        // `make migrate` names the Tenancy chain ahead of the glob. audit_config is live
        // configuration rather than history, so a row is meaningless without the tenant
        // it configures — audit_log, which has to outlive its tenant, deliberately has
        // no such key.
        await using var connection = await PostgresFixture.OpenAsync(
            _schema.Postgres.MigrationConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(connection, transaction, ProbeTenant);

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO audit_config
                (id, tenant_id, module, operation, is_enabled, created_at, created_by, row_version)
            VALUES (uuidv7(), @tenant, 'audit', 'audit.event.read', false, now(), @actor, 0)
            """, (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        command.Parameters.AddWithValue("tenant", ProbeTenant);
        command.Parameters.AddWithValue("actor", SchemaFixture.Actor);

        var act = async () => await command.ExecuteNonQueryAsync();

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.ConstraintName.Should().Be("fk_audit_config_tenant");
    }

    [Fact]
    public async Task AuditLogCarriesNoForeignKeyToTenants()
    {
        // Stated as a schema assertion rather than left to the platform-scope row's
        // success, because the two fail differently: a key added here would refuse the
        // sentinel row — which is the row the sentinel exists for — and no policy, no
        // BYPASSRLS and no role moves a constraint.
        await using var connection = await PostgresFixture.OpenAsync(
            _schema.Postgres.MigrationConnectionString);

        var keys = await SchemaQueries.ReadStringsAsync(connection,
            """
            SELECT conname FROM pg_constraint
            WHERE conrelid = 'audit_log'::regclass AND contype = 'f'
            """);

        keys.Should().BeEmpty(
            "the record of what happened to a tenant has to outlive the tenant, and the "
            + "platform-scope row's tenant has no `tenants` row by construction");
    }

    /// <summary>
    /// Counts Tenant A's <c>audit_log</c> rows under one organization, optionally with the
    /// tenant-scope hatch announced.
    /// </summary>
    /// <remarks>
    /// As <c>learnstack_app</c>, which is the whole point: the announcement is what the
    /// policy reads, and a count taken as the owner or under <c>BYPASSRLS</c> would be the
    /// same number whether or not the policy existed.
    /// </remarks>
    private static async Task<long> CountUnderAsync(
        System.Data.Common.DbConnection connection, Guid organizationId, string? scope)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);
        await SchemaQueries.SetSettingAsync(
            connection, transaction, "app.organization_id", organizationId.ToString());

        if (scope is not null)
        {
            await SchemaQueries.SetSettingAsync(connection, transaction, "app.scope", scope);
        }

        await using var read = new NpgsqlCommand(
            "SELECT count(*) FROM audit_log",
            (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);

        var count = (long)(await read.ExecuteScalarAsync())!;
        await transaction.RollbackAsync();

        return count;
    }

    private static async Task InsertAtAsync(
        System.Data.Common.DbConnection connection, Guid id, DateTimeOffset timestamp)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO audit_log
                (id, tenant_id, module, operation, operation_type, operation_class,
                 outcome, timestamp)
            VALUES (@id, @tenant, 'audit', 'audit.event.read', 'ReadSensitive', 'Should',
                    'indeterminate', @timestamp)
            """, (NpgsqlConnection)connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("tenant", ProbeTenant);
        command.Parameters.AddWithValue("timestamp", timestamp);

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Adds one row under this class's own tenant, as the platform role.</summary>
    /// <remarks>
    /// The platform role, because it is the only one that can also remove the row again:
    /// <c>learnstack_app</c> holds no <c>DELETE</c> and the owner is refused by the
    /// trigger. Under <c>BYPASSRLS</c> no announcement is needed, which is also what
    /// keeps this helper out of the way of the cases that are about announcements.
    /// </remarks>
    private async Task<Guid> InsertProbeRowAsync(Guid? organizationId)
    {
        var id = Guid.CreateVersion7();

        await using var connection = await PostgresFixture.OpenAsync(
            _schema.Postgres.PlatformConnectionString);

        await InsertAsync(connection, null, id, ProbeTenant, organizationId);

        return id;
    }

    private async Task DeleteProbeRowAsync(Guid id)
    {
        await using var connection = await PostgresFixture.OpenAsync(
            _schema.Postgres.PlatformConnectionString);
        await using var command = new NpgsqlCommand(
            "DELETE FROM audit_log WHERE id = @id", (NpgsqlConnection)connection);
        command.Parameters.AddWithValue("id", id);

        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction? transaction,
        Guid id,
        Guid tenantId,
        Guid? organizationId)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO audit_log
                (id, tenant_id, organization_id, module, operation, operation_type,
                 operation_class, outcome, timestamp)
            VALUES (@id, @tenant, @organization, 'audit', 'audit.event.read',
                    'ReadSensitive', 'Should', 'success', now())
            """, (NpgsqlConnection)connection, (NpgsqlTransaction?)transaction);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("organization", (object?)organizationId ?? DBNull.Value);

        await command.ExecuteNonQueryAsync();
    }
}
