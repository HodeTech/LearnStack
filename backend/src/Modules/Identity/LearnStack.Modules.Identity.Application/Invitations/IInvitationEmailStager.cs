using LearnStack.Modules.Identity.Domain;

namespace LearnStack.Modules.Identity.Application.Invitations;

public interface IInvitationEmailStager
{
    Task<InvitationDelivery> StageAsync(InvitationId invitationId, InvitationGenerationId generationId,
        string recipient, string entryOrigin, string locale, string tenantLabel, int lifetimeDays,
        CancellationToken cancellationToken = default);
}
