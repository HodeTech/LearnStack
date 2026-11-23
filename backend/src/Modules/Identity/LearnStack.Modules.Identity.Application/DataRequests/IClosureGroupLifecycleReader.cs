using LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Modules.Identity.Application.DataRequests;

/// <summary>
/// What a participant group's tenant and organization allow, as the closure's operator acts judge it: the three facts of
/// Tenancy's lifecycle that decide whether the group is served.
/// </summary>
/// <param name="TenantAllowsOrdinaryWork">The tenant exists and is Trial or Active.</param>
/// <param name="OrganizationBelongsToTenant">The group's organization belongs to the tenant; false for a tenant-wide group.</param>
/// <param name="OrganizationAllowsOrdinaryWork">The group's organization is Active; false for a tenant-wide group.</param>
public readonly record struct ClosureGroupLifecycle(
    bool TenantAllowsOrdinaryWork, bool OrganizationBelongsToTenant, bool OrganizationAllowsOrdinaryWork);

/// <summary>
/// The closure's read of each participant group's lifecycle on an entered platform scope (ADR-0107 § 7: through Tenancy's
/// own port, so no cross-module join exists). It is this module's own port over the Tenancy contract, because an
/// Infrastructure assembly names no other module's contract
/// (<c>ModuleInfrastructure_References_Only_Its_Own_Layers_And_Core_Infrastructure</c>); the Application layer may.
/// </summary>
public interface IClosureGroupLifecycleReader
{
    /// <summary>
    /// The most groups one read answers: Tenancy's platform read bound. A closure reads at most
    /// <see cref="LearnStack.SharedKernel.DataProtection.ClosureLimits.MaximumParticipantGroups"/> groups, which is within it.
    /// </summary>
    public const int MaximumGroups = IIdentityScopeLifecycleReader.MaximumPlatformScopes;

    /// <summary>
    /// The lifecycle of each of <paramref name="groups"/>, in their order, in one statement on the entered scope's own
    /// transaction. It announces nothing and needs no announcement.
    /// </summary>
    /// <exception cref="ArgumentException">The list is empty or longer than <see cref="MaximumGroups"/>.</exception>
    Task<IReadOnlyList<ClosureGroupLifecycle>> ReadAsync(
        IPlatformAdminScopeHandle handle, IReadOnlyList<(TenantId TenantId, OrganizationId? OrganizationId)> groups,
        CancellationToken cancellationToken = default);
}

/// <summary>The default reader: Tenancy's platform-scope lifecycle read, narrowed to the facts the closure judges.</summary>
public sealed class ClosureGroupLifecycleReader(IIdentityScopeLifecycleReader lifecycle) : IClosureGroupLifecycleReader
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<ClosureGroupLifecycle>> ReadAsync(
        IPlatformAdminScopeHandle handle, IReadOnlyList<(TenantId TenantId, OrganizationId? OrganizationId)> groups,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(groups);
        var facts = await lifecycle.ReadOnPlatformScopeAsync(
            handle, [.. groups.Select(group => new IdentityLifecycleScope(group.TenantId, group.OrganizationId))],
            cancellationToken);
        return [.. facts.Select(fact => new ClosureGroupLifecycle(
            fact.TenantAllowsOrdinaryWork, fact.OrganizationBelongsToTenant, fact.OrganizationAllowsOrdinaryWork))];
    }
}
