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
/// Equality is identity-based: two entities are equal iff their
/// <see cref="Id"/>s match. Reference equality is also exposed so EF Core's
/// change tracker behaves predictably.
/// </para>
/// </remarks>
public abstract class Entity<TId> : IHasId<TId>, IHasDomainEvents
    where TId : struct, IStronglyTypedId<Guid>
{
    private readonly List<IDomainEvent> _domainEvents = [];

    protected Entity(TId id)
    {
        Id = id;
    }

    // EF Core / ORM materialization ctor.
    protected Entity()
    {
    }

    public TId Id { get; protected init; }

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

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

        return Id.Equals(other.Id);
    }

    public override int GetHashCode() => Id.GetHashCode();
}
