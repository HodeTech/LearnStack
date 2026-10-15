using System.Diagnostics.CodeAnalysis;
using LearnStack.Application.Messaging;

namespace LearnStack.Infrastructure.Messaging;

/// <summary>One execution's sticky admission gate and task-owned cancellation lifetime.</summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Only the executor may dispose cancellation after the actual callback and scope cleanup finish.")]
public sealed class DeliveryExecutionControl
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _callbacks;
    private bool _finishing;

    public DeliveryCommitGate Gate { get; } = new();
    internal CancellationToken Token => _cancellation.Token;

    public void RequestCancellation()
    {
        // The decision cannot wait behind a callback or operate on a live unit.
        Gate.Close();
        lock (_sync)
        {
            if (_finishing || _callbacks is not null) return;
            _callbacks = _cancellation.CancelAsync();
        }
    }

    internal async Task<bool> FinishCallbacksAsync()
    {
        Task? callbacks;
        lock (_sync)
        {
            _finishing = true;
            callbacks = _callbacks;
        }
        if (callbacks is null) return true;
        try { await callbacks.ConfigureAwait(false); return true; }
        catch (Exception) { return false; }
    }

    internal void DisposeCancellation() => _cancellation.Dispose();
}
