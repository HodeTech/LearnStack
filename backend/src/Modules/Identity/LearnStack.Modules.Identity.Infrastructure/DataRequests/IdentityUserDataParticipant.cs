using System.Text.Json;
using LearnStack.Infrastructure.DataProtection;
using LearnStack.Modules.Identity.Application.DataRequests;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace LearnStack.Modules.Identity.Infrastructure.DataRequests;

/// <summary>
/// The Identity module's part in subject data requests: the subject's Memberships with their profile, role names
/// and custom-field values, and the invitations they accepted or issued
/// (<see href="../../../../../docs/decisions/0107-data-request-fulfillment-and-account-closure.md">ADR-0107 § 3</see>,
/// <see href="../../../../../docs/decisions/0106-subject-data-requests-authority-and-status.md">ADR-0106 § 5</see>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Subject predicate.</b> Rows are selected by the subject's <see cref="UserId"/> and the request's group, on the
/// ambient tenant transaction as <c>learnstack_app</c>: the Memberships are exactly the frozen snapshot that belongs
/// to the subject in the group, the invitations those the subject accepted or issued in the group.
/// </para>
/// <para>
/// <b>Excluded.</b> No identifier is written (no <c>UserId</c>, Membership, role, definition, consent or invitation
/// identity), nor the subject key, the provider subject, a verifier, an envelope or any key. Another person appears
/// only as <c>third_party</c>, and an invitee's address on an invitation the subject issued is omitted.
/// </para>
/// <para>
/// <b>Custom fields.</b> Every stored value is exported with its revision, labels and classification, including a
/// value withheld from ordinary disclosure by a withdrawn consent; a <c>None</c> classification is no exclusion.
/// </para>
/// <para>
/// <b>Bounds.</b> <c>memberships</c> is bounded by the frozen snapshot and <c>invitations_accepted</c> by the offers a
/// person can accept; <c>invitations_issued</c> grows with administrative work, so it is written newest first under
/// <see cref="DataRequestLimits.IssuedInvitationsCollectionBytes"/> and a cap hit is declared with the records
/// included and the records in total, as the audit section's is.
/// </para>
/// </remarks>
public sealed class IdentityUserDataParticipant(IdentityDbContext db, ITenantContext tenant,
    MembershipFieldRevisionReader fields) : IUserDataParticipant
{
    internal const string ParticipantKey = "identity";
    internal const string ThirdParty = "third_party";
    internal const string Self = "self";
    internal const string IssuedCollection = "invitations_issued";
    internal const string AcceptedCollection = "invitations_accepted";
    private const int Page = 200;

    /// <summary>
    /// The request's own receipt rows, reported as <c>request_receipt</c> wherever any of their <c>UserId</c> columns
    /// names the person (the subject, the requester, whoever created, cancelled or revoked them), once per row, and
    /// therefore left out of <c>attribution_reference</c> so no row is reported twice.
    /// </summary>
    private static readonly IReadOnlySet<Type> ReceiptTypes =
        new HashSet<Type> { typeof(DataSubjectRequest), typeof(DataExportArtifact), typeof(DataExportTicket) };

    /// <summary>
    /// The <c>UserId</c> columns that are a row's subject or party, not its author: the subject's own Membership is
    /// exported, and the others are reported as <c>invitation_record</c>, <c>federation_actor_reference</c> and
    /// <c>provider_event_record</c>; every other <c>UserId</c> column of the context is attribution.
    /// </summary>
    private static readonly (Type Entity, string Property)[] PartyProperties =
    [
        (typeof(Membership), nameof(Membership.UserId)),
        (typeof(Invitation), nameof(Invitation.InviterUserId)),
        (typeof(Invitation), nameof(Invitation.AcceptedBy)),
        (typeof(FederationConnection), nameof(FederationConnection.PendingActor)),
        (typeof(FederationConnection), nameof(FederationConnection.TestActor)),
        (typeof(ProviderEventTarget), nameof(ProviderEventTarget.AffectedUserId)),
    ];

    private static readonly IReadOnlyDictionary<string, long> CollectionCaps =
        new Dictionary<string, long>(StringComparer.Ordinal) { [IssuedCollection] = DataRequestLimits.IssuedInvitationsCollectionBytes };

    public string Key => ParticipantKey;

    public IReadOnlyDictionary<string, long> ExportCollectionCapBytes => CollectionCaps;

    public async Task ExportAsync(UserDataSubject subject, IUserDataExportWriter writer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(writer);
        RequireGroup(subject);
        if (!await ExportMembershipsAsync(subject, writer, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (!await ExportInvitationsAsync(subject, writer, accepted: true, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await ExportInvitationsAsync(subject, writer, accepted: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyCollection<RetainedReferenceCount>> DescribeRetainedAsync(UserDataSubject subject,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        RequireGroup(subject);
        var person = subject.SubjectUserId;
        UserId? nullablePerson = person;
        var organization = subject.Group.OrganizationId;
        var invitations = await db.Invitations.AsNoTracking()
            .LongCountAsync(invitation => invitation.OrganizationId == organization
                && (invitation.AcceptedBy == nullablePerson || invitation.InviterUserId == person), cancellationToken)
            .ConfigureAwait(false);
        var retained = new List<RetainedReferenceCount>
        {
            new(RetainedReferenceKind.RequestReceipt,
                await AttributionReferenceCounter.CountNamingRowsAsync(db, subject, ReceiptTypes, cancellationToken).ConfigureAwait(false)),
            new(RetainedReferenceKind.InvitationRecord, invitations),
            new(RetainedReferenceKind.AttributionReference,
                await AttributionReferenceCounter.CountAsync(db, subject, ReceiptTypes, PartyProperties, cancellationToken)
                    .ConfigureAwait(false)),
        };

        // Tenant-owned rows without an organization belong to the tenant-wide group alone.
        if (subject.Group.IsTenantWide)
        {
            retained.Add(new(RetainedReferenceKind.FederationActorReference, await db.FederationConnections.AsNoTracking()
                .LongCountAsync(connection => connection.PendingActor == nullablePerson || connection.TestActor == nullablePerson,
                    cancellationToken).ConfigureAwait(false)));
            retained.Add(new(RetainedReferenceKind.ProviderEventRecord, await db.ProviderEventTargets.AsNoTracking()
                .LongCountAsync(target => target.AffectedUserId == person, cancellationToken).ConfigureAwait(false)));
        }

        return retained;
    }

    private async Task<bool> ExportMembershipsAsync(UserDataSubject subject, IUserDataExportWriter writer, CancellationToken cancellationToken)
    {
        var person = subject.SubjectUserId;
        var organization = subject.Group.OrganizationId;
        var snapshot = subject.MembershipIds.Select(MembershipId.From).ToList();
        var memberships = await db.Memberships.AsNoTracking().Include(member => member.Profile).Include(member => member.RoleAssignments)
            .Where(member => snapshot.Contains(member.Id) && member.UserId == person && member.OrganizationId == organization)
            .OrderBy(member => member.CreatedAt).ThenBy(member => member.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var roleIds = memberships.SelectMany(member => member.RoleAssignments).Select(assignment => assignment.RoleId).Distinct().ToList();
        var roles = await db.Roles.AsNoTracking().Where(role => roleIds.Contains(role.Id))
            .ToDictionaryAsync(role => role.Id, role => RoleName(role.BuiltInKey), cancellationToken).ConfigureAwait(false);
        foreach (var membership in memberships)
        {
            var values = MembershipCustomFieldValues.Read(membership.Profile.CustomFieldsJson);
            var revisions = await fields.ReadAsync(values, cancellationToken).ConfigureAwait(false);
            var appended = await UserDataExportRecord.AppendAsync(writer, "memberships", json =>
                WriteMembership(json, membership, roles, values, revisions), cancellationToken).ConfigureAwait(false);
            if (appended != UserDataExportAppend.Appended)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Accepted invitations carry the subject's own address, oldest first; issued ones omit the invitee's and are
    /// written newest first under their collection cap, whose hit is declared.
    /// </summary>
    private async Task<bool> ExportInvitationsAsync(UserDataSubject subject, IUserDataExportWriter writer, bool accepted,
        CancellationToken cancellationToken)
    {
        var person = subject.SubjectUserId;
        UserId? nullablePerson = person;
        var organization = subject.Group.OrganizationId;
        var collection = accepted ? AcceptedCollection : IssuedCollection;
        var query = accepted
            ? db.Invitations.AsNoTracking().Where(invitation => invitation.OrganizationId == organization && invitation.AcceptedBy == nullablePerson)
            : db.Invitations.AsNoTracking().Where(invitation => invitation.OrganizationId == organization && invitation.InviterUserId == person);
        var ordered = accepted
            ? query.OrderBy(invitation => invitation.CreatedAt).ThenBy(invitation => invitation.Id)
            : query.OrderByDescending(invitation => invitation.CreatedAt).ThenByDescending(invitation => invitation.Id);
        long included = 0;
        for (var offset = 0; ; offset += Page)
        {
            // Bounded pages in a stable order on the one transaction; the caps and the bundle bound the total.
            var page = await ordered.Skip(offset).Take(Page).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (page.Count == 0)
            {
                return true;
            }

            var roleIds = page.Select(invitation => invitation.RoleId).Distinct().ToList();
            var roles = await db.Roles.AsNoTracking().Where(role => roleIds.Contains(role.Id))
                .ToDictionaryAsync(role => role.Id, role => RoleName(role.BuiltInKey), cancellationToken).ConfigureAwait(false);
            foreach (var invitation in page)
            {
                var appended = await UserDataExportRecord.AppendAsync(writer, collection, json =>
                    WriteInvitation(json, invitation, roles, person, accepted), cancellationToken).ConfigureAwait(false);
                if (appended == UserDataExportAppend.SectionCapReached)
                {
                    // Counted after the pages, on the same transaction; the maximum keeps the statement coherent
                    // should a statement-level snapshot see fewer rows than were written.
                    var total = await query.LongCountAsync(cancellationToken).ConfigureAwait(false);
                    writer.DeclareTruncated(collection, included, Math.Max(total, included));
                    return false;
                }

                if (appended != UserDataExportAppend.Appended)
                {
                    return false;
                }

                included++;
            }

            if (page.Count < Page)
            {
                return true;
            }
        }
    }

    private static void WriteMembership(Utf8JsonWriter json, Membership membership, IReadOnlyDictionary<RoleId, string> roles,
        IReadOnlyList<MembershipCustomFieldValue> values, IReadOnlyList<MembershipFieldRevision> revisions)
    {
        json.WriteStartObject();
        json.WriteString("status", membership.Status switch
        {
            MembershipStatus.Active => "active",
            MembershipStatus.Suspended => "suspended",
            MembershipStatus.Revoked => "revoked",
            _ => throw new InvalidOperationException("A stored membership status is not in the closed set."),
        });
        json.WriteString("scope", membership.OrganizationId is null ? "tenant" : "organization");
        json.WriteStartArray("roles");
        foreach (var role in membership.RoleAssignments.Select(assignment => roles.GetValueOrDefault(assignment.RoleId, "unavailable"))
                     .Order(StringComparer.Ordinal))
        {
            json.WriteStringValue(role);
        }

        json.WriteEndArray();
        json.WriteString("created_at", membership.CreatedAt);
        WriteInstant(json, "updated_at", membership.UpdatedAt);
        WriteInstant(json, "revoked_at", membership.DeletedAt);
        json.WriteStartObject("profile");
        WriteText(json, "display_name", membership.Profile.DisplayName);
        WriteText(json, "preferred_locale", membership.Profile.PreferredLocale);
        json.WriteStartArray("custom_fields");
        foreach (var value in values)
        {
            var revision = revisions.FirstOrDefault(candidate => candidate.DefinitionId == value.DefinitionId
                && candidate.SchemaVersion == value.SchemaVersion);
            json.WriteStartObject();
            json.WriteString("field", value.ConceptKey);
            json.WriteNumber("revision", value.SchemaVersion);
            if (revision is null)
            {
                json.WriteNull("labels");
                json.WriteNull("classification");
            }
            else
            {
                json.WriteStartObject("labels");
                foreach (var label in revision.Labels.OrderBy(item => item.Key, StringComparer.Ordinal))
                {
                    json.WriteString(label.Key, label.Value);
                }

                json.WriteEndObject();
                json.WriteString("classification", Classification(revision.Classification));
            }

            json.WriteBoolean("consent_recorded", value.ConsentEvidenceId is not null);
            json.WritePropertyName("value");
            value.Envelope.WriteTo(json);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
        json.WriteEndObject();
    }

    private static void WriteInvitation(Utf8JsonWriter json, Invitation invitation, IReadOnlyDictionary<RoleId, string> roles,
        UserId subject, bool accepted)
    {
        json.WriteStartObject();
        json.WriteString("status", invitation.Status switch
        {
            InvitationStatus.Pending => "pending",
            InvitationStatus.Accepted => "accepted",
            InvitationStatus.Revoked => "revoked",
            _ => throw new InvalidOperationException("A stored invitation status is not in the closed set."),
        });
        json.WriteString("scope", invitation.OrganizationId is null ? "tenant" : "organization");
        json.WriteString("role", roles.GetValueOrDefault(invitation.RoleId, "unavailable"));
        json.WriteString("created_at", invitation.CreatedAt);
        json.WriteString("expires_at", invitation.ExpiresAt);
        WriteInstant(json, "accepted_at", invitation.AcceptedAt);
        if (accepted)
        {
            // ADR-0107 § 3: the person's own address on the offer they accepted.
            json.WriteString("recipient_email", invitation.RecipientEmail);
            json.WriteString("issued_by", invitation.InviterUserId == subject ? Self : ThirdParty);
        }
        else
        {
            // ADR-0106 § 5: the invitee's address is another person's data and is omitted; the acceptor is a party.
            if (invitation.AcceptedBy is { } acceptor)
            {
                json.WriteString("accepted_by", acceptor == subject ? Self : ThirdParty);
            }
            else
            {
                json.WriteNull("accepted_by");
            }
        }

        json.WriteEndObject();
    }

    internal static string RoleName(BuiltInRoleKey key) => key switch
    {
        BuiltInRoleKey.TenantAdmin => "tenant_admin",
        BuiltInRoleKey.Instructor => "instructor",
        BuiltInRoleKey.Learner => "learner",
        BuiltInRoleKey.Guardian => "guardian",
        BuiltInRoleKey.Editor => "editor",
        BuiltInRoleKey.OrgAdmin => "org_admin",
        BuiltInRoleKey.OrgInstructor => "org_instructor",
        _ => "unavailable",
    };

    private static string Classification(PersonalDataCategory category) => category switch
    {
        PersonalDataCategory.None => "none",
        PersonalDataCategory.Identity => "identity",
        PersonalDataCategory.Behaviour => "behaviour",
        PersonalDataCategory.Sensitive => "sensitive",
        PersonalDataCategory.Payment => "payment",
        PersonalDataCategory.Audit => "audit",
        _ => "unavailable",
    };

    private static void WriteInstant(Utf8JsonWriter json, string name, DateTimeOffset? value)
    {
        if (value is { } instant)
        {
            json.WriteString(name, instant);
        }
        else
        {
            json.WriteNull(name);
        }
    }

    private static void WriteText(Utf8JsonWriter json, string name, string? value)
    {
        if (value is null)
        {
            json.WriteNull(name);
        }
        else
        {
            json.WriteString(name, value);
        }
    }

    private void RequireGroup(UserDataSubject subject) => UserDataParticipantGuard.RequireGroup(subject, tenant);
}
