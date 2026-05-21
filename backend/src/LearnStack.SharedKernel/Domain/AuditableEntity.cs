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
/// <remarks>
/// The actor columns (<c>CreatedBy</c> / <c>UpdatedBy</c> / <c>DeletedBy</c>)
/// store raw <see cref="Guid"/> rather than a strongly-typed <c>UserId</c>:
/// <see cref="LearnStack.SharedKernel"/> cannot depend on the Identity
/// module (which lands in Phase 02b), and audit metadata is not an
/// aggregate-root reference in the Standards 02 § Strongly-Typed
/// Identifiers sense.
/// </remarks>
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

    public Guid CreatedBy { get; protected set; }

    public DateTimeOffset? UpdatedAt { get; protected set; }

    public Guid? UpdatedBy { get; protected set; }

    public DateTimeOffset? DeletedAt { get; protected set; }

    public Guid? DeletedBy { get; protected set; }

    public uint Version { get; protected set; }

    /// <summary>
    /// Convenience projection of <see cref="DeletedAt"/>. Surfaced at this
    /// level (rather than relying on <see cref="ISoftDelete"/>'s default
    /// interface implementation) so callers can read the property through
    /// the concrete aggregate type without casting.
    /// </summary>
    public bool IsDeleted => DeletedAt.HasValue;

    /// <summary>
    /// Stamps <see cref="CreatedAt"/> / <see cref="CreatedBy"/> on first
    /// persist. Idempotent: callers may invoke it once during aggregate
    /// construction; later calls overwrite the values, which is the
    /// caller's bug rather than a silent no-op.
    /// </summary>
    public void MarkCreated(DateTimeOffset at, Guid by)
    {
        CreatedAt = at;
        CreatedBy = by;
    }

    /// <summary>
    /// Stamps <see cref="UpdatedAt"/> / <see cref="UpdatedBy"/>. Called by
    /// aggregate methods that mutate state; the audit pipeline reads these
    /// values when writing the audit entry.
    /// </summary>
    public void MarkUpdated(DateTimeOffset at, Guid by)
    {
        UpdatedAt = at;
        UpdatedBy = by;
    }

    /// <summary>
    /// Marks the entity as soft-deleted. EF's global query filter excludes
    /// soft-deleted rows by default; the platform-admin scope can opt back
    /// in explicitly.
    /// </summary>
    public void SoftDelete(DateTimeOffset at, Guid by)
    {
        DeletedAt = at;
        DeletedBy = by;
    }
}
