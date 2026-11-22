using LearnStack.Infrastructure.Jobs;
using LearnStack.Modules.Identity.Application.Contracts.DataRequests;
using LearnStack.Modules.Identity.Application.DataRequests;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Modules.Identity.Infrastructure.DataRequests;

/// <summary>
/// Reads a request's fulfilment job phase from <c>background_jobs</c> on the ambient transaction, the way the
/// invitation status reader reads its delivery job, so a dead job is seen without any hook running in it
/// (<see href="../../../../../docs/decisions/0106-subject-data-requests-authority-and-status.md">ADR-0106 § 8</see>).
/// </summary>
/// <remarks>
/// The request row carries no job identity, so the job is found by its exact route: the announced tenant and
/// organization, the export definition of that group, the transactional profile and the canonical serialization
/// of the request's own identifier, newest first. Nothing a caller supplies selects it.
/// </remarks>
public sealed class DataRequestJobPhaseReader(IUnitOfWork unit, ITenantContext tenant) : IDataRequestJobPhaseReader
{
    public async Task<DataRequestJobPhase> ReadAsync(DataSubjectRequestId requestId, DataRequestKind kind, CancellationToken cancellationToken)
    {
        RequireOwner();
        if (kind != DataRequestKind.Export)
        {
            return DataRequestJobPhase.Unavailable;
        }

        var parameters = BackgroundJobPayloadContract.Serialize(new DataExportJobParameters(requestId.Value),
            typeof(DataExportJobParameters), BackgroundJobPayloadKind.Parameters);
        await using var command = new NpgsqlCommand("""
            SELECT phase, quarantine_reason IS NOT NULL OR terminal_reason IS NOT NULL OR dead_lettered_at IS NOT NULL
            FROM public.background_jobs
            WHERE tenant_id = @tenant AND organization_id IS NOT DISTINCT FROM @organization
              AND definition_key = @definition AND profile = 'transactional' AND parameters = @parameters
            ORDER BY created_at DESC, id DESC
            LIMIT 1
            """, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("tenant", tenant.TenantId.Value);
        command.Parameters.AddWithValue("organization", NpgsqlDbType.Uuid, (object?)tenant.OrganizationId?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("definition",
            DataExportJobKeys.ExportFor(new DataRequestScopeGroup(tenant.TenantId, tenant.OrganizationId)).Value);
        command.Parameters.AddWithValue("parameters", parameters);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await using var row = await command.ExecuteReaderAsync(budget.Token).ConfigureAwait(false);
            return await row.ReadAsync(budget.Token).ConfigureAwait(false)
                ? Classify(row.GetString(0), row.GetBoolean(1))
                : DataRequestJobPhase.Unavailable;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && budget.IsCancellationRequested)
        {
            unit.MarkRollbackOnly();
            throw new InfrastructureException("Data request job phase unavailable.");
        }
        catch (NpgsqlException)
        {
            unit.MarkRollbackOnly();
            throw new InfrastructureException("Data request job phase unavailable.");
        }
    }

    public async Task<IReadOnlyDictionary<DataSubjectRequestId, DataRequestJobPhase>> ReadManyAsync(
        IReadOnlyCollection<DataSubjectRequestId> exportRequestIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exportRequestIds);
        RequireOwner();
        if (exportRequestIds.Count > 101)
        {
            throw new InvalidOperationException("A data request job phase page reads at most 101 requests.");
        }

        var phases = exportRequestIds.Distinct().ToDictionary(id => id, _ => DataRequestJobPhase.Unavailable);
        var exports = phases.Keys
            .ToDictionary(id => BackgroundJobPayloadContract.Serialize(new DataExportJobParameters(id.Value),
                typeof(DataExportJobParameters), BackgroundJobPayloadKind.Parameters), id => id, StringComparer.Ordinal);
        if (exports.Count == 0)
        {
            return phases;
        }

        // One statement for the page, newest job per request, by the same exact route as the single read.
        await using var command = new NpgsqlCommand("""
            SELECT DISTINCT ON (parameters) parameters, phase,
                quarantine_reason IS NOT NULL OR terminal_reason IS NOT NULL OR dead_lettered_at IS NOT NULL
            FROM public.background_jobs
            WHERE tenant_id = @tenant AND organization_id IS NOT DISTINCT FROM @organization
              AND definition_key = @definition AND profile = 'transactional' AND parameters = ANY(@parameters)
            ORDER BY parameters, created_at DESC, id DESC
            """, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("tenant", tenant.TenantId.Value);
        command.Parameters.AddWithValue("organization", NpgsqlDbType.Uuid, (object?)tenant.OrganizationId?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("definition",
            DataExportJobKeys.ExportFor(new DataRequestScopeGroup(tenant.TenantId, tenant.OrganizationId)).Value);
        command.Parameters.AddWithValue("parameters", exports.Keys.ToArray());
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await using var row = await command.ExecuteReaderAsync(budget.Token).ConfigureAwait(false);
            while (await row.ReadAsync(budget.Token).ConfigureAwait(false))
            {
                if (exports.TryGetValue(row.GetString(0), out var id))
                {
                    phases[id] = Classify(row.GetString(1), row.GetBoolean(2));
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && budget.IsCancellationRequested)
        {
            unit.MarkRollbackOnly();
            throw new InfrastructureException("Data request job phase unavailable.");
        }
        catch (NpgsqlException)
        {
            unit.MarkRollbackOnly();
            throw new InfrastructureException("Data request job phase unavailable.");
        }

        return phases;
    }

    private void RequireOwner()
    {
        if (!unit.HasActiveTransaction || unit.IsRollbackOnly
            || unit.GetTenantContextAnnouncement(unit.Transaction) is not { } announcement
            || announcement.TenantId != tenant.TenantId || announcement.OrganizationId != tenant.OrganizationId)
        {
            throw new InvalidOperationException("A data request job phase requires its existing announced owner.");
        }
    }

    /// <summary>Quarantine, a terminal reason or a dead letter is dead; <c>completed</c> without one is done.</summary>
    public static DataRequestJobPhase Classify(string phase, bool failed) => (phase, failed) switch
    {
        (_, true) or ("terminal_pending" or "quarantined", _) => DataRequestJobPhase.Dead,
        ("completed", false) => DataRequestJobPhase.Completed,
        _ => DataRequestJobPhase.Pending,
    };
}
