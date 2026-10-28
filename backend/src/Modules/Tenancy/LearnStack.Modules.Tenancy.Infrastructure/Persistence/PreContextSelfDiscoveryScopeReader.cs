using LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Modules.Tenancy.Infrastructure.Persistence;

/// <summary>Tenth logical setter: a sequential ordinary-source READ ONLY owner, never an authorization lane.</summary>
public sealed class PreContextSelfDiscoveryScopeReader(IUnitOfWork unit, ITenantContextAccessor accessor,
    IAuditStateCapture capture) : IOwnMembershipScopeLabels
{
    private const string LabelsSql = """
        SELECT t.display_name,o.display_name,h.host,
            current_user='learnstack_app'
                AND current_setting('transaction_read_only')='on'
                AND current_setting('transaction_isolation')='read committed'
                AND NULLIF(current_setting('app.tenant_id',true),'')::uuid=@tenant
                AND NULLIF(current_setting('app.organization_id',true),'')::uuid IS NOT DISTINCT FROM @organization
                AND NULLIF(current_setting('app.scope',true),'') IS NULL
                AND NULLIF(current_setting('app.resolving_host',true),'') IS NULL
                AND NULLIF(current_setting('app.identity_authority_id',true),'') IS NULL
                AND NULLIF(current_setting('app.identity_subject_key',true),'') IS NULL
        FROM public.tenants t
        LEFT JOIN public.organizations o ON o.tenant_id=t.id AND o.id=@organization
        JOIN public.platform_host_to_tenant h ON h.tenant_id=t.id AND h.is_active
            AND (h.organization_id IS NULL OR h.organization_id=@organization)
        WHERE t.id=@tenant AND t.deleted_at IS NULL AND t.status IN ('Trial','Active')
            AND (@organization IS NULL OR (o.id IS NOT NULL AND o.deleted_at IS NULL AND o.status='Active'))
        ORDER BY (h.organization_id IS NOT NULL) DESC,h.host
        LIMIT 1
        """;

    public async Task<OwnMembershipScopeLabel?> ReadAsync(OwnMembershipScopeWitness witness,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(witness);
        if (unit is not IPreContextSelfDiscoveryUnitOfWork self || unit.HasActiveTransaction || unit.IsRollbackOnly
            || accessor.Current is { IsResolved: true } or EventTenantContext or JobTenantContext
            || capture.HasOpenFrame || capture.Changes.Count != 0 || capture.Intents.Count != 0
            || capture.State != AuditIntentState.None || !witness.IsActive)
            throw Unavailable();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        IUnitOfWorkScope? frame = null;
        OwnMembershipScopeLabel? label = null;
        Exception? failure = null;
        try
        {
            frame = await self.BeginSelfDiscoveryTransactionAsync(witness, deadline.Token);
            await using (var command = new NpgsqlCommand(LabelsSql,
                (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!)
            { CommandTimeout = 5 })
            {
                command.Parameters.AddWithValue("tenant", witness.TenantId.Value);
                command.Parameters.AddWithValue("organization", NpgsqlDbType.Uuid,
                    witness.OrganizationId is { } org ? org.Value : DBNull.Value);
                await using var rows = await command.ExecuteReaderAsync(deadline.Token);
                if (await rows.ReadAsync(deadline.Token))
                {
                    if (rows.IsDBNull(3) || !rows.GetBoolean(3)) throw Unavailable();
                    var host = rows.GetString(2);
                    if (EffectiveHost.Normalize(host) != host) throw Unavailable();
                    label = new(rows.GetString(0), rows.IsDBNull(1) ? null : rows.GetString(1), host);
                    if (await rows.ReadAsync(deadline.Token)) throw Unavailable();
                }
            }
            if (!self.IsSelfDiscoveryOwner(witness)) throw Unavailable();
            await frame.CompleteAsync(deadline.Token);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            failure = exception;
        }
        try { if (frame is not null) await frame.DisposeAsync(); }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            unit.MarkRollbackOnly();
            failure = exception;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (failure is not null || deadline.IsCancellationRequested || !witness.IsActive) throw Unavailable();
        return label;
    }

    private static InfrastructureException Unavailable() => new("Own membership scope labels are unavailable.");
}
