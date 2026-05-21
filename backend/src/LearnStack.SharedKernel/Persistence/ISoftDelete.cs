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

    Guid? DeletedBy { get; }

    /// <summary>
    /// Convenience projection of <see cref="DeletedAt"/>.
    /// </summary>
    bool IsDeleted => DeletedAt.HasValue;
}
