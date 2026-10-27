using System.Net;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.Audit;

public enum AuthorizationEvidenceKind { Denial = 1, ClaimAssertion = 2 }

/// <summary>One centrally minted event, frozen across its writes and exact reconciliation.</summary>
public sealed class AuthorizationEvidenceOccurrence
{
    private readonly AuditEntryDraft _draft;

    private AuthorizationEvidenceOccurrence(AuthorizationEvidenceKind kind, AuditEntryDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var operation = kind switch
        {
            AuthorizationEvidenceKind.Denial => "identity.authorization.deny",
            AuthorizationEvidenceKind.ClaimAssertion => "tenancy.assertion.reject_claim_context",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        if (!StronglyTypedId.IsAssigned(draft.Id) || draft.Id.Value.Version != 7
            || !StronglyTypedId.IsAssigned(draft.TenantId) || draft.TenantId == TenantId.PlatformSentinel
            || draft.OrganizationId is { } organization && !StronglyTypedId.IsAssigned(organization)
            || draft.ActorUserId is { } actor && !StronglyTypedId.IsAssigned(actor)
            || kind == AuthorizationEvidenceKind.ClaimAssertion && draft.ActorUserId is null
            || draft.Operation != operation || draft.ModuleName != operation[..operation.IndexOf('.', StringComparison.Ordinal)]
            || draft.OperationClass != OperationClass.Must || draft.OperationType != OperationType.SecurityEvent
            || draft.Outcome != AuditOutcome.Denied || draft.ActorEmail is not null
            || draft.Timestamp == default || draft.Metadata is null
            || draft.BeforeState is not null || draft.AfterState is not null || draft.Changes is not null)
            throw new ArgumentException("Authorization evidence attribution is invalid.", nameof(draft));
        Kind = kind;
        var utc = draft.Timestamp.ToUniversalTime();
        _draft = draft with
        {
            Timestamp = new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero),
            IpAddress = CopyAddress(draft.IpAddress),
        };
    }

    public AuthorizationEvidenceKind Kind { get; }
    /// <summary>The immutable fields; copy the mutable IPAddress at either boundary.</summary>
    public AuditEntryDraft Draft => _draft with { IpAddress = CopyAddress(_draft.IpAddress) };
    public override string ToString() => nameof(AuthorizationEvidenceOccurrence);

    private static IPAddress? CopyAddress(IPAddress? address) => address is null ? null
        : address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? new IPAddress(address.GetAddressBytes(), address.ScopeId) : new IPAddress(address.GetAddressBytes());

#pragma warning disable CA1034 // A compiled source census closes central occurrence minting.
    public abstract class MintingSource
#pragma warning restore CA1034
    {
        protected static AuthorizationEvidenceOccurrence ForOccurrence(AuthorizationEvidenceKind kind, AuditEntryDraft draft) => new(kind, draft);
    }
}
