using System.Diagnostics.CodeAnalysis;
using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>The actual recipient task, cancellation and cleanup outlive every finite caller wait.</summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Only actual task and cancellation completion permit the private source to be disposed.")]
internal sealed class BackgroundJobRecipientOperation
{
    private readonly JobExecutor _executor;
    private readonly BackgroundJobLifecycleProjection _projection;
    private readonly BackgroundJobRuntimeOptions _bounds;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TaskCompletionSource<Task<JobRecipientSubmissionResult>> _published = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _returned = Signal();
    private readonly TaskCompletionSource _issued = Signal();
    private readonly TaskCompletionSource _stop = Signal();
    private readonly TaskCompletionSource _completion;
    private readonly Task _wait, _cancel, _observation;
    private int _scheduled, _retained, _cleanupFailed;
    private JobRecipientSubmissionResult? _result;

    internal BackgroundJobRecipientOperation(BackgroundJobRecipientClaim claim, JobExecutor executor,
        BackgroundJobLifecycleProjection projection, BackgroundJobRuntimeGate runtime,
        BackgroundJobRuntimeOptions bounds, TimeProvider time)
    {
        Claim = claim;
        _executor = executor;
        _projection = projection;
        _bounds = bounds;
        _time = time;
        Gate = new BackgroundJobRunGate(runtime.RunStop).OpenBoundary(BackgroundJobCheckpointKind.RecipientSubmission);
        _completion = new(this, TaskCreationOptions.RunContinuationsAsynchronously);
        _wait = WaitAsync();
        _cancel = RelayCancellationAsync();
        _observation = ObserveAsync();
    }

    internal BackgroundJobRecipientClaim Claim { get; }
    internal BackgroundJobCommitGate Gate { get; }
    internal Task Completion => _completion.Task;
    internal JobRecipientSubmissionResult? Result => Volatile.Read(ref _result);
    internal bool Retained => Volatile.Read(ref _retained) != 0;
    internal bool CleanupFailed => Volatile.Read(ref _cleanupFailed) != 0;

    // The recurrence owner publishes this concrete operation before calling Start.
    internal void Start()
    {
        if (Interlocked.Exchange(ref _scheduled, 1) != 0)
            throw new InvalidOperationException("Recipient operations start once.");
        try
        {
            _issued.TrySetResult();
            Task<JobRecipientSubmissionResult> actual;
            if (ExecutionContext.IsFlowSuppressed()) actual = Task.Run(ExecuteApplicationAsync);
            else { using (ExecutionContext.SuppressFlow()) actual = Task.Run(ExecuteApplicationAsync); }
            _published.TrySetResult(actual);
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure))
        {
            // No returned scheduling handle proves that a callback cannot arrive.
            // The prebound observer and exact recipient remain rooted.
            Volatile.Write(ref _retained, 1);
            RequestStop();
        }
    }

    private Task<JobRecipientSubmissionResult> ExecuteApplicationAsync() =>
        _executor.ExecuteRecipientAsync(Claim, _projection, Gate, _cancellation.Token);

    internal void RequestStop()
    {
        Gate.Close();
        _stop.TrySetResult();
    }

    private async Task WaitAsync()
    {
        await _issued.Task.ConfigureAwait(false);
        using var timer = new CancellationTokenSource();
        var delay = Task.Delay(_bounds.SqlTimeout + TimeSpan.FromSeconds(1), _time, timer.Token);
        try
        {
            var first = await Task.WhenAny(_returned.Task, _stop.Task, delay).ConfigureAwait(false);
            if (first != _returned.Task && !_returned.Task.IsCompleted)
            {
                Volatile.Write(ref _retained, 1);
                RequestStop();
            }
        }
        finally
        {
            await timer.CancelAsync().ConfigureAwait(false);
            await delay.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private async Task RelayCancellationAsync()
    {
        await _stop.Task.ConfigureAwait(false);
        try { await _cancellation.CancelAsync().ConfigureAwait(false); }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure))
        { Volatile.Write(ref _cleanupFailed, 1); }
    }

    private async Task ObserveAsync()
    {
        try
        {
            var actual = await _published.Task.ConfigureAwait(false);
            try { Volatile.Write(ref _result, await actual.ConfigureAwait(false)); }
            catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure))
            { Volatile.Write(ref _cleanupFailed, 1); }
            finally { _returned.TrySetResult(); RequestStop(); }
            await Task.WhenAll(_wait, _cancel).ConfigureAwait(false);
            _cancellation.Dispose();
            if (Result is not { CleanupFailed: false, RetainedCleanup.Count: 0, SourceOwner.CleanupConfirmed: true })
                Volatile.Write(ref _cleanupFailed, 1);
            if (!CleanupFailed) _completion.TrySetResult();
            else Volatile.Write(ref _retained, 1);
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure))
        {
            Volatile.Write(ref _cleanupFailed, 1);
            Volatile.Write(ref _retained, 1);
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
