using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobRecurrenceStoreTests
{
    [Theory]
    [InlineData("job")]
    [InlineData("recurrence")]
    [InlineData("occurrence")]
    [InlineData("recipient")]
    public async Task A_cold_empty_registry_observes_each_future_unsupported_obligation_without_a_deferred_marker(string artifact)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = Source(database);
        var store = Store(source);
        var declared = await PrepareFutureObservationAsync(database, store, artifact);
        var supported = await store.ReadDurableObservationAsync(
            BackgroundJobObservationQueries.Manifest(Fixture.Definitions(), declared), []);
        supported.ThrowIfFailed();
        supported.Value!.HasDeferredWork.Should().BeFalse("the stored route is valid in the binary that declared it");
        supported.Value.HasQuarantinedJobs.Should().BeFalse();
        supported.Value.HasQuarantinedControl.Should().BeFalse();
        if (artifact == "job") BackgroundJobObservationQueries.AssertQueue(supported.Value, "transactional", 1, 0);

        var definitions = BackgroundJobDefinitionRegistry.CreateEmpty();
        var recurrences = BackgroundJobRecurrenceRegistry.CreateEmpty();
        definitions.DefinitionKeys.Should().BeEmpty();
        recurrences.Keys.Should().BeEmpty();
        var cold = await store.ReadDurableObservationAsync(
            BackgroundJobObservationQueries.Manifest(definitions, recurrences), []);
        cold.ThrowIfFailed();
        AssertUnsupportedObservation(cold.Value!, artifact);
        cold.CleanupConfirmed.Should().BeTrue();

        var probe = new BackgroundJobFixtureProbe();
        await using var host = RecipientHost(database, probe, configure: services =>
        {
            services.RemoveAll<BackgroundJobDefinitionRegistry>();
            services.AddSingleton(definitions);
        });
        host.Services.GetRequiredService<BackgroundJobDefinitionRegistry>().Should().BeSameAs(definitions);
        using var globals = new RecurrenceGlobalFilters();
        var runtime = RecurrenceRuntime(host.Services, source, recurrences,
            new() { SnapshotInterval = TimeSpan.FromSeconds(1) }, definitions);
        runtime.Snapshot.Control!.Observation.Should().BeNull();
        try
        {
            await runtime.StartAsync();
            var snapshot = runtime.Snapshot;
            await RecurrenceEventuallyAsync(() =>
            {
                snapshot = runtime.Snapshot;
                return Task.FromResult(snapshot.Control is
                { ObservationHealthy: true, HasDeferredWork: true, HasQuarantinedJobs: false, HasQuarantinedControl: false }
                    && (snapshot with { Control = snapshot.Control with { HasDeferredWork = false } }).IsHealthy);
            });
            AssertUnsupportedObservation(snapshot.Control!.Observation!, artifact);
            snapshot.Control.Recurrence!.IsHealthy.Should().BeTrue(
                "there is no local recurrence declaration whose fault could mask the durable health refusal");
            snapshot.IsHealthy.Should().BeFalse();
            (snapshot with { Control = snapshot.Control with { HasDeferredWork = false } }).IsHealthy.Should().BeTrue(
                "all actual native/control facts are healthy; changing only the copied deferred bit isolates the refusal");
            await AssertFutureObservationUntouchedAsync(database, artifact);
            probe.Constructions.Should().BeEmpty();
        }
        finally
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await runtime.StopAsync(deadline.Token);
            await runtime.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        }
        runtime.Snapshot.CleanupConfirmed.Should().BeTrue();
    }

    private static void AssertUnsupportedObservation(BackgroundJobDurableObservation sample, string artifact)
    {
        sample.HasDeferredWork.Should().BeTrue();
        sample.HasQuarantinedJobs.Should().BeFalse();
        sample.HasQuarantinedControl.Should().BeFalse();
        sample.Points.Where(point => point.Family == BackgroundJobObservationFamily.Deferred)
            .Should().ContainSingle().Which.Should().Be(new BackgroundJobObservationPoint(
                BackgroundJobObservationFamily.Deferred, artifact, 1, 1, 0, 0));
        var queues = sample.Points.Where(point => point.Family == BackgroundJobObservationFamily.Queue);
        if (artifact == "job")
            queues.Should().ContainSingle().Which.Should().Be(new BackgroundJobObservationPoint(
                BackgroundJobObservationFamily.Queue, "transactional", 0, 1, 0, 0));
        else
            queues.Should().BeEmpty("there is no business job that could mask this control-only obligation");
        sample.Points.Should().NotContain(point => point.Label.Contains("jobsrecurrencefixture", StringComparison.Ordinal));
        if (artifact != "job")
            sample.Points.Where(point => point.Family == BackgroundJobObservationFamily.FanoutPending)
                .Should().ContainSingle().Which.Should().Be(new BackgroundJobObservationPoint(
                    BackgroundJobObservationFamily.FanoutPending, "unknown", artifact switch
                    { "recurrence" => 1, "occurrence" => 2, "recipient" => 3, _ => throw new ArgumentOutOfRangeException(nameof(artifact)) },
                    1, 0, 0));
    }

    private static async Task<BackgroundJobRecurrenceRegistry> PrepareFutureObservationAsync(
        DisposableSchemaDatabase database, PostgresBackgroundJobControlStore store, string artifact)
    {
        if (artifact == "job")
        {
            await using (var app = await PostgresFixture.OpenAsync(database.AppConnectionString))
                await SeedLifecycleTenantAsync(app, SchemaFixture.TenantA, SchemaFixture.OrgA1, "Active", false, true);
            var future = (await TodayAsync(database)).AddDays(3);
            await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
                definition: "jobsrecurrencefixture.run.v1", due: future);
            await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
            await SchemaQueries.ExecuteAsync(dispatcher, null, """
                UPDATE background_jobs SET available_after=original_due_at,handoff_check_at=original_due_at
                """);
            return BackgroundJobRecurrenceRegistry.CreateEmpty();
        }

        var today = await TodayAsync(database);
        var manifest = artifact == "recipient"
            ? await PrepareClaimRouteAsync(database, store, organization: false)
            : Fixture.Manifest(artifact == "recurrence" ? today.AddDays(3) : today.AddDays(-1), today.AddDays(-1));
        if (artifact != "recipient")
        {
            await AssertRegistrationAsync(store, manifest, BackgroundJobRecurrenceRegistrationResult.Registered);
            if (artifact == "occurrence")
            {
                var created = await store.MaterializeRecurrenceAsync(Fixture.Recurrences(manifest));
                created.ThrowIfFailed();
                created.Value.Should().NotBeNull();
            }
        }
        if (artifact != "recurrence")
        {
            // The parent has no remaining slot. Its completed history must not
            // mask a missing child aggregate or create a second deferred point.
            // Child schedules are immutable; only their retry availability is future.
            await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
            await SchemaQueries.ExecuteAsync(dispatcher, null, """
                UPDATE background_job_recurrences SET superseded_at=clock_timestamp();
                UPDATE background_job_occurrences SET available_after=clock_timestamp()+interval '1 day' WHERE completed_at IS NULL;
                UPDATE background_job_recipients SET available_after=clock_timestamp()+interval '1 day';
                """);
        }
        return Fixture.Recurrences(manifest);
    }

    private static async Task AssertFutureObservationUntouchedAsync(DisposableSchemaDatabase database, string artifact)
    {
        var sql = artifact switch
        {
            "job" => """
                SELECT count(*)=1 AND bool_and(original_due_at>clock_timestamp()
                    AND available_after>clock_timestamp() AND handoff_check_at>clock_timestamp()
                    AND last_error IS NULL AND phase='pending' AND normal_attempts=0 AND ownership_token IS NULL)
                FROM background_jobs
                """,
            "recurrence" => """
                SELECT count(*)=1 AND bool_and(next_scheduled_at>clock_timestamp()
                    AND available_after>clock_timestamp() AND last_error IS NULL AND quarantined_at IS NULL)
                FROM background_job_recurrences
                """,
            "occurrence" => """
                SELECT count(*)=1 AND bool_and(available_after>clock_timestamp() AND last_error IS NULL
                    AND quarantined_at IS NULL AND completed_at IS NULL AND ownership_token IS NULL)
                FROM background_job_occurrences
                """,
            "recipient" => """
                SELECT count(*)=1 AND bool_and(available_after>clock_timestamp() AND last_error IS NULL
                    AND quarantined_at IS NULL AND accepted_job_id IS NULL AND skipped_at IS NULL AND ownership_token IS NULL)
                FROM background_job_recipients
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(artifact)),
        };
        await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        (await ReadAsync<bool>(dispatcher, sql)).Should().BeTrue(
            "the cold observation must detect the obligation before any owner has written a deployment marker");
    }
}
