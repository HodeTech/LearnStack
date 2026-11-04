using System.Collections.Immutable;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;

namespace LearnStack.Modules.Identity.Application.Contracts.Seeding;

/// <summary>Original required identities, captured independently without profile or provider values.</summary>
public sealed record SeedIdentityCompletionDeclaration(TenantId TenantId, int ManifestVersion, string ManifestFingerprint,
    ImmutableArray<FixedSeedRoleDeclaration> Roles, ImmutableArray<FixedSeedMembershipDeclaration> Memberships)
{
    public override string ToString() => nameof(SeedIdentityCompletionDeclaration);
}

/// <summary>Closed retained-local completion inside the existing owner's temporary read scope.</summary>
public interface ISeedIdentityCompletionReader
{
    Task<bool> VerifyAsync(SeedCompletionReadWitness witness, CancellationToken cancellationToken = default);
}
