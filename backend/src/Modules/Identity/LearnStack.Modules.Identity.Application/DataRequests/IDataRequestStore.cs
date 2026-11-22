using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Modules.Identity.Application.DataRequests;

/// <summary>
/// The data request rows of the ambient group, read and written on the ambient tenant transaction as
/// <c>learnstack_app</c> (<see href="../../../../../docs/decisions/0106-subject-data-requests-authority-and-status.md">ADR-0106 § 8</see>).
/// </summary>
/// <remarks>
/// Every read is confined to the announced tenant and organization, by the predicate and by the row policy, so a
/// request or artifact of another group is absent rather than refused. Writes go through tracked aggregates only:
/// each transition is saved on its own, because the request and artifact guards admit exactly one arrow and one
/// row-version step per UPDATE.
/// </remarks>
public interface IDataRequestStore
{
    /// <summary>The executing data-request job: refuses a caller that is not one of the module's job roots.</summary>
    BackgroundJobId RequireCurrentJob();

    /// <summary>The request in the ambient group, locked for update; <see langword="null"/> when absent.</summary>
    Task<DataSubjectRequest?> FindLockedAsync(DataSubjectRequestId requestId, CancellationToken cancellationToken);

    /// <summary>Whether the request already has its artifact (at most one per request).</summary>
    Task<DataExportArtifactId?> FindArtifactOfRequestAsync(DataSubjectRequestId requestId, CancellationToken cancellationToken);

    /// <summary>The subject's artifacts in the ambient group that are still stored <c>available</c>.</summary>
    Task<IReadOnlyList<DataExportArtifactId>> FindAvailableArtifactsAsync(UserId subject, CancellationToken cancellationToken);

    /// <summary>Saves the changes of a request this scope loaded and tracks.</summary>
    Task SaveAsync(DataSubjectRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Where a data request's fulfilment job stands, read from the job's own row
/// (<see href="../../../../../docs/decisions/0106-subject-data-requests-authority-and-status.md">ADR-0106 § 8</see>).
/// </summary>
public enum DataRequestJobPhase
{
    /// <summary>No fulfilment job of the request is visible in its group.</summary>
    Unavailable = 1,

    /// <summary>Queued, running or retrying: the job may still finish.</summary>
    Pending = 2,

    /// <summary>The job finished with its business outcome committed.</summary>
    Completed = 3,

    /// <summary>Quarantined or dead-lettered: the job ends without a business outcome and runs no hook.</summary>
    Dead = 4,
}

/// <summary>
/// Reads the phase of a request's fulfilment job without writing anything, so a receipt can show <c>failed</c>
/// for a dead job before the reconciler has stored it.
/// </summary>
public interface IDataRequestJobPhaseReader
{
    Task<DataRequestJobPhase> ReadAsync(DataSubjectRequest request, CancellationToken cancellationToken);
}
