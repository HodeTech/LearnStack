using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.Tenancy;

/// <summary>Already verified native facts and fresh port answers; never a projection of provider scope claims.</summary>
public sealed record NativeScopeResolutionAttempt
{
    public TenantId? HostTenantId { get; init; }
    public OrganizationId? HostOrganizationId { get; init; }
    public TenantId TenantId { get; init; }
    public OrganizationId? OrganizationId { get; init; }
    public UserId ActorUserId { get; init; }
    public bool? MembershipCovers { get; init; }
    public bool? OrganizationBelongsToTenant { get; init; }
    public string? CorrelationId { get; init; }

    public override string ToString() => nameof(NativeScopeResolutionAttempt);
}
