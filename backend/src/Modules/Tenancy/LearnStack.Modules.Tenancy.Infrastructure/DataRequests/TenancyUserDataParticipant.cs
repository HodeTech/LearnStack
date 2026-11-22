using System.Text.Json;
using LearnStack.Infrastructure.DataProtection;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LearnStack.Modules.Tenancy.Infrastructure.DataRequests;

/// <summary>
/// The Tenancy module's part in subject data requests: the subject's membership field consent history in the
/// group, exported and retained as <c>consent_evidence</c>
/// (<see href="../../../../../docs/decisions/0107-data-request-fulfillment-and-account-closure.md">ADR-0107 § 3, § 6</see>).
/// </summary>
/// <remarks>
/// <para>
/// A consent row is the subject's when it names the subject and one of the request's frozen Memberships in the
/// group. A record holds the purpose, the definition revision, the notice locale, the state, the sequence and the
/// time, and who recorded it as <c>self</c> or <c>third_party</c>; the Membership and definition identities and the
/// notice digest are internal and stay out.
/// </para>
/// <para>
/// The table is append-only evidence of lawful basis (ADR-0107 § 6), so erasure leaves it and this participant
/// only describes it.
/// </para>
/// <para>
/// <b>Retained report.</b> The subject's own consent rows are <c>consent_evidence</c>, and the consent table is left
/// out of <c>attribution_reference</c>: <c>ck_membership_field_consents_actor</c> makes every row's actor and creator
/// its own subject and the table is append-only, so no consent row can attribute another person's consent to the
/// subject. Every other tenant-owned row of the context with a <c>UserId</c> naming the subject (a feature flag the
/// subject last changed, a setting or locale they created) is counted by the generic counter, once per row.
/// </para>
/// </remarks>
public sealed class TenancyUserDataParticipant(TenancyDbContext db, ITenantContext tenant) : IUserDataParticipant
{
    internal const string ParticipantKey = "tenancy";
    private const int Page = 200;

    private static readonly IReadOnlySet<Type> EvidenceTypes = new HashSet<Type> { typeof(MembershipFieldConsent) };

    public string Key => ParticipantKey;

    public async Task ExportAsync(UserDataSubject subject, IUserDataExportWriter writer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(writer);
        RequireGroup(subject);
        for (var offset = 0; ; offset += Page)
        {
            var page = await Consents(subject).OrderBy(consent => consent.CreatedAt).ThenBy(consent => consent.Sequence)
                .ThenBy(consent => consent.Id).Skip(offset).Take(Page).ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var consent in page)
            {
                var appended = await UserDataExportRecord.AppendAsync(writer, "field_consents",
                    json => Write(json, consent, subject), cancellationToken).ConfigureAwait(false);
                if (appended != UserDataExportAppend.Appended)
                {
                    return;
                }
            }

            if (page.Count < Page)
            {
                return;
            }
        }
    }

    public async Task<IReadOnlyCollection<RetainedReferenceCount>> DescribeRetainedAsync(UserDataSubject subject,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        RequireGroup(subject);
        return
        [
            new(RetainedReferenceKind.ConsentEvidence,
                await Consents(subject).LongCountAsync(cancellationToken).ConfigureAwait(false)),
            new(RetainedReferenceKind.AttributionReference,
                await AttributionReferenceCounter.CountAsync(db, subject, EvidenceTypes, cancellationToken).ConfigureAwait(false)),
        ];
    }

    private IQueryable<MembershipFieldConsent> Consents(UserDataSubject subject)
    {
        var person = subject.SubjectUserId;
        var organization = subject.Group.OrganizationId;
        var memberships = subject.MembershipIds.ToList();
        return db.MembershipFieldConsents.AsNoTracking().Where(consent => consent.SubjectUserId == person
            && consent.OrganizationId == organization && memberships.Contains(consent.MembershipId));
    }

    private static void Write(Utf8JsonWriter json, MembershipFieldConsent consent, UserDataSubject subject)
    {
        json.WriteStartObject();
        json.WriteString("purpose", consent.PurposeKey);
        json.WriteNumber("revision", consent.DefinitionVersion);
        json.WriteString("notice_locale", consent.NoticeLocale);
        json.WriteString("state", consent.State switch
        {
            MembershipFieldConsentState.Granted => "granted",
            MembershipFieldConsentState.Withdrawn => "withdrawn",
            _ => throw new InvalidOperationException("A stored consent state is not in the closed set."),
        });
        json.WriteNumber("sequence", consent.Sequence);
        json.WriteString("recorded_at", consent.CreatedAt);
        json.WriteString("recorded_by", consent.ActorUserId == subject.SubjectUserId ? "self" : "third_party");
        json.WriteEndObject();
    }

    private void RequireGroup(UserDataSubject subject)
    {
        if (subject.Group.TenantId != tenant.TenantId || subject.Group.OrganizationId != tenant.OrganizationId)
        {
            throw new InvalidOperationException("A participant reads only the announced group of its request.");
        }
    }
}

/// <summary>Registers the Tenancy module's participant from its own boundary (ADR-0107 § 1).</summary>
public static class TenancyDataRequestRegistration
{
    public static IServiceCollection AddTenancyUserDataParticipant(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IUserDataParticipant, TenancyUserDataParticipant>());
        return services;
    }
}
