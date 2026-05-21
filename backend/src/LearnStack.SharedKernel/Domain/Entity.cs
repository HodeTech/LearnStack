using System.Collections.ObjectModel;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.Domain;

/// <summary>
/// Append-only / audit aggregate base. Carries identity and raises
/// in-process <see cref="IDomainEvent"/>s; does <em>not</em> carry the
/// <c>CreatedAt</c> / <c>UpdatedAt</c> audit columns — those belong to
/// mutable aggregates and live on <see cref="AuditableEntity{TId}"/>.
/// </summary>
/// <remarks>
/// <para>
/// The audit subsystem's own <c>AuditEntry</c> aggregate inherits this base
/// directly, never <see cref="AuditableEntity{TId}"/>, because audit rows
/// are immutable once written. Architecture test
/// <c>AuditEntry_Inherits_Entity_Not_AuditableEntity</c> guards that rule.
/// </para>
/// <para>
/// Equality contract: identity-based, with three guards every aggregate
/// inherits — transient entities (<see cref="Id"/> equal to
/// <c>default(TId)</c>) are never equal to each other (reference equality
/// is the only match), runtime-type mismatches are never equal even when
/// the underlying ID matches, and the hash code partitions transient
/// instances apart so EF Core's change-tracker identity map and any
/// <c>HashSet</c> in a collection navigation behave correctly.
/// </para>
/// </remarks>
public abstract class Entity<TId> : IHasId<TId>, IHasDomainEvents
    where TId : struct, IStronglyTypedId<Guid>
{
    private readonly List<IDomainEvent> _domainEvents = [];
    private readonly ReadOnlyCollection<IDomainEvent> _domainEventsView;

    protected Entity(TId id)
    {
        Id = id;
        _domainEventsView = _domainEvents.AsReadOnly();
    }

    // EF Core / ORM materialization ctor.
    protected Entity()
    {
        _domainEventsView = _domainEvents.AsReadOnly();
    }

    public TId Id { get; protected init; }

    /// <summary>
    /// In-process domain events raised since the last <see cref="ClearDomainEvents"/>.
    /// Returns a cached <see cref="ReadOnlyCollection{T}"/> wrapper rather
    /// than the backing list so callers cannot cast back to <c>List&lt;T&gt;</c>
    /// and mutate the collection out from under the aggregate. The view is
    /// initialised once per entity (cheap reference allocation) and lives
    /// for the entity's lifetime, so the unit-of-work's hot-path access
    /// stays allocation-free.
    /// </summary>
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEventsView;

    protected void RaiseDomainEvent(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        _domainEvents.Add(domainEvent);
    }

    public void ClearDomainEvents() => _domainEvents.Clear();

    public override bool Equals(object? obj)
    {
        if (obj is not Entity<TId> other)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        // Cross-type guard: a Course and a hypothetical CourseDraft that both
        // inherit Entity<CourseId> and share an Id value are still different
        // runtime types and must not compare equal.
        if (GetType() != other.GetType())
        {
            return false;
        }

        // Transient guard: two newly-constructed aggregates carry default(TId)
        // until SaveChangesAsync stamps them. They must never collapse into
        // each other in EF's change tracker or in a HashSet-backed navigation.
        if (Id.Equals(default(TId)) || other.Id.Equals(default(TId)))
        {
            return false;
        }

        return Id.Equals(other.Id);
    }

    public override int GetHashCode() =>
        Id.Equals(default(TId))
            ? base.GetHashCode()
            : HashCode.Combine(GetType(), Id);
}
