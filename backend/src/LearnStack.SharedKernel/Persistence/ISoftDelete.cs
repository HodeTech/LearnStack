using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.Persistence;

/// <summary>
/// Marker for any entity that participates in soft deletion. EF Core
/// global query filters and the audit pipeline read this interface;
/// callers never set <see cref="DeletedAt"/> / <see cref="DeletedBy"/>
/// directly — use the aggregate's domain method (typically
/// <c>AuditableEntity.SoftDelete</c>).
/// </summary>
public interface ISoftDelete
{
    DateTimeOffset? DeletedAt { get; }

    /// <summary>
    /// The actor who soft-deleted the entity. Strongly-typed
    /// (<see cref="UserId"/>) per Standards 02 § Strongly-Typed Identifiers
    /// — no raw <see cref="Guid"/> on the marker surface. EF's Vogen value
    /// converter persists this as a UUID column.
    /// </summary>
    UserId? DeletedBy { get; }
}
