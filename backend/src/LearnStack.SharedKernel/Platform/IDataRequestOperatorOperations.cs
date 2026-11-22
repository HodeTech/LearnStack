using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.DataProtection;

namespace LearnStack.SharedKernel.Platform;

/// <summary>
/// Which list of the data-request work that needs a person an inspection reads (ADR-0107 § 8, ADR-0106 § 12).
/// </summary>
public enum DataRequestInspectionState
{
    /// <summary>Erasures whose tenant side finished and that wait for the operator's privileged step.</summary>
    AwaitingPrivilegedStep = 1,

    /// <summary>
    /// Requests still <c>scheduled</c> after their execution time: the window passed and nothing started them.
    /// </summary>
    OverdueScheduled,

    /// <summary>
    /// Requests still <c>fulfilling</c> longer than <see cref="DataRequestLimits.StuckFulfillingAfter"/> after they
    /// fell due.
    /// </summary>
    StuckFulfilling,

    /// <summary>
    /// Export artifacts past their expiry that are still <c>available</c> or <c>expired</c>: their purge has not run.
    /// </summary>
    ExpiredArtifact,
}

/// <summary>One page request of the data-request inspection.</summary>
/// <param name="State">The list to read.</param>
/// <param name="Cursor">
/// The opaque position a previous page of the same list returned, or null for the first page.
/// </param>
/// <param name="Limit">The page size the route clamped; the owner clamps it again.</param>
public sealed record DataRequestInspectionQuery(DataRequestInspectionState State, string? Cursor, int Limit);

/// <summary>An export artifact's lifecycle metadata, without its subject, size, key version or content.</summary>
/// <param name="ArtifactId">The artifact.</param>
/// <param name="Status"><c>available</c> or <c>expired</c>, as stored.</param>
/// <param name="ExpiresAt">The instant it stopped being readable.</param>
public sealed record DataExportArtifactInspection(Guid ArtifactId, string Status, DateTimeOffset ExpiresAt);

/// <summary>
/// One data request in an inspection, metadata only: no subject, requester, snapshot, progress, result or person data.
/// </summary>
/// <param name="RequestId">The request.</param>
/// <param name="TenantId">The request's tenant.</param>
/// <param name="OrganizationId">The request's organization; null for the tenant-wide group.</param>
/// <param name="Kind"><c>export</c>, <c>erasure</c> or <c>closure</c>.</param>
/// <param name="Origin"><c>self</c>, <c>administrator</c> or <c>closure_fanout</c>.</param>
/// <param name="Status">The stored status.</param>
/// <param name="CreatedAt">When it was filed.</param>
/// <param name="ExecuteAt">When its window ends, for an erasure or closure filed with one.</param>
/// <param name="CompletedAt">When it completed, if it did.</param>
/// <param name="LastRefusal">The closed refusal code it recorded, if any.</param>
/// <param name="FailureReason">The closed failure reason it recorded, if any.</param>
/// <param name="Artifact">
/// The request's expired artifact, on the <see cref="DataRequestInspectionState.ExpiredArtifact"/> list only.
/// </param>
public sealed record DataRequestInspectionItem(
    Guid RequestId, Guid TenantId, Guid? OrganizationId, string Kind, string Origin, string Status, DateTimeOffset CreatedAt,
    DateTimeOffset? ExecuteAt, DateTimeOffset? CompletedAt, string? LastRefusal, string? FailureReason,
    DataExportArtifactInspection? Artifact);

/// <summary>How an erasure completion ended.</summary>
public enum ErasureCompletionOutcome
{
    /// <summary>The audit rows were redacted, the request completed and the result row committed, together.</summary>
    Completed = 1,

    /// <summary>The request was already completed: its recorded result is returned and no second row is written.</summary>
    AlreadyCompleted,

    /// <summary>No request has that identifier.</summary>
    NotFound,

    /// <summary>The request is not an erasure.</summary>
    NotErasure,

    /// <summary>The erasure is not awaiting the privileged step; its stored status is returned.</summary>
    NotAwaiting,
}

/// <summary>The end of one erasure completion.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="TenantId">The request's tenant, when one was found.</param>
/// <param name="Status">The request's stored status after the call, when one was found.</param>
/// <param name="CompletedAt">When the request completed, for a completed request.</param>
/// <param name="Retained">
/// The request's recorded retained kinds and counts (ADR-0107 § 6) for a completed request: the tenant side's, with the
/// audit log's kinds set from the report.
/// </param>
/// <param name="Report">The redaction's closed report, for the call that completed the request; a repeat has none.</param>
public sealed record ErasureCompletionResult(
    ErasureCompletionOutcome Outcome, Guid? TenantId = null, string? Status = null, DateTimeOffset? CompletedAt = null,
    IReadOnlyDictionary<string, long>? Retained = null, AuditRedactionReport? Report = null);

/// <summary>
/// The confined owner of the operator's data-request acts (ADR-0107 § 8, ADR-0108): it consumes the operator's
/// admission in the platform scope, reads the request tables through the platform role's column grants and completes
/// an erasure with its audit redaction, its status change and its result row in one transaction.
/// </summary>
/// <remarks>
/// A malformed or foreign cursor answers null, which the route reports as a validation failure. A transient database
/// fault is thrown as <see cref="OperatorRecoveryUnavailableException"/>, with its commit outcome unknown when the
/// completion's commit was already sent; the scope's own refusals are thrown as the scope throws them.
/// </remarks>
public interface IDataRequestOperatorOperations
{
    /// <summary>Lists one page of the requested list, oldest due first; the scope's entry row is its audit.</summary>
    /// <exception cref="AuditWriteFailedException">The scope's entry row could not be written; nothing was read.</exception>
    /// <exception cref="OperatorRecoveryUnavailableException">The database could not be read; nothing changed.</exception>
    Task<InspectionPage<DataRequestInspectionItem>?> InspectAsync(
        PlatformAdmission admission, DataRequestInspectionQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes an erasure awaiting its privileged step: redacts the subject's audit rows, moves the request to
    /// <c>completed</c> and writes the MUST result row under the request's tenant, all on one transaction
    /// (ADR-0108 § 8).
    /// </summary>
    /// <exception cref="AuditWriteFailedException">A MUST row could not be written; nothing changed.</exception>
    /// <exception cref="OperatorRecoveryUnavailableException">
    /// The database could not finish, or the commit's outcome is unknown.
    /// </exception>
    Task<ErasureCompletionResult> CompleteErasureAsync(
        PlatformAdmission admission, Guid requestId, CancellationToken cancellationToken = default);
}
