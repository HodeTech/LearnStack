using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Modules.Identity.Application.Seeding;

internal static class SeedFoundationGate
{
    internal static bool ValidRoot(Guid value) => value != Guid.Empty && value.Version == 7;
    internal static bool ValidUser(UserId value) => value.IsInitialized() && value.Value != Guid.Empty && value != UserId.SystemActor;
    internal static PermissionScope Scope(ITenantContext context) =>
        context.OrganizationId is null ? PermissionScope.Tenant : PermissionScope.Organization;

    internal static bool HasActor(ITenantContext context) => context.IsResolved && context.UserId is { } actor
        && actor.IsInitialized() && actor.Value != Guid.Empty;

    internal static async Task<bool> AllowsAsync(ITenantContext context, ISeedIdentityFoundationAdmission admission,
        IIdentityScopeLifecycleReader lifecycle, SeedIdentityFoundationAct act, string expectedFingerprint,
        CancellationToken cancellationToken)
    {
        if (act.ManifestVersion != IdentityFoundationManifest.Version1.Version
            || !string.Equals(act.ExpectedFingerprint, expectedFingerprint, StringComparison.Ordinal)
            || !await admission.AllowsFoundationAsync(act, cancellationToken)) return false;
        // Closed seed admission precedes any lifecycle SQL or lock acquisition.
        var facts = await lifecycle.ReadAsync(context.TenantId, context.OrganizationId, cancellationToken);
        return facts.TenantExistsAndNotDeleted && facts.TenantAllowsOrdinaryWork
            && (context.OrganizationId is null || facts.OrganizationBelongsToTenant && facts.OrganizationAllowsOrdinaryWork);
    }

    internal static SeedIdentityFoundationAct Act(ITenantContext context, SeedIdentityFoundationOperation operation,
        Guid rootId, UserId? targetUserId, Guid? roleId, BuiltInRoleKey? builtInKey, PermissionScope scope,
        int version, string fingerprint) => new(operation, context.TenantId, context.OrganizationId,
            context.UserId!.Value, rootId, targetUserId, roleId, builtInKey, scope, version, fingerprint);
}
