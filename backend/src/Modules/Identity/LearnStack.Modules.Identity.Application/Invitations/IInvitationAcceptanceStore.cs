using LearnStack.Modules.Identity.Application.Contracts.Authorization;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;

namespace LearnStack.Modules.Identity.Application.Invitations;

public interface IInvitationWriteStore : IAggregateWriteStore<Invitation, InvitationId>;
public sealed record InvitationAcceptanceGraph(Invitation? Invitation, Membership? Membership);
public interface IInvitationAcceptanceReadStore
{
    Task<Invitation?> ReadOfferAsync(InvitationId id, CancellationToken cancellationToken);
    Task<Membership?> ReadMemberAsync(UserId actor, CancellationToken cancellationToken);
    Task<Membership?> ReadResultAsync(MembershipId id, UserId actor, CancellationToken cancellationToken);
    Task LockInvitationAsync(InvitationId id, CancellationToken cancellationToken);
    Task<InvitationAcceptanceGraph> ReadFinalAsync(InvitationId id, UserId actor, CancellationToken cancellationToken);
    Task<DateTimeOffset> DatabaseNowAsync(CancellationToken cancellationToken);
}
public interface IInvitationDelegationReader
{
    Task<IdentityAuthorizationResult> ReadAsync(UserId storedInviter, CancellationToken cancellationToken);
}
