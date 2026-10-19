using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobControlTests
{
    [Fact]
    public async Task Quarantine_observation_sees_other_tenants_and_unknown_definitions_without_payload_grants()
    {
        await using var database = await DatabaseAsync();
        await using var source = Source(database.OutboxConnectionString);
        var store = Control(source);
        var empty = await store.ReadDurableObservationAsync(BackgroundJobObservationQueries.Manifest(Fixture.Registry), []);
        empty.ThrowIfFailed();
        empty.CleanupConfirmed.Should().BeTrue();
        empty.Value!.HasQuarantinedJobs.Should().BeFalse();
        empty.Value.HasQuarantinedControl.Should().BeFalse();
        empty.CommitAcknowledged.Should().BeFalse();
        empty.KnownRollback.Should().BeFalse("a read-only observation is not evidence about a business rollback");
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            definition: "fixture.unknown.v1", tenant: SchemaFixture.TenantB);
        // A guarded status fixture, not an external execution/quiescence proof.
        await DispatchAsync(database, "UPDATE background_jobs SET phase='quarantined', quarantine_reason='unknown_outcome' WHERE id=@id",
            ("id", job.Value));
        var present = await store.ReadDurableObservationAsync(BackgroundJobObservationQueries.Manifest(Fixture.Registry), []);
        present.ThrowIfFailed();
        present.CleanupConfirmed.Should().BeTrue();
        present.Value!.HasQuarantinedJobs.Should().BeTrue();
        present.Value.HasQuarantinedControl.Should().BeFalse();
        await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        await FluentActions.Awaiting(() => SchemaQueries.ExecuteAsync(dispatcher, null,
            "SELECT parameters FROM background_jobs")).Should().ThrowAsync<PostgresException>()
            .Where(failure => failure.SqlState == PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task A_quarantine_read_requires_actual_rollback_acknowledgement_before_publishing_false()
    {
        await using var database = await DatabaseAsync();
        await using var proxy = new CommitResponseDroppingProxy(database.OutboxConnectionString,
            commitsToPass: int.MaxValue, lifetime: TimeSpan.FromSeconds(30));
        await using var source = Source(proxy.ConnectionString);
        var store = Control(source);
        proxy.HoldNextQuarantineRollbackResponse(loseResponse: false);
        var read = store.ReadDurableObservationAsync(BackgroundJobObservationQueries.Manifest(Fixture.Registry), []);
        try
        {
            await proxy.QuarantineRollbackHeld.WaitAsync(TimeSpan.FromSeconds(10));
            read.IsCompleted.Should().BeFalse("the actual read transaction has not acknowledged ROLLBACK");
            proxy.QuarantineReads.Should().Be(1);
        }
        finally
        {
            proxy.ReleaseQuarantineRollbackResponse();
            await read;
        }
        var observed = await read;
        observed.ThrowIfFailed();
        observed.Value!.HasQuarantinedJobs.Should().BeFalse();
        observed.Value.HasQuarantinedControl.Should().BeFalse();
        observed.CleanupConfirmed.Should().BeTrue();
    }

    [Fact]
    public async Task A_lost_quarantine_rollback_response_never_becomes_an_observed_empty_queue()
    {
        await using var database = await DatabaseAsync();
        await using var proxy = new CommitResponseDroppingProxy(database.OutboxConnectionString,
            commitsToPass: int.MaxValue, lifetime: TimeSpan.FromSeconds(30));
        await using var source = Source(proxy.ConnectionString);
        proxy.HoldNextQuarantineRollbackResponse(loseResponse: true);
        var read = Control(source).ReadDurableObservationAsync(BackgroundJobObservationQueries.Manifest(Fixture.Registry), []);
        try { await proxy.QuarantineRollbackHeld.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally
        {
            proxy.ReleaseQuarantineRollbackResponse();
            await read;
        }
        var observed = await read;
        observed.Failed.Should().BeTrue();
        observed.Value.Should().BeNull();
        observed.CommitAcknowledged.Should().BeFalse();
        observed.CommitUncertain.Should().BeFalse("a failed read must not create a recursive COMMIT probe");
    }

    [Fact]
    public async Task Memory_health_reads_do_not_replace_a_held_control_observation_or_issue_more_queries()
    {
        await using var database = await DatabaseAsync();
        await using var proxy = new CommitResponseDroppingProxy(database.OutboxConnectionString,
            commitsToPass: int.MaxValue, lifetime: TimeSpan.FromSeconds(40));
        await using var source = Source(proxy.ConnectionString);
        await using var host = Fixture.Host(database, new BackgroundJobFixtureProbe());
        var bounds = new BackgroundJobRuntimeOptions { SnapshotInterval = TimeSpan.FromSeconds(1) };
        var control = HealthControl(host.Services, source, bounds);
        control.Start();
        try
        {
            await WaitQuarantineHealthAsync(() => control.Snapshot.ObservationHealthy);
            control.Snapshot.HasQuarantinedJobs.Should().BeFalse();
            proxy.HoldNextQuarantineRollbackResponse(loseResponse: false);
            await proxy.QuarantineRollbackHeld.WaitAsync(TimeSpan.FromSeconds(10));
            var reads = proxy.QuarantineReads;
            for (var i = 0; i < 1_000; i++) control.Snapshot.HasQuarantinedJobs.Should().BeFalse();
            proxy.QuarantineReads.Should().Be(reads, "the real serial control operation is still awaiting its own ACK");
            control.Snapshot.CleanupConfirmed.Should().BeFalse();
        }
        finally
        {
            proxy.ReleaseQuarantineRollbackResponse();
            control.RequestStop();
            await control.Completion.WaitAsync(TimeSpan.FromSeconds(20));
        }
        control.Snapshot.CleanupConfirmed.Should().BeTrue();
    }

    [Fact]
    public async Task A_refresh_fault_keeps_the_last_sample_until_another_successful_observation()
    {
        await using var database = await DatabaseAsync();
        await using var host = Fixture.Host(database, new BackgroundJobFixtureProbe());
        await using var source = Source(database.OutboxConnectionString);
        var bounds = new BackgroundJobRuntimeOptions { SnapshotInterval = TimeSpan.FromSeconds(3) };
        var control = HealthControl(host.Services, source, bounds);
        control.Start();
        await using var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        try
        {
            await WaitQuarantineHealthAsync(() => control.Snapshot.ObservationHealthy);
            await SchemaQueries.ExecuteAsync(owner, null, "REVOKE SELECT (phase) ON background_jobs FROM learnstack_outbox_admin");
            await WaitQuarantineHealthAsync(() => !control.Snapshot.ObservationHealthy);
            control.Snapshot.HasQuarantinedJobs.Should().BeFalse("failure must preserve the last good value, not invent a new sample");
            await SchemaQueries.ExecuteAsync(owner, null, "GRANT SELECT (phase) ON background_jobs TO learnstack_outbox_admin");
            await WaitQuarantineHealthAsync(() => control.Snapshot.ObservationHealthy);
            control.Snapshot.HasQuarantinedJobs.Should().BeFalse();
        }
        finally
        {
            await SchemaQueries.ExecuteAsync(owner, null, "GRANT SELECT (phase) ON background_jobs TO learnstack_outbox_admin");
            control.RequestStop();
            await control.Completion.WaitAsync(TimeSpan.FromSeconds(20));
        }
    }

    private static BackgroundJobControl HealthControl(IServiceProvider services,
        LearnStack.Infrastructure.Persistence.PrivilegedDataSource source, BackgroundJobRuntimeOptions bounds) =>
        new(source, Fixture.Registry, services.GetRequiredService<JobExecutor>(),
            new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure), bounds, new SystemGuidFactory(), TimeProvider.System);

    private static async Task WaitQuarantineHealthAsync(Func<bool> ready)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!ready()) await Task.Delay(20, deadline.Token);
    }
}
