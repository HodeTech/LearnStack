using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Time;

namespace LearnStack.SharedKernel.Domain;

/// <summary>
/// Mutable aggregate base. Carries the audit columns every tenant-owned
/// table mirrors (<c>CreatedAt</c> / <c>CreatedBy</c> / <c>UpdatedAt</c> /
/// <c>UpdatedBy</c> / <c>DeletedAt</c> / <c>DeletedBy</c> / <c>Version</c>)
/// and implements <see cref="ISoftDelete"/> + <see cref="IOptimisticConcurrency"/>
/// so the EF global-query-filter and concurrency-token wiring picks them up
/// uniformly. Audit columns are populated by <see cref="MarkCreated"/> /
/// <see cref="MarkUpdated"/> / <see cref="SoftDelete"/>, which command
/// handlers call via the <see cref="IClock"/> they already inject.
/// </summary>
public abstract class AuditableEntity<TId>
    : Entity<TId>, ISoftDelete, IOptimisticConcurrency
    where TId : struct, IStronglyTypedId<Guid>
{
    protected AuditableEntity(TId id)
        : base(id)
    {
    }

    // EF Core / ORM materialization ctor.
    protected AuditableEntity()
    {
    }

    public DateTimeOffset CreatedAt { get; protected set; }

    public UserId CreatedBy { get; protected set; }

    public DateTimeOffset? UpdatedAt { get; protected set; }

    public UserId? UpdatedBy { get; protected set; }

    public DateTimeOffset? DeletedAt { get; protected set; }

    public UserId? DeletedBy { get; protected set; }

    public uint Version { get; protected set; }

    // ISoftDelete declares DeletedBy as Guid? for module-agnostic readers
    // (EF global query filters, RLS policy plumbing). Surface the raw Guid
    // projection through explicit interface implementation so callers
    // holding the concrete type read the strongly-typed UserId? while
    // cross-cutting infrastructure sees a Guid? at the marker layer.
    Guid? ISoftDelete.DeletedBy => DeletedBy?.Value;

    /// <summary>
    /// Convenience projection of <see cref="DeletedAt"/>. EF global query
    /// filters typically gate on this property.
    /// </summary>
    public bool IsDeleted => DeletedAt.HasValue;

    /// <summary>
    /// Stamps <see cref="CreatedAt"/> / <see cref="CreatedBy"/> on first
    /// persist. Throws when the aggregate already has a non-default
    /// <see cref="CreatedAt"/> — audit-trail integrity rules out silent
    /// overwrites.
    /// </summary>
    public void MarkCreated(DateTimeOffset at, UserId by)
    {
        if (CreatedAt != default)
        {
            throw new InvalidOperationException(
                "MarkCreated has already been called on this aggregate; the created-at / created-by columns are immutable after first stamp.");
        }

        CreatedAt = at;
        CreatedBy = by;
    }

    /// <summary>
    /// Stamps <see cref="UpdatedAt"/> / <see cref="UpdatedBy"/>. Called by
    /// aggregate methods that mutate state; the audit pipeline reads these
    /// values when writing the audit entry.
    /// </summary>
    public void MarkUpdated(DateTimeOffset at, UserId by)
    {
        UpdatedAt = at;
        UpdatedBy = by;
    }

    /// <summary>
    /// Marks the entity as soft-deleted. Also bumps
    /// <see cref="UpdatedAt"/> / <see cref="UpdatedBy"/> so the
    /// "last touched at" timestamp is monotonic — replication / sync /
    /// reporting jobs that scan on <c>UpdatedAt</c> see soft-deletes
    /// without keying off <c>DeletedAt</c> separately. The audit row still
    /// classifies the action as a delete via its own operation type.
    /// </summary>
    public void SoftDelete(DateTimeOffset at, UserId by)
    {
        DeletedAt = at;
        DeletedBy = by;
        UpdatedAt = at;
        UpdatedBy = by;
    }
}
