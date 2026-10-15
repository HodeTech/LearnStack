using System.Data.Common;
using System.Reflection;
using FluentAssertions;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class EventDeliveryMigrationTests(SchemaFixture schema)
{
    private const string Predecessor = "20261012222654_align_outbox_recovery_index";
    private const string Subscription = "customization.initialize-content-type-default.v1";
    private const string Token = "12345678-1234-4234-8234-123456789abc";
    private const string InsertDelivery = """
        INSERT INTO event_deliveries
            (tenant_id, subscription_id, event_id, organization_id, occurred_at,
             correlation_id, causation_id, actor_user_id, type, topic, partition_key, payload)
        VALUES (@tenant, @subscription, @event, @organization, now(),
                '00-11111111111111111111111111111111-1111111111111111-01',
                NULL, @actor, 'TenantCreatedIntegrationEventV1',
                'learnstack.tenancy.tenant', @partition, '{"version":1}')
        """;

    private static readonly string[] AcceptanceColumns =
    [
        "tenant_id", "subscription_id", "event_id", "organization_id", "occurred_at",
        "correlation_id", "causation_id", "actor_user_id", "type", "topic", "partition_key", "payload",
    ];

    private static readonly string[] SchedulingColumns =
    [
        "attempts", "last_error", "available_after", "locked_by", "locked_until",
        "terminal_reason", "dead_lettered_at", "replay_count", "replayed_at",
    ];

    private static readonly string[] SchedulerReadColumns =
    [
        "tenant_id", "subscription_id", "event_id", "organization_id", "occurred_at",
        "type", "topic", "partition_key", "processed_at", "attempts", "last_error",
        "available_after", "locked_by", "locked_until", "terminal_reason", "dead_lettered_at",
        "replay_count", "replayed_at",
    ];

    [Fact]
    public async Task Fresh_down_reapply_preserves_the_producer_and_restores_the_complete_schema()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var context = Platform(database);
        context.Database.HasPendingModelChanges().Should().BeFalse();
        var before = await InventoryAsync(database.MigrationConnectionString);
        before.Should().HaveCountGreaterThan(50, "the fingerprint must cover actual columns, constraints, indexes, policies and grants");

        await using (var app = await PostgresFixture.OpenAsync(database.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await SchemaQueries.AssertApplicationRoleAsync(app);
            await AnnounceAsync(app, transaction, SchemaFixture.TenantA);
            await SchemaQueries.ExecuteAsync(app, transaction, """
                INSERT INTO outbox_messages (tenant_id, correlation_id, type, topic, partition_key, payload)
                VALUES (@tenant, 'producer-fact', 'TenantCreated', 'learnstack.tenancy.tenant', 'retained', '{}')
                """, ("tenant", SchemaFixture.TenantA));
            await InsertAsync(app, transaction, SchemaFixture.TenantA, null, Guid.NewGuid());
            await transaction.CommitAsync();
        }

        await context.GetService<IMigrator>().MigrateAsync(Predecessor);
        await using (var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString))
        {
            (await SchemaQueries.CountAsync(owner, "SELECT count(*) FROM pg_proc WHERE proname = 'fn_inbox_messages_immutable'"))
                .Should().Be(0, "Down must remove the table-owned function as well as its triggers");
            (await SchemaQueries.CountAsync(owner, """
                SELECT count(*) FROM pg_class WHERE relnamespace = 'public'::regnamespace
                  AND relname IN ('inbox_messages','event_deliveries')
                """)).Should().Be(0);
        }
        await using (var scheduler = await PostgresFixture.OpenAsync(database.OutboxConnectionString))
        {
            (await SchemaQueries.CountAsync(scheduler, "SELECT count(*) FROM outbox_messages WHERE correlation_id = 'producer-fact'"))
                .Should().Be(1);
        }

        await context.Database.MigrateAsync();
        (await InventoryAsync(database.MigrationConnectionString)).Should().Equal(before);
        await using var restored = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var restoredTransaction = await restored.BeginTransactionAsync();
        await AnnounceAsync(restored, restoredTransaction, SchemaFixture.TenantA);
        await InsertAsync(restored, restoredTransaction, SchemaFixture.TenantA, null, Guid.NewGuid());
    }

    [Fact]
    public async Task Canonical_document_ddl_produces_the_same_applied_inventory_as_the_migration()
    {
        await using var canonical = await DisposableSchemaDatabase.CreateAsync(schema.Postgres, applyMigrations: false);
        await MigrationChains.ApplyTenancyAsync(canonical.MigrationConnectionString);
        var document = await File.ReadAllTextAsync(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(RepositoryPath.RolesScript())!, "../../../docs/standards/05-database.md")));
        var section = document[document.IndexOf("## Inbox\n", StringComparison.Ordinal)..];
        var start = section.IndexOf("```sql\n", StringComparison.Ordinal) + "```sql\n".Length;
        var end = section.IndexOf("\n```", start, StringComparison.Ordinal);
        start.Should().BeGreaterThan("```sql\n".Length);
        end.Should().BeGreaterThan(start);
        await using (var owner = await PostgresFixture.OpenAsync(canonical.MigrationConnectionString))
        {
            await SchemaQueries.ExecuteAsync(owner, null, section[start..end]);
        }
        (await InventoryAsync(canonical.MigrationConnectionString))
            .Should().Equal(await InventoryAsync(schema.Postgres.MigrationConnectionString),
                "the canonical DDL must build the actual shipped columns, checks, RLS, indexes and exact grants");
    }

    [Fact]
    public async Task Exact_column_grants_exclude_envelope_reads_completion_forgery_and_purge()
    {
        await using var owner = await PostgresFixture.OpenAsync(schema.Postgres.MigrationConnectionString);
        var ledgerColumns = await SchemaQueries.ReadStringsAsync(owner, """
            SELECT column_name FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'event_deliveries' ORDER BY column_name
            """);
        ledgerColumns.Should().HaveCount(22).And.NotContain("metadata");
        foreach (var role in new[] { "learnstack_app", "learnstack_platform", "learnstack_outbox_admin", "PUBLIC" })
        {
            foreach (var privilege in new[] { "SELECT", "INSERT", "UPDATE", "DELETE" })
            {
                var expected = (role, privilege) switch
                {
                    ("learnstack_app", "SELECT") => ledgerColumns,
                    ("learnstack_app", "INSERT") => AcceptanceColumns.ToList(),
                    ("learnstack_app", "UPDATE") => ["processed_at"],
                    ("learnstack_outbox_admin", "SELECT") => SchedulerReadColumns.ToList(),
                    ("learnstack_outbox_admin", "UPDATE") => SchedulingColumns.ToList(),
                    _ => [],
                };
                (await GrantedColumnsAsync(owner, role, "event_deliveries", privilege))
                    .Should().BeEquivalentTo(expected, $"{role} has an exact {privilege} column budget");
                var inboxExpected = (role, privilege) switch
                {
                    ("learnstack_app", "SELECT") => new[] { "tenant_id", "subscription_id", "event_id", "recorded_at" },
                    ("learnstack_app", "INSERT") => ["tenant_id", "subscription_id", "event_id"],
                    _ => [],
                };
                (await GrantedColumnsAsync(owner, role, "inbox_messages", privilege))
                    .Should().BeEquivalentTo(inboxExpected);
            }
        }
        (await SchemaQueries.CountAsync(owner, """
            SELECT count(*) FROM information_schema.table_privileges
            WHERE table_schema = 'public' AND table_name IN ('event_deliveries','inbox_messages')
              AND grantee <> 'learnstack_migration'
              AND NOT (grantee = 'learnstack_app' AND privilege_type = 'SELECT')
            """)).Should().Be(0, "column grants must not be widened by table privileges");
    }

    [Fact]
    public async Task Real_roles_accept_claim_complete_and_retain_inert_lease_provenance()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        var eventId = Guid.NewGuid();
        await using (var app = await PostgresFixture.OpenAsync(database.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await SchemaQueries.AssertApplicationRoleAsync(app);
            await AnnounceAsync(app, transaction, SchemaFixture.TenantA, SchemaFixture.OrgA1);
            await InsertAsync(app, transaction, SchemaFixture.TenantA, SchemaFixture.OrgA1, eventId);
            await transaction.CommitAsync();
        }
        await using (var scheduler = await PostgresFixture.OpenAsync(database.OutboxConnectionString))
        {
            await using var claim = new NpgsqlCommand("""
                WITH candidate AS (
                    SELECT tenant_id, subscription_id, event_id FROM event_deliveries
                    WHERE processed_at IS NULL AND dead_lettered_at IS NULL
                      AND terminal_reason IS NULL AND attempts < 6 AND locked_by IS NULL
                      AND available_after <= clock_timestamp()
                    ORDER BY available_after, occurred_at, event_id, tenant_id, subscription_id
                    FOR UPDATE SKIP LOCKED LIMIT 1)
                UPDATE event_deliveries d
                SET locked_by = @token, locked_until = clock_timestamp() + interval '30 seconds', attempts = attempts + 1
                FROM candidate c WHERE d.tenant_id = c.tenant_id
                    AND d.subscription_id = c.subscription_id AND d.event_id = c.event_id
                RETURNING d.event_id
                """, (NpgsqlConnection)scheduler);
            claim.Parameters.AddWithValue("token", Token);
            (await claim.ExecuteScalarAsync()).Should().Be(eventId, "column UPDATE grants must permit actual row locking and claiming");
        }
        await using (var app = await PostgresFixture.OpenAsync(database.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await AnnounceAsync(app, transaction, SchemaFixture.TenantA, SchemaFixture.OrgA1);
            await SchemaQueries.ExecuteAsync(app, transaction, """
                INSERT INTO inbox_messages (tenant_id, subscription_id, event_id)
                VALUES (@tenant, @subscription, @event)
                """, ("tenant", SchemaFixture.TenantA), ("subscription", Subscription), ("event", eventId));
            await using var complete = new NpgsqlCommand("""
                UPDATE event_deliveries SET processed_at = clock_timestamp()
                WHERE tenant_id = @tenant AND subscription_id = @subscription AND event_id = @event
                    AND processed_at IS NULL AND dead_lettered_at IS NULL AND terminal_reason IS NULL
                    AND locked_by = @token AND locked_until > clock_timestamp()
                """, (NpgsqlConnection)app, (NpgsqlTransaction)transaction);
            complete.Parameters.AddWithValue("tenant", SchemaFixture.TenantA);
            complete.Parameters.AddWithValue("subscription", Subscription);
            complete.Parameters.AddWithValue("event", eventId);
            complete.Parameters.AddWithValue("token", Token);
            (await complete.ExecuteNonQueryAsync()).Should().Be(1);
            (await SchemaQueries.CountAsync(app, """
                SELECT count(*) FROM inbox_messages WHERE recorded_at = transaction_timestamp()
                """, transaction)).Should().Be(1);
            await transaction.CommitAsync();
        }
        await using var verify = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        (await SchemaQueries.CountAsync(verify, $"SELECT count(*) FROM event_deliveries WHERE processed_at IS NOT NULL AND locked_by = '{Token}' AND locked_until IS NOT NULL"))
            .Should().Be(1);
        foreach (var predicate in new[]
        {
            "terminal_reason IS NULL AND attempts < 6 AND locked_by IS NULL",
            "terminal_reason IS NULL AND (locked_by IS NOT NULL OR attempts >= 6)",
            "terminal_reason IS NOT NULL",
        })
        {
            (await SchemaQueries.CountAsync(verify, $"SELECT count(*) FROM event_deliveries WHERE processed_at IS NULL AND dead_lettered_at IS NULL AND ({predicate})"))
                .Should().Be(0, "completed rows with retained ownership must never reenter a scheduler path");
        }
    }

    [Theory]
    [InlineData(false, "SELECT payload FROM event_deliveries")]
    [InlineData(false, "SELECT correlation_id FROM event_deliveries")]
    [InlineData(false, "SELECT causation_id FROM event_deliveries")]
    [InlineData(false, "SELECT actor_user_id FROM event_deliveries")]
    [InlineData(false, "SELECT * FROM inbox_messages")]
    [InlineData(false, "TRUNCATE inbox_messages")]
    [InlineData(false, "TRUNCATE event_deliveries")]
    [InlineData(false, "UPDATE event_deliveries SET processed_at = now()")]
    [InlineData(false, "UPDATE event_deliveries SET topic = 'other'")]
    [InlineData(false, "INSERT INTO event_deliveries (tenant_id) VALUES ('11111111-1111-7111-8111-111111111111')")]
    [InlineData(false, "DELETE FROM event_deliveries")]
    [InlineData(false, "INSERT INTO inbox_messages (tenant_id) VALUES ('11111111-1111-7111-8111-111111111111')")]
    [InlineData(true, "UPDATE event_deliveries SET attempts = 0")]
    [InlineData(true, "UPDATE event_deliveries SET locked_by = NULL, locked_until = NULL")]
    [InlineData(true, "UPDATE event_deliveries SET replay_count = 0, replayed_at = NULL")]
    [InlineData(true, "UPDATE event_deliveries SET payload = '{}'::jsonb")]
    [InlineData(true, "UPDATE event_deliveries SET organization_id = NULL")]
    [InlineData(true, "DELETE FROM event_deliveries")]
    [InlineData(true, "INSERT INTO event_deliveries (attempts) VALUES (0)")]
    [InlineData(true, "INSERT INTO inbox_messages (recorded_at) VALUES (now())")]
    [InlineData(true, "UPDATE inbox_messages SET recorded_at = now()")]
    [InlineData(true, "DELETE FROM inbox_messages")]
    [InlineData(true, "TRUNCATE inbox_messages")]
    [InlineData(true, "TRUNCATE event_deliveries")]
    public async Task Actual_sql_refuses_privileges_outside_each_role_budget(bool application, string sql)
    {
        await using var connection = await PostgresFixture.OpenAsync(application
            ? schema.Postgres.AppConnectionString : schema.Postgres.OutboxConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        if (application)
        {
            await SchemaQueries.AssertApplicationRoleAsync(connection);
            await AnnounceAsync(connection, transaction, SchemaFixture.TenantA);
        }
        var execute = async () => await SchemaQueries.ExecuteAsync(connection, transaction, sql);
        (await execute.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Composite_identity_deduplicates_each_subscription_and_tenant_independently()
    {
        await using var connection = await PostgresFixture.OpenAsync(schema.Postgres.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(connection);
        var eventId = Guid.NewGuid();
        foreach (var tenant in new[] { SchemaFixture.TenantA, SchemaFixture.TenantB })
        {
            await AnnounceAsync(connection, transaction, tenant);
            foreach (var subscription in new[] { Subscription, "customization.initialize-taxonomy-default.v1" })
            {
                await InsertAsync(connection, transaction, tenant, null, eventId, subscription);
                await SchemaQueries.ExecuteAsync(connection, transaction, """
                    INSERT INTO inbox_messages (tenant_id, subscription_id, event_id)
                    VALUES (@tenant, @subscription, @event)
                    """, ("tenant", tenant), ("subscription", subscription), ("event", eventId));
            }
            (await CountEventAsync(connection, transaction, "event_deliveries", eventId)).Should().Be(2);
            (await CountEventAsync(connection, transaction, "inbox_messages", eventId)).Should().Be(2);
        }
        await transaction.SaveAsync("duplicate");
        var duplicate = async () => await InsertAsync(connection, transaction, SchemaFixture.TenantB, null, eventId);
        (await duplicate.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("pk_event_deliveries");
        await transaction.RollbackAsync("duplicate");
        var duplicateMarker = async () => await SchemaQueries.ExecuteAsync(connection, transaction, """
            INSERT INTO inbox_messages (tenant_id, subscription_id, event_id)
            VALUES (@tenant, @subscription, @event)
            """, ("tenant", SchemaFixture.TenantB), ("subscription", Subscription), ("event", eventId));
        (await duplicateMarker.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("pk_inbox_messages");
    }

    [Theory]
    [InlineData("customization.initialize-content-type-default.v1", true)]
    [InlineData("module1.effect-2.v12", true)]
    [InlineData("Module.effect.v1", false)]
    [InlineData("module.éffect.v1", false)]
    [InlineData("module.effect.v0", false)]
    [InlineData("module.effect.v01", false)]
    [InlineData("module.effect.other.v1", false)]
    [InlineData("module.effect.v1\n", false)]
    [InlineData("module.effect.v1 ", false)]
    public async Task Subscription_grammar_has_ascii_accepting_and_refusing_controls(string subscription, bool valid)
    {
        await using var app = await PostgresFixture.OpenAsync(schema.Postgres.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await AnnounceAsync(app, transaction, SchemaFixture.TenantA);
        foreach (var table in new[] { "event_deliveries", "inbox_messages" })
        {
            await transaction.SaveAsync("grammar");
            var insert = async () =>
            {
                if (table == "event_deliveries")
                {
                    await InsertAsync(app, transaction, SchemaFixture.TenantA, null, Guid.NewGuid(), subscription);
                }
                else
                {
                    await SchemaQueries.ExecuteAsync(app, transaction, """
                        INSERT INTO inbox_messages (tenant_id, subscription_id, event_id)
                        VALUES (@tenant, @subscription, @event)
                        """, ("tenant", SchemaFixture.TenantA), ("subscription", subscription), ("event", Guid.NewGuid()));
                }
            };
            if (valid) await insert.Should().NotThrowAsync();
            else (await insert.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be($"ck_{table}_subscription");
            await transaction.RollbackAsync("grammar");
        }
    }

    [Theory]
    [InlineData("attempts = -1", "ck_event_deliveries_attempts")]
    [InlineData("attempts = 7", null)]
    [InlineData("last_error = repeat('é', 128)", null)]
    [InlineData("last_error = repeat('é', 129)", "ck_event_deliveries_last_error_size")]
    [InlineData("locked_by = 'worker'", "ck_event_deliveries_lease")]
    [InlineData("locked_by = 'worker', locked_until = now()", "ck_event_deliveries_lease")]
    [InlineData("locked_by = '12345678-1234-4234-8234-123456789abc', locked_until = now()", null)]
    [InlineData("locked_by = '12345678-1234-4234-8234-123456789ABC', locked_until = now()", "ck_event_deliveries_lease")]
    [InlineData("locked_by = '00000000-0000-0000-0000-000000000000', locked_until = now()", "ck_event_deliveries_lease")]
    [InlineData("locked_until = now()", "ck_event_deliveries_lease")]
    [InlineData("terminal_reason = 'unknown_type'", null)]
    [InlineData("terminal_reason = 'retired_type'", null)]
    [InlineData("terminal_reason = 'invalid_envelope'", null)]
    [InlineData("terminal_reason = 'invalid_subscription'", null)]
    [InlineData("terminal_reason = 'retry_exhausted'", null)]
    [InlineData("terminal_reason = 'undeclared'", "ck_event_deliveries_terminal_reason")]
    [InlineData("dead_lettered_at = now()", "ck_event_deliveries_state")]
    [InlineData("dead_lettered_at = now(), terminal_reason = 'retry_exhausted'", null)]
    [InlineData("processed_at = now(), terminal_reason = 'retry_exhausted'", "ck_event_deliveries_state")]
    [InlineData("replay_count = -1", "ck_event_deliveries_replay")]
    [InlineData("replay_count = 1", "ck_event_deliveries_replay")]
    [InlineData("replayed_at = now()", "ck_event_deliveries_replay")]
    [InlineData("replay_count = 1, replayed_at = now()", null)]
    public async Task Ledger_state_checks_preserve_valid_facts_and_refuse_invalid_combinations(string assignment, string? constraint)
    {
        // CHECK semantics are independent of the caller's privileges. Owner setup
        // keeps the row and mutation in one rolled-back transaction; role budgets
        // and actual permitted transitions are separately proved above.
        await using var owner = await PostgresFixture.OpenAsync(schema.Postgres.MigrationConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();
        await AnnounceAsync(owner, transaction, SchemaFixture.TenantA);
        var eventId = Guid.NewGuid();
        await InsertAsync(owner, transaction, SchemaFixture.TenantA, null, eventId);
        var update = async () =>
        {
            await using var command = new NpgsqlCommand($"UPDATE event_deliveries SET {assignment} WHERE event_id = @event",
                (NpgsqlConnection)owner, (NpgsqlTransaction)transaction);
            command.Parameters.AddWithValue("event", eventId);
            (await command.ExecuteNonQueryAsync()).Should().Be(1);
        };
        if (constraint is null) await update.Should().NotThrowAsync();
        else (await update.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be(constraint);
    }

    [Fact]
    public async Task Organization_immutability_is_enforced_even_when_the_owner_has_column_update_privilege()
    {
        await using var owner = await PostgresFixture.OpenAsync(schema.Postgres.MigrationConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();
        await AnnounceAsync(owner, transaction, SchemaFixture.TenantA, SchemaFixture.OrgA1);
        var eventId = Guid.NewGuid();
        await InsertAsync(owner, transaction, SchemaFixture.TenantA, SchemaFixture.OrgA1, eventId);
        var move = async () => await SchemaQueries.ExecuteAsync(owner, transaction,
            "UPDATE event_deliveries SET organization_id = @organization WHERE event_id = @event",
            ("organization", SchemaFixture.OrgA2), ("event", eventId));
        var error = (await move.Should().ThrowAsync<PostgresException>()).Which;
        error.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        error.MessageText.Should().Contain("organization_id is immutable after insert");
    }

    [Theory]
    [InlineData("UPDATE inbox_messages SET recorded_at = recorded_at + interval '1 second'", "tg_inbox_messages_immutable")]
    [InlineData("DELETE FROM inbox_messages", "tg_inbox_messages_immutable")]
    [InlineData("TRUNCATE inbox_messages", "tg_inbox_messages_no_truncate")]
    public async Task Immutable_inbox_guard_refuses_the_owner_and_a_removed_guard_is_a_failing_control(string sql, string trigger)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();
        await AnnounceAsync(owner, transaction, SchemaFixture.TenantA);
        await SchemaQueries.ExecuteAsync(owner, transaction, """
            INSERT INTO inbox_messages (tenant_id, subscription_id, event_id)
            VALUES (@tenant, @subscription, @event)
            """, ("tenant", SchemaFixture.TenantA), ("subscription", Subscription), ("event", Guid.NewGuid()));
        await transaction.SaveAsync("guard");
        var mutation = async () => await SchemaQueries.ExecuteAsync(owner, transaction, sql);
        var error = (await mutation.Should().ThrowAsync<PostgresException>()).Which;
        error.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        error.MessageText.Should().Contain("inbox_messages is immutable");
        await transaction.RollbackAsync("guard");
        // Remove only the owned trigger in a disposable database. The same SQL
        // must now reach the seeded row, proving that the guard above mattered.
        await SchemaQueries.ExecuteAsync(owner, transaction, $"DROP TRIGGER {trigger} ON inbox_messages");
        await using var dirty = new NpgsqlCommand(sql, (NpgsqlConnection)owner, (NpgsqlTransaction)transaction);
        (await dirty.ExecuteNonQueryAsync()).Should().Be(sql.StartsWith("TRUNCATE", StringComparison.Ordinal) ? -1 : 1);
    }

    [Theory]
    [InlineData("tenant", "00000000-0000-0000-0000-000000000000")]
    [InlineData("tenant", "00000000-0000-7000-8000-000000000002")]
    [InlineData("event", "00000000-0000-0000-0000-000000000000")]
    [InlineData("organization", "00000000-0000-0000-0000-000000000000")]
    [InlineData("actor", "00000000-0000-0000-0000-000000000000")]
    [InlineData("causation", "00000000-0000-0000-0000-000000000000")]
    public async Task Application_acceptance_refuses_invalid_identity_without_repair(string member, string invalid)
    {
        await using var app = await PostgresFixture.OpenAsync(schema.Postgres.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app);
        var tenant = member == "tenant" ? Guid.Parse(invalid) : SchemaFixture.TenantA;
        var eventId = member == "event" ? Guid.Parse(invalid) : Guid.NewGuid();
        Guid? organization = member == "organization" ? Guid.Parse(invalid) : null;
        await AnnounceAsync(app, transaction, tenant, organization);
        await transaction.SaveAsync("identity");
        var insert = async () => await SchemaQueries.ExecuteAsync(app, transaction,
            InsertDelivery.Replace("NULL, @actor", "@causation, @actor", StringComparison.Ordinal),
            ("tenant", tenant), ("subscription", Subscription), ("event", eventId),
            ("organization", (object?)organization ?? DBNull.Value), ("partition", tenant.ToString()),
            ("actor", member == "actor" ? Guid.Parse(invalid) : SchemaFixture.Actor),
            ("causation", member == "causation" ? Guid.Parse(invalid) : DBNull.Value));
        (await insert.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("ck_event_deliveries_identity");
        await transaction.RollbackAsync("identity");
        if (member is "tenant" or "event")
        {
            var marker = async () => await SchemaQueries.ExecuteAsync(app, transaction, """
                INSERT INTO inbox_messages (tenant_id, subscription_id, event_id)
                VALUES (@tenant, @subscription, @event)
                """, ("tenant", tenant), ("subscription", Subscription), ("event", eventId));
            (await marker.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("ck_inbox_messages_identity");
        }
    }

    [Fact]
    public async Task Scheduling_indexes_cover_eligibility_and_total_order_including_tied_event_ids()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        var tiedEvent = Guid.Parse("11111111-0000-4000-8000-000000000001");
        var taxonomy = "customization.initialize-taxonomy-default.v1";
        await using (var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString))
        await using (var transaction = await owner.BeginTransactionAsync())
        {
            // Equal availability, occurrence and event identities force selection
            // to use the final tenant/subscription tie-breakers.
            foreach (var (tenant, subscription) in new[]
            {
                (SchemaFixture.TenantB, Subscription), (SchemaFixture.TenantA, taxonomy), (SchemaFixture.TenantA, Subscription),
            })
            {
                await AnnounceAsync(owner, transaction, tenant);
                await InsertAsync(owner, transaction, tenant, null, tiedEvent, subscription);
                await SchemaQueries.ExecuteAsync(owner, transaction, """
                    UPDATE event_deliveries SET available_after = '2026-01-01', occurred_at = '2026-01-01'
                    WHERE tenant_id = @tenant AND subscription_id = @subscription AND event_id = @event
                    """, ("tenant", tenant), ("subscription", subscription), ("event", tiedEvent));
            }
            await AnnounceAsync(owner, transaction, SchemaFixture.TenantA);
            foreach (var assignment in new[]
            {
                "available_after = now() + interval '1 day'",
                $"locked_by = '{Token}', locked_until = now() + interval '1 day'",
                $"locked_by = '{Token}', locked_until = now() - interval '1 day'",
                "attempts = 6",
                "terminal_reason = 'invalid_subscription'",
                "terminal_reason = 'retry_exhausted', dead_lettered_at = now()",
                $"processed_at = now(), locked_by = '{Token}', locked_until = now() - interval '1 day', attempts = 6",
            })
            {
                var eventId = Guid.NewGuid();
                await InsertAsync(owner, transaction, SchemaFixture.TenantA, null, eventId);
                await SchemaQueries.ExecuteAsync(owner, transaction, $"UPDATE event_deliveries SET {assignment} WHERE event_id = @event", ("event", eventId));
            }
            await transaction.CommitAsync();
        }
        await using var scheduler = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        await using var queryTransaction = await scheduler.BeginTransactionAsync();
        // This proves each partial index can satisfy the production candidate query/order;
        // disabling sequential scans makes no claim about production cost estimates.
        await SchemaQueries.ExecuteAsync(scheduler, queryTransaction, "SET LOCAL enable_seqscan = off");
        var indexQueries = new[]
        {
            ("pending", "Normal", 3),
            ("recovery", "Recovery", 2),
            ("terminal", "Terminal", 1),
        };
        foreach (var (suffix, predicate, expected) in indexQueries)
        {
            var rows = await SchemaQueries.ReadStringsAsync(scheduler, $"""
                SELECT event_id::text || ':' || tenant_id::text || ':' || subscription_id FROM event_deliveries
                WHERE {DeliveryPredicate(predicate)}
                ORDER BY available_after, occurred_at, event_id, tenant_id, subscription_id
                """, queryTransaction);
            rows.Should().HaveCount(expected);
            if (suffix == "pending")
            {
                rows.Should().Equal(
                    $"{tiedEvent}:{SchemaFixture.TenantA}:{Subscription}",
                    $"{tiedEvent}:{SchemaFixture.TenantA}:{taxonomy}",
                    $"{tiedEvent}:{SchemaFixture.TenantB}:{Subscription}");
            }
            await using var selection = DeliverySelection(scheduler, queryTransaction, predicate);
            selection.CommandText = "EXPLAIN (FORMAT JSON) " + selection.CommandText;
            var plan = (string)(await selection.ExecuteScalarAsync())!;
            plan.Should().Contain($"\"Index Name\": \"ix_event_deliveries_{suffix}\"")
                .And.NotContain("\"Node Type\": \"Sort\"")
                .And.NotContain("\"Node Type\": \"Incremental Sort\"");
        }
    }

    private static string DeliveryPredicate(string name) =>
        typeof(DeliveryDispatchStore).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)?.GetRawConstantValue() as string
        ?? throw new InvalidOperationException("The production delivery eligibility predicate is missing: " + name);

    private static NpgsqlCommand DeliverySelection(DbConnection scheduler, DbTransaction transaction, string predicate)
    {
        // Read the actual predicates, including time/lease and manifest/partition
        // admission, so a production change cannot leave an independently copied
        // WHERE clause passing. Locking and LIMIT match AcquireAsync's candidate.
        var command = new NpgsqlCommand($"""
            SELECT tenant_id, subscription_id, event_id FROM event_deliveries
            WHERE {DeliveryPredicate(predicate)}{DeliveryPredicate("Admitted")}
            ORDER BY available_after, occurred_at, event_id, tenant_id, subscription_id
            FOR UPDATE SKIP LOCKED LIMIT 1
            """, (NpgsqlConnection)scheduler, (NpgsqlTransaction)transaction);
        command.Parameters.AddWithValue("held_types", Array.Empty<string>());
        command.Parameters.AddWithValue("held_subscriptions", Array.Empty<string>());
        command.Parameters.AddWithValue("blocked", Array.Empty<string>());
        return command;
    }

    [Fact]
    public async Task Scheduler_status_grants_allow_terminalization_and_targeted_replay_without_retention_dependencies()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        var eventId = Guid.NewGuid();
        await using (var app = await PostgresFixture.OpenAsync(database.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await SchemaQueries.AssertApplicationRoleAsync(app);
            await AnnounceAsync(app, transaction, SchemaFixture.TenantA);
            await InsertAsync(app, transaction, SchemaFixture.TenantA, null, eventId);
            await SchemaQueries.ExecuteAsync(app, transaction, """
                INSERT INTO outbox_messages (id, tenant_id, correlation_id, type, topic, partition_key, payload)
                VALUES (@event, @tenant, 'retention-control', 'TenantCreated', 'learnstack.tenancy.tenant', 'retained', '{}');
                INSERT INTO inbox_messages (tenant_id, subscription_id, event_id)
                VALUES (@tenant, @subscription, @marker)
                """, ("event", eventId), ("tenant", SchemaFixture.TenantA), ("subscription", Subscription), ("marker", Guid.NewGuid()));
            await transaction.CommitAsync();
        }
        await using (var scheduler = await PostgresFixture.OpenAsync(database.OutboxConnectionString))
        {
            await using var status = new NpgsqlCommand("""
                UPDATE event_deliveries SET attempts = 6, last_error = 'execution:handler:failed',
                    terminal_reason = 'retry_exhausted', dead_lettered_at = clock_timestamp(),
                    locked_by = NULL, locked_until = NULL
                WHERE tenant_id = @tenant AND subscription_id = @subscription AND event_id = @event
                    AND processed_at IS NULL
                RETURNING event_id
                """, (NpgsqlConnection)scheduler);
            status.Parameters.AddWithValue("tenant", SchemaFixture.TenantA);
            status.Parameters.AddWithValue("subscription", Subscription);
            status.Parameters.AddWithValue("event", eventId);
            (await status.ExecuteScalarAsync()).Should().Be(eventId);
            status.CommandText = """
                UPDATE event_deliveries SET attempts = 0, last_error = NULL,
                    terminal_reason = NULL, dead_lettered_at = NULL,
                    available_after = clock_timestamp(), locked_by = NULL, locked_until = NULL,
                    replay_count = replay_count + 1, replayed_at = clock_timestamp()
                WHERE tenant_id = @tenant AND subscription_id = @subscription AND event_id = @event
                    AND processed_at IS NULL AND dead_lettered_at IS NOT NULL AND locked_by IS NULL
                RETURNING replay_count
                """;
            (await status.ExecuteScalarAsync()).Should().Be(1, "all nine granted status columns support terminal and replay transitions");
        }
        await using (var platform = await PostgresFixture.OpenAsync(database.PlatformConnectionString))
        {
            await SchemaQueries.ExecuteAsync(platform, null, "DELETE FROM outbox_messages WHERE id = @event", ("event", eventId));
        }
        await using var verify = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var read = await verify.BeginTransactionAsync();
        await AnnounceAsync(verify, read, SchemaFixture.TenantA);
        (await SchemaQueries.CountAsync(verify, "SELECT count(*) FROM event_deliveries WHERE replay_count = 1 AND payload = '{\"version\":1}'::jsonb", read))
            .Should().Be(1, "producer retention must not cascade into retained subscription envelopes");
        (await SchemaQueries.CountAsync(verify, "SELECT count(*) FROM inbox_messages", read))
            .Should().Be(1, "replay and producer retention never reset completed deduplication history");
        (await SchemaQueries.CountAsync(verify, "SELECT count(*) FROM outbox_messages", read)).Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Application_organization_reads_widen_only_with_the_tenant_hatch(bool tenantHatch)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        var canaries = await SeedOrganizationCanariesAsync(database);
        var expected = new List<string> { canaries["tenant"].EventId.ToString(), canaries["own"].EventId.ToString() };
        if (tenantHatch) expected.Add(canaries["sibling"].EventId.ToString());
        (await ReadOrganizationCanariesAsync(database, tenantHatch)).Should().BeEquivalentTo(expected,
            "an A1 session reads its own and tenant-wide envelopes; only the tenant hatch adds A2, never tenant B");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Application_acceptance_requires_exact_organization_even_with_the_tenant_read_hatch(bool tenantHatch)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await SeedOrganizationCanariesAsync(database);
        (string Name, Guid? SessionOrg, Guid TargetTenant, Guid? TargetOrg, bool Allowed)[] cases =
        [
            ("tenant session / tenant row", null, SchemaFixture.TenantA, null, true),
            ("tenant session / A1 row", null, SchemaFixture.TenantA, SchemaFixture.OrgA1, false),
            ("tenant session / A2 row", null, SchemaFixture.TenantA, SchemaFixture.OrgA2, false),
            ("tenant session / foreign org", null, SchemaFixture.TenantA, SchemaFixture.OrgB1, false),
            ("A1 session / A1 row", SchemaFixture.OrgA1, SchemaFixture.TenantA, SchemaFixture.OrgA1, true),
            ("A1 session / tenant row", SchemaFixture.OrgA1, SchemaFixture.TenantA, null, false),
            ("A1 session / A2 row", SchemaFixture.OrgA1, SchemaFixture.TenantA, SchemaFixture.OrgA2, false),
            ("A1 session / foreign org", SchemaFixture.OrgA1, SchemaFixture.TenantA, SchemaFixture.OrgB1, false),
            ("A1 session / foreign tenant", SchemaFixture.OrgA1, SchemaFixture.TenantB, SchemaFixture.OrgA1, false),
            ("tenant session / foreign tenant", null, SchemaFixture.TenantB, null, false),
        ];
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        foreach (var testCase in cases)
        {
            await AnnounceAsync(app, transaction, SchemaFixture.TenantA, testCase.SessionOrg);
            await SchemaQueries.SetSettingAsync(app, transaction, "app.scope", tenantHatch ? "tenant" : "");
            await transaction.SaveAsync("acceptance");
            var eventId = Guid.NewGuid();
            var insert = async () => await InsertAsync(app, transaction, testCase.TargetTenant, testCase.TargetOrg, eventId);
            if (testCase.Allowed)
            {
                await insert.Should().NotThrowAsync(testCase.Name);
                (await CountEventAsync(app, transaction, "event_deliveries", eventId)).Should().Be(1, testCase.Name);
            }
            else
            {
                var error = (await insert.Should().ThrowAsync<PostgresException>(testCase.Name)).Which;
                error.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
                error.MessageText.Should().Contain("row-level security", "column permissions alone cannot prove scope enforcement");
            }
            await transaction.RollbackAsync("acceptance");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fenced_application_completion_writes_only_its_exact_organization_even_with_the_read_hatch(bool tenantHatch)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        var canaries = await SeedOrganizationCanariesAsync(database);
        foreach (var name in new[] { "tenant", "sibling", "foreign-tenant", "foreign-org" })
        {
            (await CompleteOrganizationCanaryAsync(database, canaries[name], tenantHatch)).Should().Be(0,
                $"A1 must not complete {name}, even with its correct token and live lease");
        }
        (await CompleteOrganizationCanaryAsync(database, canaries["own"], tenantHatch)).Should().Be(1,
            "the same fenced statement and granted completion column must succeed for A1's own live delivery");
        await using var scheduler = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        (await SchemaQueries.CountAsync(scheduler, "SELECT count(*) FROM event_deliveries WHERE processed_at IS NOT NULL"))
            .Should().Be(1);
    }

    [Fact]
    public async Task Organization_read_proof_detects_an_isolated_tenant_only_using_policy_mutant()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        var canaries = await SeedOrganizationCanariesAsync(database);
        (await ReadOrganizationCanariesAsync(database, tenantHatch: false)).Should().BeEquivalentTo(
            [canaries["tenant"].EventId.ToString(), canaries["own"].EventId.ToString()]);
        await using (var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString))
        {
            // Change only the disposable database's READ predicate; retain tenant
            // isolation, WITH CHECK and both restrictive write guards.
            await SchemaQueries.ExecuteAsync(owner, null, """
                ALTER POLICY event_deliveries_isolation ON event_deliveries
                USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                """);
        }
        (await ReadOrganizationCanariesAsync(database, tenantHatch: false)).Should().BeEquivalentTo(
            [canaries["tenant"].EventId.ToString(), canaries["own"].EventId.ToString(), canaries["sibling"].EventId.ToString()],
            "the planted missing organization READ term must expose A2 while tenant B stays hidden");
    }

    [Fact]
    public async Task Acceptance_scope_proof_detects_an_isolated_read_hatch_in_with_check_mutant()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await SeedOrganizationCanariesAsync(database);
        await using (var app = await PostgresFixture.OpenAsync(database.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await SchemaQueries.AssertApplicationRoleAsync(app);
            await AnnounceAsync(app, transaction, SchemaFixture.TenantA, SchemaFixture.OrgA1);
            await SchemaQueries.SetSettingAsync(app, transaction, "app.scope", "tenant");
            var insert = async () => await InsertAsync(app, transaction, SchemaFixture.TenantA, null, Guid.NewGuid());
            var error = (await insert.Should().ThrowAsync<PostgresException>()).Which;
            error.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
            error.MessageText.Should().Contain("row-level security");
        }
        await using (var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString))
        {
            // The historical failure shape copies the permissive READ hatch into
            // INSERT's WITH CHECK. Restrictive UPDATE/DELETE guards cannot fix INSERT.
            await SchemaQueries.ExecuteAsync(owner, null, """
                ALTER POLICY event_deliveries_isolation ON event_deliveries
                WITH CHECK (
                    tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                    AND (organization_id IS NULL
                         OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
                         OR current_setting('app.scope', true) = 'tenant'))
                """);
        }
        await using var dirtyApp = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(dirtyApp);
        await using var dirtyTransaction = await dirtyApp.BeginTransactionAsync();
        await AnnounceAsync(dirtyApp, dirtyTransaction, SchemaFixture.TenantA, SchemaFixture.OrgA1);
        await SchemaQueries.SetSettingAsync(dirtyApp, dirtyTransaction, "app.scope", "tenant");
        var dirtyEvent = Guid.NewGuid();
        await InsertAsync(dirtyApp, dirtyTransaction, SchemaFixture.TenantA, null, dirtyEvent);
        (await CountEventAsync(dirtyApp, dirtyTransaction, "event_deliveries", dirtyEvent)).Should().Be(1,
            "a weakened WITH CHECK must make the forbidden A1-to-tenant-wide acceptance observable");
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("sibling")]
    public async Task Restrictive_update_guard_stops_targeting_before_the_remaining_with_check_backstop(string target)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        var canaries = await SeedOrganizationCanariesAsync(database);
        (await CompleteOrganizationCanaryAsync(database, canaries[target], tenantHatch: true)).Should().Be(0,
            "the tenant READ hatch must not admit a readable foreign-organization row as an UPDATE target");
        await using (var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString))
        {
            // Drop only the restrictive UPDATE guard in this disposable database.
            // WITH CHECK remains exact, so the mutant targets the row but still
            // cannot commit it: its observable result changes from zero to 42501.
            await SchemaQueries.ExecuteAsync(owner, null,
                "DROP POLICY event_deliveries_org_write_guard ON event_deliveries");
        }
        var dirtyCompletion = async () => await CompleteOrganizationCanaryAsync(database, canaries[target], tenantHatch: true);
        var error = (await dirtyCompletion.Should().ThrowAsync<PostgresException>()).Which;
        error.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        error.MessageText.Should().Contain("row-level security");
        (await CompleteOrganizationCanaryAsync(database, canaries["own"], tenantHatch: true)).Should().Be(1,
            "the unchanged WITH CHECK still accepts A1's own leased delivery");
        await using var scheduler = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        (await SchemaQueries.CountAsync(scheduler, "SELECT count(*) FROM event_deliveries WHERE processed_at IS NOT NULL"))
            .Should().Be(1, "the removed guard does not bypass the remaining exact WITH CHECK");
    }

    private sealed record OrganizationCanary(Guid TenantId, Guid? OrganizationId, Guid EventId, string LeaseToken);

    private static async Task<Dictionary<string, OrganizationCanary>> SeedOrganizationCanariesAsync(DisposableSchemaDatabase database)
    {
        var canaries = new Dictionary<string, OrganizationCanary>(StringComparer.Ordinal)
        {
            ["tenant"] = new(SchemaFixture.TenantA, null, Guid.NewGuid(), Guid.NewGuid().ToString()),
            ["own"] = new(SchemaFixture.TenantA, SchemaFixture.OrgA1, Guid.NewGuid(), Guid.NewGuid().ToString()),
            ["sibling"] = new(SchemaFixture.TenantA, SchemaFixture.OrgA2, Guid.NewGuid(), Guid.NewGuid().ToString()),
            ["foreign-tenant"] = new(SchemaFixture.TenantB, null, Guid.NewGuid(), Guid.NewGuid().ToString()),
            ["foreign-org"] = new(SchemaFixture.TenantB, SchemaFixture.OrgB1, Guid.NewGuid(), Guid.NewGuid().ToString()),
        };
        await using (var app = await PostgresFixture.OpenAsync(database.AppConnectionString))
        {
            await SchemaQueries.AssertApplicationRoleAsync(app);
            await using var transaction = await app.BeginTransactionAsync();
            foreach (var canary in canaries.Values)
            {
                await AnnounceAsync(app, transaction, canary.TenantId, canary.OrganizationId);
                await InsertAsync(app, transaction, canary.TenantId, canary.OrganizationId, canary.EventId);
            }
            await transaction.CommitAsync();
        }
        await using var scheduler = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        foreach (var canary in canaries.Values)
        {
            await using var lease = new NpgsqlCommand("""
                UPDATE event_deliveries SET locked_by = @token,
                    locked_until = clock_timestamp() + interval '30 seconds', attempts = 1
                WHERE tenant_id = @tenant AND subscription_id = @subscription AND event_id = @event
                    AND processed_at IS NULL AND terminal_reason IS NULL AND dead_lettered_at IS NULL
                    AND locked_by IS NULL
                """, (NpgsqlConnection)scheduler);
            lease.Parameters.AddWithValue("tenant", canary.TenantId);
            lease.Parameters.AddWithValue("subscription", Subscription);
            lease.Parameters.AddWithValue("event", canary.EventId);
            lease.Parameters.AddWithValue("token", canary.LeaseToken);
            (await lease.ExecuteNonQueryAsync()).Should().Be(1);
        }
        (await SchemaQueries.CountAsync(scheduler, """
            SELECT count(*) FROM event_deliveries
            WHERE processed_at IS NULL AND locked_by IS NOT NULL AND locked_until > clock_timestamp()
            """)).Should().Be(5, "every refusal must have a real live leased row, rather than a missing or expired target");
        return canaries;
    }

    private static async Task<List<string>> ReadOrganizationCanariesAsync(DisposableSchemaDatabase database, bool tenantHatch)
    {
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await AnnounceAsync(app, transaction, SchemaFixture.TenantA, SchemaFixture.OrgA1);
        await SchemaQueries.SetSettingAsync(app, transaction, "app.scope", tenantHatch ? "tenant" : "");
        // No application WHERE clause can hide a missing tenant/org policy here.
        return await SchemaQueries.ReadStringsAsync(app, "SELECT event_id::text FROM event_deliveries", transaction);
    }

    private static async Task<int> CompleteOrganizationCanaryAsync(
        DisposableSchemaDatabase database, OrganizationCanary canary, bool tenantHatch)
    {
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await AnnounceAsync(app, transaction, SchemaFixture.TenantA, SchemaFixture.OrgA1);
        await SchemaQueries.SetSettingAsync(app, transaction, "app.scope", tenantHatch ? "tenant" : "");
        await using var complete = new NpgsqlCommand("""
            UPDATE event_deliveries SET processed_at = clock_timestamp()
            WHERE tenant_id = @tenant AND subscription_id = @subscription AND event_id = @event
                AND processed_at IS NULL AND dead_lettered_at IS NULL AND terminal_reason IS NULL
                AND locked_by = @token AND locked_until > clock_timestamp()
            """, (NpgsqlConnection)app, (NpgsqlTransaction)transaction);
        complete.Parameters.AddWithValue("tenant", canary.TenantId);
        complete.Parameters.AddWithValue("subscription", Subscription);
        complete.Parameters.AddWithValue("event", canary.EventId);
        complete.Parameters.AddWithValue("token", canary.LeaseToken);
        var affected = await complete.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        return affected;
    }

    private static PlatformDbContext Platform(DisposableSchemaDatabase database) => new(
        new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(database.MigrationConnectionString,
            provider => provider.MigrationsHistoryTable(PlatformDbContextFactory.HistoryTable)).Options);

    private static async Task AnnounceAsync(DbConnection connection, DbTransaction transaction, Guid tenant, Guid? organization = null)
    {
        await SchemaQueries.SetTenantAsync(connection, transaction, tenant);
        await SchemaQueries.SetSettingAsync(connection, transaction, "app.organization_id", organization?.ToString() ?? "");
        await SchemaQueries.SetSettingAsync(connection, transaction, "app.scope", "");
    }

    private static Task InsertAsync(DbConnection connection, DbTransaction transaction, Guid tenant,
        Guid? organization, Guid eventId, string subscription = Subscription) =>
        SchemaQueries.ExecuteAsync(connection, transaction, InsertDelivery,
            ("tenant", tenant), ("subscription", subscription), ("event", eventId),
            ("organization", (object?)organization ?? DBNull.Value), ("actor", SchemaFixture.Actor), ("partition", tenant.ToString()));

    private static async Task<long> CountEventAsync(DbConnection connection, DbTransaction transaction, string table, Guid eventId)
    {
        // Table comes only from the two literal callers in this test.
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table} WHERE event_id = @event",
            (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        command.Parameters.AddWithValue("event", eventId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<List<string>> GrantedColumnsAsync(DbConnection connection, string role, string table, string privilege)
    {
        // DELETE has no column privilege in PostgreSQL; table privileges still
        // appear in column_privileges for SELECT/INSERT/UPDATE automatically.
        await using var command = new NpgsqlCommand("""
            SELECT column_name FROM information_schema.column_privileges
            WHERE table_schema = 'public' AND grantee = @role AND table_name = @table
              AND privilege_type = @privilege ORDER BY column_name
            """, (NpgsqlConnection)connection);
        command.Parameters.AddWithValue("role", role);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("privilege", privilege);
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(reader.GetString(0));
        return result;
    }

    private static async Task<List<string>> InventoryAsync(string connectionString)
    {
        await using var owner = await PostgresFixture.OpenAsync(connectionString);
        return await SchemaQueries.ReadStringsAsync(owner, """
            SELECT item FROM (
                SELECT 'column:' || table_name || ':' || ordinal_position || ':' || column_name || ':' ||
                       data_type || ':' || is_nullable || ':' || coalesce(column_default, '') AS item
                FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name IN ('event_deliveries','inbox_messages')
                UNION ALL
                SELECT 'constraint:' || c.relname || ':' || conname || ':' || pg_get_constraintdef(k.oid)
                FROM pg_constraint k JOIN pg_class c ON c.oid = k.conrelid
                WHERE c.relnamespace = 'public'::regnamespace AND c.relname IN ('event_deliveries','inbox_messages')
                UNION ALL
                SELECT 'index:' || tablename || ':' || indexname || ':' || indexdef FROM pg_indexes
                WHERE schemaname = 'public' AND tablename IN ('event_deliveries','inbox_messages')
                UNION ALL
                SELECT 'policy:' || tablename || ':' || policyname || ':' || permissive || ':' || roles::text || ':' || cmd || ':' ||
                       coalesce(qual, '') || ':' || coalesce(with_check, '') FROM pg_policies
                WHERE schemaname = 'public' AND tablename IN ('event_deliveries','inbox_messages')
                UNION ALL
                SELECT 'rls:' || relname || ':' || relrowsecurity || ':' || relforcerowsecurity FROM pg_class
                WHERE relnamespace = 'public'::regnamespace AND relname IN ('event_deliveries','inbox_messages')
                UNION ALL
                SELECT 'grant:' || table_name || ':' || grantee || ':' || column_name || ':' || privilege_type
                FROM information_schema.column_privileges
                WHERE table_schema = 'public' AND table_name IN ('event_deliveries','inbox_messages')
                UNION ALL
                SELECT 'table-grant:' || table_name || ':' || grantee || ':' || privilege_type
                FROM information_schema.table_privileges
                WHERE table_schema = 'public' AND table_name IN ('event_deliveries','inbox_messages')
                UNION ALL
                SELECT 'trigger:' || c.relname || ':' || pg_get_triggerdef(t.oid)
                FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid
                WHERE c.relnamespace = 'public'::regnamespace AND c.relname IN ('event_deliveries','inbox_messages') AND NOT t.tgisinternal
                UNION ALL
                SELECT 'function:' || pg_get_functiondef(oid) FROM pg_proc
                WHERE pronamespace = 'public'::regnamespace AND proname = 'fn_inbox_messages_immutable'
            ) inventory ORDER BY item
            """);
    }
}
