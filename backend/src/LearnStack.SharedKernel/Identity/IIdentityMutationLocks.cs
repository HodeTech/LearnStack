using System.Collections.Immutable;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.Identity;

public enum IdentityTenantLockMode { Shared = 1, Exclusive = 2 }

/// <summary>Complete ordered contextual mutation plan; never a caller's authorization assertion.</summary>
public sealed record IdentityMutationLockPlan
{
    public IdentityMutationLockPlan(IEnumerable<UserId> userIds, TenantId tenantId, IdentityTenantLockMode tenantMode)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        if (!tenantId.IsInitialized() || tenantId.Value == Guid.Empty || tenantId == TenantId.PlatformSentinel)
            throw new ArgumentException("Identity mutation tenant is invalid.", nameof(tenantId));
        if (!Enum.IsDefined(tenantMode)) throw new ArgumentOutOfRangeException(nameof(tenantMode));
        var users = userIds.ToImmutableArray();
        if (users.Any(user => !user.IsInitialized() || user.Value == Guid.Empty || user == UserId.SystemActor))
            throw new ArgumentException("Identity mutation target is invalid.", nameof(userIds));
        UserIds = users.Distinct().OrderBy(IdentityAdvisoryLockKey.ForUser).ToImmutableArray();
        TenantId = tenantId;
        TenantMode = tenantMode;
    }

    public ImmutableArray<UserId> UserIds { get; }
    public TenantId TenantId { get; }
    public IdentityTenantLockMode TenantMode { get; }
}

/// <summary>Transaction-bound User-before-tenant locking on the current trusted tenant owner.</summary>
public interface IIdentityMutationLocks
{
    Task AcquireAsync(IdentityMutationLockPlan plan, CancellationToken cancellationToken = default);
    void RequireCurrent(IdentityMutationLockPlan plan);
}
