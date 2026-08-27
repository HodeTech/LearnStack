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
    where TId : struct, IStronglyTypedId<Guid>, IEquatable<TId>
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

    /// <summary>
    /// The optimistic-concurrency token, mapped to <c>row_version bigint</c>
    /// (<see href="../../../../docs/decisions/0039-optimistic-concurrency-token.md">ADR-0039</see>).
    /// </summary>
    /// <remarks>
    /// Advanced by <see cref="Touch"/>, which every update path routes through,
    /// so an audited mutation is a versioned mutation. It starts at <c>0</c> and
    /// the column's <c>DEFAULT 0</c> agrees, so an insert needs no special case.
    /// </remarks>
    public long Version { get; protected set; }

    /// <summary>
    /// Convenience projection of <see cref="DeletedAt"/> for in-process
    /// callers (aggregate methods, application services, mappers). EF
    /// global query filters should gate on <see cref="DeletedAt"/> directly
    /// (<c>e =&gt; e.DeletedAt == null</c>) — <see cref="IsDeleted"/> is a
    /// computed CLR property and is NOT guaranteed to translate to SQL by
    /// EF Core's expression translator. Packet 7 wires the filters
    /// accordingly.
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
        EnsureValidAuditInput(at, by);

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
        EnsureValidAuditInput(at, by);
        Touch(at, by);
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
        EnsureValidAuditInput(at, by);

        DeletedAt = at;
        DeletedBy = by;
        Touch(at, by);
    }

    /// <summary>
    /// The one update primitive: stamps <see cref="UpdatedAt"/> /
    /// <see cref="UpdatedBy"/> and advances <see cref="Version"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every path that stamps an update goes through here</b>, and that is the
    /// whole point rather than tidiness. <see cref="SoftDelete"/> used to assign
    /// the two fields itself; with the version counter living in
    /// <see cref="MarkUpdated"/> alone, a soft delete would have left the token
    /// where it was, and a client holding the pre-delete ETag would still satisfy
    /// <c>If-Match</c> on the row it had just deleted. The guarantee ADR-0039
    /// wants — an audited mutation is a versioned mutation — is a property of
    /// this method existing, not of the two callers remembering.
    /// </para>
    /// </remarks>
    private void Touch(DateTimeOffset at, UserId by)
    {
        UpdatedAt = at;
        UpdatedBy = by;
        Version++;
    }

    // Audit metadata must always be meaningful: the default timestamp
    // (0001-01-01) and the default UserId (Guid.Empty) are programmer-error
    // sentinels rather than legitimate audit values. Fail loud at the call
    // site rather than persisting them.
    private static void EnsureValidAuditInput(DateTimeOffset at, UserId by)
    {
        if (at == default)
        {
            throw new ArgumentException(
                "Audit timestamp must be a meaningful instant, not default(DateTimeOffset). Pass the value from IClock.UtcNow.",
                nameof(at));
        }

        // IsInitialized() first: reading Value on an unset Vogen id throws
        // ValueObjectValidationException from inside the id type, which is neither
        // this guard's contract nor a message a caller can act on.
        if (!by.IsInitialized() || by.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "Audit actor must be a real UserId, not default(UserId). Pass the resolved ITenantContext.UserId.",
                nameof(by));
        }
    }
}
