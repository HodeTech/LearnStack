using LearnStack.Modules.Customization.Application.Contracts.Seeding;
using LearnStack.Modules.Education.Application.Contracts.Seeding;
using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Tenancy.Application.Contracts.Seeding;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace LearnStack.Tools.Seeder;

/// <summary>Runs narrow owning reads before entering the separately typed completion operation.</summary>
internal sealed class FixedSeedCompletionVerifier(FixedSeedCompletionSource source, IUnitOfWork unit,
    IServiceProvider services) : IFixedSeedOriginCompletionVerifier
{
    public async Task<bool> VerifyAsync(AdoptSeedAdministrationOriginCommand request,
        SeedAdministrationOriginPreparedTarget prepared, IdentityMutationLockPlan plan,
        CancellationToken cancellationToken = default)
    {
        var witness = source.Capture(request, prepared, plan);
        if (witness is null || unit is not IFixedSeedPrerequisiteUnitOfWork owner) return false;
        witness.RequireCurrent();
        var tenancy = await services.GetRequiredService<ISeedTenantCompletionReader>().VerifyAsync(witness, cancellationToken);
        witness.RequireCurrent();
        if (!tenancy) return false;
        var customization = await services.GetRequiredService<ISeedCustomizationCompletionReader>().VerifyAsync(witness, cancellationToken);
        witness.RequireCurrent();
        if (!customization) return false;
        var complete = await owner.VerifySeedCompletionAsync(witness, cancellationToken);
        witness.RequireCurrent();
        return complete;
    }
}

/// <summary>One closed operation, selected by constructor registration while the actual owner remains narrow.</summary>
internal sealed class FixedSeedCompletionReadOperation(ISeedEducationCompletionReader education,
    ISeedIdentityCompletionReader identity) : ISeedCompletionReadOperation
{
    public async Task<bool> ExecuteAsync(SeedCompletionReadWitness witness, CancellationToken cancellationToken)
    {
        witness.RequireCurrent();
        var educationAccepted = await education.VerifyAsync(witness, cancellationToken);
        witness.RequireCurrent();
        if (!educationAccepted) return false;
        var identityAccepted = await identity.VerifyAsync(witness, cancellationToken);
        witness.RequireCurrent();
        return identityAccepted;
    }
}

internal sealed class FixedSeedCompletionReadOperationResolver(IServiceProvider services) : ISeedCompletionReadOperationResolver
{
    public ISeedCompletionReadOperation Resolve() => services.GetRequiredService<FixedSeedCompletionReadOperation>();
}
