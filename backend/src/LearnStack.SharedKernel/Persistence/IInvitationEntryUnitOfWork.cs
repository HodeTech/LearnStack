using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.SharedKernel.Persistence;

public interface IInvitationEntryUnitOfWork
{
    Task<IUnitOfWorkScope> BeginInvitationEntryTransactionAsync(InvitationEntryScopeWitness witness,
        ITenantContext context, CancellationToken cancellationToken = default);
    bool IsInvitationEntryOwner(InvitationEntryScopeWitness witness);
}
