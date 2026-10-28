using LearnStack.SharedKernel.Persistence;

namespace LearnStack.SharedKernel.Audit;

/// <summary>Only the central event writer borrows the independent two-slot evidence lane.</summary>
public interface IAuthorizationEvidenceSessionFactory
{
    Task<IAuthorizationEvidenceSession?> OpenAsync(AuthorizationEvidenceOccurrence occurrence,
        CancellationToken cancellationToken = default);
}

/// <summary>One connector and shared deadline; duplicate verification replaces, never overlaps, its transaction.</summary>
public interface IAuthorizationEvidenceSession : IAsyncDisposable
{
    IUnitOfWork Unit { get; }
    CancellationToken DeadlineToken { get; }
    Task BeginVerificationAsync();
    Task CompleteAsync();
    void MarkCleanupFailed();
}
