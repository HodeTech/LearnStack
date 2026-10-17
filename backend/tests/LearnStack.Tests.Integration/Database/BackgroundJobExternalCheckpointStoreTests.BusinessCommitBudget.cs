using System.Diagnostics;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobExternalCheckpointStoreTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Business_commit_rechecks_the_observed_lease_after_actual_completion_sql_cleanup_without_token_cancellation(
        bool preparing, bool expire)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var trace = ActivityTraceId.CreateRandom();
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            preparing ? BackgroundJobRuntimeFixture.ExternalKey : BackgroundJobRuntimeFixture.CapacityKey,
            capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup, profile: preparing ? "external" : "transactional",
            traceparent: $"00-{trace}-{ActivitySpanId.CreateRandom()}-01");
        await using var host = preparing ? RecoveryHost(database, preparing: true)
            : Fixture.Host(database, new BackgroundJobFixtureProbe());
        var executor = host.Services.GetRequiredService<JobExecutor>();
        ConfirmedStartReceipt receipt;
        if (preparing) receipt = await AdmitFixtureAsync(host.Services, database, await FixtureClaim.ClaimAsync(database, job));
        else
        {
            var claim = await host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>().TryClaimAsync(job, 1);
            claim.Should().NotBeNull();
            var admitted = await executor.ExecuteAsync(claim!, BackgroundJobTestRun.Create());
            admitted.Outcome.Should().Be(JobStartExecutionOutcome.Started);
            receipt = admitted.Receipt!;
        }
        DateTimeOffset until;
        await using (var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString))
        await using (var command = new NpgsqlCommand("""
            UPDATE background_jobs SET ownership_until=clock_timestamp()+interval '5 seconds'
            WHERE id=@id RETURNING ownership_until
            """, (NpgsqlConnection)dispatcher))
        {
            command.Parameters.AddWithValue("id", job.Value);
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            until = reader.GetFieldValue<DateTimeOffset>(0);
        }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = 0;
        var observedError = false;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Npgsql",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.TraceId != trace || activity.GetTagItem("db.query.text") is not string sql) return;
                var target = preparing ? sql.StartsWith("UPDATE background_job_attempts a SET plan_fingerprint=", StringComparison.Ordinal)
                    : sql.StartsWith("UPDATE background_job_reservations SET released_at=", StringComparison.Ordinal)
                        && sql.Contains("release_reason='completed'", StringComparison.Ordinal);
                if (!target || Interlocked.CompareExchange(ref observed, 1, 0) != 0) return;
                observedError = activity.Status == ActivityStatusCode.Error;
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            },
        };
        ActivitySource.AddActivityListener(listener);
        var gate = receipt.Run.OpenBoundary(preparing ? BackgroundJobCheckpointKind.Prepare : BackgroundJobCheckpointKind.Transactional);
        var actual = Task.Run(() => executor.ExecuteBusinessAsync(receipt, gate, CancellationToken.None));
        Exception? premiseFailure = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            actual.IsCompleted.Should().BeFalse();
            gate.CommitStarted.Should().BeFalse();
            receipt.Run.IsClosed.Should().BeFalse();
            if (expire)
            {
                await using var clock = await PostgresFixture.OpenAsync(database.AppConnectionString);
                var wait = Stopwatch.StartNew();
                while (true)
                {
                    await using var command = new NpgsqlCommand("SELECT clock_timestamp()>=@until", (NpgsqlConnection)clock) { CommandTimeout = 5 };
                    command.Parameters.AddWithValue("until", until);
                    if (await command.ExecuteScalarAsync() is true) break;
                    if (wait.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("The exact completion lease did not expire.");
                    await Task.Delay(10);
                }
                gate.IsClosed.Should().BeFalse("no cancellation/timer closes this independently exercised app owner");
            }
        }
        catch (Exception failure) { premiseFailure = failure; }
        finally { release.TrySetResult(); }
        var result = await actual.WaitAsync(TimeSpan.FromSeconds(15));
        premiseFailure.Should().BeNull();
        observed.Should().Be(1);
        observedError.Should().BeFalse("the pause follows successful actual completion SQL and reader cleanup");
        result.ExecutionIssued.Should().BeTrue();
        result.CommitAcknowledged.Should().Be(!expire);
        result.CommitStarted.Should().Be(!expire);
        result.KnownRollback.Should().Be(expire);
        result.DurableOutcomeUncertain.Should().BeFalse();
        result.CleanupFailed.Should().BeFalse();
        result.SourceOwner.CleanupConfirmed.Should().BeTrue();
        gate.IsClosed.Should().Be(expire);
        var stored = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
        stored.Phase.Should().Be(preparing ? expire ? "preparing" : "prepared" : expire ? "executing" : "completed");
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
        await using var persisted = new NpgsqlCommand("""
            SELECT j.plan IS NOT NULL,j.plan_fingerprint IS NOT NULL,j.operation_id IS NOT NULL,
                j.prepared_at IS NOT NULL,a.plan_fingerprint IS NOT NULL,a.operation_id IS NOT NULL,
                a.execute_intent_at IS NOT NULL,r.released_at IS NOT NULL,r.release_reason,j.processed_at IS NOT NULL
            FROM background_jobs j JOIN background_job_attempts a ON a.id=j.current_attempt_id
                JOIN background_job_reservations r ON r.tenant_id=a.tenant_id AND r.attempt_id=a.id
                    AND r.job_id=j.id AND r.capacity_group=j.capacity_group
            WHERE j.id=@id AND a.id=@attempt
            """, (NpgsqlConnection)app, (NpgsqlTransaction)transaction);
        persisted.Parameters.AddWithValue("id", job.Value);
        persisted.Parameters.AddWithValue("attempt", receipt.AttemptId.Value);
        await using var row = await persisted.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        for (var index = 0; index < 6; index++) row.GetBoolean(index).Should().Be(preparing && !expire,
            "the immutable plan and original-attempt stamp commit together or both roll back");
        row.GetBoolean(6).Should().BeFalse("neither business stage invents external ExecuteIntent");
        row.GetBoolean(7).Should().Be(!preparing && !expire);
        if (!preparing && !expire) row.GetString(8).Should().Be("completed");
        else row.IsDBNull(8).Should().BeTrue("Prepare and a rolled-back completion retain the exact reservation");
        row.GetBoolean(9).Should().Be(!preparing && !expire);
    }
}
