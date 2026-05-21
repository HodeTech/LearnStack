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
/// <para>
/// Domain-event collection state is lazily allocated: the backing
/// <c>List</c> and its <see cref="ReadOnlyCollection{T}"/> view are only
/// created on first raise or first read. EF Core materialises every loaded
/// aggregate through the parameterless ctor on read paths; the lazy
/// approach keeps materialisation allocation-free for the common
/// query case (paginated reads, projections), and pays the allocation only
/// when an aggregate actually raises events (command paths).
/// </para>
/// </remarks>
public abstract class Entity<TId> : IHasId<TId>, IHasDomainEvents
    where TId : struct, IStronglyTypedId<Guid>
{
    private List<IDomainEvent>? _domainEvents;
    private ReadOnlyCollection<IDomainEvent>? _domainEventsView;

    protected Entity(TId id)
    {
        Id = id;
    }

    // EF Core / ORM materialization ctor.
    protected Entity()
    {
    }

    public TId Id { get; protected init; }

    /// <summary>
    /// In-process domain events raised since the last <see cref="ClearDomainEvents"/>.
    /// Returns a cached <see cref="ReadOnlyCollection{T}"/> wrapper rather
    /// than the backing <c>List</c> directly so callers cannot downcast
    /// and mutate the collection out from under the aggregate. Both the
    /// backing list and the wrapper are lazily allocated on first
    /// access / first raise.
    /// </summary>
    public IReadOnlyCollection<IDomainEvent> DomainEvents =>
        _domainEventsView ??= (_domainEvents ??= []).AsReadOnly();

    protected void RaiseDomainEvent(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        (_domainEvents ??= []).Add(domainEvent);
    }

    public void ClearDomainEvents() => _domainEvents?.Clear();

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
