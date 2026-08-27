using System.Collections.Concurrent;
using System.Collections.Immutable;
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

    /// <summary>
    /// The key whose work the current execution flow is inside, if any.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Queuing work for a key from inside that key's own work is a deadlock by
    /// construction: the new unit chains behind a tail that cannot complete
    /// until the current one returns. Measured on the version with no detection
    /// at all — it hung, and the partition stayed wedged for every later event
    /// for the life of the process.
    /// </para>
    /// <para>
    /// <b>Detected to refuse, never to run inline.</b> An earlier attempt ran
    /// the reentrant call inline, reasoning that the caller <i>is</i> the
    /// sequence. It is not sound: an <see cref="AsyncLocal{T}"/> flows into
    /// every task started inside a unit, so a fire-and-forget
    /// <c>_ = RunSequentiallyFor(sameKey, …)</c> inherited the marker and ran
    /// <i>concurrently</i> with the unit it should have queued behind —
    /// measured, and the one guarantee this class exists for. The detection is
    /// the same either way; only the action differs, and that asymmetry is the
    /// whole point. A false positive from a spawned flow throws where it could
    /// have queued: loud, diagnosable, safe. A false positive that runs inline
    /// is a silent concurrency violation.
    /// </para>
    /// <para>
    /// An instance field, not static: two hosts in one process — which the
    /// integration tests build deliberately — otherwise share one marker, and
    /// being inside a key on one serializer would speak for the other.
    /// </para>
    /// <para>
    /// It records every ancestor key on the flow, not just the innermost one.
    /// Comparing against the innermost alone catches <c>A → A</c> and misses
    /// <c>A → B → A</c>, which is the same cycle one hop longer: measured, five
    /// out of five attempts hung, silently and permanently, with no exception
    /// and no log. A cycle through any number of keys is still a cycle.
    /// </para>
    /// </remarks>
    private readonly AsyncLocal<ImmutableHashSet<string>?> _executingKeys = new();

    public Task RunSequentiallyFor(string partitionKey, Func<Task> work)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);
        ArgumentNullException.ThrowIfNull(work);

        // Refused rather than deadlocked. The caller that hits this is a
        // consumer publishing from inside a handler, which Standards 20 already
        // forbids for its own reasons — a handler writes to the outbox, and the
        // OutboxProcessor is the only sanctioned publisher. Answering it with a
        // message beats answering it with a hang.
        var ancestors = _executingKeys.Value ?? ImmutableHashSet<string>.Empty;

        if (ancestors.Contains(partitionKey))
        {
            return Task.FromException(new InvalidOperationException(
                $"Work for partition key '{partitionKey}' is already running on this "
                + "execution flow, and queuing more behind it would wait for a unit that "
                + "cannot finish until this one returns. An integration-event handler "
                + "must not publish — it writes to the outbox, and the OutboxProcessor "
                + "publishes (Standards 20 § IEventBus)."));
        }

        Task queued;
        Task observer;

        // The read-modify-write of the tail has to be atomic against another
        // publisher for the same key, or two units both continue from the same
        // predecessor and run concurrently — which is the one thing this class
        // exists to prevent. AddOrUpdate cannot express it: its update factory
        // may run more than once under contention, and running it twice would
        // queue the work twice.
        lock (_gate)
        {
            var previous = _tails.TryGetValue(partitionKey, out var tail) ? tail : Task.CompletedTask;

            queued = previous.ContinueWith(
                    _ => RunMarked(_executingKeys, partitionKey, work),
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
            observer = queued.ContinueWith(
                static completed => { _ = completed.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            _tails[partitionKey] = observer;
        }

        // Attached to the observer captured INSIDE the lock, not re-read from
        // the dictionary. Measured on the version that re-read it: another
        // publisher's retirement could remove the key in the window between the
        // lock and the indexer, and the caller got a KeyNotFoundException for an
        // event whose work had already been queued and delivered — a success
        // answered with a failure, which on the outbox path means the row is
        // marked failed and redelivered.
        _ = observer.ContinueWith(
            _ => Retire(partitionKey, observer),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return queued;
    }

    private static async Task RunMarked(
        AsyncLocal<ImmutableHashSet<string>?> executingKeys,
        string partitionKey,
        Func<Task> work)
    {
        var previous = executingKeys.Value;
        executingKeys.Value = (previous ?? ImmutableHashSet<string>.Empty).Add(partitionKey);

        try
        {
            await work().ConfigureAwait(false);
        }
        finally
        {
            executingKeys.Value = previous;
        }
    }

    /// <summary>How many partition keys currently have work chained. A diagnostic.</summary>
    /// <remarks>
    /// Exposed so the "one entry per in-flight key, not per key ever seen" claim
    /// can be asserted directly rather than inferred.
    /// </remarks>
    public int TrackedPartitions => _tails.Count;

    private void Retire(string partitionKey, Task observer)
    {
        lock (_gate)
        {
            // Only when this key's tail is still the one that just finished.
            // Another publisher may have chained onto it in the meantime, and
            // dropping the entry then would let the next unit start from
            // Task.CompletedTask — running concurrently with work still in
            // flight, which is the ordering break this class prevents.
            if (_tails.TryGetValue(partitionKey, out var tail) && ReferenceEquals(tail, observer))
            {
                _tails.TryRemove(new KeyValuePair<string, Task>(partitionKey, tail));
            }
        }
    }
}
