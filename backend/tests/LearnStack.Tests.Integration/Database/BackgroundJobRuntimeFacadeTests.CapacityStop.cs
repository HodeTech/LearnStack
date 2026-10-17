using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobRuntimeFacadeTests
{
    [Fact]
    public async Task Native_host_stop_after_recovery_ACK_does_not_issue_capacity_checkpoint()
    {
        await using var database = await DatabaseAsync();
        var bounds = NativeCommitFaultBounds();
        var state = new CapacityStopState();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBody = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new BackgroundJobFixtureProbe
        {
            Body = async (_, _, _) =>
            {
                state.OriginalOwner = JobApplicationSourceOwner.Current!;
                entered.TrySetResult();
                await releaseBody.Task;
                return Result<None>.Fail(new Error(LocalizedMessage.Of("lockey_dependency_unavailable")));
            },
        };
        await using var host = CapacityStopHost(database, probe, state, bounds);
        var physical = new BackgroundJobPhysicalPoolSettings(true, 50);
        var controlName = "capacity_stop_before_" + Guid.NewGuid().ToString("N");
        await using var source = Source(database, physical, controlName);
        using var globals = new GlobalFilterSnapshot();
        using var deadline = new CancellationTokenSource(TestWait);
        using var allowStop = new ManualResetEventSlim();
        var syncHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = Runtime(host.Services, source, bounds, physical);
        await using var locker = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        NpgsqlTransaction? locked = null;
        Task? holder = null;
        var stopCallback = new FacadeCleanupSignals();
        CancellationTokenRegistration stopRegistration = default;
        try
        {
            await runtime.StartAsync(deadline.Token);
            await EventuallyQualificationAsync(() => Task.FromResult(runtime.Snapshot.IsHealthy), deadline.Token);
            var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
                BackgroundJobRuntimeFixture.CapacityKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup);
            await entered.Task.WaitAsync(deadline.Token);
            var execution = NativeCommitExecution(runtime, job);
            locked = await locker.BeginTransactionAsync(deadline.Token);
            await LockNativeJobAsync(locker, locked, job, deadline.Token);
            releaseBody.TrySetResult();
            await WaitForNativeJobLockAsync(database, locker.ProcessID, "learnstack_outbox_admin", controlName, deadline.Token);
            var control = (BackgroundJobControl)typeof(BackgroundJobRuntime).GetField("_jobs", PrivateInstance)!.GetValue(runtime)!;
            var sync = typeof(BackgroundJobControl).GetField("_sync", PrivateInstance)!.GetValue(control)!;
            var controlStop = (CancellationTokenSource)typeof(BackgroundJobControl).GetField("_stop", PrivateInstance)!.GetValue(control)!;
            stopRegistration = controlStop.Token.Register(() =>
            {
                stopCallback.Entered.TrySetResult();
                stopCallback.Release.Task.GetAwaiter().GetResult();
                stopCallback.Joined.TrySetResult();
            });
            // A dedicated synchronous holder owns this monitor throughout;
            // the async test never resumes a monitor on a different thread.
            holder = Task.Run(() =>
            {
                lock (sync)
                {
                    syncHeld.TrySetResult();
                    if (!allowStop.Wait(TestWait)) throw new TimeoutException("The pre-issuance stop barrier was not released.");
                    runtime.RequestStop();
                }
            });
            await syncHeld.Task.WaitAsync(deadline.Token);
            await locked.RollbackAsync(deadline.Token);
            await locked.DisposeAsync();
            locked = null;
            await EventuallyQualificationAsync(() => Task.FromResult(CapacityRecoveryAckPublished(control)), deadline.Token);
            state.OriginalOwner!.CleanupConfirmed.Should().BeTrue("the original business and callbacks have joined before recovery ACK");
            allowStop.Set();
            await holder.WaitAsync(deadline.Token);
            await stopCallback.Entered.Task.WaitAsync(deadline.Token);
            // The actual control loop has passed the guarded issuance window.
            // Its stop callback holds final request release, so a second seam
            // cannot hide an incorrectly issued checkpoint by joining first.
            var loops = (TaskCompletionSource<Task[]>)typeof(BackgroundJobControl).GetField("_loopsPublished", PrivateInstance)!.GetValue(control)!;
            var publishedLoops = await loops.Task.WaitAsync(deadline.Token);
            await publishedLoops[1].WaitAsync(deadline.Token);
            typeof(BackgroundJobExecutionControl).GetField("_capacityCheckpoint", PrivateInstance)!.GetValue(execution)
                .Should().BeNull("host stop won the issuance lock after an actual recovery ACK");
            state.Entered.Task.IsCompleted.Should().BeFalse();
            (await ReadCapacityCleanupEvidenceAsync(database, job)).Should().Be((false, false, (string?)null));
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
            probe.Constructions.Should().Equal("CapacityRoot");
            var reopen = () => state.OriginalOwner.Enter();
            runtime.Completion.IsCompleted.Should().BeFalse();
            stopCallback.Release.TrySetResult();
            await stopCallback.Joined.Task.WaitAsync(deadline.Token);
            await runtime.StopAsync(deadline.Token);
            await runtime.Completion.WaitAsync(deadline.Token);
            reopen.Should().Throw<InvalidOperationException>();
        }
        finally
        {
            releaseBody.TrySetResult();
            allowStop.Set();
            stopCallback.Release.TrySetResult();
            if (locked is not null) { await locked.RollbackAsync(); await locked.DisposeAsync(); }
            if (holder is not null) await holder.WaitAsync(TestWait);
            await stopRegistration.DisposeAsync();
            using var cleanup = new CancellationTokenSource(TestWait);
            await runtime.StopAsync(cleanup.Token);
            await runtime.Completion.WaitAsync(cleanup.Token);
            await DisposePinnedSourceAsync(source);
        }
    }

    [Fact]
    public async Task Native_host_stop_joins_issued_capacity_checkpoint_private_callback_before_disposing_sources()
    {
        await using var database = await DatabaseAsync();
        var bounds = NativeCommitFaultBounds();
        var state = new CapacityStopState();
        var probe = new BackgroundJobFixtureProbe
        {
            Body = (_, _, _) =>
            {
                state.OriginalOwner = JobApplicationSourceOwner.Current!;
                return Task.FromResult(Result<None>.Fail(new Error(LocalizedMessage.Of("lockey_dependency_unavailable"))));
            },
        };
        await using var host = CapacityStopHost(database, probe, state, bounds);
        var physical = new BackgroundJobPhysicalPoolSettings(true, 50);
        await using var source = Source(database, physical);
        var applicationSource = host.Services.GetRequiredService<ApplicationDataSourceLifetime>();
        using var globals = new GlobalFilterSnapshot();
        using var deadline = new CancellationTokenSource(TestWait);
        var runtime = Runtime(host.Services, source, bounds, physical);
        try
        {
            await runtime.StartAsync(deadline.Token);
            var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
                BackgroundJobRuntimeFixture.CapacityKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup);
            await state.Entered.Task.WaitAsync(deadline.Token);
            var execution = NativeCommitExecution(runtime, job);
            var checkpoint = (BackgroundJobCapacityCheckpointOperation)typeof(BackgroundJobExecutionControl)
                .GetField("_capacityCheckpoint", PrivateInstance)!.GetValue(execution)!;
            checkpoint.Should().NotBeNull();
            checkpoint.Completion.IsCompleted.Should().BeFalse();
            state.Command!.SourceOwner.Should().BeSameAs(state.OriginalOwner);
            applicationSource.Dispose();
            source.Dispose();
            state.Callback.Entered.Task.IsCompleted.Should().BeFalse("host stop must precede the checkpoint cancellation");
            checkpoint.TimelyReturn.IsCompleted.Should().BeFalse();
            runtime.RequestStop();
            await state.Callback.Entered.Task.WaitAsync(deadline.Token);
            using (var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
                await FluentActions.Awaiting(() => runtime.StopAsync(caller.Token)).Should().ThrowAsync<OperationCanceledException>();
            runtime.Completion.IsCompleted.Should().BeFalse();
            applicationSource.IsDisposalComplete.Should().BeFalse();
            source.IsDisposalComplete.Should().BeFalse();

            state.ReleaseObserver.TrySetResult();
            await state.ObserverReturned.Task.WaitAsync(deadline.Token);
            var actual = (Task<JobCapacityCleanupResult>)typeof(BackgroundJobCapacityCheckpointOperation)
                .GetField("_actual", PrivateInstance)!.GetValue(checkpoint)!;
            var actualResult = await actual.WaitAsync(deadline.Token);
            actualResult.CleanupFailed.Should().BeFalse();
            actualResult.CommitAcknowledged.Should().BeFalse();
            actualResult.KnownRollback.Should().BeTrue();
            // The real app task is finished, so only its separately owned
            // private cancellation callback can justify continued retention.
            var early = await Task.WhenAny(checkpoint.Completion, Task.Delay(TimeSpan.FromMilliseconds(200), deadline.Token));
            early.Should().NotBeSameAs(checkpoint.Completion);
            state.Callback.Joined.Task.IsCompleted.Should().BeFalse();
            runtime.Completion.IsCompleted.Should().BeFalse();
            applicationSource.IsDisposalComplete.Should().BeFalse();
            source.IsDisposalComplete.Should().BeFalse();
            (await ReadCapacityCleanupEvidenceAsync(database, job)).Should().Be((false, false, (string?)null));
            state.Callback.Release.TrySetResult();
            await state.Callback.Joined.Task.WaitAsync(deadline.Token);
            (await checkpoint.Completion.WaitAsync(deadline.Token)).CleanupFailed.Should().BeFalse();
            await runtime.Completion.WaitAsync(deadline.Token);
            (await ReadCapacityCleanupEvidenceAsync(database, job)).Should().Be((false, false, (string?)null));
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
            probe.Constructions.Should().Equal("CapacityRoot");
            await EventuallyQualificationAsync(() => Task.FromResult(applicationSource.IsDisposalComplete && source.IsDisposalComplete), deadline.Token);
        }
        finally
        {
            state.ReleaseObserver.TrySetResult();
            state.Callback.Release.TrySetResult();
            await state.Registration.DisposeAsync();
            using var cleanup = new CancellationTokenSource(TestWait);
            await runtime.StopAsync(cleanup.Token);
            await runtime.Completion.WaitAsync(cleanup.Token);
            await DisposePinnedSourceAsync(source);
        }
    }

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> CapacityStopHost(DisposableSchemaDatabase database,
        BackgroundJobFixtureProbe probe, CapacityStopState state, BackgroundJobRuntimeOptions bounds) =>
        Fixture.Host(database, probe, configure: services =>
        {
            services.RemoveAll<BackgroundJobDefinitionRegistry>();
            services.AddSingleton(Fixture.CapacityRegistry());
            services.RemoveAll<BackgroundJobRuntimeOptions>();
            services.AddSingleton(bounds);
            services.AddSingleton(state);
            services.AddTransient<IPipelineBehavior<RecordBackgroundJobCapacityCleanupCommand, Result<None>>, CapacityStopObserver>();
        });

    private static bool CapacityRecoveryAckPublished(BackgroundJobControl control)
    {
        var operation = typeof(BackgroundJobControl).GetField("_controlOperation", PrivateInstance)!.GetValue(control);
        if (operation is not OwnedBackgroundJobControl<BackgroundJobCapacityRecoveryReceipt> { CleanupConfirmed: true, Value: not null }) return false;
        var requests = (System.Collections.IEnumerable)typeof(BackgroundJobControl).GetField("_requests", PrivateInstance)!.GetValue(control)!;
        var request = requests.Cast<object>().SingleOrDefault();
        return request is not null && ReferenceEquals(operation, request.GetType().GetProperty("Acquisition", PrivateInstance)!.GetValue(request));
    }

    private sealed class CapacityStopState
    {
        internal JobApplicationSourceOwner? OriginalOwner { get; set; }
        internal RecordBackgroundJobCapacityCleanupCommand? Command { get; set; }
        internal CancellationTokenRegistration Registration { get; set; }
        internal FacadeCleanupSignals Callback { get; } = new();
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseObserver { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ObserverReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class CapacityStopObserver(CapacityStopState state)
        : IPipelineBehavior<RecordBackgroundJobCapacityCleanupCommand, Result<None>>
    {
        public async Task<Result<None>> Handle(RecordBackgroundJobCapacityCleanupCommand request,
            RequestHandlerDelegate<Result<None>> next, CancellationToken cancellationToken)
        {
            state.Command = request;
            state.Registration = cancellationToken.Register(() =>
            {
                state.Callback.Entered.TrySetResult();
                state.Callback.Release.Task.GetAwaiter().GetResult();
                state.Callback.Joined.TrySetResult();
            });
            state.Entered.TrySetResult();
            try
            {
                await state.ReleaseObserver.Task;
                cancellationToken.ThrowIfCancellationRequested();
                return await next();
            }
            finally { state.ObserverReturned.TrySetResult(); }
        }
    }
}
