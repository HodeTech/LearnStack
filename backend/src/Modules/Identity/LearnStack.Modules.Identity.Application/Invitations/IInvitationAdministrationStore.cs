using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;

namespace LearnStack.Modules.Identity.Application.Invitations;

public interface IInvitationAdministrationStore : IAggregateWriteStore<Invitation, InvitationId>
{
    Task<Invitation?> ReadAsync(InvitationId id, CancellationToken cancellationToken);
    Task<Invitation?> FindLockedAsync(InvitationId id, CancellationToken cancellationToken);
    Task<IReadOnlyList<Invitation>> ListAsync(InvitationId? after, int take, CancellationToken cancellationToken);
    Task<IReadOnlyList<Role>> ReadRolesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// At most <paramref name="take"/> pending invitations <paramref name="inviter"/> issued in the ambient group, oldest
    /// identifier first, locked and tracked: the offers a tenant erasure revokes (ADR-0107 § 5). A fresh read, as
    /// <see cref="FindLockedAsync"/> is.
    /// </summary>
    Task<IReadOnlyList<Invitation>> FindPendingIssuedLockedAsync(UserId inviter, int take, CancellationToken cancellationToken);
}
