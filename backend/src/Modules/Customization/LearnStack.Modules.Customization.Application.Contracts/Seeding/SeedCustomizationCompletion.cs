using LearnStack.SharedKernel.Permissions;

namespace LearnStack.Modules.Customization.Application.Contracts.Seeding;

/// <summary>Closed fixed-origin completion on the already prepared ordinary narrow owner.</summary>
public interface ISeedCustomizationCompletionReader
{
    Task<bool> VerifyAsync(SeedCompletionReadWitness witness, CancellationToken cancellationToken = default);
}
