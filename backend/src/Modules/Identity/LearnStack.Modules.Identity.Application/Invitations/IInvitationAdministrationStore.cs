using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Persistence;

namespace LearnStack.Modules.Identity.Application.Invitations;

public interface IInvitationAdministrationStore : IAggregateWriteStore<Invitation, InvitationId>
{
    Task<Invitation?> ReadAsync(InvitationId id, CancellationToken cancellationToken);
    Task<Invitation?> FindLockedAsync(InvitationId id, CancellationToken cancellationToken);
    Task<IReadOnlyList<Invitation>> ListAsync(InvitationId? after, int take, CancellationToken cancellationToken);
    Task<IReadOnlyList<Role>> ReadRolesAsync(CancellationToken cancellationToken);
}
