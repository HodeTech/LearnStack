using LearnStack.SharedKernel.Domain;
using MediatR;

namespace LearnStack.Application.Messaging;

/// <summary>
/// Stages saving-source snapshots, promotes successful saves, and retains facts until
/// the physical owner completes. Tracking retention serves cleanup, never save eligibility.
/// </summary>
public sealed class DomainEventCollector(ProducerExecution producer, IPublisher publisher) : IDisposable
{
    /// <summary>Total successful promotions allowed in one physical transaction.</summary>
    public const int MaxEventsPerTransaction = 256;

    private readonly HashSet<IHasDomainEvents> _sources = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, List<PendingEvent>> _stages = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<IDomainEvent> _promoted = new(ReferenceEqualityComparer.Instance);
    private readonly Queue<PendingEvent> _queue = new();
    private readonly List<Action> _trackers = [];
    private bool _draining;
    private bool _disposed;

    /// <summary>Registers a context-lifetime observer, retained across transaction-state cleanup.</summary>
    public void RegisterTracker(Action observeTrackedSources)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(observeTrackedSources);
        _trackers.Add(observeTrackedSources);
    }

    /// <summary>Reobserves already tracked sources at owner entry when a DI scope is reused.</summary>
    public void RefreshTrackedSources()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var observe in _trackers) observe();
    }

    /// <summary>Retains even an empty source so later uncollected raises are discarded at termination.</summary>
    public void Observe(IHasDomainEvents source)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(source);
        _sources.Add(source);
    }

    /// <summary>
    /// Snapshots only sources this context is actually about to save. Original event lists
    /// stay intact until owner cleanup; failed saves can retry the same facts.
    /// </summary>
    /// <remarks>
    /// Event-bearing saves require an enrolled writable producer invocation, even when
    /// SQL authority has already been announced. Background/admin/seed writers use the
    /// shared execution coordinator; a raw transaction alone cannot own completion.
    /// Event-free saves do not acquire producer authority through this collector.
    /// </remarks>
    public void Stage(object context, IEnumerable<IHasDomainEvents> savingSources)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(savingSources);
        if (_draining)
        {
            producer.Poison();
            throw new InvalidOperationException("Domain saves cannot run during domain dispatch.");
        }

        // EF can fail in another SavingChanges interceptor before its failure callback.
        // A subsequent attempt replaces that unconfirmed snapshot, never promotes it.
        _stages.Remove(context);

        var pending = new List<PendingEvent>();
        ProducerExecution.Origin? origin = null;
        foreach (var source in savingSources)
        {
            Observe(source);
            foreach (var domainEvent in source.DomainEvents)
            {
                if (_promoted.Contains(domainEvent)) continue;
                origin ??= producer.CaptureOrigin();
                pending.Add(new PendingEvent(domainEvent, origin));
            }
        }

        _stages.Add(context, pending);
    }

    /// <summary>Promotes only a successful, nonzero save; any post-save contract fault poisons the unit.</summary>
    public void Promote(object context, int savedCount)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(context);
        if (!_stages.Remove(context, out var pending) || savedCount <= 0) return;

        try
        {
            if (_draining)
            {
                throw new InvalidOperationException("Domain dispatch cannot recursively promote events.");
            }

            foreach (var item in pending)
            {
                producer.RequireOrigin(item.Origin);
                if (_promoted.Contains(item.Event)) continue;
                if (_promoted.Count == MaxEventsPerTransaction)
                {
                    throw new InvalidOperationException("A transaction cannot produce more than 256 domain events.");
                }

                producer.RecordProducedWork(item.Origin);
                _promoted.Add(item.Event);
                _queue.Enqueue(item);
            }
        }
        catch
        {
            // Persistence already happened. Even a caller swallowing this exception
            // must not commit business state whose fact could not be admitted.
            producer.Poison();
            throw;
        }
    }

    /// <summary>Failed/canceled persistence discards only its stage, leaving source facts available to retry.</summary>
    public void Abort(object context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _stages.Remove(context);
    }

    /// <summary>The sole sequential notification dispatch site, called by the physical owner before audit.</summary>
    public async Task DrainAsync(ProducerExecution.Invocation owner, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        producer.RequireOwner(owner);
        if (_draining)
        {
            producer.Poison();
            throw new InvalidOperationException("Domain drain cannot be reentered.");
        }

        _draining = true;
        try
        {
            while (_queue.TryDequeue(out var pending))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var dispatch = producer.EnterDomainDispatch(pending.Origin, pending.Event.EventId);
                await publisher.Publish(pending.Event, cancellationToken);
            }
        }
        catch
        {
            producer.Poison();
            throw;
        }
        finally
        {
            _draining = false;
        }
    }

    /// <summary>Owner-terminal cleanup; joiners never invoke it. Safe to repeat at scoped disposal.</summary>
    public void Clear()
    {
        foreach (var source in _sources) source.ClearDomainEvents();
        _sources.Clear();
        _stages.Clear();
        _queue.Clear();
        _promoted.Clear();
        _draining = false;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        Clear();
        _trackers.Clear();
        _disposed = true;
    }

    private sealed record PendingEvent(IDomainEvent Event, ProducerExecution.Origin Origin);
}
