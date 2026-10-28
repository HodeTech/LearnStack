using System.Collections.Immutable;
using LearnStack.Modules.Identity.Application.Contracts.Authorization;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;

namespace LearnStack.Modules.Identity.Application.Abstractions;

/// <summary>Opens only a candidate-bound isolated authorization child, never a general query source.</summary>
public interface IIdentityAuthorizationSessionFactory
{
    Task<IIdentityAuthorizationSession?> OpenAsync(AuthorizationCandidate candidate, CancellationToken cancellationToken = default);
}

/// <summary>One confined physical child shared by Identity and the separate Tenancy lifecycle statement.</summary>
public interface IIdentityAuthorizationSession : IAsyncDisposable
{
    IUnitOfWork Unit { get; }
    CancellationToken DeadlineToken { get; }
    Task<ImmutableArray<AuthorizationMembershipFacts>> ReadMembershipsAsync(CancellationToken cancellationToken = default);
    Task CompleteAsync();
    void MarkCleanupFailed();
}
