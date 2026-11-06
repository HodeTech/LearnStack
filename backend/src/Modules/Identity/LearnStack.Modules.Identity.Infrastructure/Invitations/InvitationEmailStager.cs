using LearnStack.SharedKernel.Errors;
using Npgsql;
using System.Net;
using System.Security.Cryptography;
using LearnStack.SharedKernel.Identity;
using LearnStack.Application.Messaging;
using LearnStack.Infrastructure.Email;
using LearnStack.Modules.Identity.Application.Contracts.Invitations;
using LearnStack.Modules.Identity.Application.Invitations;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Email;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Localization;

namespace LearnStack.Modules.Identity.Infrastructure.Invitations;

public sealed class InvitationEmailStager(IUnitOfWork unit, ProducerExecution producer, IBackgroundJobScheduler scheduler,
    InvitationEmailEnvelopeWriter envelopes, InvitationEmailDatabasePrivacy privacy, IGuidFactory ids) : IInvitationEmailStager
{
    public async Task<InvitationDelivery> StageAsync(InvitationId invitationId, InvitationGenerationId generationId,
        string recipient, string entryOrigin, string locale, string tenantLabel, int lifetimeDays,
        CancellationToken cancellationToken = default)
    {
        var origin = producer.CaptureJobOrigin(unit);
        if (origin.OperationType.Assembly != typeof(InvitationEmailParameters).Assembly
            || origin.OperationType.FullName is not ("LearnStack.Modules.Identity.Application.Contracts.Invitations.CreateInvitationCommand"
                or "LearnStack.Modules.Identity.Application.Contracts.Invitations.ReissueInvitationCommand")
            || !InvitationMailbox.TryNormalize(recipient, out var mailbox)
            || !Uri.TryCreate(entryOrigin, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.GetLeftPart(UriPartial.Authority) != entryOrigin || uri.UserInfo.Length != 0
            || locale is not { Length: > 0 and <= LocaleTag.MaxLength } || !LocaleTag.IsWellFormed(locale)
            || !InvitationDisplayLabel.TryNormalize(tenantLabel, out var label)
            || lifetimeDays is < 1 or > 30)
            throw new InvalidOperationException("Invitation email staging refused.");
        try { await privacy.RequireAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception failure) when (failure is not (OutOfMemoryException or StackOverflowException))
        {
            unit.MarkRollbackOnly();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InfrastructureException("Invitation email staging unavailable.");
        }
        var spool = ids.NewUuidV7();
        var secretBytes = RandomNumberGenerator.GetBytes(32);
        try
        {
            var secret = Convert.ToBase64String(secretBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var digestBytes = InvitationVerifier.Compute(invitationId.Value, secretBytes);
            string digest;
            try { digest = Convert.ToHexStringLower(digestBytes); }
            finally { CryptographicOperations.ZeroMemory(digestBytes); }
            var code = invitationId.Value.ToString("D") + "." + secret;
            var cleanLink = entryOrigin + "/invitations";
            var link = cleanLink + "#" + code;
            // Mail copy falls back independently; delivery retains the tenant's enabled content tag.
            var turkish = locale.Equals("tr", StringComparison.OrdinalIgnoreCase)
                || locale.StartsWith("tr-", StringComparison.OrdinalIgnoreCase);
            var title = turkish ? "LearnStack daveti" : "LearnStack invitation";
            var lead = turkish ? "Daveti inceleyin:" : "Review your invitation:";
            var text = label + "\n" + lead + "\n" + link + "\n\n" + cleanLink + "\n" + code;
            var html = "<p>" + WebUtility.HtmlEncode(label) + "</p><p>" + lead + "</p><p><a href=\""
                + WebUtility.HtmlEncode(link) + "\">" + title + "</a></p><p>" + WebUtility.HtmlEncode(cleanLink)
                + "</p><p>" + WebUtility.HtmlEncode(code) + "</p>";
            var key = origin.Announcement.OrganizationId is null ? "identity.invitation_email_tenant.v1" : "identity.invitation_email_organization.v1";
            var job = await scheduler.EnqueueAsync(new BackgroundJobKey<InvitationEmailParameters>(key),
                new(invitationId.Value, generationId.Value, spool), cancellationToken).ConfigureAwait(false);
            producer.RequireJobOrigin(origin, unit);
            await envelopes.StageAsync(new(spool, origin.Announcement.TenantId.Value, origin.Announcement.OrganizationId?.Value,
                invitationId.Value, generationId.Value, job.Value), new EmailSubmission(mailbox, title, text, html, spool.ToString("N") + "@learnstack.invalid"), cancellationToken).ConfigureAwait(false);
            return new(generationId, job, digest, entryOrigin, locale, lifetimeDays);
        }
        catch (Exception failure) when (failure is NpgsqlException or InvalidOperationException or TimeoutException)
        { unit.MarkRollbackOnly(); cancellationToken.ThrowIfCancellationRequested(); throw new InfrastructureException("Invitation email staging unavailable."); }
        catch { unit.MarkRollbackOnly(); throw; }
        finally { CryptographicOperations.ZeroMemory(secretBytes); }
    }
}
