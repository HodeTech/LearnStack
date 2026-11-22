using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.SharedKernel.Persistence;

/// <summary>
/// The retained own data-request scope's one physical owner (ADR-0106 § 9): it opens a fresh transaction in
/// the witness's mode and announces exactly the witness's group, never a joined, tenant-wide or retargeted one.
/// </summary>
public interface IOwnDataRequestUnitOfWork
{
    Task<IUnitOfWorkScope> BeginOwnDataRequestTransactionAsync(OwnDataRequestScopeWitness witness,
        ITenantContext context, CancellationToken cancellationToken = default);
    bool IsOwnDataRequestOwner(OwnDataRequestScopeWitness witness);
}
