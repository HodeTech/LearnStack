using LearnStack.Modules.Identity.Application.Contracts.Invitations;

namespace LearnStack.Api.Authentication;

/// <summary>Already parsed and selected HTTP input, never authorization or persistence authority.</summary>
internal sealed class InvitationEntryRequest
{
    internal InvitationEntryRequest(object request)
    {
        if (request is not (InspectInvitationQuery or AcceptInvitationCommand or GetOwnInvitationResultQuery))
            throw new ArgumentException("Invitation request refused.");
        Request = request;
    }
    internal object Request { get; }
    public override string ToString() => nameof(InvitationEntryRequest);
}
