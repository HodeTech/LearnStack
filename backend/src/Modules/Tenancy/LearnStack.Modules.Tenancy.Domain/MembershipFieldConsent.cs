using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;

namespace LearnStack.Modules.Tenancy.Domain;

public enum MembershipFieldConsentState { Granted = 1, Withdrawn = 2 }

/// <summary>An immutable act for one exact membership, definition revision and purpose.</summary>
[TenantOwned]
[OrganizationScoped]
public sealed class MembershipFieldConsent : AuditableEntity<MembershipFieldConsentId>,
    IAggregateRoot<MembershipFieldConsentId>, IOrganizationScoped
{
    private MembershipFieldConsent() { PurposeKey = null!; NoticeLocale = null!; NoticeHash = null!; }

    public TenantId TenantId { get; private set; }
    public OrganizationId? OrganizationId { get; private set; }
    [PiiSensitive] public Guid MembershipId { get; private set; }
    public UserId SubjectUserId { get; private set; }
    public UserId ActorUserId { get; private set; }
    [PiiSensitive] public Guid DefinitionId { get; private set; }
    [PiiSensitive] public int DefinitionVersion { get; private set; }
    [PiiSensitive] public string PurposeKey { get; private set; }
    [PiiSensitive] public string NoticeLocale { get; private set; }
    [PiiSensitive] public string NoticeHash { get; private set; }
    public long Sequence { get; private set; }
    public MembershipFieldConsentState State { get; private set; }
    public MembershipFieldConsentId? PredecessorId { get; private set; }
    public long PredecessorSequence { get; private set; }

    /// <summary>
    /// Constructs an act after the caller has established the owning membership and exact
    /// definition/notice under the tenant fence. The caller must load durable predecessor
    /// and latest grant under that same fence before calling this factory.
    /// </summary>
    public static MembershipFieldConsent Create(
        MembershipFieldConsentId id, TenantId tenantId, OrganizationId? organizationId,
        Guid membershipId, UserId subjectUserId, UserId actorUserId,
        Guid definitionId, int definitionVersion, string purposeKey, string noticeLocale,
        string noticeHash, long sequence, MembershipFieldConsentState state,
        MembershipFieldConsentId? predecessorId, long predecessorSequence, DateTimeOffset at)
    {
        if (!id.IsInitialized() || id.Value == Guid.Empty || id.Value.Version != 7)
            throw new ArgumentException("Consent identifier is invalid.", nameof(id));
        TenantOwnership.EnsureRealTenant(tenantId, "Consent tenant is invalid.", nameof(tenantId));
        if (organizationId is { } org && (!org.IsInitialized() || org.Value == Guid.Empty))
            throw new ArgumentException("Consent organization is invalid.", nameof(organizationId));
        if (membershipId == Guid.Empty || definitionId == Guid.Empty || definitionVersion < 1
            || !subjectUserId.IsInitialized() || subjectUserId.Value == Guid.Empty || subjectUserId == UserId.SystemActor
            || actorUserId != subjectUserId || !Enum.IsDefined(state)
            || (sequence == 1 && state != MembershipFieldConsentState.Granted)
            || sequence < 1 || sequence > 9007199254740991L || predecessorSequence != sequence - 1
            || (predecessorSequence == 0) != (predecessorId is null)
            || (predecessorId is { } predecessor && (!predecessor.IsInitialized() || predecessor.Value == Guid.Empty)))
            throw new ArgumentException("Consent act is invalid.");
        ArgumentException.ThrowIfNullOrWhiteSpace(purposeKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(noticeLocale);
        ArgumentException.ThrowIfNullOrWhiteSpace(noticeHash);
        if (purposeKey.Length > 64 || noticeLocale.Length > 35 || noticeHash.Length != 64
            || purposeKey.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
            || noticeHash.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("Consent pin is invalid.");
        var consent = new MembershipFieldConsent
        {
            Id = id,
            TenantId = tenantId,
            OrganizationId = organizationId,
            MembershipId = membershipId,
            SubjectUserId = subjectUserId,
            ActorUserId = actorUserId,
            DefinitionId = definitionId,
            DefinitionVersion = definitionVersion,
            PurposeKey = purposeKey,
            NoticeLocale = noticeLocale,
            NoticeHash = noticeHash,
            Sequence = sequence,
            State = state,
            PredecessorId = predecessorId,
            PredecessorSequence = predecessorSequence,
        };
        consent.MarkCreated(at, actorUserId);
        return consent;
    }
}
