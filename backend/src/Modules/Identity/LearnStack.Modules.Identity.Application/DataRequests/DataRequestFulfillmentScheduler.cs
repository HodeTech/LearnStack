using LearnStack.Modules.Identity.Application.Contracts.DataRequests;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Modules.Identity.Application.DataRequests;

/// <summary>
/// The four transactional definitions of the export workflow, one per purpose and scope group
/// (<see href="../../../../../docs/decisions/0107-data-request-fulfillment-and-account-closure.md">ADR-0107 § 2</see>).
/// The erasure definitions are Step 2's.
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
    /// <summary>Submits the job of the request's kind and group; only an <c>export</c> has one in this step.</summary>
    /// <exception cref="InvalidOperationException">
    /// The request is not a <c>requested</c> export of the announced group: a programming error, because the creation
    /// path decides the kind and the group before it submits.
    /// </exception>
    ValueTask SubmitAsync(DataSubjectRequest request, CancellationToken cancellationToken);

    /// <summary>Schedules the purge of an artifact's chunks at <paramref name="dueAt"/>, its expiry (ADR-0107 § 4).</summary>
    ValueTask SchedulePurgeAsync(DataExportArtifactId artifactId, DateTimeOffset dueAt, CancellationToken cancellationToken);
}

/// <summary>The default <see cref="IDataRequestFulfillmentScheduler"/>, over the typed job scheduler.</summary>
public sealed class DataRequestFulfillmentScheduler(IBackgroundJobScheduler scheduler, ITenantContext tenant)
    : IDataRequestFulfillmentScheduler
{
    public async ValueTask SubmitAsync(DataSubjectRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Kind != DataRequestKind.Export || request.Status != DataRequestStatus.Requested
            || request.TenantId != tenant.TenantId || request.OrganizationId != tenant.OrganizationId)
        {
            throw new InvalidOperationException("Only a requested export of the announced group has a fulfilment job in this step.");
        }

        await scheduler.EnqueueAsync(DataExportJobKeys.ExportFor(request.Group),
            new DataExportJobParameters(request.Id.Value), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SchedulePurgeAsync(DataExportArtifactId artifactId, DateTimeOffset dueAt, CancellationToken cancellationToken)
    {
        if (artifactId.Value == Guid.Empty || artifactId.Value.Version != 7 || dueAt == default)
        {
            throw new InvalidOperationException("A purge is scheduled for an artifact at its expiry.");
        }

        var key = tenant.OrganizationId is null ? DataExportJobKeys.TenantPurge : DataExportJobKeys.OrganizationPurge;
        await scheduler.ScheduleAsync(key, new DataExportPurgeParameters(artifactId.Value), dueAt, cancellationToken)
            .ConfigureAwait(false);
    }
}
