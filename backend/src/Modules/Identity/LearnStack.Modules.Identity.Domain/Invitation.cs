using System.Security.Cryptography;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;

namespace LearnStack.Modules.Identity.Domain;

public enum InvitationStatus { Pending = 1, Accepted = 2, Revoked = 3 }
public enum InvitationAcceptanceDisposition { Created = 1, AlreadyMember = 2 }
public enum InvitationSubmissionStatus { Queued = 1, SubmissionAccepted = 2, Failed = 3 }

/// <summary>Nonsecret identifiers and private digest for one already-staged delivery.</summary>
public sealed record InvitationDelivery(InvitationGenerationId GenerationId, BackgroundJobId JobId,
    [property: PiiSensitive] string VerifierDigest, string EntryOrigin, string EntryLocale, int LifetimeDays)
{
    public override string ToString() => nameof(InvitationDelivery);
}

/// <summary>One immutable scoped offer; only the acceptance owner can pair consumption with Membership.</summary>
[TenantOwned]
[OrganizationScoped]
public sealed class Invitation : AuditableEntity<InvitationId>, IAggregateRoot<InvitationId>, IOrganizationScoped
{
    private Invitation() { }

    public TenantId TenantId { get; private set; }
    public OrganizationId? OrganizationId { get; private set; }
    [PiiSensitive]
    public string RecipientEmail { get; private set; } = string.Empty;
    public UserId InviterUserId { get; private set; }
    public RoleId RoleId { get; private set; }
    public long PinnedRoleVersion { get; private set; }
    public InvitationStatus Status { get; private set; }
    public InvitationGenerationId GenerationId { get; private set; }
    public long Generation { get; private set; }
    public DateTimeOffset GenerationCreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    [PiiSensitive]
    public string? VerifierDigest { get; private set; }
    [PiiSensitive]
    public string? ConsumedVerifierDigest { get; private set; }
    public string EntryOrigin { get; private set; } = string.Empty;
    public string EntryLocale { get; private set; } = string.Empty;
    public BackgroundJobId CurrentDeliveryJobId { get; private set; }
    public InvitationSubmissionStatus SubmissionStatus { get; private set; }
    public InvitationEmailReceiptId? SubmissionReceiptId { get; private set; }
    public UserId? AcceptedBy { get; private set; }
    public MembershipId? ResultMembershipId { get; private set; }
    public InvitationAcceptanceDisposition? AcceptanceDisposition { get; private set; }
    public DateTimeOffset? AcceptedAt { get; private set; }

    public static Result<Invitation> Create(InvitationId id, TenantId tenant, OrganizationId? organization,
        string email, Role role, InvitationDelivery delivery, DateTimeOffset at, UserId inviter)
    {
        ArgumentNullException.ThrowIfNull(role);
        ArgumentNullException.ThrowIfNull(delivery);
        if (!ValidId(id) || !StronglyTypedId.IsAssigned(tenant) || tenant == TenantId.PlatformSentinel
            || organization is { } org && !StronglyTypedId.IsAssigned(org)
            || !ValidActor(inviter) || !InvitationMailbox.TryNormalize(email, out var normalized)
            || !ValidDelivery(delivery) || !CompatibleRole(role, tenant, organization) || !ValidInstant(at, delivery.LifetimeDays))
            return Result<Invitation>.Fail(IdentityFailures.Refused());
        var invitation = new Invitation
        {
            Id = id,
            TenantId = tenant,
            OrganizationId = organization,
            RecipientEmail = normalized,
            InviterUserId = inviter,
            RoleId = role.Id,
            PinnedRoleVersion = role.Version,
            Status = InvitationStatus.Pending,
            Generation = 1,
        };
        invitation.SetDelivery(delivery, at);
        invitation.MarkCreated(at, inviter);
        return Result<Invitation>.Ok(invitation);
    }

    public bool IsExpired(DateTimeOffset databaseNow) => Status == InvitationStatus.Pending && databaseNow >= ExpiresAt;

    public bool MatchesPendingVerifier(string digest) => Status == InvitationStatus.Pending && SameDigest(VerifierDigest, digest);

    public bool MatchesConsumedVerifier(string digest) => Status == InvitationStatus.Accepted && SameDigest(ConsumedVerifierDigest, digest);

    public Result<None> Reissue(Role role, InvitationDelivery delivery, DateTimeOffset at, UserId actor)
    {
        ArgumentNullException.ThrowIfNull(role);
        ArgumentNullException.ThrowIfNull(delivery);
        if (Status != InvitationStatus.Pending || IsDeleted || Generation == long.MaxValue || Version == long.MaxValue
            || !ValidActor(actor) || !ValidDelivery(delivery) || delivery.GenerationId == GenerationId
            || delivery.JobId == CurrentDeliveryJobId || at < GenerationCreatedAt || !ValidInstant(at, delivery.LifetimeDays)
            || SameDigest(VerifierDigest, delivery.VerifierDigest)
            || !CompatibleRole(role, TenantId, OrganizationId) || role.Id != RoleId || role.Version != PinnedRoleVersion)
            return IdentityFailures.BusinessRule();
        MarkUpdated(at, actor);
        Generation++;
        SetDelivery(delivery, at);
        return Result<None>.Ok(None.Value);
    }

    public Result<None> Revoke(DateTimeOffset at, UserId actor)
    {
        if (Status != InvitationStatus.Pending || IsDeleted || Version == long.MaxValue
            || !ValidActor(actor) || at < GenerationCreatedAt || at.Offset != TimeSpan.Zero) return IdentityFailures.BusinessRule();
        MarkUpdated(at, actor);
        Status = InvitationStatus.Revoked;
        VerifierDigest = null;
        return Result<None>.Ok(None.Value);
    }

    /// <summary>Caller proves fresh provider/delegation/locks; this method enforces the actual result graph.</summary>
    public Result<None> Accept(Membership membership, InvitationAcceptanceDisposition disposition,
        string digest, DateTimeOffset databaseNow, UserId actor)
    {
        ArgumentNullException.ThrowIfNull(membership);
        if (Status != InvitationStatus.Pending || IsDeleted || Version == long.MaxValue || IsExpired(databaseNow)
            || databaseNow < GenerationCreatedAt || databaseNow.Offset != TimeSpan.Zero || !ValidActor(actor) || !MatchesPendingVerifier(digest)
            || membership.TenantId != TenantId || membership.OrganizationId != OrganizationId || membership.UserId != actor
            || membership.Status != MembershipStatus.Active || membership.IsDeleted
            || disposition is not (InvitationAcceptanceDisposition.Created or InvitationAcceptanceDisposition.AlreadyMember)
            || disposition == InvitationAcceptanceDisposition.Created
                && (membership.RoleAssignments.Count != 1 || membership.RoleAssignments.Single().RoleId != RoleId))
            return IdentityFailures.BusinessRule();
        MarkUpdated(databaseNow, actor);
        Status = InvitationStatus.Accepted;
        ConsumedVerifierDigest = VerifierDigest;
        VerifierDigest = null;
        AcceptedBy = actor;
        AcceptedAt = databaseNow;
        ResultMembershipId = membership.Id;
        AcceptanceDisposition = disposition;
        return Result<None>.Ok(None.Value);
    }

    public Result<None> RecordSubmission(InvitationGenerationId generation, BackgroundJobId job, InvitationEmailReceiptId receipt,
        InvitationSubmissionStatus status, DateTimeOffset at, UserId actor)
    {
        if (generation != GenerationId || job != CurrentDeliveryJobId) return Result<None>.Ok(None.Value);
        if (!StronglyTypedId.IsAssigned(receipt) || receipt.Value.Version != 7
            || status is not (InvitationSubmissionStatus.SubmissionAccepted or InvitationSubmissionStatus.Failed)
            || !StronglyTypedId.IsAssigned(actor) || at < GenerationCreatedAt || at.Offset != TimeSpan.Zero) return IdentityFailures.BusinessRule();
        if (SubmissionReceiptId is { } selected)
            return selected == receipt && SubmissionStatus == status ? Result<None>.Ok(None.Value) : IdentityFailures.BusinessRule();
        if (Version == long.MaxValue) return IdentityFailures.BusinessRule();
        MarkUpdated(at, actor);
        SubmissionReceiptId = receipt;
        SubmissionStatus = status;
        return Result<None>.Ok(None.Value);
    }

    private void SetDelivery(InvitationDelivery delivery, DateTimeOffset at)
    {
        GenerationId = delivery.GenerationId;
        GenerationCreatedAt = at;
        ExpiresAt = at.AddDays(delivery.LifetimeDays);
        VerifierDigest = delivery.VerifierDigest;
        EntryOrigin = delivery.EntryOrigin;
        EntryLocale = delivery.EntryLocale;
        CurrentDeliveryJobId = delivery.JobId;
        SubmissionStatus = InvitationSubmissionStatus.Queued;
        SubmissionReceiptId = null;
    }

    private static bool CompatibleRole(Role role, TenantId tenant, OrganizationId? organization) =>
        !role.IsDeleted && role.TenantId == tenant
        && role.PermissionScope == (organization is null ? PermissionScope.Tenant : PermissionScope.Organization);

    private static bool ValidId(InvitationId id) => StronglyTypedId.IsAssigned(id) && id.Value.Version == 7;
    private static bool ValidActor(UserId actor) => StronglyTypedId.IsAssigned(actor) && actor.Value.Version == 7 && actor != UserId.SystemActor;
    private static bool ValidDigest(string? value) => value is { Length: 64 } && value.All(c => char.IsAsciiHexDigitLower(c));
    private static bool ValidInstant(DateTimeOffset at, int days) => at != default && at.Offset == TimeSpan.Zero
        && at <= DateTimeOffset.MaxValue.AddDays(-days);

    private static bool ValidDelivery(InvitationDelivery delivery) =>
        StronglyTypedId.IsAssigned(delivery.GenerationId) && delivery.GenerationId.Value.Version == 7
        && StronglyTypedId.IsAssigned(delivery.JobId) && delivery.JobId.Value.Version == 7
        && ValidDigest(delivery.VerifierDigest) && delivery.LifetimeDays is >= 1 and <= 30
        && delivery.EntryLocale is { Length: > 0 and <= 35 } && LocaleTag.IsWellFormed(delivery.EntryLocale)
        && Uri.TryCreate(delivery.EntryOrigin, UriKind.Absolute, out var origin) && origin.Scheme == Uri.UriSchemeHttps
        && origin.UserInfo.Length == 0 && origin.Query.Length == 0 && origin.Fragment.Length == 0
        && string.Equals(delivery.EntryOrigin, origin.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal);

    private static bool SameDigest(string? actual, string? supplied) => ValidDigest(actual) && ValidDigest(supplied)
        && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual!), Convert.FromHexString(supplied!));
}
