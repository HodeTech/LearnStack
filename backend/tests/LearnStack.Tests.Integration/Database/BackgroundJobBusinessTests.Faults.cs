using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobBusinessTests
{
    [Fact]
    public async Task Admission_acknowledgement_is_published_while_actual_scope_cleanup_is_still_pending()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        var probe = new BackgroundJobFixtureProbe();
        var cleanup = new BusinessCleanupProbe();
        var observation = new BackgroundJobAdmissionObservation
        {
            OnStart = (command, services) => { _ = services.GetRequiredService<BusinessCleanup>(); return Task.CompletedTask; },
        };
        await using var host = Fixture.Host(database, probe, configure: services =>
        {
            observation.Configure(services);
            services.AddSingleton(cleanup);
            services.AddScoped<BusinessCleanup>();
        });
        var claim = (await host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>().TryClaimAsync(job, 1))!;
        var publication = new BackgroundJobStartPublication();
        var running = host.Services.GetRequiredService<JobExecutor>().ExecutePublishedAsync(claim, publication, BackgroundJobTestRun.Create().OpenBoundary(BackgroundJobCheckpointKind.Admission), null, CancellationToken.None);
        try
        {
            var receipt = await publication.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            receipt.Should().NotBeNull();
            await cleanup.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            running.IsCompleted.Should().BeFalse();
            receipt!.SourceOwner.CleanupConfirmed.Should().BeFalse("the actual admission unit is still retained");
            probe.Constructions.Should().BeEmpty();
            (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!.CurrentAttempt.Should().Be(receipt.AttemptId.Value);
        }
        finally { cleanup.Release.TrySetResult(); }
        var result = await running.WaitAsync(TimeSpan.FromSeconds(10));
        result.CleanupFailed.Should().BeFalse();
        result.SourceOwner.CleanupConfirmed.Should().BeTrue();
        result.Receipt.Should().BeSameAs(await publication.Completion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Acknowledged_business_success_survives_actual_cleanup_and_Activity_observer_faults(bool activityFault)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var trace = ActivityTraceId.CreateRandom();
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            traceparent: $"00-{trace}-{ActivitySpanId.CreateRandom()}-01");
        var probe = new BackgroundJobFixtureProbe();
        var cleanup = new BusinessCleanupProbe { Throw = true };
        cleanup.Release.SetResult();
        var callbacks = 0;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name.Length == 0,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = stopped =>
            {
                if (!activityFault || stopped.OperationName != "job.execution" || stopped.TraceId != trace) return;
                callbacks++;
                throw new InvalidOperationException("Untrusted observer details");
            },
        };
        ActivitySource.AddActivityListener(listener);
        var original = new BackgroundJobFixtureContext();
        await using var host = Fixture.Host(database, probe, original, services =>
        {
            services.AddSingleton(cleanup);
            services.AddScoped<BusinessCleanup>();
        });
        IUnitOfWork? unit = null;
        System.Data.Common.DbConnection? connection = null;
        probe.Body = (_, services, _) =>
        {
            unit = services.GetRequiredService<IUnitOfWork>();
            connection = unit.Connection;
            if (!activityFault) _ = services.GetRequiredService<BusinessCleanup>();
            return Task.FromResult(Result.Ok(None.Value));
        };
        var admission = await AdmitAsync(host.Services, job);
        using var outer = new Activity("business-cleanup-parent").SetIdFormat(ActivityIdFormat.W3C).Start();
        var result = await host.Services.GetRequiredService<JobExecutor>().ExecuteBusinessAsync(admission.Receipt!, admission.Receipt!.Run.OpenBoundary(BackgroundJobCheckpointKind.Transactional));
        result.CommitAcknowledged.Should().BeTrue();
        result.Decision.Disposition.Should().Be(BackgroundJobFailureDisposition.Succeeded);
        result.CleanupFailed.Should().BeTrue();
        result.RetainedCleanup.Should().NotBeEmpty();
        result.DurableOutcomeUncertain.Should().BeFalse();
        unit!.Transaction.Should().BeNull();
        connection!.State.Should().Be(ConnectionState.Closed);
        Activity.Current.Should().BeSameAs(outer);
        host.Services.GetRequiredService<ITenantContextAccessor>().Current.Should().BeSameAs(original);
        callbacks.Should().Be(activityFault ? 1 : 0);
        (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!.Phase.Should().Be("completed");
    }

    [Fact]
    public async Task A_landed_business_commit_without_acknowledgement_requires_reconciliation()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        await using var proxy = new CommitResponseDroppingProxy(database.AppConnectionString, commitsToPass: 2);
        var probe = new BackgroundJobFixtureProbe();
        await using var host = Fixture.Host(database, probe, appConnectionString: proxy.ConnectionString);
        var admission = await AdmitAsync(host.Services, job);
        var result = await host.Services.GetRequiredService<JobExecutor>().ExecuteBusinessAsync(admission.Receipt!, admission.Receipt!.Run.OpenBoundary(BackgroundJobCheckpointKind.Transactional))
            .WaitAsync(TimeSpan.FromSeconds(10));
        proxy.DroppedCommitResponses.Should().Be(1);
        result.CommitStarted.Should().BeTrue();
        result.CommitAcknowledged.Should().BeFalse();
        result.KnownRollback.Should().BeFalse();
        result.DurableOutcomeUncertain.Should().BeTrue();
        result.Decision.Disposition.Should().Be(BackgroundJobFailureDisposition.Reconcile);
        (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!.Phase.Should().Be("completed");
        probe.Constructions.Should().Equal("TenantRoot");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Final_completion_samples_fresh_time_after_the_actual_row_lock_wait(bool expire)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new BackgroundJobFixtureProbe
        {
            Body = async (_, _, _) => { entered.SetResult(); await finishHandler.Task; return Result.Ok(None.Value); },
        };
        await using var host = Fixture.Host(database, probe);
        var admission = await AdmitAsync(host.Services, job);
        var running = host.Services.GetRequiredService<JobExecutor>().ExecuteBusinessAsync(admission.Receipt!, admission.Receipt!.Run.OpenBoundary(BackgroundJobCheckpointKind.Transactional));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using var locker = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        await using var transaction = await locker.BeginTransactionAsync();
        await SchemaQueries.ExecuteAsync(locker, transaction,
            expire ? "UPDATE background_jobs SET ownership_until=clock_timestamp()+interval '200 milliseconds' WHERE id=@id"
                : "SELECT id FROM background_jobs WHERE id=@id FOR UPDATE", ("id", job.Value));
        var released = false;
        try
        {
            finishHandler.SetResult();
            await WaitForFinalLockAsync(database.AppConnectionString);
            await SchemaQueries.ExecuteAsync(locker, transaction, "SELECT pg_sleep(0.3)");
            await transaction.CommitAsync();
            released = true;
            var result = await running.WaitAsync(TimeSpan.FromSeconds(10));
            result.CommitAcknowledged.Should().Be(!expire);
            result.CommitStarted.Should().Be(!expire);
            result.KnownRollback.Should().Be(expire);
            result.DurableOutcomeUncertain.Should().BeFalse();
        }
        finally
        {
            finishHandler.TrySetResult();
            if (!released) await transaction.RollbackAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(10));
        }
        probe.Constructions.Should().Equal("TenantRoot");
        (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!.Phase.Should().Be(expire ? "executing" : "completed");
    }

    private static async Task WaitForFinalLockAsync(string connectionString)
    {
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            await using var command = new NpgsqlCommand("""
                SELECT count(*) FROM pg_stat_activity WHERE datname=current_database()
                    AND usename='learnstack_app' AND wait_event_type='Lock' AND query LIKE '%FOR UPDATE%'
                """, connection);
            if ((long)(await command.ExecuteScalarAsync())! > 0) return;
            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }
        throw new TimeoutException("The actual job completion did not reach its final row lock.");
    }

    private sealed class BusinessCleanupProbe
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Throw { get; init; }
    }

    private sealed record BusinessLog(Type Category, string Text, Exception? Exception);

    private sealed class BusinessLogs
    {
        internal ConcurrentQueue<BusinessLog> Entries { get; } = new();
    }

    private sealed class BusinessLogger<T>(BusinessLogs logs) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            logs.Entries.Enqueue(new(typeof(T), formatter(state, exception), exception));
    }

    private sealed class BusinessCleanup(BusinessCleanupProbe probe, IUnitOfWork unit) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            unit.Transaction.Should().BeNull();
            probe.Entered.TrySetResult();
            await probe.Release.Task;
            if (probe.Throw) throw new InvalidOperationException("Untrusted cleanup details");
        }
    }
}
