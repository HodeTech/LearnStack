using System.Globalization;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.SharedKernel.Audit;

/// <summary>One tenant delivery's minimized provider fact; never a general audit metadata channel.</summary>
public sealed class ProviderEventAuditEvidence
{
    private readonly object _gate = new();
    private AuditEntryId? _intent;

    private ProviderEventAuditEvidence(EventTenantContext delivery, Guid target, Guid receipt, Guid providerEvent,
        IdentityAuthorityId authority, string realm, UserId affected, IdentityProviderEventKind kind, DateTimeOffset sourceAt)
    {
        if (!delivery.IsResolved || delivery.UserId != UserId.SystemActor || delivery.ModuleName != "audit"
            || delivery.OrganizationId is not null || delivery.CausalActorUserId is not null
            || delivery.EventId == Guid.Empty || delivery.EventId.Version != 7 || target == Guid.Empty || target.Version != 7
            || receipt == Guid.Empty || receipt.Version != 7 || providerEvent == Guid.Empty || !StronglyTypedId.IsAssigned(authority) || !StronglyTypedId.IsAssigned(affected)
            || affected == UserId.SystemActor || string.IsNullOrWhiteSpace(realm) || realm.Length > 255 || !JsonValue.IsStorableText(realm)
            || !Enum.IsDefined(kind) || sourceAt.Offset != TimeSpan.Zero)
            throw new ArgumentException("Provider audit evidence has no exact tenant delivery.");
        TenantId = delivery.TenantId; EventId = delivery.EventId; TargetId = target; ReceiptId = receipt; ProviderEventId = providerEvent;
        AuthorityId = authority; RealmId = realm; AffectedUserId = affected; Kind = kind; SourceOccurredAt = sourceAt;
    }

    public TenantId TenantId { get; }
    public Guid EventId { get; }
    public Guid TargetId { get; }
    public Guid ReceiptId { get; }
    public Guid ProviderEventId { get; }
    public IdentityAuthorityId AuthorityId { get; }
    public string RealmId { get; }
    public UserId AffectedUserId { get; }
    public IdentityProviderEventKind Kind { get; }
    public DateTimeOffset SourceOccurredAt { get; }

    public void BindTo(AuditIntent intent)
    {
        RequireShape(intent);
        lock (_gate)
        {
            if (_intent is not null) throw new InvalidOperationException("Provider evidence is already attached.");
            _intent = intent.Id;
        }
    }

    public void RequireIntent(AuditIntent intent)
    {
        RequireShape(intent);
        lock (_gate)
            if (_intent != intent.Id) throw new InvalidOperationException("Provider evidence belongs to another audit intent.");
    }

    private void RequireShape(AuditIntent intent)
    {
        if (!ReferenceEquals(intent.ProviderEvent, this) || intent.TenantId != TenantId || intent.OrganizationId is not null
            || intent.ActorUserId != UserId.SystemActor || intent.CausalActorUserId is not null || intent.CausationId != EventId
            || intent.ModuleName != "audit" || intent.Operation != "audit.provider_event.observe"
            || intent.OperationType != OperationType.SecurityEvent || intent.OperationClass != OperationClass.Must
            || intent.EntityType != typeof(ProviderEventAuditEvidence) || intent.Authorization is not null || intent.GlobalIdentity is not null
            || intent.JobId is not null || intent.ParentJobId is not null)
            throw new InvalidOperationException("Provider evidence cannot authorize another audit operation.");
    }

    public string ToJson()
    {
        var outcome = Kind is IdentityProviderEventKind.LoginFailure or IdentityProviderEventKind.LogoutFailure
            or IdentityProviderEventKind.PasswordResetRequestFailure or IdentityProviderEventKind.PasswordResetFailure
            or IdentityProviderEventKind.CredentialUpdateFailure or IdentityProviderEventKind.CredentialRemovalFailure
            or IdentityProviderEventKind.PasswordUpdateFailure or IdentityProviderEventKind.TotpUpdateFailure
            or IdentityProviderEventKind.TotpRemovalFailure ? "error" : "success";
        return "{\"version\":1,\"scope\":\"provider_account_history\",\"authority_id\":" + AuditJson.Quote(AuthorityId.Value.ToString("D"))
            + ",\"realm_id\":" + AuditJson.Quote(RealmId) + ",\"provider_event_id\":" + AuditJson.Quote(ProviderEventId.ToString("D"))
            + ",\"target_id\":" + AuditJson.Quote(TargetId.ToString("D")) + ",\"affected_user_id\":" + AuditJson.Quote(AffectedUserId.Value.ToString("D"))
            + ",\"receipt_id\":" + AuditJson.Quote(ReceiptId.ToString("D"))
            + ",\"kind\":" + AuditJson.Quote(Kind.ToString()) + ",\"provider_outcome\":" + AuditJson.Quote(outcome)
            + ",\"source_occurred_at\":" + AuditJson.Quote(SourceOccurredAt.ToString("O", CultureInfo.InvariantCulture)) + "}";
    }
    public override string ToString() => nameof(ProviderEventAuditEvidence);

#pragma warning disable CA1034 // Exactly the real Audit subscriber mints these facts.
    public abstract class MintingSource
#pragma warning restore CA1034
    {
        protected static ProviderEventAuditEvidence Observe(EventTenantContext delivery, Guid target, Guid receipt, Guid providerEvent,
            IdentityAuthorityId authority, string realm, UserId affected, IdentityProviderEventKind kind, DateTimeOffset sourceAt) =>
            new(delivery, target, receipt, providerEvent, authority, realm, affected, kind, sourceAt);
    }
}
