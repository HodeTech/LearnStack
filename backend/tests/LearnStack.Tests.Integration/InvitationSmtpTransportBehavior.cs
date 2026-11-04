namespace LearnStack.Tests.Integration;

public enum InvitationSmtpTransportBehavior
{
    Accept,
    AcceptThenClose,
    TemporaryRecipientRejection,
    PermanentRecipientRejection,
    DropFinalReply,
    HoldFinalReply,
}
