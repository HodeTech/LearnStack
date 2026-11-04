using LearnStack.SharedKernel.Permissions;

namespace LearnStack.Modules.Tenancy.Application.Contracts.Seeding;

/// <summary>Closed topology completion, on the already prepared ordinary owner in narrow scope.</summary>
public interface ISeedTenantCompletionReader
{
    Task<bool> VerifyAsync(SeedCompletionReadWitness witness, CancellationToken cancellationToken = default);
}
