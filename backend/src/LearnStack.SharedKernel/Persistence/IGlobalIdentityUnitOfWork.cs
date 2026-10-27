using System.Data.Common;
using LearnStack.SharedKernel.Identity;

namespace LearnStack.SharedKernel.Persistence;

/// <summary>Exact global-self authority on the common ambient connection; not tenant authority or a general SQL executor.</summary>
public interface IGlobalIdentityUnitOfWork : IUnitOfWork
{
    bool IsGlobalIdentityTransaction { get; }

    Task<IUnitOfWorkScope> BeginGlobalIdentityTransactionAsync(GlobalIdentityOwnerDescriptor descriptor,
        TransactionMode mode, CancellationToken cancellationToken = default);

    GlobalIdentityContextAnnouncement? GetGlobalIdentityAnnouncement(DbTransaction? transaction);

    Task AcquireGlobalIdentitySubjectLockAsync(GlobalIdentityOwnerDescriptor descriptor,
        CancellationToken cancellationToken = default);

    void EnrollGlobalIdentityContext(object context, GlobalIdentityOwnerDescriptor descriptor);

    bool IsGlobalIdentityContextEnrolled(object context, DbTransaction? transaction);

    /// <summary>Validates deferred guards and seals further EF/capture mutation before physical COMMIT admission.</summary>
    Task ValidateGlobalIdentityConstraintsAsync(GlobalIdentityOwnerDescriptor descriptor,
        CancellationToken cancellationToken = default);
}
