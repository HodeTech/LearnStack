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
/// Equality contract: identity-based, defined once in
/// <see cref="Equals(Entity{TId}?)"/>; <see cref="Equals(object?)"/> and
/// <c>operator ==</c> both delegate to it. Three guards every aggregate
/// inherits — an entity whose <see cref="Id"/> is uninitialized is equal only
/// to itself by reference, runtime-type mismatches are never equal even when
/// the underlying ID matches, and the hash code partitions uninitialized
/// instances apart so a <c>HashSet</c> in a collection navigation, a
/// <c>Distinct()</c> or a <c>Contains</c> behaves correctly.
/// </para>
/// <para>
/// EF Core's change tracker is <em>not</em> among the reasons: its identity map
/// keys on the primary-key value through a <c>ValueComparer</c> and tracks
/// instances by reference, so it never calls these members.
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
public abstract class Entity<TId> : IHasId<TId>, IHasDomainEvents, IEquatable<Entity<TId>>
    where TId : struct, IStronglyTypedId<Guid>, IEquatable<TId>
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

    /// <summary>
    /// Identity equality, typed. This is the single implementation;
    /// <see cref="Equals(object?)"/> and <c>operator ==</c> both delegate here so
    /// the three guards below cannot be bypassed by picking a different overload.
    /// </summary>
    public bool Equals(Entity<TId>? other)
    {
        if (other is null)
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

        // Transient guard: an aggregate constructed without an id carries an
        // uninitialized TId until one is minted. Two of those must never collapse
        // into each other in a HashSet-backed navigation or a Distinct().
        if (!Id.IsInitialized() || !other.Id.IsInitialized())
        {
            return false;
        }

        return Id.Equals(other.Id);
    }

    /// <summary>
    /// Sealed on purpose. A derived aggregate that overrode this — or
    /// <see cref="GetHashCode"/> — could redefine <c>operator ==</c> too and
    /// reintroduce the <c>a == b</c> / <c>a.Equals(b)</c> split this type exists to
    /// prevent. With both sealed, a derived <c>operator ==</c> can no longer silence
    /// CS0660 / CS0661, so it fails the build instead.
    /// </summary>
    public sealed override bool Equals(object? obj) => Equals(obj as Entity<TId>);

    /// <summary>
    /// Identity equality. Delegates to <see cref="Equals(Entity{TId}?)"/>, so a
    /// transient aggregate is not equal to another transient aggregate even when
    /// both sides are written as <c>==</c>.
    /// </summary>
    public static bool operator ==(Entity<TId>? left, Entity<TId>? right) =>
        left is null ? right is null : left.Equals(right);

    /// <summary>The negation of <see cref="op_Equality"/>.</summary>
    public static bool operator !=(Entity<TId>? left, Entity<TId>? right) => !(left == right);

    public sealed override int GetHashCode() =>
        Id.IsInitialized()
            ? HashCode.Combine(GetType(), Id)
            : base.GetHashCode();
}
