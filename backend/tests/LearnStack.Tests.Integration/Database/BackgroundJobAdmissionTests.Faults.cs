using System.Diagnostics;
using System.Data;
using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobAdmissionTests
{
    [Fact]
    public async Task Expiry_during_an_actual_row_lock_wait_spends_no_attempt()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        var probe = new BackgroundJobFixtureProbe();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observation = new BackgroundJobAdmissionObservation
        {
            OnStart = (_, _) => { entered.TrySetResult(); return Task.CompletedTask; },
        };
        await using var host = Fixture.Host(database, probe, configure: observation.Configure);
        var claim = (await host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>().TryClaimAsync(job, 1))!;
        await using var locker = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        await using var transaction = await locker.BeginTransactionAsync();
        await SchemaQueries.ExecuteAsync(locker, transaction, """
            UPDATE background_jobs SET ownership_until = clock_timestamp() + interval '200 milliseconds' WHERE id = @id
            """, ("id", job.Value));
        var running = host.Services.GetRequiredService<JobExecutor>().ExecuteAsync(claim);
        var released = false;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitForApplicationRowLockAsync(database.AppConnectionString);
            await SchemaQueries.ExecuteAsync(locker, transaction, "SELECT pg_sleep(0.3)");
            await using (var expired = new NpgsqlCommand(
                "SELECT ownership_until <= clock_timestamp() FROM background_jobs WHERE id = @id",
                (NpgsqlConnection)locker, (NpgsqlTransaction)transaction))
            {
                expired.Parameters.AddWithValue("id", job.Value);
                (await expired.ExecuteScalarAsync()).Should().Be(true, "database time crossed the lease while the row lock was held");
            }
            await transaction.CommitAsync();
            released = true;
            var result = await running.WaitAsync(TimeSpan.FromSeconds(10));
            result.Outcome.Should().Be(JobStartExecutionOutcome.LostClaim);
            result.Receipt.Should().BeNull();
        }
        finally
        {
            if (!released) await transaction.RollbackAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(10));
        }
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(0);
        probe.Constructions.Should().BeEmpty();
    }

    [Fact]
    public async Task Competing_old_and_current_real_routing_claims_commit_exactly_one_start()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        var probe = new BackgroundJobFixtureProbe();
        var arrivals = 0;
        var bothStarting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observation = new BackgroundJobAdmissionObservation
        {
            OnStart = async (_, _) =>
            {
                if (Interlocked.Increment(ref arrivals) == 2) bothStarting.TrySetResult();
                await bothStarting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            },
        };
        await using var host = Fixture.Host(database, probe, configure: observation.Configure);
        var store = host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>();
        var old = (await store.TryClaimAsync(job, 1))!;
        await using (var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString))
            await SchemaQueries.ExecuteAsync(dispatcher, null,
                "UPDATE background_jobs SET ownership_until = clock_timestamp() - interval '1 microsecond' WHERE id = @id",
                ("id", job.Value));
        var current = (await store.TryClaimAsync(job, 1))!;
        current.Should().NotBeNull();
        current.Token.Should().NotBe(old.Token);
        var executor = host.Services.GetRequiredService<JobExecutor>();
        var results = await Task.WhenAll(executor.ExecuteAsync(old), executor.ExecuteAsync(current))
            .WaitAsync(TimeSpan.FromSeconds(10));
        arrivals.Should().Be(2, "both actual app-role start transactions must overlap before either can take the job-row lock");
        results.Count(result => result.Outcome == JobStartExecutionOutcome.Started).Should().Be(1);
        results.Count(result => result.Outcome == JobStartExecutionOutcome.LostClaim).Should().Be(1);
        results.Count(result => result.Receipt is not null).Should().Be(1);
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
        probe.Constructions.Should().BeEmpty();
    }

    [Fact]
    public async Task A_landed_start_with_a_dropped_COMMIT_acknowledgement_never_activates_a_receipt_or_restarts()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        // The first COMMIT belongs to the real physical read-only pre-read. Only
        // the second server CommandComplete COMMIT response is withheld.
        await using var proxy = new CommitResponseDroppingProxy(database.AppConnectionString, commitsToPass: 1);
        var probe = new BackgroundJobFixtureProbe();
        var roots = Fixture.RootConstructions;
        var original = new BackgroundJobFixtureContext();
        await using var host = Fixture.Host(database, probe, original, appConnectionString: proxy.ConnectionString);
        var store = host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>();
        var claim = (await store.TryClaimAsync(job, 1))!;
        using var activity = new Activity("uncertain-admission-proof").SetIdFormat(ActivityIdFormat.W3C).Start();
        var result = await host.Services.GetRequiredService<JobExecutor>().ExecuteAsync(claim).WaitAsync(TimeSpan.FromSeconds(10));
        proxy.DroppedCommitResponses.Should().Be(1);
        result.Outcome.Should().Be(JobStartExecutionOutcome.Indeterminate);
        result.Receipt.Should().BeNull("a socket close after server COMMIT is not an acknowledged activation receipt");
        var row = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
        row.NormalAttempts.Should().Be(1, "the server committed before the proxy withheld its acknowledgement");
        row.Phase.Should().Be("executing");
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
        (await store.TryClaimAsync(job, 1)).Should().BeNull("a spent execution cannot re-enter ordinary routing");
        probe.Constructions.Should().BeEmpty();
        Fixture.RootConstructions.Should().Be(roots);
        host.Services.GetRequiredService<ITenantContextAccessor>().Current.Should().BeSameAs(original);
        Activity.Current.Should().BeSameAs(activity);
    }

    [Fact]
    public async Task A_post_acknowledgement_scope_disposal_fault_retains_the_committed_receipt_and_restores_context()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        var probe = new BackgroundJobFixtureProbe();
        var observation = new BackgroundJobAdmissionObservation { FailCleanup = true };
        var original = new BackgroundJobFixtureContext();
        await using var host = Fixture.Host(database, probe, original, observation.Configure);
        var claim = (await host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>().TryClaimAsync(job, 1))!;
        using var activity = new Activity("cleanup-admission-proof").SetIdFormat(ActivityIdFormat.W3C).Start();
        var result = await host.Services.GetRequiredService<JobExecutor>().ExecuteAsync(claim);
        result.Outcome.Should().Be(JobStartExecutionOutcome.Started);
        result.Receipt.Should().NotBeNull();
        result.CleanupFailed.Should().BeTrue();
        observation.StartScopeDisposed.Should().BeTrue("the fault is raised by actual scope disposal after the commit");
        observation.StartConnection.Should().NotBeNull();
        observation.StartConnection!.State.Should().Be(ConnectionState.Closed,
            "DI stops disposal at a throwing service, so the executor must explicitly join its retained physical unit cleanup");
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
        host.Services.GetRequiredService<ITenantContextAccessor>().Current.Should().BeSameAs(original);
        Activity.Current.Should().BeSameAs(activity);
        probe.Constructions.Should().BeEmpty();
    }

    [Fact]
    public async Task A_preread_scope_disposal_fault_closes_its_actual_connection_and_cannot_start_an_attempt()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        var probe = new BackgroundJobFixtureProbe();
        var observation = new BackgroundJobAdmissionObservation { FailReadCleanup = true };
        var original = new BackgroundJobFixtureContext();
        await using var host = Fixture.Host(database, probe, original, observation.Configure);
        var claim = (await host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>().TryClaimAsync(job, 1))!;
        using var activity = new Activity("read-cleanup-proof").SetIdFormat(ActivityIdFormat.W3C).Start();
        var result = await host.Services.GetRequiredService<JobExecutor>().ExecuteAsync(claim);
        result.Outcome.Should().Be(JobStartExecutionOutcome.Failed);
        result.Receipt.Should().BeNull();
        result.CleanupFailed.Should().BeTrue();
        observation.ReadScopeDisposed.Should().BeTrue();
        observation.ReadConnection.Should().NotBeNull();
        observation.ReadConnection!.State.Should().Be(ConnectionState.Closed);
        observation.StartUnit.Should().BeNull("failed pre-read cleanup never permits a start scope");
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(0);
        host.Services.GetRequiredService<ITenantContextAccessor>().Current.Should().BeSameAs(original);
        Activity.Current.Should().BeSameAs(activity);
        probe.Constructions.Should().BeEmpty();
    }

    [Fact]
    public async Task A_throwing_Activity_stopped_observer_cannot_replace_a_confirmed_start_or_prevent_restoration()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var trace = ActivityTraceId.CreateRandom();
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            traceparent: $"00-{trace}-{ActivitySpanId.CreateRandom()}-01");
        var probe = new BackgroundJobFixtureProbe();
        var original = new BackgroundJobFixtureContext();
        var callbacks = 0;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name.Length == 0,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = stopped =>
            {
                if (stopped.OperationName != "job.admission" || stopped.TraceId != trace) return;
                callbacks++;
                throw new InvalidOperationException("Deliberate ActivityStopped observer failure");
            },
        };
        ActivitySource.AddActivityListener(listener);
        await using var host = Fixture.Host(database, probe, original);
        var claim = (await host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>().TryClaimAsync(job, 1))!;
        using var activity = new Activity("activity-cleanup-proof").SetIdFormat(ActivityIdFormat.W3C).Start();
        var result = await host.Services.GetRequiredService<JobExecutor>().ExecuteAsync(claim);
        callbacks.Should().Be(1, "the fault must arise from the actual admission Activity's Stop callback");
        result.Outcome.Should().Be(JobStartExecutionOutcome.Started);
        result.Receipt.Should().NotBeNull();
        result.CleanupFailed.Should().BeTrue();
        Activity.Current.Should().BeSameAs(activity);
        host.Services.GetRequiredService<ITenantContextAccessor>().Current.Should().BeSameAs(original);
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
    }

    private static async Task WaitForApplicationRowLockAsync(string connectionString)
    {
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            await using var command = new NpgsqlCommand("""
                SELECT count(*) FROM pg_stat_activity WHERE datname = current_database()
                    AND usename = 'learnstack_app' AND wait_event_type = 'Lock' AND query LIKE '%FOR UPDATE%'
                """, connection);
            if ((long)(await command.ExecuteScalarAsync())! > 0) return;
            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }
        throw new TimeoutException("The real application admission did not reach its row-lock wait.");
    }
}
