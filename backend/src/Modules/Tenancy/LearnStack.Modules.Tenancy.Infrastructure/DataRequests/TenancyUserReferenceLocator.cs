using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.DataProtection;

namespace LearnStack.Modules.Tenancy.Infrastructure.DataRequests;

/// <summary>
/// Where the Tenancy module's audited aggregates hold a person or Membership reference in their audit snapshots
/// (<see href="../../../../../../docs/decisions/0108-privileged-audit-redaction.md">ADR-0108 § 7</see>).
/// </summary>
/// <remarks>
/// A tenant's feature flags are contained in its snapshot, keyed by flag, so their updater is a member path. A
/// consent's Membership is a plain <c>Guid</c> the capture already writes as the redaction sentinel; it is declared
/// anyway, so the census holds it to the same rule as every typed reference and a change to the marker cannot leave it
/// in the clear.
/// </remarks>
public sealed class TenancyUserReferenceLocator : IUserReferenceLocator
{
    public IReadOnlyCollection<UserReferenceDeclaration> Redactable { get; } =
    [
        .. UserReferenceDeclarations.Attribution(typeof(Tenant), typeof(Organization), typeof(TenantSetting),
            typeof(MembershipFieldConsent)),
        new(typeof(Tenant), UserReferencePath.Member(nameof(Tenant.FeatureFlags), nameof(TenantFeatureFlag.UpdatedBy)),
            UserReferenceKind.User),
        new(typeof(MembershipFieldConsent), UserReferencePath.Of(nameof(MembershipFieldConsent.SubjectUserId)),
            UserReferenceKind.User),
        new(typeof(MembershipFieldConsent), UserReferencePath.Of(nameof(MembershipFieldConsent.ActorUserId)),
            UserReferenceKind.User),
        new(typeof(MembershipFieldConsent), UserReferencePath.Of(nameof(MembershipFieldConsent.MembershipId)),
            UserReferenceKind.Membership),
    ];

    public IReadOnlyCollection<RetainedUserReference> Retained { get; } = [];
}
