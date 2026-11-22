using System.Diagnostics;
using LearnStack.Modules.Identity.Application.Contracts.DataRequests;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;
using Microsoft.Extensions.Logging;

namespace LearnStack.Modules.Identity.Application.DataRequests;

public sealed record RunTenantDataExportCommand(DataExportJobParameters Parameters) : IRequest<Result<None>>;
public sealed record RunOrganizationDataExportCommand(DataExportJobParameters Parameters) : IRequest<Result<None>>;
public sealed record PurgeTenantDataExportCommand(DataExportPurgeParameters Parameters) : IRequest<Result<None>>;
public sealed record PurgeOrganizationDataExportCommand(DataExportPurgeParameters Parameters) : IRequest<Result<None>>;

/// <summary>
/// The four transactional roots of the export workflow
/// (<see href="../../../../../docs/decisions/0107-data-request-fulfillment-and-account-closure.md">ADR-0107 § 2–§ 4</see>):
/// scope-specific declarations sharing one mechanic, run as the system actor under the trusted job policy on the
/// job's own tenant transaction, never in the platform scope.
/// </summary>
/// <remarks>
/// <para>
/// <b>One transaction.</b> The transactional runtime owns exactly one transaction per root, so the export takes
/// <c>requested → fulfilling</c> and then <c>fulfilling → completed</c> (or <c>failed</c>) as separate UPDATEs of
/// that one transaction, each one arrow and one row-version step as the request guard requires. A failure rolls
/// every write back, the request is <c>requested</c> again and the runtime's retry re-runs the whole export, so a
/// retried or resumed job never stores twice; a commit stores the request, its artifact, every chunk and the
/// purge job together.
/// </para>
/// <para>
/// <b>Recorded no-ops.</b> The runtime has no cancel call, so a job whose request is missing, of another kind,
/// cancelled or already terminal completes without changing anything, and its MUST row records that it ran.
/// </para>
/// <para>
/// <b>Fences.</b> The run holds the Identity tenant fence shared from its first read, which the request guard
/// requires of every request write and which orders it against a tenant erasure taking the same fence exclusively
/// (ADR-0107 § 5): an erasure either cancels a still requested export first, or finds it completed and revokes
/// its artifact.
/// </para>
/// </remarks>
/// <summary>
/// The four transactional roots of the export workflow
/// (<see href="../../../../../docs/decisions/0107-data-request-fulfillment-and-account-closure.md">ADR-0107 § 2–§ 4</see>):
/// scope-specific declarations sharing one mechanic, run as the system actor under the trusted job policy on the
/// job's own tenant transaction, never in the platform scope.
/// </summary>
/// <remarks>
/// <para>
/// <b>One transaction.</b> The transactional runtime owns exactly one transaction per root, so the export takes
/// <c>requested → fulfilling</c> and then <c>fulfilling → completed</c> (or <c>failed</c>) as separate UPDATEs of
/// that one transaction, each one arrow and one row-version step as the request guard requires. A failure rolls
/// every write back, the request is <c>requested</c> again and the runtime's retry re-runs the whole export, so a
/// retried or resumed job never stores twice; a commit stores the request, its artifact, every chunk and the
/// purge job together. A durable <c>fulfilling</c> export therefore never exists: no committed state lies between
/// <c>requested</c> and the terminal one, and there is no partial run to resume.
/// </para>
/// <para>
/// <b>Recorded no-ops.</b> The runtime has no cancel call, so a job whose request is missing, of another kind,
/// cancelled or already terminal completes without changing anything, and its MUST row records that it ran.
/// </para>
/// <para>
/// <b>Fences.</b> The run holds the Identity tenant fence shared from its first read, which the request guard
/// requires of every request write and which orders it against a tenant erasure taking the same fence exclusively
/// (ADR-0107 § 5): an erasure either cancels a still requested export first, or finds it completed and revokes
/// its artifact.
/// </para>
/// <para>
/// <b>Signals.</b> Each run records <see cref="DataExportTelemetry"/> and one log line carrying the request or
/// artifact identifier, the kind, the outcome, the byte and chunk counts and the duration; never a subject, a
/// group, a participant's content or another person's identifier.
/// </para>
/// </remarks>
public sealed partial class DataExportJobHandlers(IDataRequestStore store, IIdentityMutationLocks locks, ITenantContext tenant,
    IAuditSubject audit, IClock clock, IGuidFactory ids, IDataExportBundleBuilder bundles, IUserDataParticipantRegistry participants,
    IDataExportArtifactStore artifacts, IDataRequestFulfillmentScheduler scheduler, ILogger<DataExportJobHandlers> logger)
    : IRequestHandler<RunTenantDataExportCommand, Result<None>>,
      IRequestHandler<RunOrganizationDataExportCommand, Result<None>>,
      IRequestHandler<PurgeTenantDataExportCommand, Result<None>>,
      IRequestHandler<PurgeOrganizationDataExportCommand, Result<None>>
{
    private const string ExportKind = "export";

    public Task<Result<None>> Handle(RunTenantDataExportCommand request, CancellationToken cancellationToken) =>
        ObserveExportAsync(request.Parameters, cancellationToken);

    public Task<Result<None>> Handle(RunOrganizationDataExportCommand request, CancellationToken cancellationToken) =>
        ObserveExportAsync(request.Parameters, cancellationToken);

    public Task<Result<None>> Handle(PurgeTenantDataExportCommand request, CancellationToken cancellationToken) =>
        ObservePurgeAsync(request.Parameters, cancellationToken);

    public Task<Result<None>> Handle(PurgeOrganizationDataExportCommand request, CancellationToken cancellationToken) =>
        ObservePurgeAsync(request.Parameters, cancellationToken);

    /// <summary>Runs the export and records its outcome; a failure is recorded and rethrown unchanged.</summary>
    private async Task<Result<None>> ObserveExportAsync(DataExportJobParameters parameters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var started = Stopwatch.GetTimestamp();
        ExportRun run;
        try
        {
            run = await ExportAsync(parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (RecordFailure(parameters.RequestId, started, failure))
        {
            throw; // Unreachable: the filter records and declines, so the exception propagates unwound.
        }

        var duration = Stopwatch.GetElapsedTime(started);
        DataExportTelemetry.Export(run.Outcome, duration, run.ByteCount, run.TruncatedCollections);
        switch (run.Outcome)
        {
            case DataExportOutcome.Completed:
                LogExportCompleted(logger, parameters.RequestId, ExportKind, run.ByteCount ?? 0, run.ChunkCount ?? 0,
                    run.TruncatedCollections, duration.TotalMilliseconds);
                break;
            case DataExportOutcome.FailedTooLarge:
                LogExportTooLarge(logger, parameters.RequestId, ExportKind, duration.TotalMilliseconds);
                break;
            default:
                LogExportNoOp(logger, parameters.RequestId, ExportKind, run.NoOpReason ?? "unknown", duration.TotalMilliseconds);
                break;
        }

        return Done();
    }

    private bool RecordFailure(Guid requestId, long started, Exception failure)
    {
        var duration = Stopwatch.GetElapsedTime(started);
        DataExportTelemetry.Export(DataExportOutcome.Failed, duration);
        // The exception's type only: a message or a provider's detail is not a closed value.
        LogExportFailed(logger, requestId, ExportKind, failure.GetType().Name, duration.TotalMilliseconds);
        return false;
    }

    private async Task<ExportRun> ExportAsync(DataExportJobParameters parameters, CancellationToken cancellationToken)
    {
        _ = store.RequireCurrentJob();
        if (parameters.RequestId == Guid.Empty || parameters.RequestId.Version != 7)
        {
            return ExportRun.NoOp("invalid_parameters");
        }

        await locks.AcquireAsync(new IdentityMutationLockPlan([], tenant.TenantId, IdentityTenantLockMode.Shared), cancellationToken)
            .ConfigureAwait(false);
        var request = await store.FindLockedAsync(DataSubjectRequestId.From(parameters.RequestId), cancellationToken)
            .ConfigureAwait(false);
        if (request is null)
        {
            return ExportRun.NoOp("missing");
        }

        audit.Designate(request);

        // Only a requested export runs: a durable fulfilling export never exists (see the remarks), and every other
        // status is cancelled or terminal.
        if (request.Kind != DataRequestKind.Export)
        {
            return ExportRun.NoOp("not_export");
        }

        if (request.Status != DataRequestStatus.Requested)
        {
            return ExportRun.NoOp("not_requested");
        }

        var now = clock.UtcNow;
        var actor = tenant.UserId ?? UserId.SystemActor;
        Require(request.StartFulfilling(now, actor));
        await store.SaveAsync(request, cancellationToken).ConfigureAwait(false);

        var subject = new UserDataSubject(request.Id.Value, request.Kind, request.Origin, request.SubjectUserId,
            request.Group, request.SnapshotMembershipIds.Select(membership => membership.Value));
        var built = await bundles.BuildAsync(subject, now, cancellationToken).ConfigureAwait(false);
        if (built.IsTooLarge)
        {
            Require(request.Fail(DataRequestFailureReason.ExportTooLarge, now, actor));
            await store.SaveAsync(request, cancellationToken).ConfigureAwait(false);
            return new ExportRun(DataExportOutcome.FailedTooLarge, null, null, 0, null);
        }

        long byteCount;
        int chunkCount;
        int truncated;
        using (var bundle = built.Bundle!)
        {
            (byteCount, chunkCount, truncated) = (bundle.ByteCount, bundle.ChunkCount, bundle.Truncations.Length);
            await StoreAsync(request, bundle, now, cancellationToken).ConfigureAwait(false);
        }

        foreach (var participant in participants.Participants)
        {
            var before = request.Version;
            Require(request.RecordParticipantCompleted(participant.Key, now, actor));
            if (request.Version != before)
            {
                await store.SaveAsync(request, cancellationToken).ConfigureAwait(false);
            }
        }

        var retained = await DescribeRetainedAsync(subject, cancellationToken).ConfigureAwait(false);
        Require(request.Complete(retained, now, actor));
        await store.SaveAsync(request, cancellationToken).ConfigureAwait(false);
        return new ExportRun(DataExportOutcome.Completed, byteCount, chunkCount, truncated, null);
    }

    /// <summary>
    /// Revokes the subject's earlier available artifact in the group, then stores the new one and schedules its purge
    /// at its expiry: a subject holds at most one available artifact per group (ADR-0107 § 4).
    /// </summary>
    private async Task StoreAsync(DataSubjectRequest request, IDataExportBundle bundle, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var earlier in await store.FindAvailableArtifactsAsync(request.SubjectUserId, cancellationToken).ConfigureAwait(false))
        {
            var outcome = await artifacts.PurgeAsync(new DataExportArtifactLocator(earlier.Value, request.Group),
                DataExportArtifactStatus.Revoked, cancellationToken).ConfigureAwait(false);
            if (outcome is not (DataExportArtifactStatus.Revoked or DataExportArtifactStatus.Purged))
            {
                throw new InvalidOperationException("An earlier export artifact could not be withdrawn.");
            }
        }

        var artifactId = ids.NewUuidV7();
        if (artifactId == Guid.Empty || artifactId.Version != 7)
        {
            throw new InvalidOperationException("The identity factory must return a UUIDv7.");
        }

        var stored = await artifacts.PutAsync(new DataExportArtifactPut(artifactId, request.Id.Value, request.SubjectUserId,
                request.Group, bundle.ByteCount, bundle.Truncations), bundle.ReadChunksAsync(cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        if (stored.ArtifactId != artifactId || stored.RequestId != request.Id.Value || stored.ExpiresAt <= now)
        {
            throw new InvalidOperationException("The stored export artifact does not match its request.");
        }

        await scheduler.SchedulePurgeAsync(artifactId, stored.ExpiresAt, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Every participant's retained references for the subject and group, summed per closed kind (ADR-0107 § 6).</summary>
    private async Task<IReadOnlyCollection<RetainedReferenceCount>> DescribeRetainedAsync(UserDataSubject subject,
        CancellationToken cancellationToken)
    {
        var totals = new SortedDictionary<RetainedReferenceKind, long>();
        foreach (var participant in participants.Participants)
        {
            foreach (var retained in await participant.DescribeRetainedAsync(subject, cancellationToken).ConfigureAwait(false))
            {
                totals[retained.Kind] = checked(totals.GetValueOrDefault(retained.Kind) + retained.Count);
            }
        }

        return [.. totals.Select(total => new RetainedReferenceCount(total.Key, total.Value))];
    }

    private async Task<Result<None>> ObservePurgeAsync(DataExportPurgeParameters parameters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var started = Stopwatch.GetTimestamp();
        var outcome = await PurgeAsync(parameters, cancellationToken).ConfigureAwait(false);
        DataExportTelemetry.Purge(outcome);
        var duration = Stopwatch.GetElapsedTime(started);
        if (outcome == DataExportPurgeOutcome.NotDue)
        {
            LogPurgeNotDue(logger, parameters.ArtifactId, duration.TotalMilliseconds);

            // Still available means the database clock has not reached the expiry the job was scheduled for; the
            // runtime retries rather than leave the chunks behind.
            throw new InvalidOperationException("The export artifact is not yet due for purge.");
        }

        var label = DataExportTelemetry.Label(outcome);
        LogPurged(logger, parameters.ArtifactId, label, duration.TotalMilliseconds);
        return Done();
    }

    private async Task<DataExportPurgeOutcome> PurgeAsync(DataExportPurgeParameters parameters, CancellationToken cancellationToken)
    {
        _ = store.RequireCurrentJob();
        if (parameters.ArtifactId == Guid.Empty || parameters.ArtifactId.Version != 7)
        {
            return DataExportPurgeOutcome.NoOp;
        }

        var group = new DataRequestScopeGroup(tenant.TenantId, tenant.OrganizationId);
        var outcome = await artifacts.PurgeAsync(new DataExportArtifactLocator(parameters.ArtifactId, group),
            DataExportArtifactStatus.Purged, cancellationToken).ConfigureAwait(false);

        // Absent or revoked: nothing is left to purge; purged: no chunk is left, by this run or an earlier one.
        return outcome switch
        {
            DataExportArtifactStatus.Available => DataExportPurgeOutcome.NotDue,
            DataExportArtifactStatus.Purged => DataExportPurgeOutcome.Purged,
            _ => DataExportPurgeOutcome.NoOp,
        };
    }

    /// <summary>A transition refused here is a programming error: every precondition was checked under the row lock.</summary>
    private static void Require(Result<None> transition)
    {
        if (transition.IsFailure)
        {
            throw new InvalidOperationException("A data request transition that its own preconditions admitted was refused.");
        }
    }

    private static Result<None> Done() => Result<None>.Ok(None.Value);

    [LoggerMessage(EventId = 7501, Level = LogLevel.Information,
        Message = "Data export {RequestId} ({Kind}) completed: {ByteCount} bytes in {ChunkCount} chunks, {TruncatedCollections} truncated collections, in {DurationMilliseconds} ms.")]
    private static partial void LogExportCompleted(ILogger logger, Guid requestId, string kind, long byteCount, int chunkCount,
        int truncatedCollections, double durationMilliseconds);

    [LoggerMessage(EventId = 7502, Level = LogLevel.Information,
        Message = "Data export job for request {RequestId} ({Kind}) completed as a recorded no-op ({Reason}) in {DurationMilliseconds} ms.")]
    private static partial void LogExportNoOp(ILogger logger, Guid requestId, string kind, string reason, double durationMilliseconds);

    [LoggerMessage(EventId = 7503, Level = LogLevel.Warning,
        Message = "Data export {RequestId} ({Kind}) failed with export_too_large after {DurationMilliseconds} ms; nothing was stored.")]
    private static partial void LogExportTooLarge(ILogger logger, Guid requestId, string kind, double durationMilliseconds);

    [LoggerMessage(EventId = 7504, Level = LogLevel.Warning,
        Message = "Data export {RequestId} ({Kind}) attempt failed ({FailureType}) after {DurationMilliseconds} ms; its transaction rolls back for the runtime's retry.")]
    private static partial void LogExportFailed(ILogger logger, Guid requestId, string kind, string failureType, double durationMilliseconds);

    [LoggerMessage(EventId = 7505, Level = LogLevel.Information,
        Message = "Data export artifact {ArtifactId} purge finished ({Outcome}) in {DurationMilliseconds} ms.")]
    private static partial void LogPurged(ILogger logger, Guid artifactId, string outcome, double durationMilliseconds);

    [LoggerMessage(EventId = 7506, Level = LogLevel.Warning,
        Message = "Data export artifact {ArtifactId} is not yet due for purge after {DurationMilliseconds} ms; the runtime retries.")]
    private static partial void LogPurgeNotDue(ILogger logger, Guid artifactId, double durationMilliseconds);

    /// <summary>What one export run reached: the outcome, and for a stored bundle its size, chunks and truncations.</summary>
    private sealed record ExportRun(DataExportOutcome Outcome, long? ByteCount, int? ChunkCount, int TruncatedCollections, string? NoOpReason)
    {
        public static ExportRun NoOp(string reason) => new(DataExportOutcome.NoOp, null, null, 0, reason);
    }
}
