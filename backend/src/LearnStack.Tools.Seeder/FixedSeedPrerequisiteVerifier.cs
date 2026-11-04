using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Tenancy.Application.Contracts.Seeding;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace LearnStack.Tools.Seeder;

/// <summary>Joins owning contracts on this scope; narrow Tenancy reads finish before the Identity hatch.</summary>
internal sealed class FixedSeedPrerequisiteVerifier(FixedSeedPrerequisiteSource source, IUnitOfWork unit,
    IServiceProvider services) : IFixedSeedPrerequisiteVerifier
{
    public async Task<bool> VerifyAsync(object request, IdentityMutationLockPlan plan, CancellationToken cancellationToken = default)
    {
        var witness = source.Capture(request, plan);
        if (witness is null || unit is not IFixedSeedPrerequisiteUnitOfWork owner) return false;
        witness.RequireCurrent();
        // Tenancy's four handlers already freshly checked their own stage after target preparation.
        // Resolving the same scoped contract here avoids a guard/verifier construction cycle.
        if (witness.GetDeclaration<FixedSeedPrerequisiteDeclaration>().Stage != FixedSeedPrerequisiteStage.Tenancy)
        {
            var allowed = await services.GetRequiredService<ISeedTenantContinuationGuard>()
                .AllowsIdentityContinuationAsync(witness.TenantId, cancellationToken);
            witness.RequireCurrent();
            if (!allowed) return false;
        }
        var verified = await owner.VerifySeedPrerequisitesAsync(witness, cancellationToken);
        witness.RequireCurrent();
        return verified;
    }
}

/// <summary>Defers the one metadata reader until the scoped physical owner has been constructed.</summary>
internal sealed class FixedSeedPrerequisiteReadOperationResolver(IServiceProvider services) : ISeedPrerequisiteReadOperationResolver
{
    public ISeedPrerequisiteReadOperation Resolve() =>
        (ISeedPrerequisiteReadOperation)services.GetRequiredService<IFixedSeedIdentityPrerequisiteReader>();
}
