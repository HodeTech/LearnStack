using System.Collections.Concurrent;
using LearnStack.SharedKernel.Messaging;

namespace LearnStack.Infrastructure.Messaging;

/// <summary>
/// Serialises work per partition key by chaining each unit onto the tail of that
/// key's queue — concurrent across keys, sequential within one.
/// </summary>
/// <remarks>
/// <para>
/// The chain is the whole mechanism: each key maps to the <see cref="Task"/> of
/// the last unit queued for it, and a new unit continues from that task rather
/// than starting fresh. A lock per key would do the same thing while blocking a
/// thread pool thread for the duration of a handler; this blocks nobody.
/// </para>
/// <para>
/// A key's chain is dropped once nothing is queued behind it, so the map holds
/// one entry per <b>in-flight</b> key rather than one per key ever seen. A
/// structure that grew with the key space would be the same defect the cache's
/// ceiling exists to prevent, in a component nobody thinks to look at.
/// </para>
/// </remarks>
public sealed class PartitionSerializer : IPartitionSerializer
{
    private readonly ConcurrentDictionary<string, Task> _tails = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public Task RunSequentiallyFor(string partitionKey, Func<Task> work)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);
        ArgumentNullException.ThrowIfNull(work);

        Task queued;

        // The read-modify-write of the tail has to be atomic against another
        // publisher for the same key, or two units both continue from the same
        // predecessor and run concurrently — which is the one thing this class
        // exists to prevent. AddOrUpdate cannot express it: its update factory
        // may run more than once under contention, and running it twice would
        // queue the work twice.
        lock (_gate)
        {
            var previous = _tails.TryGetValue(partitionKey, out var tail) ? tail : Task.CompletedTask;

            // Faults do not break the chain. A handler that throws must not stop
            // every later event for that aggregate from being delivered — the
            // failure belongs to one unit, and the caller awaiting `queued` is
            // the one that sees it.
            queued = previous.ContinueWith(
                    _ => work(),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default)
                .Unwrap();

            // The chain continues from a COPY, for two separate reasons that are
            // easy to conflate. A ContinueWith whose delegate does not throw
            // completes successfully whatever its antecedent did, which is what
            // keeps a failed unit from faulting everything queued behind it.
            // Reading `Exception` inside it is the other reason: a publisher is
            // free not to await what RunSequentiallyFor returns, and then nobody
            // observes the fault — TaskScheduler.UnobservedTaskException fires,
            // with no request and no correlation id attached to it.
            _tails[partitionKey] = queued.ContinueWith(
                static completed => { _ = completed.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        _ = _tails[partitionKey].ContinueWith(
            _ => Retire(partitionKey),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return queued;
    }

    /// <summary>How many partition keys currently have work chained. A diagnostic.</summary>
    /// <remarks>
    /// Exposed so the "one entry per in-flight key, not per key ever seen" claim
    /// can be asserted directly rather than inferred.
    /// </remarks>
    public int TrackedPartitions => _tails.Count;

    private void Retire(string partitionKey)
    {
        lock (_gate)
        {
            // Only when this key's tail is the one that just finished. Another
            // publisher may have chained onto it in the meantime, and dropping
            // the entry then would let the next unit start from
            // Task.CompletedTask — running concurrently with work still in
            // flight, which is the ordering break this class prevents.
            if (_tails.TryGetValue(partitionKey, out var tail) && tail.IsCompleted)
            {
                _tails.TryRemove(new KeyValuePair<string, Task>(partitionKey, tail));
            }
        }
    }
}
