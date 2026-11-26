using System.Data;
using LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Modules.Tenancy.Infrastructure.Persistence;

/// <summary>
/// One uncached statement over Tenancy's own tables, on the caller's announced app transaction, or for many scopes at once
/// on an entered platform scope's transaction.
/// </summary>
public sealed class IdentityScopeLifecycleReader(IUnitOfWork unit) : IIdentityScopeLifecycleReader
{
    // The platform form: the same four facts per scope, one row per scope in the scopes' order. The tenant-wide scope travels
    // as the nil identifier, which no organization has, so it joins no organization and its facts are false as above. The
    // tenant and organization rows are read by their primary keys.
    private const string PlatformLifecycleSql = """
        SELECT scope.position,
               t.id IS NOT NULL AND t.deleted_at IS NULL,
               t.id IS NOT NULL AND t.deleted_at IS NULL AND t.status IN ('Trial', 'Active'),
               o.id IS NOT NULL,
               o.id IS NOT NULL AND o.deleted_at IS NULL AND o.status = 'Active'
        FROM unnest(@tenants::uuid[], @organizations::uuid[]) WITH ORDINALITY AS scope(tenant_id, organization_id, position)
        LEFT JOIN public.tenants AS t ON t.id = scope.tenant_id
        LEFT JOIN public.organizations AS o
          ON o.tenant_id = t.id AND o.id = NULLIF(scope.organization_id, '00000000-0000-0000-0000-000000000000'::uuid)
        ORDER BY scope.position
        """;

    private const string LifecycleSql = """
        SELECT t.id IS NOT NULL AND t.deleted_at IS NULL,
               t.id IS NOT NULL AND t.deleted_at IS NULL AND t.status IN ('Trial', 'Active'),
               o.id IS NOT NULL,
               o.id IS NOT NULL AND o.deleted_at IS NULL AND o.status = 'Active',
               current_setting('transaction_isolation') = 'read committed',
               current_setting('transaction_read_only') = 'on',
               NULLIF(current_setting('app.tenant_id', true), '')::uuid = @tenant
                 AND NULLIF(current_setting('app.organization_id', true), '')::uuid IS NOT DISTINCT FROM @organization
                 AND NULLIF(current_setting('app.scope', true), '') IS NULL
        FROM (VALUES (1)) AS query_anchor(value)
        LEFT JOIN public.tenants AS t ON t.id = @tenant
        LEFT JOIN public.organizations AS o
          ON o.tenant_id = t.id AND o.id = @organization
        """;

    public async Task<IdentityScopeLifecycle> ReadAsync(TenantId tenantId, OrganizationId? organizationId,
        CancellationToken cancellationToken = default)
    {
        if (!tenantId.IsInitialized() || tenantId.Value == Guid.Empty || tenantId == TenantId.PlatformSentinel
            || !unit.HasActiveTransaction || unit.IsRollbackOnly
            || unit.Transaction is not { IsolationLevel: IsolationLevel.ReadCommitted } transaction
            || unit.GetTenantContextAnnouncement(transaction) is not { Generation: > 0 } announced
            || announced.TenantId != tenantId || announced.OrganizationId != organizationId)
            throw new InfrastructureException("Identity scope lifecycle is unavailable.");

        try
        {
            await using var command = new NpgsqlCommand(LifecycleSql,
                (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)transaction)
            { CommandTimeout = 10 };
            command.Parameters.AddWithValue("tenant", tenantId.Value);
            command.Parameters.AddWithValue("organization", NpgsqlDbType.Uuid, organizationId is { } org
                ? org.Value : DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return new(false, false, false, false);
            if (!reader.GetBoolean(4)) throw new InfrastructureException("Identity scope lifecycle is unavailable.");
            return new(reader.GetBoolean(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetBoolean(3));
        }
        catch (NpgsqlException)
        {
            throw new InfrastructureException("Identity scope lifecycle is unavailable.");
        }
    }

    public async Task<IdentityScopeLifecycle> ReadAuthorizationAsync(IUnitOfWork child, AuthorizationCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(candidate);
        if (!candidate.IsActive || child.Mode != TransactionMode.ReadOnly || !child.HasActiveTransaction
            || child.IsRollbackOnly || child.Transaction is not { IsolationLevel: IsolationLevel.ReadCommitted } transaction
            || child.GetTenantContextAnnouncement(transaction) is not { Generation: > 0 } announced
            || announced.TenantId != candidate.TenantId || announced.OrganizationId != candidate.OrganizationId)
            throw new IdentityScopeLifecycleReadException(false);

        NpgsqlCommand? command = null;
        NpgsqlDataReader? reader = null;
        IdentityScopeLifecycle? facts = null;
        var failed = false;
        try
        {
            command = new NpgsqlCommand(LifecycleSql, (NpgsqlConnection)child.Connection,
                (NpgsqlTransaction)transaction)
            { CommandTimeout = 5 };
            command.Parameters.AddWithValue("tenant", candidate.TenantId.Value);
            command.Parameters.AddWithValue("organization", NpgsqlDbType.Uuid,
                candidate.OrganizationId is { } organization ? organization.Value : DBNull.Value);
            reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) facts = new(false, false, false, false);
            else if (!reader.GetBoolean(4) || !reader.GetBoolean(5) || reader.IsDBNull(6) || !reader.GetBoolean(6)) failed = true;
            else facts = new(reader.GetBoolean(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetBoolean(3));
            if (!candidate.IsActive || child.GetTenantContextAnnouncement(transaction) != announced) failed = true;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            // No provider payload crosses the module boundary, including disposal failures.
            failed = true;
        }

        var readerDisposed = await TryDisposeAsync(reader);
        var commandDisposed = await TryDisposeAsync(command);
        if (!readerDisposed || !commandDisposed) throw new IdentityScopeLifecycleReadException(true);
        cancellationToken.ThrowIfCancellationRequested();
        if (failed || facts is null) throw new IdentityScopeLifecycleReadException(false);
        return facts;
    }

    public async Task<IReadOnlyList<IdentityScopeLifecycle>> ReadOnPlatformScopeAsync(IPlatformAdminScopeHandle handle,
        IReadOnlyList<IdentityLifecycleScope> scopes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(scopes);
        if (scopes.Count is 0 or > IIdentityScopeLifecycleReader.MaximumPlatformScopes)
            throw new ArgumentException("A platform lifecycle read names between one and the bounded number of scopes.", nameof(scopes));

        var tenants = new Guid[scopes.Count];
        var organizations = new Guid[scopes.Count];
        for (var index = 0; index < scopes.Count; index++)
        {
            var scope = scopes[index] ?? throw new ArgumentException("A platform lifecycle read names no empty scope.", nameof(scopes));
            if (!StronglyTypedId.IsAssigned(scope.TenantId) || scope.TenantId == TenantId.PlatformSentinel
                || scope.OrganizationId is { } unassigned && !StronglyTypedId.IsAssigned(unassigned))
                throw new ArgumentException("A platform lifecycle read names real tenants and assigned organizations.", nameof(scopes));
            tenants[index] = scope.TenantId.Value;
            organizations[index] = scope.OrganizationId?.Value ?? Guid.Empty;
        }

        // The handle's own statement facade: no announcement, and a failed statement leaves the scope to be abandoned by
        // its owner, who decides whether the fault is retryable.
        var rows = await handle.QueryAsync(PlatformLifecycleSql,
            [new PlatformSqlParameter("tenants", tenants), new PlatformSqlParameter("organizations", organizations)],
            reader => (Position: reader.GetInt64(0), Facts: new IdentityScopeLifecycle(
                reader.GetBoolean(1), reader.GetBoolean(2), reader.GetBoolean(3), reader.GetBoolean(4))),
            cancellationToken);
        if (rows.Count != scopes.Count || rows.Where((row, index) => row.Position != index + 1).Any())
            throw new InfrastructureException("Identity scope lifecycle is unavailable.");
        return [.. rows.Select(row => row.Facts)];
    }

    private static async ValueTask<bool> TryDisposeAsync(IAsyncDisposable? resource)
    {
        if (resource is null) return true;
        try { await resource.DisposeAsync(); return true; }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException)) { return false; }
    }
}
