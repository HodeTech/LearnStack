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

        private Proof(PostgresFixture postgres, DisposableSchemaDatabase database)
        {
            _postgres = postgres;
            Database = database;
            _platform = LearnStack.Api.Composition.PersistenceCompositionExtensions.BuildPlatformDataSource(database.PlatformConnectionString);
            var handlers = IntegrationEventHandlerRegistry.FromServiceDescriptors(
                [ServiceDescriptor.Scoped<IIntegrationEventHandler<InspectionEvent>, InspectionHandler>()]);
            var registry = OutboxEventRegistry.Create(
                [OutboxEventRegistration.Required(typeof(InspectionEvent), [OutboxSubscriptionManifest.Active(Subscription, typeof(InspectionHandler))],
                    "declared.previous.inspection")], [], handlers, [typeof(InspectionEvent).Assembly]);
            var scope = new PlatformAdminScope(
                new PrivilegedDataSource(() => _platform), new AuditCatalog([new TenancyAuditCatalogSource()]),
                AuditStoreScopeFactory(), new SystemClock(), new SystemGuidFactory(), NullLogger<PlatformAdminScope>.Instance);
            Inspection = new DeliveryInspection(scope, registry);
        }

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

        private Task SqlAsync(string sql) => _postgres.ExecuteAsSuperuserAsync(sql, RebindProofSupport.DatabaseOf(Database.MigrationConnectionString));

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
