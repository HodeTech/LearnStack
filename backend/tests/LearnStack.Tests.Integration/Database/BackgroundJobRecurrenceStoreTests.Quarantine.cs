using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobRecurrenceStoreTests
{
    [Theory]
    [InlineData("recurrences")]
    [InlineData("occurrences")]
    [InlineData("recipients")]
    public async Task A_cold_runtime_observes_each_unknown_control_quarantine_and_retains_truth_after_a_refresh_fault(string kind)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = Source(database);
        var store = Store(source);
        var empty = await store.ReadDurableObservationAsync(BackgroundJobObservationQueries.Manifest(Fixture.Definitions()), []);
        empty.ThrowIfFailed();
        empty.Value!.HasQuarantinedJobs.Should().BeFalse();
        empty.Value.HasQuarantinedControl.Should().BeFalse();
        empty.CleanupConfirmed.Should().BeTrue();
        var today = await TodayAsync(database);
        var manifest = Fixture.Manifest(today.AddDays(-2), today.AddDays(-2));
        if (kind == "recipients")
            _ = await PrepareClaimRouteAsync(database, store, organization: false);
        else
        {
            await AssertRegistrationAsync(store, manifest, BackgroundJobRecurrenceRegistrationResult.Registered);
            if (kind == "occurrences")
            {
                var materialized = await store.MaterializeRecurrenceAsync(Fixture.Recurrences(manifest));
                materialized.ThrowIfFailed();
                materialized.Value.Should().NotBeNull();
            }
        }
        // Only this one stored table becomes quarantined. The other OR arms
        // must stay false; no business job exists to mask a missing arm.
        var table = kind switch
        {
            "recurrences" => "background_job_recurrences",
            "occurrences" => "background_job_occurrences",
            "recipients" => "background_job_recipients",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        await using (var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString))
            await SchemaQueries.ExecuteAsync(dispatcher, null,
                "UPDATE " + table + " SET quarantined_at=clock_timestamp(),last_error='invalid_control'");
        var present = await store.ReadDurableObservationAsync(BackgroundJobObservationQueries.Manifest(Fixture.Definitions()), []);
        present.ThrowIfFailed();
        present.Value!.HasQuarantinedJobs.Should().BeFalse();
        present.Value.HasQuarantinedControl.Should().BeTrue();
        present.CleanupConfirmed.Should().BeTrue();

        await using var host = RecipientHost(database, new());
        using var globals = new RecurrenceGlobalFilters();
        var runtime = RecurrenceRuntime(host.Services, source, BackgroundJobRecurrenceRegistry.CreateEmpty(),
            new() { SnapshotInterval = TimeSpan.FromSeconds(1) });
        using var metrics = new BackgroundJobMetricProbe(host.Services.GetRequiredService<BackgroundJobObservation>());
        runtime.Snapshot.Control!.HasQuarantinedControl.Should().BeNull();
        await using var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        try
        {
            await runtime.StartAsync();
            await RecurrenceEventuallyAsync(() => Task.FromResult(runtime.Snapshot.Control is
            { ObservationHealthy: true, HasQuarantinedJobs: false, HasQuarantinedControl: true }));
            runtime.Snapshot.Control!.Recurrence!.IsHealthy.Should().BeTrue(
                "the empty local recurrence registry cannot hide durable quarantine from an earlier process");
            runtime.Snapshot.IsHealthy.Should().BeFalse();
            BackgroundJobMetricProbe.One(metrics.Read(), "learnstack_job_state_count", ("kind", "quarantined"),
                ("artifact", kind[..^1])).Should().Be(1);
            await SchemaQueries.ExecuteAsync(owner, null,
                "REVOKE SELECT ON " + table + " FROM learnstack_outbox_admin");
            await using (var refused = await PostgresFixture.OpenAsync(database.OutboxConnectionString))
                await FluentActions.Awaiting(() => SchemaQueries.ExecuteAsync(refused, null,
                    "SELECT quarantined_at FROM " + table)).Should().ThrowAsync<PostgresException>()
                    .Where(failure => failure.SqlState == PostgresErrorCodes.InsufficientPrivilege);
            await RecurrenceEventuallyAsync(() => Task.FromResult(runtime.Snapshot.Control!.ObservationFailed));
            runtime.Snapshot.Control!.HasQuarantinedControl.Should().BeTrue();
            runtime.Snapshot.Control.HasQuarantinedJobs.Should().BeFalse();
            runtime.Snapshot.IsHealthy.Should().BeFalse();
            var failedSample = metrics.Read();
            BackgroundJobMetricProbe.One(failedSample, "learnstack_job_state_count", ("kind", "quarantined"),
                ("artifact", kind[..^1])).Should().Be(1);
            BackgroundJobMetricProbe.One(failedSample, "learnstack_job_component_state", ("component", "durable_observation"),
                ("state", "refresh_failed")).Should().Be(1);
            await SchemaQueries.ExecuteAsync(owner, null,
                "GRANT SELECT ON " + table + " TO learnstack_outbox_admin");
            await RecurrenceEventuallyAsync(() => Task.FromResult(runtime.Snapshot.Control!.ObservationHealthy));
            runtime.Snapshot.Control!.HasQuarantinedControl.Should().BeTrue();
            runtime.Snapshot.IsHealthy.Should().BeFalse();
        }
        finally
        {
            await SchemaQueries.ExecuteAsync(owner, null,
                "GRANT SELECT ON " + table + " TO learnstack_outbox_admin");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await runtime.StopAsync(deadline.Token);
            await runtime.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        }
        runtime.Snapshot.CleanupConfirmed.Should().BeTrue();
    }
}
