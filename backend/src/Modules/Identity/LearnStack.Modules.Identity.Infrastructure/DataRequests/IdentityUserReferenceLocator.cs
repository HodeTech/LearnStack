using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.DataProtection;

namespace LearnStack.Modules.Identity.Infrastructure.DataRequests;

/// <summary>
/// Where the Identity module's audited aggregates hold a person or Membership reference in their audit snapshots
/// (<see href="../../../../../../docs/decisions/0108-privileged-audit-redaction.md">ADR-0108 § 7</see>).
/// </summary>
/// <remarks>
/// Every copy is redactable except the key of a User or a Membership, which is the row's own <c>entity_id</c>.
/// The table rows these snapshots describe keep their references under ADR-0107 § 6 (a Membership tombstone, a
/// request receipt, a federation actor); the audit copies are not on ADR-0108 § 6's list of survivors.
/// </remarks>
public sealed class IdentityUserReferenceLocator : IUserReferenceLocator
{
    public IReadOnlyCollection<UserReferenceDeclaration> Redactable { get; } =
    [
        .. UserReferenceDeclarations.Attribution(typeof(User), typeof(Membership), typeof(Role), typeof(Invitation),
            typeof(FederationConnection), typeof(DataSubjectRequest), typeof(DataExportArtifact)),
        new(typeof(Membership), UserReferencePath.Of(nameof(Membership.UserId)), UserReferenceKind.User),
        new(typeof(Invitation), UserReferencePath.Of(nameof(Invitation.InviterUserId)), UserReferenceKind.User),
        new(typeof(Invitation), UserReferencePath.Of(nameof(Invitation.AcceptedBy)), UserReferenceKind.User),
        new(typeof(Invitation), UserReferencePath.Of(nameof(Invitation.ResultMembershipId)), UserReferenceKind.Membership),
        new(typeof(FederationConnection), UserReferencePath.Of(nameof(FederationConnection.PendingActor)), UserReferenceKind.User),
        new(typeof(FederationConnection), UserReferencePath.Of(nameof(FederationConnection.TestActor)), UserReferenceKind.User),
        new(typeof(DataSubjectRequest), UserReferencePath.Of(nameof(DataSubjectRequest.SubjectUserId)), UserReferenceKind.User),
        new(typeof(DataSubjectRequest), UserReferencePath.Of(nameof(DataSubjectRequest.RequesterUserId)), UserReferenceKind.User),
        new(typeof(DataSubjectRequest), UserReferencePath.Elements(nameof(DataSubjectRequest.MembershipSnapshotJson)),
            UserReferenceKind.Membership),
        new(typeof(DataExportArtifact), UserReferencePath.Of(nameof(DataExportArtifact.SubjectUserId)), UserReferenceKind.User),
        new(typeof(DataExportTicket), UserReferencePath.Of(nameof(DataExportTicket.CreatedBy)), UserReferenceKind.User),
    ];

    public IReadOnlyCollection<RetainedUserReference> Retained { get; } =
    [
        UserReferenceDeclarations.OwnKey(typeof(User)),
        UserReferenceDeclarations.OwnKey(typeof(Membership)),
    ];
}
