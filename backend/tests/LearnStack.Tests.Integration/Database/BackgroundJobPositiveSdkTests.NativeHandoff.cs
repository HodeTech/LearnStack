using System.Runtime.ExceptionServices;
using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobPositiveSdkTests
{
    [Fact]
    public async Task Native_fixture_start_waits_for_previous_actual_cleanup_before_one_same_generation_hint()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var provider = await BackgroundJobProviderJournalDatabase.CreateAsync(schema.Postgres);
        var construction = new BackgroundJobPositiveProviderConstructionProbe(hold: true);
        var renewal = new NativeSettlementTimeProvider(retainedRecovery: false);
        var bounds = new BackgroundJobRuntimeOptions { RenewalInterval = NativeSettlementTimeProvider.RenewalPacing };
        await using var host = Fixture.Host(database, provider.Settings, construction, services =>
        {
            services.RemoveAll<BackgroundJobRuntimeOptions>();
            services.AddSingleton(bounds);
        });
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        using var globals = new NativeSdkGlobalFilterSnapshot();
        var time = new NativeHandoffCleanupTime(bounds.TransactionTimeout, renewal);
        var runtime = NativeSdkRuntime(host.Services, source, bounds, time);
        BackgroundJobBridge? bridge = null;
        using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var pulseStop = new CancellationTokenSource();
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHelper = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NpgsqlConnection? blocker = null;
        NpgsqlTransaction? transaction = null;
        Task? pulse = null, helper = null, pacing = null;
        ExceptionDispatchInfo? failure = null;
        var boundary = "submit";
        void Preserve(Exception error, string at)
        {
            if (failure is not null) return;
            failure = ExceptionDispatchInfo.Capture(error);
            WriteNativeHandoffFailure(at, error, pulse, helper, pacing, time, construction, runtime);
        }
        try
        {
            var submitted = await Fixture.SubmitAsync(host.Services, Fixture.Parameters(Guid.CreateVersion7(),
                OrganizationId.From(Guid.CreateVersion7()), OrganizationId.From(Guid.CreateVersion7())));
            submitted.IsSuccess.Should().BeTrue();
            var job = submitted.Value;
            boundary = "bridge";
            var storage = HangfireStorageBootstrap.Create(source, Fixture.Registry.Infrastructure, new HangfireBoundedLogProvider());
            bridge = new BackgroundJobBridge(new PostgresBackgroundJobBridgeStore(source, Fixture.Registry,
                bounds, new SystemGuidFactory()), new HangfireWakeupClient(storage, HangfireRuntimeFilters.Create()), bounds, TimeProvider.System);
            (await bridge.RunQuantumAsync(startup.Token)).Should().BeTrue();
            while (bridge.Snapshot.Outstanding != 0) await Task.Delay(10, startup.Token);
            bridge.Snapshot.Acknowledged.Should().Be(1);
            await bridge.StopAsync(startup.Token);
            boundary = "lock";
            blocker = await source.OpenConnectionAsync(startup.Token);
            transaction = await blocker.BeginTransactionAsync(startup.Token);
            string original;
            await using (var command = new NpgsqlCommand("SELECT vendor_job_id FROM background_jobs WHERE id=@id FOR UPDATE", blocker, transaction) { CommandTimeout = 5 })
            {
                command.Parameters.AddWithValue("id", job.Value);
                original = (string)(await command.ExecuteScalarAsync(startup.Token))!;
            }
            pulse = KeepNativeHandoffLockAsync(blocker, transaction, pulseStop.Token);
            boundary = "runtime_start";
            await runtime.StartAsync(startup.Token);
            // The initial empty recovery creates no typed lease. Keep real
            // renewal pacing held through the later normal Prepare/Arm, so a
            // row-lock refusal cannot replace the constructor-entry subject.
            pacing = renewal.ProveNormalDeadlineArmedBeforePacingAsync(startup.Token);
            boundary = "cleanup_enter";
            var entered = await Task.WhenAny(time.Entered, pulse).WaitAsync(startup.Token);
            entered.Should().BeSameAs(time.Entered, "a failed keepalive cannot silently release the controlled lock");
            await time.Entered;
            boundary = "lock_release";
            // The first real empty acquisition now retains its own actual timer
            // cleanup and local recovery identity. Release the DB lock before
            // testing hint assistance, so it cannot explain the second refusal.
            pulseStop.Cancel();
            await pulse;
            await transaction.CommitAsync(startup.Token);
            await transaction.DisposeAsync();
            transaction = null;
            ((BackgroundJobControl)NativeSdkField(runtime, "_jobs")).CanRoute(job).Should().BeFalse();
            runtime.Snapshot.Control!.Executions.Should().Be(1);
            (await NativeSdkVendorStateAsync(source, original, startup.Token)).Should().Be("Processing");
            boundary = "hint_start";
            helper = EnsureNativeSdkStartAsync(runtime, source, job, construction.Entered, startup.Token, async () =>
            {
                observed.TrySetResult();
                await releaseHelper.Task.WaitAsync(startup.Token);
            });
            (await Task.WhenAny(observed.Task, helper).WaitAsync(startup.Token)).Should().BeSameAs(observed.Task);
            boundary = "hint_count";
            long nativeCount;
            string newest;
            await using (var read = await source.OpenConnectionAsync(startup.Token))
            await using (var count = new NpgsqlCommand("SELECT count(*),max(id)::text FROM public.job", read) { CommandTimeout = 5 })
            await using (var row = await count.ExecuteReaderAsync(startup.Token))
            {
                (await row.ReadAsync(startup.Token)).Should().BeTrue();
                nativeCount = row.GetInt64(0);
                newest = row.GetString(1);
            }
            if (nativeCount > 1)
            {
                boundary = "premature_hint";
                // Counterfactual: the premature real extra hint is consumed
                // while the prior local identity remains held; no fake work
                // result or fabricated cleanup signal makes it succeed.
                while (await NativeSdkVendorStateAsync(source, newest, startup.Token) != "Succeeded")
                    await Task.Delay(10, startup.Token);
                ((BackgroundJobControl)NativeSdkField(runtime, "_jobs")).CanRoute(job).Should().BeFalse();
            }
            construction.Calls.Should().Be(0);
            (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!.NormalAttempts.Should().Be(0);
            nativeCount.Should().Be(1, "no extra native hint may be consumed before preceding private cleanup joins");
            boundary = "release_cleanup";
            time.Release();
            await time.Joined.WaitAsync(startup.Token);
            releaseHelper.TrySetResult();
            await Task.WhenAll(helper, pacing);
            boundary = "construction";
            await construction.Entered.WaitAsync(startup.Token);
            boundary = "renewal";
            await renewal.WaitForFirstCallbackAsync(startup.Token);
            boundary = "assert";
            construction.Calls.Should().Be(1);
            (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!.NormalAttempts.Should().Be(1);
            (await GenerationAsync(database, job)).Should().Be(1);
            runtime.Snapshot.Bridge!.PriorHintRepairsAcknowledged.Should().Be(0);
        }
        catch (Exception error) when (NativeWaitCatchable(error)) { Preserve(error, boundary); }
        finally
        {
            runtime.RequestStop();
            renewal.ReleaseAll();
            startup.Cancel();
            time.Release();
            releaseHelper.TrySetResult();
            construction.Release();
            pulseStop.Cancel();
            async Task ObserveCleanupAsync(Func<Task> operation)
            {
                try { await operation(); }
                catch (Exception error) when (NativeWaitCatchable(error)) { Preserve(error, "cleanup"); }
            }
            try { if (helper is not null) await helper; }
            catch (OperationCanceledException) when (startup.IsCancellationRequested) { }
            catch (Exception error) when (NativeWaitCatchable(error)) { Preserve(error, "cleanup"); }
            try { if (pacing is not null) await pacing; }
            catch (OperationCanceledException) when (startup.IsCancellationRequested) { }
            catch (Exception error) when (NativeWaitCatchable(error)) { Preserve(error, "cleanup"); }
            if (pulse is not null) await ObserveCleanupAsync(() => pulse);
            if (transaction is not null) await ObserveCleanupAsync(() => transaction.DisposeAsync().AsTask());
            if (blocker is not null) await ObserveCleanupAsync(() => blocker.DisposeAsync().AsTask());
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            if (bridge is not null) await ObserveCleanupAsync(() => bridge.StopAsync(cleanup.Token));
            await ObserveCleanupAsync(() => runtime.StopAsync(cleanup.Token));
            var runtimeJoined = false;
            await ObserveCleanupAsync(async () =>
            {
                await runtime.Completion.WaitAsync(cleanup.Token);
                runtimeJoined = true;
            });
            await ObserveCleanupAsync(async () =>
            {
                await renewal.JoinAsync(runtimeJoined).WaitAsync(cleanup.Token);
                renewal.ThrowIfFaulted();
            });
        }
        failure?.Throw();
    }

    // First-failure memory only: no SQL, repair, cancellation or provider call.
    private void WriteNativeHandoffFailure(string boundary, Exception error, Task? pulse, Task? helper, Task? pacing,
        NativeHandoffCleanupTime time, BackgroundJobPositiveProviderConstructionProbe construction, BackgroundJobRuntime runtime)
    {
        static string State(Task? task) => task?.Status switch
        {
            null => "not_issued",
            TaskStatus.RanToCompletion => "succeeded",
            TaskStatus.Canceled => "canceled",
            TaskStatus.Faulted => "faulted",
            _ => "pending",
        };
        static string Count(long? value) => value is >= 0 and <= 10000
            ? value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unavailable";
        var kind = error switch
        {
            TimeoutException => "timeout",
            OperationCanceledException => "cancellation",
            Xunit.Sdk.XunitException => "assertion",
            InvalidOperationException => "invalid_state",
            _ => "other",
        };
        try
        {
            var control = runtime.Snapshot.Control;
            output.WriteLine("Native handoff failure;boundary=" + boundary + ";kind=" + kind
                + ";pulse=" + State(pulse) + ";helper=" + State(helper) + ";pacing=" + State(pacing)
                + ";cleanup=" + State(time.Entered) + ";joined=" + State(time.Joined)
                + ";constructor=" + State(construction.Entered)
                + ";executions=" + Count(control?.Executions)
                + ";renewalRefused=" + Count(control?.LeaseLossRenewalRefused)
                + ";leaseElapsed=" + Count(control?.LeaseLossElapsed) + ".");
        }
        catch (Exception observation) when (NativeWaitCatchable(observation)) { }
    }

    private static async Task KeepNativeHandoffLockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            await using var command = new NpgsqlCommand("SELECT 1", connection, transaction) { CommandTimeout = 5 };
            // Join an issued bounded command before touching the transaction; cancel only pacing.
            (await command.ExecuteScalarAsync(CancellationToken.None)).Should().Be(1);
            try { await Task.Delay(200, stopping); }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        }
    }

    private sealed class NativeHandoffCleanupTime(TimeSpan frameworkWait, TimeProvider time) : TimeProvider
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _joined = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _captured;
        public override long TimestampFrequency => System.TimestampFrequency;
        public override long GetTimestamp() => System.GetTimestamp();
        public override DateTimeOffset GetUtcNow() => System.GetUtcNow();
        internal Task Entered => _entered.Task;
        internal Task Joined => _joined.Task;
        internal void Release() => _release.TrySetResult();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var actual = time.CreateTimer(callback, state, dueTime, period);
            return state is HangfireGenerationWork.Invocation && dueTime == frameworkWait
                && Interlocked.CompareExchange(ref _captured, 1, 0) == 0 ? new HeldTimer(actual, this) : actual;
        }
        private sealed class HeldTimer(ITimer actual, NativeHandoffCleanupTime owner) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => actual.Change(dueTime, period);
            public void Dispose() => actual.Dispose();
            public async ValueTask DisposeAsync()
            {
                await actual.DisposeAsync();
                owner._entered.TrySetResult();
                await owner._release.Task;
                owner._joined.TrySetResult();
            }
        }
    }
}
