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
    /// EF Core's expression translator. <c>TenantQueryFilters</c> wires them
    /// accordingly, as of Packet 7.
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
        EnsureCreated();
        Touch(at, by);
    }

    /// <summary>
    /// Marks the entity as soft-deleted, and stamps the update columns with the
    /// same instant so a job scanning <c>UpdatedAt</c> sees the delete without
    /// keying off <c>DeletedAt</c> separately. The audit row still classifies the
    /// action as a delete via its own operation type.
    /// </summary>
    /// <remarks>
    /// Throws when the entity is already soft-deleted, for the reason
    /// <see cref="MarkCreated"/> throws on a second call: the second delete would
    /// overwrite who deleted the row and when, and audit-trail integrity rules out
    /// silent overwrites. A handler that has loaded an already-deleted aggregate
    /// should refuse with <c>Result.Fail(business_rule_violation, …)</c> before
    /// reaching this method — arriving here means the check was not made.
    /// </remarks>
    public void SoftDelete(DateTimeOffset at, UserId by)
    {
        EnsureValidAuditInput(at, by);

        EnsureCreated();

        if (DeletedAt is not null)
        {
            throw new InvalidOperationException(
                "This aggregate is already soft-deleted; the deleted-at / deleted-by columns are immutable after the first delete.");
        }

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

        // `checked` so the wrap is an exception rather than a sign flip. 2^63
        // updates to one row is not a reachable bound, but an unchecked ++ that
        // silently produces a negative token would make every subsequent ETag
        // comparison meaningless, and the cost of ruling it out is one keyword.
        checked
        {
            Version++;
        }
    }

    /// <summary>
    /// Refuses an update on an aggregate that was never created.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured before this guard existed: <c>MarkUpdated</c> on a fresh
    /// aggregate succeeded and left <see cref="CreatedAt"/> at
    /// <c>0001-01-01T00:00:00Z</c> — the exact programmer-error sentinel
    /// <see cref="EnsureValidAuditInput"/> refuses as an *argument* and which
    /// its own comment says must never be persisted. Worse, a later
    /// <c>MarkCreated</c> then succeeded, because its guard reads
    /// <c>CreatedAt != default</c> and the sentinel still satisfied it — leaving
    /// a row whose <c>updated_at</c> precedes its <c>created_at</c>.
    /// </para>
    /// <para>
    /// The ordering is not something callers can be relied on to keep: it is one
    /// missing <c>Create()</c> factory call away, and nothing downstream would
    /// notice, because both columns are populated and neither is null.
    /// </para>
    /// </remarks>
    private void EnsureCreated()
    {
        if (CreatedAt == default)
        {
            throw new InvalidOperationException(
                "MarkCreated has not been called on this aggregate; an update cannot precede creation. Construct it through its aggregate factory.");
        }
    }

    private static void EnsureValidAuditInput(DateTimeOffset at, UserId by) =>
        AuditInput.EnsureValid(at, by);
}

/// <summary>
/// The pair every audit stamp is made of: a meaningful instant and a real actor.
/// </summary>
/// <remarks>
/// Lifted out of <see cref="AuditableEntity{TId}"/> because the rule is the pair,
/// not the base class. An entity that carries <c>updated_at</c> / <c>updated_by</c>
/// without deriving — a composite-keyed row with no surrogate id, which cannot
/// derive — needs the same guard, and the one that skipped it accepted
/// <c>default(DateTimeOffset)</c> and an uninitialized actor and threw
/// <c>ValueObjectValidationException</c> from inside the Vogen EF converter at
/// persist time instead: three layers from the call, and naming neither the
/// property nor the aggregate.
/// </remarks>
public static class AuditInput
{
    /// <summary>
    /// Refuses the two programmer-error sentinels: the default timestamp
    /// (0001-01-01) and the default / empty <see cref="UserId"/>.
    /// </summary>
    public static void EnsureValid(DateTimeOffset at, UserId by)
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
