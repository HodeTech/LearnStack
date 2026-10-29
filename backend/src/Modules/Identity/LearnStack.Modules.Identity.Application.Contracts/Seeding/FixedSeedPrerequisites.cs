using System.Collections.Immutable;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;

namespace LearnStack.Modules.Identity.Application.Contracts.Seeding;

public enum FixedSeedPrerequisiteStage { Tenancy = 1, Role = 2, Membership = 3, Profile = 4 }

/// <summary>Immutable metadata pins captured by the closed Seeder source, never a submitted manifest.</summary>
public sealed record FixedSeedRoleDeclaration(Guid Id, BuiltInRoleKey BuiltInKey, PermissionScope Scope,
    ImmutableArray<PermissionDefinition> Grants);

public sealed record FixedSeedMembershipDeclaration(Guid Id, UserId UserId, OrganizationId? OrganizationId,
    Guid RoleId, bool IsFirstAdmin)
{
    public override string ToString() => nameof(FixedSeedMembershipDeclaration);
}

/// <summary>The target Tenant's original ordered v1 prefix; contains no profile or provider values.</summary>
public sealed record FixedSeedPrerequisiteDeclaration(TenantId TenantId, int ManifestVersion, string ManifestFingerprint,
    FixedSeedPrerequisiteStage Stage, int TargetIndex, ImmutableArray<FixedSeedRoleDeclaration> Roles,
    ImmutableArray<FixedSeedMembershipDeclaration> Memberships)
{
    public override string ToString() => nameof(FixedSeedPrerequisiteDeclaration);
}

/// <summary>Used only by the eight fixed writers after their complete plan and target preparation.</summary>
public interface IFixedSeedPrerequisiteVerifier
{
    Task<bool> VerifyAsync(object request, IdentityMutationLockPlan plan, CancellationToken cancellationToken = default);
}

public sealed class DenyAllFixedSeedPrerequisiteVerifier : IFixedSeedPrerequisiteVerifier
{
    public Task<bool> VerifyAsync(object request, IdentityMutationLockPlan plan, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }
}

/// <summary>Closed metadata-only projection; the common owner alone supplies its temporary read scope.</summary>
public interface IFixedSeedIdentityPrerequisiteReader
{
    Task<bool> VerifyAsync(SeedPrerequisiteReadWitness witness, CancellationToken cancellationToken = default);
}
