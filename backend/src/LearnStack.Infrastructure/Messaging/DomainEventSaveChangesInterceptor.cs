using System.Runtime.CompilerServices;
using LearnStack.Application.Messaging;
using LearnStack.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace LearnStack.Infrastructure.Messaging;

/// <summary>Collects domain facts only from successful saves, while retaining tracked sources for terminal cleanup.</summary>
/// <remarks>
/// Saving a domain fact requires the shared producer execution boundary. Staging refuses
/// event-bearing saves before SQL when a caller opens only a raw announced transaction;
/// the interceptor does not create an owner or dispatch on the caller's behalf.
/// </remarks>
public sealed class DomainEventSaveChangesInterceptor(DomainEventCollector collector) : SaveChangesInterceptor
{
    private readonly ConditionalWeakTable<DbContext, object> _attached = new();

    /// <summary>Attaches before a context begins tracking so empty and subsequently detached sources remain observable.</summary>
    public void Attach(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_attached.TryGetValue(context, out _)) return;
        _attached.Add(context, new object());
        context.ChangeTracker.Tracked += OnTracked;
        var reference = new WeakReference<DbContext>(context);
        collector.RegisterTracker(() =>
        {
            if (!reference.TryGetTarget(out var trackedContext)) return;
            try
            {
                ObserveTrackedSources(trackedContext);
            }
            catch (ObjectDisposedException)
            {
                // Explicitly disposed contexts can outlive their transaction in a
                // reused scope, but have no tracker to re-register for the next owner.
            }
        });
        ObserveTrackedSources(context);
    }

    private void OnTracked(object? sender, EntityTrackedEventArgs args)
    {
        if (args.Entry.Entity is IHasDomainEvents source) collector.Observe(source);
    }

    private void ObserveTrackedSources(DbContext context)
    {
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.Entity is IHasDomainEvents source) collector.Observe(source);
        }
    }

    private void Stage(DbContext? context, bool suppressed)
    {
        if (context is null) return;
        Attach(context);
        // SavingChanges precedes EF's automatic detection. Snapshot only actual changed
        // entries; retained detached/unchanged sources must not hitchhike on this save.
        context.ChangeTracker.DetectChanges();
        ObserveTrackedSources(context);
        if (suppressed)
        {
            collector.Abort(context);
            return;
        }

        collector.Stage(context, context.ChangeTracker.Entries()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(entry => entry.Entity).OfType<IHasDomainEvents>());
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Stage(eventData.Context, result.HasResult);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Stage(eventData.Context, result.HasResult);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (eventData.Context is { } context) collector.Promote(context, result);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context) collector.Promote(context, result);
        return ValueTask.FromResult(result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        if (eventData.Context is { } context) collector.Abort(context);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context) collector.Abort(context);
        return Task.CompletedTask;
    }

    public override void SaveChangesCanceled(DbContextEventData eventData)
    {
        if (eventData.Context is { } context) collector.Abort(context);
    }

    public override Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context) collector.Abort(context);
        return Task.CompletedTask;
    }
}
