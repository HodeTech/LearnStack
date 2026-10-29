using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Pagination;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;

namespace LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;

public static class OwnTenantNavigationContract
{
    public const string Operation = "identity.membership.list_own_contexts";
}

/// <summary>Tenancy labels for current-tenant own navigation; callers supply no scope or coverage facts.</summary>
public interface IOwnTenantNavigationReader
{
    Task<Result<Page<OwnTenantNavigationItem>>> ReadAsync(string? cursor, int limit, CancellationToken cancellationToken = default);
}

public sealed record OwnTenantNavigationItem(TenantId TenantId, OrganizationId? OrganizationId,
    string TenantName, string? OrganizationName, string EntryHost);

/// <summary>Injected Identity-owned current-fact provider, not a caller-supplied permission or navigation selector.</summary>
public interface IOwnTenantNavigationCoverage
{
    Task<Result<OwnTenantNavigationAccess>> ReadCurrentAsync(CancellationToken cancellationToken = default);
}

/// <summary>One provider result. Tenancy validates its live invocation and obtains it only through the registered port.</summary>
public sealed record OwnTenantNavigationAccess(OperationInvocation Invocation, bool HasTenantMembership)
{
    public override string ToString() => nameof(OwnTenantNavigationAccess);
}
