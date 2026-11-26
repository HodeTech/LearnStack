using System.Collections.Frozen;

namespace LearnStack.SharedKernel.DataProtection;

/// <summary>The three data-request kinds (ADR-0106 § 1).</summary>
/// <remarks>Zero is deliberately undefined, so an unassigned field is never a valid kind.</remarks>
public enum DataRequestKind
{
    Export = 1,
    Erasure = 2,
    Closure = 3,
}

/// <summary>The closed request statuses of ADR-0106 § 8; the transitions live on the request aggregate.</summary>
public enum DataRequestStatus
{
    Requested = 1,
    Scheduled = 2,
    Fulfilling = 3,
    AwaitingPrivilegedStep = 4,
    Completed = 5,
    Blocked = 6,
    Failed = 7,
    Cancelled = 8,
}

/// <summary>Who filed a request (ADR-0106 § 3, ADR-0107 § 7).</summary>
/// <remarks>
/// <see cref="ClosureFanout"/> is the per-group erasure a closure's Begin creates; its business
/// actor is the system actor and it has no requester.
/// </remarks>
public enum DataRequestOrigin
{
    Self = 1,
    Administrator = 2,
    ClosureFanout = 3,
}

/// <summary>
/// The closed refusal codes a request can record in <c>last_refusal</c> (ADR-0106 § 7,
/// ADR-0107 § 7).
/// </summary>
/// <remarks>
/// A refusal at creation is a response and writes no request; a refusal that leaves a scheduled
/// request in place is recorded, and a re-check refusal at execution ends the request
/// <see cref="DataRequestStatus.Blocked"/> with its code. <see cref="BlockedWindow"/> is a closure
/// Begin attempted before the execution time; it never blocks a request.
/// </remarks>
public enum DataRequestRefusal
{
    BlockedWindow = 1,
    BlockedHold = 2,
    BlockedLastAdministrator = 3,
}

/// <summary>The closed reasons a request ends <see cref="DataRequestStatus.Failed"/>.</summary>
/// <remarks>
/// <see cref="ExportTooLarge"/> is ADR-0107 § 4's bundle ceiling. <see cref="ExecutionFailed"/> records
/// a job that ended without a business outcome (ADR-0106 § 8 reads <c>failed</c> from the job phase).
/// </remarks>
public enum DataRequestFailureReason
{
    ExportTooLarge = 1,
    ExecutionFailed = 2,
}

/// <summary>
/// The closed retained-reference kinds a receipt reports (ADR-0107 § 6); the receipt carries the
/// kind and a count, never a row identifier.
/// </summary>
public enum RetainedReferenceKind
{
    ConsentEvidence = 1,
    InvitationRecord = 2,
    InvitationEnvelope = 3,
    AuditActorReference = 4,
    AuditEntityReference = 5,
    AuditMetadataReference = 6,
    UnscopedAuditRows = 7,
    TransportEvidence = 8,
    ExecutionEvidence = 9,
    ProviderEventRecord = 10,
    IdempotencyCache = 11,
    AttributionReference = 12,
    FederationActorReference = 13,
    RequestReceipt = 14,
    ExternalCopy = 15,
    UnservedGroup = 16,
}

/// <summary>The lifecycle of an export artifact (ADR-0107 § 4).</summary>
/// <remarks>
/// <see cref="Revoked"/> and <see cref="Purged"/> hold no chunk; <see cref="Expired"/> may still hold
/// chunks until the purge job runs, and is unreadable either way because expiry is a time check.
/// </remarks>
public enum DataExportArtifactStatus
{
    Available = 1,
    Revoked = 2,
    Expired = 3,
    Purged = 4,
}

/// <summary>The stored and wire spelling of every data-request vocabulary.</summary>
public static class DataRequestCodes
{
    public static ClosedCodeTable<DataRequestKind> Kinds { get; } = new(
        (DataRequestKind.Export, "export"),
        (DataRequestKind.Erasure, "erasure"),
        (DataRequestKind.Closure, "closure"));

    public static ClosedCodeTable<DataRequestStatus> Statuses { get; } = new(
        (DataRequestStatus.Requested, "requested"),
        (DataRequestStatus.Scheduled, "scheduled"),
        (DataRequestStatus.Fulfilling, "fulfilling"),
        (DataRequestStatus.AwaitingPrivilegedStep, "awaiting_privileged_step"),
        (DataRequestStatus.Completed, "completed"),
        (DataRequestStatus.Blocked, "blocked"),
        (DataRequestStatus.Failed, "failed"),
        (DataRequestStatus.Cancelled, "cancelled"));

    public static ClosedCodeTable<DataRequestOrigin> Origins { get; } = new(
        (DataRequestOrigin.Self, "self"),
        (DataRequestOrigin.Administrator, "administrator"),
        (DataRequestOrigin.ClosureFanout, "closure_fanout"));

    public static ClosedCodeTable<DataRequestRefusal> Refusals { get; } = new(
        (DataRequestRefusal.BlockedWindow, "blocked_window"),
        (DataRequestRefusal.BlockedHold, "blocked_hold"),
        (DataRequestRefusal.BlockedLastAdministrator, "blocked_last_administrator"));

    public static ClosedCodeTable<DataRequestFailureReason> FailureReasons { get; } = new(
        (DataRequestFailureReason.ExportTooLarge, "export_too_large"),
        (DataRequestFailureReason.ExecutionFailed, "execution_failed"));

    public static ClosedCodeTable<RetainedReferenceKind> RetainedKinds { get; } = new(
        (RetainedReferenceKind.ConsentEvidence, "consent_evidence"),
        (RetainedReferenceKind.InvitationRecord, "invitation_record"),
        (RetainedReferenceKind.InvitationEnvelope, "invitation_envelope"),
        (RetainedReferenceKind.AuditActorReference, "audit_actor_reference"),
        (RetainedReferenceKind.AuditEntityReference, "audit_entity_reference"),
        (RetainedReferenceKind.AuditMetadataReference, "audit_metadata_reference"),
        (RetainedReferenceKind.UnscopedAuditRows, "unscoped_audit_rows"),
        (RetainedReferenceKind.TransportEvidence, "transport_evidence"),
        (RetainedReferenceKind.ExecutionEvidence, "execution_evidence"),
        (RetainedReferenceKind.ProviderEventRecord, "provider_event_record"),
        (RetainedReferenceKind.IdempotencyCache, "idempotency_cache"),
        (RetainedReferenceKind.AttributionReference, "attribution_reference"),
        (RetainedReferenceKind.FederationActorReference, "federation_actor_reference"),
        (RetainedReferenceKind.RequestReceipt, "request_receipt"),
        (RetainedReferenceKind.ExternalCopy, "external_copy"),
        (RetainedReferenceKind.UnservedGroup, "unserved_group"));

    public static ClosedCodeTable<DataExportArtifactStatus> ArtifactStatuses { get; } = new(
        (DataExportArtifactStatus.Available, "available"),
        (DataExportArtifactStatus.Revoked, "revoked"),
        (DataExportArtifactStatus.Expired, "expired"),
        (DataExportArtifactStatus.Purged, "purged"));

    /// <summary>
    /// The statuses that hold a group's one active slot (ADR-0106 § 7); every other status is
    /// terminal and frees it.
    /// </summary>
    public static FrozenSet<DataRequestStatus> NonTerminal { get; } = new[]
    {
        DataRequestStatus.Requested, DataRequestStatus.Scheduled,
        DataRequestStatus.Fulfilling, DataRequestStatus.AwaitingPrivilegedStep,
    }.ToFrozenSet();

    /// <summary>Whether a status is one of the four terminal statuses of ADR-0106 § 8.</summary>
    public static bool IsTerminal(DataRequestStatus status) =>
        Enum.IsDefined(status) && !NonTerminal.Contains(status);
}
