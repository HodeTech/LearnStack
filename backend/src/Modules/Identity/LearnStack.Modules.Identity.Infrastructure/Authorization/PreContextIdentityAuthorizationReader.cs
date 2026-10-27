using System.Collections.Immutable;
using System.Data;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Application.Contracts.Authorization;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Modules.Identity.Infrastructure.Authorization;

/// <summary>Committed authorization facts from one isolated child, closed before any business or audit continuation.</summary>
public sealed class PreContextIdentityAuthorizationReader(
    IdentityAuthorizationReadSource source, IIdentityScopeLifecycleReader lifecycle) : IIdentityAuthorizationReader
{
    private static readonly string[] TenantRoleKeys = RoleKeys(PermissionScope.Tenant);
    private static readonly string[] OrganizationRoleKeys = RoleKeys(PermissionScope.Organization);
    private static readonly string[] TenantPermissionKeys = PermissionKeys(PermissionScope.Tenant);
    private static readonly string[] OrganizationPermissionKeys = PermissionKeys(PermissionScope.Organization);

    // The two small over-read bounds detect excess facts; neither silently truncates authority.
    private const string AuthorizationSql = """
        WITH eligible_memberships AS MATERIALIZED (
            SELECT member.id, member.organization_id
            FROM public.memberships AS member
            WHERE member.tenant_id = @tenant AND member.user_id = @actor
              AND member.status = 'Active' AND member.deleted_at IS NULL AND member.deleted_by IS NULL
              AND (member.organization_id IS NULL OR member.organization_id = @organization)
            ORDER BY member.id
            LIMIT 3
        ), eligible_roles AS MATERIALIZED (
            SELECT assignment.membership_id, role.id, role.built_in_key, role.permission_scope
            FROM eligible_memberships AS member
            JOIN public.membership_role_assignments AS assignment
              ON assignment.tenant_id = @tenant AND assignment.membership_id = member.id
             AND assignment.organization_id IS NOT DISTINCT FROM member.organization_id
            JOIN public.roles AS role
              ON role.tenant_id = @tenant AND role.id = assignment.role_id AND role.deleted_at IS NULL AND role.deleted_by IS NULL
            WHERE (member.organization_id IS NULL AND role.permission_scope = 'Tenant'
                       AND role.built_in_key = ANY(@tenant_roles))
               OR (member.organization_id IS NOT NULL AND role.permission_scope = 'Organization'
                       AND role.built_in_key = ANY(@organization_roles))
            ORDER BY role.id, assignment.membership_id
            LIMIT 9
        )
        SELECT member.id, member.organization_id, role.id, role.built_in_key, role.permission_scope,
               grant_row.permission_scope, grant_row.permission_key,
               current_setting('transaction_read_only') = 'on'
                 AND current_setting('transaction_isolation') = 'read committed'
                 AND current_user = 'learnstack_app'
                 AND NULLIF(current_setting('app.tenant_id', true), '')::uuid = @tenant
                 AND NULLIF(current_setting('app.organization_id', true), '')::uuid IS NOT DISTINCT FROM @organization
                 AND NULLIF(current_setting('app.scope', true), '') IS NULL
                 AND NULLIF(current_setting('app.resolving_host', true), '') IS NULL
                 AND NULLIF(current_setting('app.identity_authority_id', true), '') IS NULL
                 AND NULLIF(current_setting('app.identity_subject_key', true), '') IS NULL
        FROM (VALUES (1)) AS query_anchor(value)
        LEFT JOIN eligible_memberships AS member ON true
        LEFT JOIN eligible_roles AS role ON role.membership_id = member.id
        LEFT JOIN public.role_permission_grants AS grant_row
          ON grant_row.tenant_id = @tenant AND grant_row.role_id = role.id
         AND grant_row.permission_scope = role.permission_scope
         AND ((grant_row.permission_scope = 'Tenant' AND grant_row.permission_key = ANY(@tenant_permissions))
           OR (grant_row.permission_scope = 'Organization' AND grant_row.permission_key = ANY(@organization_permissions)))
        ORDER BY member.id, role.id, grant_row.permission_scope, grant_row.permission_key
        """;

    public async Task<IdentityAuthorizationResult> ReadAsync(AuthorizationCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();
        AuthorizationReadOwner? owner = null;
        var result = Empty(candidate, IdentityAuthorizationOutcome.Unavailable);
        var cleaned = true;
        try
        {
            if (candidate.IsActive)
            {
                owner = await source.OpenAsync(candidate, cancellationToken);
                if (owner is not null)
                {
                    var transaction = owner.Unit.Transaction;
                    var announced = RequireAnnouncement(owner.Unit, candidate, transaction);
                    var memberships = await ReadMembershipsAsync(owner, candidate, owner.DeadlineToken);
                    RequireSameAnnouncement(owner.Unit, candidate, transaction, announced);
                    // Tenancy owns this separate statement. READ COMMITTED gives each statement its own committed view.
                    var state = await lifecycle.ReadAuthorizationAsync(owner.Unit, candidate, owner.DeadlineToken);
                    RequireSameAnnouncement(owner.Unit, candidate, transaction, announced);
                    var covers = memberships.Length > 0 && state.TenantExistsAndNotDeleted && state.TenantAllowsOrdinaryWork
                        && (candidate.OrganizationId is null || (state.OrganizationBelongsToTenant && state.OrganizationAllowsOrdinaryWork));
                    result = covers
                        ? new(IdentityAuthorizationOutcome.Covered, candidate.Actor, candidate.TenantId, candidate.OrganizationId, memberships)
                        : Empty(candidate, IdentityAuthorizationOutcome.NotCovered);
                    await owner.CompleteAsync();
                }
            }
        }
        catch (IdentityScopeLifecycleReadException exception)
        {
            if (exception.CleanupFailed) owner?.MarkCleanupFailed();
            result = Empty(candidate, IdentityAuthorizationOutcome.Unavailable);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            // Storage, internal deadline and owner faults expose no provider payload or false noncoverage.
            result = Empty(candidate, IdentityAuthorizationOutcome.Unavailable);
        }
        finally
        {
            if (owner is not null)
            {
                try { await owner.DisposeAsync(); }
                catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException)) { cleaned = false; }
            }
        }

        // Owned cleanup finishes (or retains its slot) before request cancellation leaves the adapter.
        cancellationToken.ThrowIfCancellationRequested();
        return cleaned && candidate.IsActive && owner?.DeadlineToken.IsCancellationRequested != true
            ? result : Empty(candidate, IdentityAuthorizationOutcome.Unavailable);
    }

    private static async Task<ImmutableArray<AuthorizationMembershipFacts>> ReadMembershipsAsync(
        AuthorizationReadOwner owner, AuthorizationCandidate candidate, CancellationToken cancellationToken)
    {
        NpgsqlCommand? command = null;
        NpgsqlDataReader? rows = null;
        try
        {
            command = new NpgsqlCommand(AuthorizationSql, (NpgsqlConnection)owner.Unit.Connection,
                (NpgsqlTransaction)owner.Unit.Transaction!)
            { CommandTimeout = 5 };
            command.Parameters.AddWithValue("tenant", candidate.TenantId.Value);
            command.Parameters.AddWithValue("actor", candidate.Actor.Value);
            command.Parameters.AddWithValue("organization", NpgsqlDbType.Uuid,
                candidate.OrganizationId is { } organization ? organization.Value : DBNull.Value);
            command.Parameters.AddWithValue("tenant_roles", TenantRoleKeys);
            command.Parameters.AddWithValue("organization_roles", OrganizationRoleKeys);
            command.Parameters.AddWithValue("tenant_permissions", TenantPermissionKeys);
            command.Parameters.AddWithValue("organization_permissions", OrganizationPermissionKeys);
            rows = await command.ExecuteReaderAsync(cancellationToken);
            var memberships = new Dictionary<Guid, MembershipFacts>();
            var roleIds = new HashSet<Guid>();
            while (await rows.ReadAsync(cancellationToken))
            {
                if (rows.IsDBNull(7) || !rows.GetBoolean(7)) throw Unavailable();
                if (rows.IsDBNull(0)) continue;
                var membershipId = rows.GetGuid(0);
                if (!memberships.TryGetValue(membershipId, out var membership))
                {
                    membership = new(membershipId, rows.IsDBNull(1) ? null : OrganizationId.From(rows.GetGuid(1)));
                    if (memberships.Values.Any(existing => existing.Organization == membership.Organization)) throw Unavailable();
                    memberships.Add(membershipId, membership);
                    if (memberships.Count > 2) throw Unavailable();
                }
                if (rows.IsDBNull(2)) continue;
                var roleId = rows.GetGuid(2);
                if (!membership.Roles.TryGetValue(roleId, out var role))
                {
                    if (!Enum.TryParse<BuiltInRoleKey>(rows.GetString(3), out var key)
                        || !Enum.TryParse<PermissionScope>(rows.GetString(4), out var scope)
                        || IdentityFoundationManifest.Version1.FindTemplate(key, scope) is null) throw Unavailable();
                    if (membership.Roles.Values.Any(existing => existing.Key == key && existing.Scope == scope)) throw Unavailable();
                    role = new(roleId, key, scope);
                    membership.Roles.Add(roleId, role);
                    roleIds.Add(roleId);
                    if (roleIds.Count > 8) throw Unavailable();
                }
                if (rows.IsDBNull(5)) continue;
                var permissionKey = rows.GetString(6);
                if (rows.GetString(5) != role.Scope.ToString()
                    || !IdentityFoundationManifest.Version1.IsActive(role.Scope, permissionKey)) throw Unavailable();
                role.Grants.Add(new(role.Scope, permissionKey));
            }
            return [.. memberships.Values.OrderBy(member => member.Id).Select(member => new AuthorizationMembershipFacts(
                member.Id, member.Organization, [.. member.Roles.Values.OrderBy(role => role.Id).Select(role => new AuthorizationRoleFacts(
                    role.Id, role.Key, role.Scope, [.. role.Grants.OrderBy(grant => grant.Key, StringComparer.Ordinal)]))]))];
        }
        finally
        {
            var readerDisposed = await TryDisposeAsync(rows);
            var commandDisposed = await TryDisposeAsync(command);
            if (!readerDisposed || !commandDisposed) owner.MarkCleanupFailed();
        }
    }

    private static TenantContextAnnouncement RequireAnnouncement(IUnitOfWork unit, AuthorizationCandidate candidate,
        System.Data.Common.DbTransaction? transaction)
    {
        if (!candidate.IsActive || !unit.HasActiveTransaction || unit.Mode != TransactionMode.ReadOnly || unit.IsRollbackOnly
            || transaction is not { IsolationLevel: IsolationLevel.ReadCommitted }
            || unit.GetTenantContextAnnouncement(transaction) is not { Generation: > 0 } announced
            || announced.TenantId != candidate.TenantId || announced.OrganizationId != candidate.OrganizationId)
            throw Unavailable();
        return announced;
    }

    private static void RequireSameAnnouncement(IUnitOfWork unit, AuthorizationCandidate candidate,
        System.Data.Common.DbTransaction? transaction, TenantContextAnnouncement announced)
    {
        if (RequireAnnouncement(unit, candidate, transaction) != announced) throw Unavailable();
    }

    private static IdentityAuthorizationResult Empty(AuthorizationCandidate candidate, IdentityAuthorizationOutcome outcome) =>
        new(outcome, candidate.Actor, candidate.TenantId, candidate.OrganizationId, []);

    private static InfrastructureException Unavailable() => new("Identity authorization is unavailable.");

    private static string[] RoleKeys(PermissionScope scope) => [.. IdentityFoundationManifest.Version1.Templates
        .Where(template => template.Scope == scope).Select(template => template.BuiltInKey.ToString())];

    private static string[] PermissionKeys(PermissionScope scope) => [.. IdentityFoundationManifest.Version1.Definitions
        .Where(definition => definition.IsActive && definition.Scope == scope).Select(definition => definition.Key)];

    private static async ValueTask<bool> TryDisposeAsync(IAsyncDisposable? resource)
    {
        if (resource is null) return true;
        try { await resource.DisposeAsync(); return true; }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException)) { return false; }
    }

    private sealed class MembershipFacts(Guid id, OrganizationId? organization)
    {
        public Guid Id { get; } = id;
        public OrganizationId? Organization { get; } = organization;
        public Dictionary<Guid, RoleFacts> Roles { get; } = [];
    }

    private sealed class RoleFacts(Guid id, BuiltInRoleKey key, PermissionScope scope)
    {
        public Guid Id { get; } = id;
        public BuiltInRoleKey Key { get; } = key;
        public PermissionScope Scope { get; } = scope;
        public HashSet<PermissionDefinition> Grants { get; } = [];
    }
}
