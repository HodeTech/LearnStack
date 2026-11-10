using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.SharedKernel.Persistence;

public interface IConsentSubjectUnitOfWork
{
    Task<IUnitOfWorkScope> BeginConsentSubjectTransactionAsync(ConsentSubjectScopeWitness witness,
        ITenantContext context, CancellationToken cancellationToken = default);
    bool IsConsentSubjectOwner(ConsentSubjectScopeWitness witness);
}
