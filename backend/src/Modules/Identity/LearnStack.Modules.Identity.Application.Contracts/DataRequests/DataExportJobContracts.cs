using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Modules.Identity.Application.Contracts.DataRequests;

/// <summary>
/// The export job's only input: the request it fulfils
/// (<see href="../../../../../docs/decisions/0107-data-request-fulfillment-and-account-closure.md">ADR-0107 § 2</see>).
/// The group, the subject and the snapshot are read from the request row, never from the job.
/// </summary>
public sealed record DataExportJobParameters(Guid RequestId) : IBackgroundJobParameters;

/// <summary>The delayed purge job's only input: the artifact whose chunks expire (ADR-0107 § 2, § 4).</summary>
public sealed record DataExportPurgeParameters(Guid ArtifactId) : IBackgroundJobParameters;
