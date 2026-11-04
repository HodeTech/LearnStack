using LearnStack.SharedKernel.Permissions;

namespace LearnStack.Modules.Education.Application.Contracts.Seeding;

/// <summary>Closed fixed-origin completion inside the existing owner's temporary read scope.</summary>
public interface ISeedEducationCompletionReader
{
    Task<bool> VerifyAsync(SeedCompletionReadWitness witness, CancellationToken cancellationToken = default);
}
