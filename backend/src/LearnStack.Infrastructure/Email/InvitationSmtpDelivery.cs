using LearnStack.SharedKernel.Email;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Infrastructure.Email;

/// <summary>Finite durable provider facts; identifiers alone cannot enter the provider source.</summary>
public sealed record InvitationSmtpResult(BackgroundJobExternalState State, Guid? ReceiptId,
    Guid? EvidenceId, BackgroundJobQuiescenceKind? Quiescence, BackgroundJobExternalFailurePolicy? FailurePolicy);

public sealed class InvitationSmtpDelivery(InvitationSmtpProviderDataSource source, InvitationEmailCipher cipher,
    IEmailProvider email, IGuidFactory ids)
{
    public async Task<InvitationSmtpResult> ObserveAsync(BackgroundJobExternalContext context,
        Guid invitationId, Guid generationId, Guid spoolId, bool recovery, CancellationToken cancellationToken = default)
    {
        var receipt = InvitationSmtpExecutionReceipt.Current ?? throw InvitationSmtpExecutionReceipt.Refused();
        receipt.Require(invitationId, generationId, spoolId, context, recovery);
        await using var owner = await source.OpenAsync(receipt, cancellationToken).ConfigureAwait(false);
        if (owner is null) return Unknown();
        var record = await owner.ClaimOrReadAsync(ids.NewUuidV7(), ids.NewUuidV7(), ids.NewUuidV7(), cancellationToken).ConfigureAwait(false);
        if (record.State != "sending") return Report(record);
        if (recovery || record.Envelope is null) return Unknown();
        var binding = new InvitationEmailBinding(spoolId, receipt.TenantId, receipt.OrganizationId, invitationId, generationId, receipt.JobId);
        EmailSubmission message;
        try { message = cipher.Decrypt(binding, record.Envelope); }
        catch (InvalidOperationException) { return Unknown(); }
        var observation = await email.SubmitAsync(message with { MessageId = context.OperationId.ToString("N") + "@learnstack.invalid" }, cancellationToken).ConfigureAwait(false);
        if (!observation.ActualCleanup)
        {
            owner.RetainUnconfirmedSmtp(email);
            return Unknown();
        }
        if (observation.State == EmailSubmissionState.Unknown) return Unknown();
        var state = observation.State switch
        {
            EmailSubmissionState.Accepted => "accepted",
            EmailSubmissionState.TemporaryRejected => "temporary_rejected",
            EmailSubmissionState.PermanentRejected => "permanent_rejected",
            _ => throw InvitationSmtpExecutionReceipt.Refused(),
        };
        // Owner retained across actual SMTP cleanup; this independent finite SQL call records late evidence.
        return Report(await owner.CompleteAsync(state, ids.NewUuidV7(), ids.NewUuidV7()).ConfigureAwait(false));
    }
    private static InvitationSmtpResult Report(ProviderRecord record)
    {
        if (record.ReceiptId is not { } receipt || record.EvidenceId is not { } evidence) return Unknown();
        return record.State switch
        {
            "accepted" => new(BackgroundJobExternalState.Succeeded, receipt, evidence, BackgroundJobQuiescenceKind.ExecutionTerminated, null),
            "temporary_rejected" => new(BackgroundJobExternalState.Failed, receipt, evidence, BackgroundJobQuiescenceKind.ExecutionTerminated, BackgroundJobExternalFailurePolicy.TransientRepeatable),
            "permanent_rejected" => new(BackgroundJobExternalState.Failed, receipt, evidence, BackgroundJobQuiescenceKind.ExecutionTerminated, BackgroundJobExternalFailurePolicy.Permanent),
            "cancelled_before_send" => new(BackgroundJobExternalState.Cancelled, receipt, evidence, BackgroundJobQuiescenceKind.ProviderFenced, BackgroundJobExternalFailurePolicy.TransientRepeatable),
            _ => Unknown(),
        };
    }
    private static InvitationSmtpResult Unknown() => new(BackgroundJobExternalState.Unknown, null, null, null, null);
}
