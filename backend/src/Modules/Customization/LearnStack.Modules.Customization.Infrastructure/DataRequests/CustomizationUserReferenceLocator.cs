using LearnStack.Modules.Customization.Domain;
using LearnStack.SharedKernel.DataProtection;

namespace LearnStack.Modules.Customization.Infrastructure.DataRequests;

/// <summary>
/// Where the Customization module's audited definitions hold a person reference in their audit snapshots: their
/// creator, updater and deleter only
/// (<see href="../../../../../../docs/decisions/0108-privileged-audit-redaction.md">ADR-0108 § 7</see>).
/// </summary>
public sealed class CustomizationUserReferenceLocator : IUserReferenceLocator
{
    public IReadOnlyCollection<UserReferenceDeclaration> Redactable { get; } =
    [
        .. UserReferenceDeclarations.Attribution(typeof(TenantContentType), typeof(TenantLevelTaxonomy),
            typeof(TenantCustomFieldDef)),
    ];

    public IReadOnlyCollection<RetainedUserReference> Retained { get; } = [];
}
