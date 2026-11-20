using System.Text.Json;
using FluentAssertions;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.MultiTenancy;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Infrastructure.PlatformOperator;
using LearnStack.Modules.Tenancy.Application.Audit;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Messaging;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using LearnStack.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The operator's metadata-only inspection of dead-lettered work against a real database and the real platform
/// scope (ADR-0103 § 1): its pages, its cursors, the one entry row each page writes and what the platform role's
/// column grants refuse.
/// </summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class DeliveryInspectionTests(SchemaFixture schema)
{
    private const string Subscription = "test.inspect.v1";
    private static readonly string KnownType = typeof(InspectionEvent).AssemblyQualifiedName!;

    [Fact]
    public async Task A_page_lists_dead_lettered_outbox_rows_oldest_first_with_the_fence_and_pages_by_cursor()
    {
        await using var proof = await Proof.CreateAsync(schema.Postgres);
        var ids = new[] { Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7() };
        for (var index = 0; index < ids.Length; index++)
            await proof.OutboxAsync(ids[index], $"attempts = 6, terminal_reason = 'retry_exhausted', dead_lettered_at = '2026-10-0{index + 1} 12:00:00.123456+00'");
        await proof.OutboxAsync(Guid.CreateVersion7(), "processed_at = now()");
        await proof.OutboxAsync(Guid.CreateVersion7(), "attempts = 6, terminal_reason = 'retry_exhausted'");

        var first = await proof.Inspection.InspectOutboxAsync(proof.Admission(), DeliveryInspectionState.DeadLettered, null, 2);
        var second = await proof.Inspection.InspectOutboxAsync(proof.Admission(), DeliveryInspectionState.DeadLettered, first!.NextCursor, 2);

        first.Items.Select(row => row.EventId).Should().Equal(ids[0], ids[1]);
        first.NextCursor.Should().NotBeNull();
        second!.Items.Select(row => row.EventId).Should().Equal([ids[2]], "a page after the last row holds only it");
        second.NextCursor.Should().BeNull();
        var row = first.Items[0];
        row.TenantId.Should().Be(SchemaFixture.TenantA);
        row.Attempts.Should().Be(6);
        row.TerminalReason.Should().Be("retry_exhausted");
        row.DeadLetteredAt!.Value.UtcTicks.Should().Be(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero).AddTicks(1234560).UtcTicks,
            "the fence instant comes back to the microsecond");
        row.EventType.Should().Be("InspectionEvent", "the registry's label stands for the stored type text");
        row.Actionable.Should().BeTrue();
    }

    [Fact]
    public async Task A_terminal_requested_row_is_listed_and_never_actionable()
    {
        await using var proof = await Proof.CreateAsync(schema.Postgres);
        var id = Guid.CreateVersion7();
        await proof.OutboxAsync(id, "attempts = 6, terminal_reason = 'retry_exhausted'");
        await proof.OutboxAsync(Guid.CreateVersion7(), "attempts = 6, terminal_reason = 'retry_exhausted', dead_lettered_at = now()");

        var page = await proof.Inspection.InspectOutboxAsync(proof.Admission(), DeliveryInspectionState.TerminalRequested, null, 10);

        var row = page!.Items.Should().ContainSingle().Which;
        row.EventId.Should().Be(id);
        row.DeadLetteredAt.Should().BeNull();
        row.Actionable.Should().BeFalse("its state is shown and a reset is not offered");
    }

    [Fact]
    public async Task Deliveries_show_the_replay_fence_and_mark_what_a_replay_could_act_on()
    {
        await using var proof = await Proof.CreateAsync(schema.Postgres);
        var replayable = Guid.CreateVersion7();
        var envelope = Guid.CreateVersion7();
        await proof.DeliveryAsync(replayable, Subscription, "terminal_reason = 'retry_exhausted', dead_lettered_at = '2026-10-01 12:00:00.5+00', replay_count = 2, replayed_at = now()");
        await proof.DeliveryAsync(envelope, Subscription, "terminal_reason = 'invalid_envelope', dead_lettered_at = '2026-10-02 12:00:00+00'");

        var page = await proof.Inspection.InspectDeliveriesAsync(proof.Admission(), DeliveryInspectionState.DeadLettered, null, 10);

        page!.Items.Select(row => row.EventId).Should().Equal(replayable, envelope);
        page.Items[0].Should().Match<EventDeliveryInspection>(row => row.ReplayCount == 2 && row.TerminalReason == "retry_exhausted"
            && row.SubscriptionId == Subscription && row.Actionable && row.EventType == "InspectionEvent");
        page.Items[1].Actionable.Should().BeFalse("an envelope that is invalid cannot become valid");
    }

    [Fact]
    public async Task Quarantined_jobs_list_with_their_reservation_occupancy_and_no_parameters()
    {
        await using var proof = await Proof.CreateAsync(schema.Postgres);
        var job = await BackgroundJobRuntimeRows.InsertAsync(
            proof.Database.AppConnectionString, definition: "fixture.unknown.v1", tenant: SchemaFixture.TenantA);
        await using (var dispatcher = await PostgresFixture.OpenAsync(proof.Database.OutboxConnectionString))
            await SchemaQueries.ExecuteAsync(dispatcher, null,
                "UPDATE background_jobs SET phase = 'quarantined', quarantine_reason = 'unknown_outcome' WHERE id = @id", ("id", job.Value));

        var page = await proof.Inspection.InspectJobsAsync(proof.Admission(), null, 10);

        var row = page!.Items.Should().ContainSingle().Which;
        row.JobId.Should().Be(job.Value);
        row.Phase.Should().Be("quarantined");
        row.QuarantineReason.Should().Be("unknown_outcome");
        row.DefinitionKey.Should().Be("fixture.unknown.v1");
        row.HeldReservations.Should().Be(0);
        JsonSerializer.Serialize(row).Should().NotContain("parameters", "no envelope or parameter column is read");
    }

    [Fact]
    public async Task Each_page_writes_one_entry_row_naming_the_inspection_and_no_result_row_and_the_scope_changes_nothing()
    {
        await using var proof = await Proof.CreateAsync(schema.Postgres);
        await proof.OutboxAsync(Guid.CreateVersion7(), "attempts = 6, terminal_reason = 'retry_exhausted', dead_lettered_at = now()");
        var admission = proof.Admission("INC-103");

        await proof.Inspection.InspectOutboxAsync(admission, DeliveryInspectionState.DeadLettered, null, 10);

        var rows = await proof.AuditAsync(admission.Operator.CorrelationId!);
        var entry = rows.Should().ContainSingle("the scope reads and changes nothing, so it writes no result row").Which;
        entry.Operation.Should().Be(PlatformAdminScope.EnterOperation);
        entry.Reason.Should().Be("delivery_inspect");
        entry.Metadata.Should().Contain("INC-103", "a sentinel-tenant row is unreadable to every tenant, so it carries the ticket");
    }

    [Fact]
    public async Task A_malformed_or_foreign_cursor_answers_null_and_enters_no_scope()
    {
        await using var proof = await Proof.CreateAsync(schema.Postgres);
        var admission = proof.Admission();

        (await proof.Inspection.InspectOutboxAsync(admission, DeliveryInspectionState.DeadLettered, "not-a-cursor", 10)).Should().BeNull();
        (await proof.Inspection.InspectDeliveriesAsync(proof.Admission(), DeliveryInspectionState.DeadLettered,
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("v1~outbox.DeadLettered~1~x")), 10)).Should().BeNull("a cursor of another resource is refused");

        (await proof.AuditAsync(admission.Operator.CorrelationId!)).Should().BeEmpty();
    }

    [Fact]
    public async Task The_platform_role_cannot_select_an_excluded_column_of_any_inspected_table()
    {
        await using var proof = await Proof.CreateAsync(schema.Postgres);
        await using var platform = await PostgresFixture.OpenAsync(proof.Database.PlatformConnectionString);

        foreach (var sql in new[]
                 {
                     "SELECT payload FROM outbox_messages", "SELECT correlation_id FROM outbox_messages",
                     "SELECT locked_by FROM outbox_messages", "SELECT * FROM outbox_messages",
                     "SELECT payload FROM event_deliveries", "SELECT correlation_id FROM event_deliveries",
                     "SELECT locked_by FROM event_deliveries", "SELECT * FROM event_deliveries",
                     "SELECT parameters FROM background_jobs", "SELECT plan FROM background_jobs",
                     "SELECT traceparent FROM background_jobs", "SELECT operation_id FROM background_jobs",
                     "SELECT ownership_token FROM background_jobs", "SELECT * FROM background_jobs",
                 })
        {
            await FluentActions.Awaiting(() => SchemaQueries.ExecuteAsync(platform, null, sql)).Should()
                .ThrowAsync<PostgresException>(sql).Where(failure => failure.SqlState == PostgresErrorCodes.InsufficientPrivilege, sql);
        }

        await SchemaQueries.ExecuteAsync(platform, null, "SELECT id, attempts, dead_lettered_at FROM outbox_messages");
        await SchemaQueries.ExecuteAsync(platform, null, "SELECT job_id, released_at FROM background_job_reservations");
    }

    [Fact]
    public async Task Job_occupancy_counts_the_unreleased_reservations_of_the_job_and_of_its_capacity_group()
    {
        await using var proof = await Proof.CreateAsync(schema.Postgres);
        var held = await BackgroundJobRuntimeRows.InsertAsync(
            proof.Database.AppConnectionString, definition: "fixture.unknown.v1", tenant: SchemaFixture.TenantA,
            capacityGroup: "fixture.resource");
        var neighbour = await BackgroundJobRuntimeRows.InsertAsync(
            proof.Database.AppConnectionString, definition: "fixture.unknown.v1", tenant: SchemaFixture.TenantA,
            capacityGroup: "fixture.resource");
        await using (var dispatcher = await PostgresFixture.OpenAsync(proof.Database.OutboxConnectionString))
            await SchemaQueries.ExecuteAsync(dispatcher, null,
                "UPDATE background_jobs SET phase = 'quarantined', quarantine_reason = 'unknown_outcome' WHERE id = @id", ("id", held.Value));
        // The fixture rows are written with the foreign keys and guard triggers off: the proof is the two counts, and
        // a reservation's own lifecycle has its own proofs. One unreleased and one released reservation belong to the
        // quarantined job; a second job of the same group holds one more.
        await proof.SqlAsync($"""
            SET session_replication_role = replica;
            INSERT INTO background_job_capacity (tenant_id, capacity_group)
                SELECT DISTINCT tenant_id, capacity_group FROM background_jobs WHERE id IN ('{held.Value}', '{neighbour.Value}')
                ON CONFLICT DO NOTHING;
            INSERT INTO background_job_attempts (id, tenant_id, job_id, stage, ordinal, admission_token, deadline)
                SELECT uuidv7(), tenant_id, id, 'normal', ordinal, uuidv7(), clock_timestamp() + interval '4 minutes'
                FROM background_jobs, (VALUES (1), (2)) AS attempts(ordinal) WHERE id = '{held.Value}'
                UNION ALL
                SELECT uuidv7(), tenant_id, id, 'normal', 1, uuidv7(), clock_timestamp() + interval '4 minutes'
                FROM background_jobs WHERE id = '{neighbour.Value}';
            INSERT INTO background_job_reservations (tenant_id, capacity_group, job_id, attempt_id, reserved_at, released_at, release_reason)
                SELECT j.tenant_id, j.capacity_group, j.id, a.id, clock_timestamp() - interval '1 minute',
                    CASE WHEN a.ordinal = 2 AND j.id = '{held.Value}' THEN clock_timestamp() END,
                    CASE WHEN a.ordinal = 2 AND j.id = '{held.Value}' THEN 'completed' END
                FROM background_jobs j JOIN background_job_attempts a ON a.job_id = j.id AND a.tenant_id = j.tenant_id
                WHERE j.id IN ('{held.Value}', '{neighbour.Value}');
            """);

        var page = await proof.Inspection.InspectJobsAsync(proof.Admission(), null, 10);

        var row = page!.Items.Should().ContainSingle("only the quarantined job is listed").Which;
        row.JobId.Should().Be(held.Value);
        row.HeldReservations.Should().Be(1, "the released reservation of the job is not held");
        row.GroupOccupancy.Should().Be(2, "the job's unreleased reservation and its neighbour's, both of one capacity group");
    }

    [Fact]
    public async Task A_cursor_belongs_to_the_resource_and_state_that_issued_it_even_when_its_key_is_valid_for_another()
    {
        await using var proof = await Proof.CreateAsync(schema.Postgres);
        for (var index = 1; index <= 2; index++)
        {
            await proof.OutboxAsync(Guid.CreateVersion7(), $"attempts = 6, terminal_reason = 'retry_exhausted', dead_lettered_at = '2026-10-0{index} 12:00:00+00'");
            await proof.DeliveryAsync(Guid.CreateVersion7(), Subscription, $"terminal_reason = 'retry_exhausted', dead_lettered_at = '2026-10-0{index} 12:00:00+00'");
        }

        var outbox = (await proof.Inspection.InspectOutboxAsync(proof.Admission(), DeliveryInspectionState.DeadLettered, null, 1))!.NextCursor;
        var delivery = (await proof.Inspection.InspectDeliveriesAsync(proof.Admission(), DeliveryInspectionState.DeadLettered, null, 1))!.NextCursor;
        outbox.Should().NotBeNull();
        delivery.Should().NotBeNull();

        // Each was issued for the route and state it came from: the same position under another one is refused.
        (await proof.Inspection.InspectOutboxAsync(proof.Admission(), DeliveryInspectionState.TerminalRequested, outbox, 10)).Should().BeNull();
        (await proof.Inspection.InspectOutboxAsync(proof.Admission(), DeliveryInspectionState.DeadLettered, delivery, 10)).Should().BeNull();
        (await proof.Inspection.InspectDeliveriesAsync(proof.Admission(), DeliveryInspectionState.DeadLettered, outbox, 10)).Should().BeNull();
        (await proof.Inspection.InspectDeliveriesAsync(proof.Admission(), DeliveryInspectionState.TerminalRequested, delivery, 10)).Should().BeNull();
        (await proof.Inspection.InspectOutboxAsync(proof.Admission(), DeliveryInspectionState.DeadLettered, outbox, 10)).Should().NotBeNull();
    }

    [Fact]
    public async Task Deliveries_page_through_equal_instants_without_a_repeat_or_a_gap()
    {
        await using var proof = await Proof.CreateAsync(schema.Postgres);
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.CreateVersion7()).ToArray();
        foreach (var id in ids)
            await proof.DeliveryAsync(id, Subscription, "terminal_reason = 'retry_exhausted', dead_lettered_at = '2026-10-01 12:00:00.000001+00'");

        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await proof.Inspection.InspectDeliveriesAsync(proof.Admission(), DeliveryInspectionState.DeadLettered, cursor, 2);
            seen.AddRange(page!.Items.Select(row => row.EventId));
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null && pages < 10);

        pages.Should().Be(3);
        seen.Should().BeEquivalentTo(ids, "every row once").And.OnlyHaveUniqueItems();
        seen.Should().Equal(ids.OrderBy(id => id), "equal instants are ordered by the row's own key");
    }

    [Fact]
    public async Task Terminal_requested_rows_page_in_the_order_their_dead_letter_is_due()
    {
        await using var proof = await Proof.CreateAsync(schema.Postgres);
        var late = Guid.CreateVersion7();
        var early = Guid.CreateVersion7();
        await proof.OutboxAsync(late, "attempts = 6, terminal_reason = 'retry_exhausted', available_after = '2026-10-03 12:00:00+00'");
        await proof.OutboxAsync(early, "attempts = 6, terminal_reason = 'retry_exhausted', available_after = '2026-10-02 12:00:00+00'");

        var first = await proof.Inspection.InspectOutboxAsync(proof.Admission(), DeliveryInspectionState.TerminalRequested, null, 1);
        var second = await proof.Inspection.InspectOutboxAsync(proof.Admission(), DeliveryInspectionState.TerminalRequested, first!.NextCursor, 1);

        first.Items.Single().EventId.Should().Be(early);
        second!.Items.Single().EventId.Should().Be(late);
        second.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task The_platform_role_can_select_exactly_the_twelve_outbox_columns_the_inspection_projects()
    {
        await using var proof = await Proof.CreateAsync(schema.Postgres);
        await using var platform = await PostgresFixture.OpenAsync(proof.Database.PlatformConnectionString);
        await using var command = new NpgsqlCommand("""
            SELECT attname FROM pg_attribute
            WHERE attrelid = 'outbox_messages'::regclass AND attnum > 0 AND NOT attisdropped
                AND has_column_privilege('learnstack_platform', 'outbox_messages', attnum, 'SELECT')
            ORDER BY attname
            """, (NpgsqlConnection)platform);
        var granted = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) granted.Add(reader.GetString(0));

        granted.Should().Equal(
            "attempts", "available_after", "dead_lettered_at", "id", "last_error", "locked_until", "occurred_at",
            "organization_id", "processed_at", "tenant_id", "terminal_reason", "type");
    }

    [Theory]
    [InlineData("publish:failed:provider", "publish:failed:provider")]
    [InlineData("migration:legacy:redacted", "migration:legacy:redacted")]
    [InlineData("<b>stored text</b>", "unrecognized")]
    [InlineData("Free Text With Spaces", "unrecognized")]
    public async Task The_last_error_a_page_shows_is_a_descriptor_or_the_word_unrecognized(string stored, string shown)
    {
        await using var proof = await Proof.CreateAsync(schema.Postgres);
        await proof.OutboxAsync(Guid.CreateVersion7(), $"attempts = 6, terminal_reason = 'retry_exhausted', dead_lettered_at = now(), last_error = '{stored}'");
        await proof.DeliveryAsync(Guid.CreateVersion7(), Subscription, $"terminal_reason = 'retry_exhausted', dead_lettered_at = now(), last_error = '{stored}'");

        var outbox = await proof.Inspection.InspectOutboxAsync(proof.Admission(), DeliveryInspectionState.DeadLettered, null, 10);
        var delivery = await proof.Inspection.InspectDeliveriesAsync(proof.Admission(), DeliveryInspectionState.DeadLettered, null, 10);

        outbox!.Items.Single().LastError.Should().Be(shown);
        delivery!.Items.Single().LastError.Should().Be(shown);
    }

    [Fact]
    public async Task A_refused_credential_is_a_defect_and_an_unreachable_database_is_a_retryable_outage()
    {
        await using var proof = await Proof.CreateAsync(schema.Postgres);
        var wrongPassword = new NpgsqlConnectionStringBuilder(proof.Database.PlatformConnectionString)
        {
            Password = "not-the-password",
            Pooling = false,
        }.ConnectionString;
        var unreachable = new NpgsqlConnectionStringBuilder(proof.Database.PlatformConnectionString)
        {
            Host = "127.0.0.1",
            Port = 1,
            Pooling = false,
            Timeout = 3,
        }.ConnectionString;
        await using var refusedSource = LearnStack.Api.Composition.PersistenceCompositionExtensions.BuildPlatformDataSource(wrongPassword);
        await using var unreachableSource = LearnStack.Api.Composition.PersistenceCompositionExtensions.BuildPlatformDataSource(unreachable);

        // A retry cannot cure a refused credential, so it surfaces as the defect it is and not as a retryable outage.
        var refused = async () => await proof.InspectionOver(refusedSource)
            .InspectOutboxAsync(proof.Admission(), DeliveryInspectionState.DeadLettered, null, 10);
        (await refused.Should().ThrowAsync<NpgsqlException>()).Which.IsTransient.Should().BeFalse();

        var outage = async () => await proof.InspectionOver(unreachableSource)
            .InspectDeliveriesAsync(proof.Admission(), DeliveryInspectionState.DeadLettered, null, 10);
        (await outage.Should().ThrowAsync<OperatorRecoveryUnavailableException>()).Which.InnerException.Should().BeAssignableTo<NpgsqlException>();
    }

    [Fact]
    public async Task Deliveries_with_equal_instants_page_by_tenant_then_subscription_then_event()
    {
        await using var proof = await Proof.CreateAsync(schema.Postgres);
        // The event ids run against the subscriptions' order, so a keyset that dropped a column would skip a row.
        var larger = Guid.Parse("ffffffff-0000-7000-8000-000000000001");
        var smaller = Guid.Parse("00000000-0000-7000-8000-000000000001");
        const string instant = "terminal_reason = 'retry_exhausted', dead_lettered_at = '2026-10-01 12:00:00.000001+00'";
        await proof.DeliveryAsync(larger, "a.first.v1", instant);
        await proof.DeliveryAsync(smaller, "b.second.v1", instant);

        var first = await proof.Inspection.InspectDeliveriesAsync(proof.Admission(), DeliveryInspectionState.DeadLettered, null, 1);
        var second = await proof.Inspection.InspectDeliveriesAsync(proof.Admission(), DeliveryInspectionState.DeadLettered, first!.NextCursor, 1);

        first.Items.Single().SubscriptionId.Should().Be("a.first.v1");
        second!.Items.Single().SubscriptionId.Should().Be("b.second.v1", "the subscription orders before the event id");
        second.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task The_dead_letter_indexes_exist_with_the_keys_and_predicates_the_inspection_orders_and_filters_by()
    {
        await using var proof = await Proof.CreateAsync(schema.Postgres);
        await using var platform = await PostgresFixture.OpenAsync(proof.Database.PlatformConnectionString);
        async Task<string?> Definition(string index)
        {
            await using var command = new NpgsqlCommand("SELECT indexdef FROM pg_indexes WHERE indexname = @name", (NpgsqlConnection)platform);
            command.Parameters.AddWithValue("name", index);
            return await command.ExecuteScalarAsync() as string;
        }

        (await Definition("ix_outbox_messages_dead_lettered")).Should()
            .Contain("(dead_lettered_at, id)").And.Contain("WHERE (dead_lettered_at IS NOT NULL)");
        (await Definition("ix_event_deliveries_dead_lettered")).Should()
            .Contain("(dead_lettered_at, tenant_id, subscription_id, event_id)").And.Contain("WHERE (dead_lettered_at IS NOT NULL)");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_dead_lettered_pages_use_their_partial_index_in_key_order_with_no_sort(bool afterCursor)
    {
        // The statement is the text the inspection runs. Sequential and bitmap scans are disabled so that the only
        // plan left is an ordered scan of the index: the index being absent from it, or a Sort above it, means the
        // statement's predicate or its order no longer matches the index, and a page would scan and sort every dead
        // letter.
        await using var proof = await Proof.CreateAsync(schema.Postgres);
        await proof.SqlAsync($"""
            INSERT INTO outbox_messages (id, tenant_id, organization_id, occurred_at, correlation_id, type, topic, partition_key,
                payload, attempts, terminal_reason, dead_lettered_at, processed_at)
            SELECT uuidv7(), '{SchemaFixture.TenantA}', '{SchemaFixture.OrgA1}', now(), 'c', '{KnownType}',
                'learnstack.test.inspection', 'k', '[]'::jsonb, CASE WHEN g % 30 = 0 THEN 6 ELSE 0 END,
                CASE WHEN g % 30 = 0 THEN 'retry_exhausted' END,
                CASE WHEN g % 30 = 0 THEN now() - make_interval(secs => g) END,
                CASE WHEN g % 30 = 0 THEN NULL ELSE now() END
            FROM generate_series(1, 6000) g;
            INSERT INTO event_deliveries (tenant_id, subscription_id, event_id, organization_id, occurred_at, correlation_id, type,
                topic, partition_key, payload, terminal_reason, dead_lettered_at, processed_at)
            SELECT '{SchemaFixture.TenantA}', '{Subscription}', uuidv7(), '{SchemaFixture.OrgA1}', now(), 'c', '{KnownType}',
                'learnstack.test.inspection', 'k', '[]'::jsonb,
                CASE WHEN g % 30 = 0 THEN 'retry_exhausted' END,
                CASE WHEN g % 30 = 0 THEN now() - make_interval(secs => g) END,
                CASE WHEN g % 30 = 0 THEN NULL ELSE now() END
            FROM generate_series(1, 6000) g;
            ANALYZE outbox_messages;
            ANALYZE event_deliveries;
            """);
        await using var platform = await PostgresFixture.OpenAsync(proof.Database.PlatformConnectionString);

        async Task<string> Plan(string sql, params (string Name, object Value)[] parameters)
        {
            await using var command = new NpgsqlCommand("SET enable_seqscan = off; SET enable_bitmapscan = off; EXPLAIN (FORMAT JSON) " + sql, (NpgsqlConnection)platform);
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            return (await command.ExecuteScalarAsync())!.ToString()!;
        }

        var after = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var outbox = await Plan(
            DeliveryInspection.OutboxSql(DeliveryInspectionState.DeadLettered, afterCursor),
            [("size", 21), .. afterCursor ? new (string, object)[] { ("after", after), ("key", Guid.CreateVersion7()) } : []]);
        var delivery = await Plan(
            DeliveryInspection.DeliverySql(DeliveryInspectionState.DeadLettered, afterCursor),
            [("size", 21), .. afterCursor
                ? new (string, object)[] { ("after", after), ("tenant", SchemaFixture.TenantA), ("subscription", Subscription), ("event", Guid.CreateVersion7()) }
                : []]);

        outbox.Should().Contain("ix_outbox_messages_dead_lettered").And.NotContain("\"Node Type\": \"Sort\"").And.NotContain("Seq Scan");
        delivery.Should().Contain("ix_event_deliveries_dead_lettered").And.NotContain("\"Node Type\": \"Sort\"").And.NotContain("Seq Scan");
    }

    [IntegrationEventPartition(nameof(TenantId), tenantWide: true)]
    public sealed record InspectionEvent : IntegrationEventBase
    {
        public override string Topic => "learnstack.test.inspection";
        public override string PartitionKey => TenantId.ToString("D");
    }

    public sealed class InspectionHandler : IIntegrationEventHandler<InspectionEvent>
    {
        public Task HandleAsync(InspectionEvent fact, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class Proof : IAsyncDisposable
    {
        private readonly PostgresFixture _postgres;
        private readonly NpgsqlDataSource _platform;
        private readonly OutboxEventRegistry _registry;

        private Proof(PostgresFixture postgres, DisposableSchemaDatabase database)
        {
            _postgres = postgres;
            Database = database;
            _platform = LearnStack.Api.Composition.PersistenceCompositionExtensions.BuildPlatformDataSource(database.PlatformConnectionString);
            var handlers = IntegrationEventHandlerRegistry.FromServiceDescriptors(
                [ServiceDescriptor.Scoped<IIntegrationEventHandler<InspectionEvent>, InspectionHandler>()]);
            _registry = OutboxEventRegistry.Create(
                [OutboxEventRegistration.Required(typeof(InspectionEvent), [OutboxSubscriptionManifest.Active(Subscription, typeof(InspectionHandler))],
                    "declared.previous.inspection")], [], handlers, [typeof(InspectionEvent).Assembly]);
            Inspection = InspectionOver(_platform);
        }

        /// <summary>The inspection as production builds it, over the platform data source a test supplies.</summary>
        internal DeliveryInspection InspectionOver(NpgsqlDataSource platform) => new(
            new PlatformAdminScope(
                new PrivilegedDataSource(() => platform), new AuditCatalog([new TenancyAuditCatalogSource()]),
                AuditStoreScopeFactory(), new SystemClock(), new SystemGuidFactory(), NullLogger<PlatformAdminScope>.Instance),
            _registry);

        internal DisposableSchemaDatabase Database { get; }

        internal DeliveryInspection Inspection { get; }

        internal static async Task<Proof> CreateAsync(PostgresFixture postgres)
        {
            var proof = new Proof(postgres, await DisposableSchemaDatabase.CreateAsync(postgres));
            await proof.SqlAsync($"""
                INSERT INTO tenants (id, slug, display_name, status, created_at, created_by, row_version)
                VALUES ('{SchemaFixture.TenantA}', 'inspection', 'Inspection', 'Trial', now(), '{UserId.SystemActor.Value}', 0);
                INSERT INTO organizations (id, tenant_id, slug, display_name, status, created_at, created_by, row_version)
                VALUES ('{SchemaFixture.OrgA1}', '{SchemaFixture.TenantA}', 'hq', 'HQ', 'Active', now(), '{UserId.SystemActor.Value}', 0);
                UPDATE tenants SET default_organization_id = '{SchemaFixture.OrgA1}' WHERE id = '{SchemaFixture.TenantA}';
                """);
            return proof;
        }

        internal PlatformAdmission Admission(string? ticket = null) => TestPlatformAdmissions.Admission(
            PlatformOperations.DeliveryInspect, ticket, correlationId: "00-inspect-" + Guid.NewGuid().ToString("N"));

        internal Task OutboxAsync(Guid id, string assignments) => SqlAsync($"""
            INSERT INTO outbox_messages (id, tenant_id, organization_id, occurred_at, correlation_id, type, topic, partition_key, payload)
            VALUES ('{id}', '{SchemaFixture.TenantA}', '{SchemaFixture.OrgA1}', '2026-10-01 12:00:00+00', 'c', '{KnownType}', 'learnstack.test.inspection', 'k', '[]'::jsonb);
            UPDATE outbox_messages SET {assignments} WHERE id = '{id}';
            """);

        internal Task DeliveryAsync(Guid id, string subscription, string assignments) => SqlAsync($"""
            INSERT INTO event_deliveries (tenant_id, subscription_id, event_id, organization_id, occurred_at, correlation_id, type, topic, partition_key, payload)
            VALUES ('{SchemaFixture.TenantA}', '{subscription}', '{id}', '{SchemaFixture.OrgA1}', '2026-10-01 12:00:00+00', 'c', '{KnownType}', 'learnstack.test.inspection', 'k', '[]'::jsonb);
            UPDATE event_deliveries SET {assignments} WHERE event_id = '{id}';
            """);

        internal async Task<List<(string Operation, string? Reason, string Metadata)>> AuditAsync(string correlation)
        {
            await using var connection = await PostgresFixture.OpenAsync(Database.PlatformConnectionString);
            await using var command = new NpgsqlCommand(
                "SELECT operation, reason, metadata::text FROM audit_log WHERE correlation_id = @c ORDER BY timestamp", (NpgsqlConnection)connection);
            command.Parameters.AddWithValue("c", correlation);
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<(string, string?, string)>();
            while (await reader.ReadAsync()) rows.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2)));
            return rows;
        }

        internal Task SqlAsync(string sql) => _postgres.ExecuteAsSuperuserAsync(sql, RebindProofSupport.DatabaseOf(Database.MigrationConnectionString));

        public async ValueTask DisposeAsync()
        {
            await _platform.DisposeAsync();
            await Database.DisposeAsync();
        }

        private static IServiceScopeFactory AuditStoreScopeFactory() =>
            new ServiceCollection()
                .AddMetrics()
                .AddLogging()
                .AddScoped<IAuditStateCapture, AuditStateCapture>()
                .AddSingleton<IAuditHealth, AuditHealth>()
                .AddSingleton<IAuditCatalog>(new AuditCatalog([new TenancyAuditCatalogSource()]))
                .AddSingleton<ITenantContextAccessor>(StaticTenantContextAccessor.Unresolved)
                .AddScoped<LearnStack.SharedKernel.Identity.IGlobalIdentityEnrollment, GlobalIdentityEnrollment>()
                .AddSingleton(new Lazy<NpgsqlDataSource>(
                    () => throw new InvalidOperationException("The scope's row is written on its own connection.")))
                .AddScoped<IAuditStore, PostgresAuditStore>()
                .BuildServiceProvider()
                .GetRequiredService<IServiceScopeFactory>();
    }
}
