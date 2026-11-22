using LearnStack.Infrastructure.Jobs;
using LearnStack.Modules.Identity.Application.Contracts.DataRequests;
using LearnStack.Modules.Identity.Application.DataRequests;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Npgsql;

namespace LearnStack.Modules.Identity.Infrastructure.DataRequests;

/// <summary>
/// Reads a request's fulfilment job phase from <c>background_jobs</c> on the ambient transaction, the way the
/// invitation status reader reads its delivery job, so a dead job is seen without any hook running in it
/// (<see href="../../../../../docs/decisions/0106-subject-data-requests-authority-and-status.md">ADR-0106 § 8</see>).
/// </summary>
/// <remarks>
/// The request row carries no job identity, so the job is found by its exact route: the announced tenant and
/// organization, the export or erasure definition of that group, the transactional profile and the canonical
/// serialization of the request's own identifier, newest first. Nothing a caller supplies selects it.
/// </remarks>
public sealed class DataRequestJobPhaseReader(IUnitOfWork unit, ITenantContext tenant) : IDataRequestJobPhaseReader
{
    /// <summary>
    /// One request's newest job by its exact route. A constant so the plan test explains exactly this statement; the
    /// index it seeks is <c>ix_background_jobs_definition</c>.
    /// </summary>
    internal const string SingleStatement = """
        SELECT phase, quarantine_reason IS NOT NULL OR terminal_reason IS NOT NULL OR dead_lettered_at IS NOT NULL
        FROM public.background_jobs
        WHERE tenant_id = @tenant AND organization_id IS NOT DISTINCT FROM @organization
          AND definition_key = @definition AND profile = 'transactional' AND parameters = @parameters
        ORDER BY created_at DESC, id DESC
        LIMIT 1
        """;

    /// <summary>A page's newest job per request, by the same exact route, in one statement; planned like the single read.</summary>
    internal const string PageStatement = """
        SELECT DISTINCT ON (parameters) parameters, phase,
            quarantine_reason IS NOT NULL OR terminal_reason IS NOT NULL OR dead_lettered_at IS NOT NULL
        FROM public.background_jobs
        WHERE tenant_id = @tenant AND organization_id IS NOT DISTINCT FROM @organization
          AND definition_key = @definition AND profile = 'transactional' AND parameters = ANY(@parameters)
        ORDER BY parameters, created_at DESC, id DESC
        """;

    public async Task<DataRequestJobPhase> ReadAsync(DataSubjectRequestId requestId, DataRequestKind kind, CancellationToken cancellationToken)
    {
        RequireOwner();
        if (Route(kind) is not { } definition)
        {
            return DataRequestJobPhase.Unavailable;
        }

        await using var command = DataRequestSql.Command(unit, tenant, SingleStatement);
        command.Parameters.AddWithValue("definition", definition);
        command.Parameters.AddWithValue("parameters", Parameters(requestId, kind));
        using var budget = DataRequestSql.Budget(cancellationToken);
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
        IReadOnlyCollection<DataSubjectRequestId> requestIds, DataRequestKind kind, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestIds);
        RequireOwner();
        if (requestIds.Count > 101)
        {
            throw new InvalidOperationException("A data request job phase page reads at most 101 requests.");
        }

        var phases = requestIds.Distinct().ToDictionary(id => id, _ => DataRequestJobPhase.Unavailable);
        if (phases.Count == 0 || Route(kind) is not { } definition)
        {
            return phases;
        }

        var routed = phases.Keys.ToDictionary(id => Parameters(id, kind), id => id, StringComparer.Ordinal);

        // One statement for the page, newest job per request, by the same exact route as the single read.
        await using var command = DataRequestSql.Command(unit, tenant, PageStatement);
        command.Parameters.AddWithValue("definition", definition);
        command.Parameters.AddWithValue("parameters", routed.Keys.ToArray());
        using var budget = DataRequestSql.Budget(cancellationToken);
        try
        {
            await using var row = await command.ExecuteReaderAsync(budget.Token).ConfigureAwait(false);
            while (await row.ReadAsync(budget.Token).ConfigureAwait(false))
            {
                if (routed.TryGetValue(row.GetString(0), out var id))
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

    /// <summary>The definition key of the kind's fulfilment job in the announced group; a closure has none in this step.</summary>
    private string? Route(DataRequestKind kind)
    {
        var group = new DataRequestScopeGroup(tenant.TenantId, tenant.OrganizationId);
        return kind switch
        {
            DataRequestKind.Export => DataExportJobKeys.ExportFor(group).Value,
            DataRequestKind.Erasure => DataErasureJobKeys.ErasureFor(group).Value,
            _ => null,
        };
    }

    /// <summary>The canonical stored parameters of the request's own fulfilment job.</summary>
    private static string Parameters(DataSubjectRequestId requestId, DataRequestKind kind) => kind == DataRequestKind.Erasure
        ? BackgroundJobPayloadContract.Serialize(new DataErasureJobParameters(requestId.Value), typeof(DataErasureJobParameters),
            BackgroundJobPayloadKind.Parameters)
        : BackgroundJobPayloadContract.Serialize(new DataExportJobParameters(requestId.Value), typeof(DataExportJobParameters),
            BackgroundJobPayloadKind.Parameters);

    private void RequireOwner()
    {
        if (!DataRequestOwner.IsAnnounced(unit, tenant, writable: false))
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
