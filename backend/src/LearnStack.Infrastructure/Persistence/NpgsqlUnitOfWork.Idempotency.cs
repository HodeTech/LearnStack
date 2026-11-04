using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using LearnStack.SharedKernel.Idempotency;

namespace LearnStack.Infrastructure.Persistence;

public sealed partial class NpgsqlUnitOfWork : IIdempotencyBusinessOwnerCleanup
{
    private readonly object _cleanupGate = new();
    private readonly List<Task<bool>> _transactionCleanups = [];
    private bool _transactionCleanupFailed;
    private bool _idempotencyEnding;
    private TaskCompletionSource<bool>? _unitCleanup;
    private Task<bool>? _idempotencyEnd;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        TaskCompletionSource<bool> completion;
        lock (_cleanupGate)
        {
            // Preserve the ordinary idempotent-disposal contract. The dedicated
            // outcome boundary below joins the first attempt's actual completion.
            if (_unitCleanup is not null) return;
            completion = _unitCleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try
        {
            await DisposeCoreAsync();
            completion.TrySetResult(true);
        }
        finally { completion.TrySetResult(false); }
    }

    /// <inheritdoc />
    public ValueTask<bool> EndBusinessOwnerAsync()
    {
        lock (_cleanupGate)
        {
            _idempotencyEnding = true;
            return new(_idempotencyEnd ??= EndBusinessOwnerCoreAsync());
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The lifecycle port reports failed cleanup without exposing or replacing its primary exception.")]
    private async Task<bool> EndBusinessOwnerCoreAsync()
    {
        // Publish the single owned task before invoking provider callbacks. A
        // concurrent/repeated observer joins it rather than starting more cleanup.
        await Task.Yield();
        Task<bool>[] pending;
        lock (_cleanupGate) pending = _transactionCleanups.ToArray();
        foreach (var cleanup in pending) await cleanup;
        try { await DisposeAsync(); }
        catch
        {
            // Ordinary DisposeAsync retains its primary failure. This port conveys
            // only positive cleanup evidence, never provider exception payloads.
            return false;
        }
        Task<bool> unitCleanup;
        lock (_cleanupGate) unitCleanup = _unitCleanup!.Task;
        if (!await unitCleanup) return false;
        lock (_cleanupGate) return !_transactionCleanupFailed && _transactionCleanups.Count == 0;
    }

    private async ValueTask DisposeOwnedTransactionAsync(DbTransaction transaction)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_cleanupGate) _transactionCleanups.Add(completion.Task);
        var succeeded = false;
        try
        {
            await transaction.DisposeAsync();
            succeeded = true;
        }
        finally
        {
            lock (_cleanupGate)
            {
                _transactionCleanupFailed |= !succeeded;
                _transactionCleanups.Remove(completion.Task);
            }
            completion.TrySetResult(succeeded);
        }
    }
}
