using LearnStack.SharedKernel.Identity;

namespace LearnStack.SharedKernel.Persistence;

/// <summary>The common ordinary-source owner accepts only a single-use captured own-Membership tuple.</summary>
public interface IPreContextSelfDiscoveryUnitOfWork : IUnitOfWork
{
    Task<IUnitOfWorkScope> BeginSelfDiscoveryTransactionAsync(OwnMembershipScopeWitness witness,
        CancellationToken cancellationToken = default);
    bool IsSelfDiscoveryOwner(OwnMembershipScopeWitness witness);
}
