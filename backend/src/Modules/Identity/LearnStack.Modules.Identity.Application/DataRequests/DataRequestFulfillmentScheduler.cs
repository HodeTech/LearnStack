using LearnStack.Modules.Identity.Application.Contracts.DataRequests;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Modules.Identity.Application.DataRequests;

/// <summary>
/// The four transactional definitions of the export workflow, one per purpose and scope group
/// (<see href="../../../../../docs/decisions/0107-data-request-fulfillment-and-account-closure.md">ADR-0107 § 2</see>).
/// </summary>
public static class DataExportJobKeys
{
    public static BackgroundJobKey<DataExportJobParameters> TenantExport { get; } = new("identity.data_export_tenant.v1");
    public static BackgroundJobKey<DataExportJobParameters> OrganizationExport { get; } = new("identity.data_export_organization.v1");
    public static BackgroundJobKey<DataExportPurgeParameters> TenantPurge { get; } = new("identity.data_export_purge_tenant.v1");
    public static BackgroundJobKey<DataExportPurgeParameters> OrganizationPurge { get; } = new("identity.data_export_purge_organization.v1");

    /// <summary>The export definition of a group.</summary>
    public static BackgroundJobKey<DataExportJobParameters> ExportFor(DataRequestScopeGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return group.IsTenantWide ? TenantExport : OrganizationExport;
    }
}

/// <summary>
/// The two transactional definitions of the tenant-side erasure, one per scope group
/// (<see href="../../../../../docs/decisions/0107-data-request-fulfillment-and-account-closure.md">ADR-0107 § 2, § 5</see>).
/// </summary>
public static class DataErasureJobKeys
{
    public static BackgroundJobKey<DataErasureJobParameters> TenantErasure { get; } = new("identity.data_erasure_tenant.v1");
    public static BackgroundJobKey<DataErasureJobParameters> OrganizationErasure { get; } = new("identity.data_erasure_organization.v1");

    /// <summary>The erasure definition of a group.</summary>
    public static BackgroundJobKey<DataErasureJobParameters> ErasureFor(DataRequestScopeGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return group.IsTenantWide ? TenantErasure : OrganizationErasure;
    }
}

/// <summary>
/// What a new request's fulfilment needs, as values rather than the aggregate: submitting a job writes no request, and
/// the aggregate write port stays the one the census counts.
/// </summary>
/// <param name="ExecuteAt">
/// When a scheduled request may execute; <see langword="null"/> for an export, which runs at once, and for a closure's
/// fan-out erasure, which is born <c>fulfilling</c>.
/// </param>
/// <param name="Origin">Who filed the request: the person, an administrator, or a closure's fan-out.</param>
public sealed record DataRequestFulfillment(Guid RequestId, DataRequestKind Kind, DataRequestStatus Status, DataRequestScopeGroup Group,
    DateTimeOffset? ExecuteAt, DataRequestOrigin Origin);

/// <summary>
/// Submits a data request's fulfilment work on the caller's own transaction
/// (<see href="../../../../../docs/decisions/0107-data-request-fulfillment-and-account-closure.md">ADR-0107 § 2</see>).
/// </summary>
/// <remarks>
/// The request's creation calls <see cref="SubmitAsync"/> inside the transaction that inserts the request, so the
/// row and its job commit together or not at all; the job's tenant, organization and producer come from that
/// transaction's trusted enrollment, never from this call.
/// </remarks>
public interface IDataRequestFulfillmentScheduler
{
    /// <summary>
    /// Submits the job of the request's kind and group: a requested export's at once, a scheduled erasure's at its
    /// execution time, the end of the cancellable window (ADR-0107 § 2), and a closure's fan-out erasure's at once, because
    /// it is born <c>fulfilling</c> and its job resumes it from there (ADR-0107 § 7). A scheduled closure is accepted and
    /// submits nothing: it has no job, because its execution is the operator's Begin (ADR-0107 § 7).
    /// </summary>
    /// <remarks>
    /// A fan-out erasure's job is submitted again when a repeated Begin finds the request still <c>fulfilling</c> with no
    /// job in flight; the job runs a <c>fulfilling</c> request past the participants its progress records, and a second job
    /// of a request the first one finished is a recorded no-op.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The request is not a <c>requested</c> export, a <c>scheduled</c> erasure or closure with its execution time, or a
    /// <c>fulfilling</c> fan-out erasure without one, of the announced group: a programming error, because the creation
    /// path decides the kind and the group before it submits.
    /// </exception>
    ValueTask SubmitAsync(DataRequestFulfillment fulfillment, CancellationToken cancellationToken);

    /// <summary>Schedules the purge of an artifact's chunks at <paramref name="dueAt"/>, its expiry (ADR-0107 § 4).</summary>
    ValueTask SchedulePurgeAsync(Guid artifactId, DateTimeOffset dueAt, CancellationToken cancellationToken);
}

/// <summary>The default <see cref="IDataRequestFulfillmentScheduler"/>, over the typed job scheduler.</summary>
public sealed class DataRequestFulfillmentScheduler(IBackgroundJobScheduler scheduler, ITenantContext tenant)
    : IDataRequestFulfillmentScheduler
{
    public async ValueTask SubmitAsync(DataRequestFulfillment fulfillment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fulfillment);
        var group = fulfillment.Group ?? throw new ArgumentException("A fulfilment names its group.", nameof(fulfillment));
        if (fulfillment.RequestId == Guid.Empty || fulfillment.RequestId.Version != 7
            || group.TenantId != tenant.TenantId || group.OrganizationId != tenant.OrganizationId)
        {
            throw new InvalidOperationException("A fulfilment job is submitted only for a request of the announced group.");
        }

        switch (fulfillment)
        {
            case { Kind: DataRequestKind.Export, Status: DataRequestStatus.Requested, ExecuteAt: null }:
                await scheduler.EnqueueAsync(DataExportJobKeys.ExportFor(group), new DataExportJobParameters(fulfillment.RequestId),
                    cancellationToken).ConfigureAwait(false);
                return;
            case
            {
                Kind: DataRequestKind.Erasure, Status: DataRequestStatus.Scheduled, ExecuteAt: { } due,
                Origin: not DataRequestOrigin.ClosureFanout
            } when due != default:
                // ADR-0107 § 2: due when the window ends, so no recurrence is introduced; the runtime has no cancel call,
                // and a job whose request was cancelled meanwhile completes as a recorded no-op.
                await scheduler.ScheduleAsync(DataErasureJobKeys.ErasureFor(group), new DataErasureJobParameters(fulfillment.RequestId),
                    due, cancellationToken).ConfigureAwait(false);
                return;
            case { Kind: DataRequestKind.Closure, Status: DataRequestStatus.Scheduled, ExecuteAt: { } begins } when begins != default:
                // ADR-0107 § 7: a closure is executed by the operator's Begin, an act in the platform scope that no job
                // holds the authority for; filing one submits nothing.
                return;
            case { Kind: DataRequestKind.Erasure, Status: DataRequestStatus.Fulfilling, ExecuteAt: null, Origin: DataRequestOrigin.ClosureFanout }:
                // ADR-0107 § 7: a closure's fan-out erasure has no window; its job runs now, on the producer transaction
                // that created or adopted the request, so the two commit together or not at all.
                await scheduler.EnqueueAsync(DataErasureJobKeys.ErasureFor(group), new DataErasureJobParameters(fulfillment.RequestId),
                    cancellationToken).ConfigureAwait(false);
                return;
            default:
                throw new InvalidOperationException(
                    "Only a requested export, a scheduled erasure or closure, or a closure's fulfilling fan-out erasure, of the announced group is a fulfilment.");
        }
    }

    public async ValueTask SchedulePurgeAsync(Guid artifactId, DateTimeOffset dueAt, CancellationToken cancellationToken)
    {
        if (artifactId == Guid.Empty || artifactId.Version != 7 || dueAt == default)
        {
            throw new InvalidOperationException("A purge is scheduled for an artifact at its expiry.");
        }

        var key = tenant.OrganizationId is null ? DataExportJobKeys.TenantPurge : DataExportJobKeys.OrganizationPurge;
        await scheduler.ScheduleAsync(key, new DataExportPurgeParameters(artifactId), dueAt, cancellationToken)
            .ConfigureAwait(false);
    }
}
