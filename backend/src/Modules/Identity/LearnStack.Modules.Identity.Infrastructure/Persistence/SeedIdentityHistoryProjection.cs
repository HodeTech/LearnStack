using System.Collections.Immutable;
using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

/// <summary>Closed seed verification of exact retained roots on the app-role tenant owner.</summary>
/// <remarks>No filter override, global User join, new scope announcement or historical directory.</remarks>
public sealed class SeedStateReader(IdentityDbContext db, IUnitOfWork unit) : ISeedStateReader
{
    public Task<RoleSeedDto?> ReadRoleAsync(RoleId roleId, CancellationToken cancellationToken = default) =>
        ContextualIdentityStorageControl.ReadAsync(() => WithCommandTimeoutAsync(() => ReadRoleCoreAsync(roleId, cancellationToken)));

    private async Task<RoleSeedDto?> ReadRoleCoreAsync(RoleId roleId, CancellationToken ct)
    {
        ContextualIdentityStorageControl.EnrollCurrentTenantOwner(db, unit);
        var stored = await Query<RoleProjection>("""
            SELECT id AS "Id", tenant_id AS "TenantId", built_in_key AS "BuiltInKey", permission_scope AS "PermissionScope",
                   row_version AS "Version", deleted_at AS "DeletedAt", deleted_by AS "DeletedBy",
                   created_at AS "CreatedAt", created_by AS "CreatedBy", updated_at AS "UpdatedAt", updated_by AS "UpdatedBy",
                   adopted_release_version AS "AdoptedReleaseVersion", adopted_release_fingerprint AS "AdoptedReleaseFingerprint"
              FROM public.roles WHERE tenant_id = @tenant AND id = @id
            """, roleId.Value).SingleOrDefaultAsync(ct);
        if (stored is null) return null;
        if (!Enum.TryParse<BuiltInRoleKey>(stored.BuiltInKey, out var key)
            || !Enum.TryParse<PermissionScope>(stored.PermissionScope, out var scope)) throw InvalidGraph();
        var row = new RoleSeedDto(stored.Id, TenantId.From(stored.TenantId), key, scope, stored.Version,
            stored.DeletedAt, Actor(stored.DeletedBy), [], Evidence(stored.CreatedAt, stored.CreatedBy, stored.UpdatedAt, stored.UpdatedBy),
            stored.AdoptedReleaseVersion, stored.AdoptedReleaseFingerprint);
        var grants = await Query<GrantProjection>("""
            SELECT permission_scope AS "PermissionScope", permission_key AS "PermissionKey" FROM public.role_permission_grants
             WHERE tenant_id = @tenant AND role_id = @id ORDER BY permission_scope, permission_key
            """, roleId.Value).ToListAsync(ct);
        var definitions = ImmutableArray.CreateBuilder<PermissionDefinition>();
        foreach (var grant in grants)
        {
            if (!Enum.TryParse<PermissionScope>(grant.PermissionScope, out var permissionScope)) throw InvalidGraph();
            definitions.Add(new(permissionScope, grant.PermissionKey));
        }
        var values = definitions.ToImmutable();
        var valid = IdentityFoundationManifest.Version1.FindTemplate(row.BuiltInKey, row.PermissionScope) is not null
            && values.Distinct().Count() == values.Length
            && values.All(value => value.Scope == row.PermissionScope
                && IdentityRoleRelease.Version2.IsActive(value.Scope, value.Key)
                && (value.Key != IdentityFoundationManifest.RoleAdmin || row.BuiltInKey == BuiltInRoleKey.TenantAdmin))
            && (row.BuiltInKey != BuiltInRoleKey.TenantAdmin
                || IdentityFoundationManifest.Version1.TenantAdminKernel.All(values.Contains));
        return row with { Grants = values, GraphIsValid = valid };
    }

    public Task<MembershipSeedDto?> ReadMembershipAsync(MembershipId membershipId, CancellationToken cancellationToken = default) =>
        ContextualIdentityStorageControl.ReadAsync(() => WithCommandTimeoutAsync(() => ReadMembershipCoreAsync(membershipId, cancellationToken)));

    private async Task<MembershipSeedDto?> ReadMembershipCoreAsync(MembershipId membershipId, CancellationToken ct)
    {
        ContextualIdentityStorageControl.EnrollCurrentTenantOwner(db, unit);
        var stored = await Query<MembershipProjection>("""
            SELECT id AS "Id", tenant_id AS "TenantId", organization_id AS "OrganizationId", user_id AS "UserId", status AS "Status",
                   row_version AS "Version", deleted_at AS "DeletedAt", deleted_by AS "DeletedBy",
                   created_at AS "CreatedAt", created_by AS "CreatedBy", updated_at AS "UpdatedAt", updated_by AS "UpdatedBy"
              FROM public.memberships
             WHERE tenant_id = @tenant AND id = @id AND organization_id IS NOT DISTINCT FROM @organization
            """, membershipId.Value, organization: true).SingleOrDefaultAsync(ct);
        if (stored is null) return null;
        var row = new MembershipSeedDto(stored.Id, TenantId.From(stored.TenantId),
            stored.OrganizationId is { } organization ? OrganizationId.From(organization) : null, UserId.From(stored.UserId),
            stored.Status, stored.Version, stored.DeletedAt, Actor(stored.DeletedBy), null, null, [],
            Evidence(stored.CreatedAt, stored.CreatedBy, stored.UpdatedAt, stored.UpdatedBy));
        var profiles = await Query<ProfileProjection>("""
            SELECT display_name AS "DisplayName", preferred_locale AS "PreferredLocale" FROM public.membership_profiles
             WHERE tenant_id = @tenant AND membership_id = @id AND organization_id IS NOT DISTINCT FROM @organization
            """, membershipId.Value, organization: true).Take(2).ToListAsync(ct);
        var valid = false;
        if (profiles.Count > 0)
        {
            row = row with { DisplayName = profiles[0].DisplayName, PreferredLocale = profiles[0].PreferredLocale };
            valid = MembershipProfile.IsValid(row.DisplayName, row.PreferredLocale) && profiles.Count == 1;
        }
        var assignments = await Query<AssignmentProjection>("""
            SELECT a.role_id AS "RoleId", r.id IS NOT NULL AND r.permission_scope = @capability
                   AND (r.deleted_at IS NULL OR @historical) AS "RoleIsValid"
              FROM public.membership_role_assignments AS a
              LEFT JOIN public.roles AS r ON r.tenant_id = a.tenant_id AND r.id = a.role_id
             WHERE a.tenant_id = @tenant AND a.membership_id = @id
               AND a.organization_id IS NOT DISTINCT FROM @organization ORDER BY a.role_id
            """, membershipId.Value, true,
            new NpgsqlParameter("capability", row.OrganizationId is null ? "Tenant" : "Organization"),
            new NpgsqlParameter("historical", row.Status == "Revoked")).ToListAsync(ct);
        var roleIds = ImmutableArray.CreateBuilder<Guid>();
        foreach (var assignment in assignments)
        {
            roleIds.Add(assignment.RoleId);
            valid &= assignment.RoleIsValid;
        }
        return row with { RoleIds = roleIds.ToImmutable(), GraphIsValid = valid };
    }

    private IQueryable<T> Query<T>(string sql, Guid id, bool organization = false, params NpgsqlParameter[] additional)
    {
        var parameters = new List<object>
        {
            new NpgsqlParameter("tenant", db.CurrentTenantId.Value), new NpgsqlParameter("id", id),
        };
        if (organization)
            parameters.Add(new NpgsqlParameter("organization", NpgsqlDbType.Uuid)
            {
                Value = db.CurrentOrganizationId is { } value ? value.Value : DBNull.Value,
            });
        parameters.AddRange(additional);
        // Unmapped primitive projections retain the exact predicates and RLS while
        // preserving Identity's EF command guard, fixed diagnostics and trace boundary.
        return db.Database.SqlQueryRaw<T>(sql, parameters.ToArray());
    }

    private async Task<T> WithCommandTimeoutAsync<T>(Func<Task<T>> read)
    {
        var previous = db.Database.GetCommandTimeout();
        db.Database.SetCommandTimeout(10);
        try { return await read(); }
        finally { db.Database.SetCommandTimeout(previous); }
    }

    private static SeedRootEvidence Evidence(DateTimeOffset createdAt, Guid createdBy, DateTimeOffset? updatedAt, Guid? updatedBy) =>
        new(createdAt, UserId.From(createdBy), updatedAt, Actor(updatedBy));
    private static UserId? Actor(Guid? value) => value is { } actor ? UserId.From(actor) : null;
    private static IdentityFoundationStorageException InvalidGraph() => new(IdentityFoundationStorageFailure.Graph);

    private sealed class RoleProjection
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public string BuiltInKey { get; set; } = "";
        public string PermissionScope { get; set; } = "";
        public long Version { get; set; }
        public DateTimeOffset? DeletedAt { get; set; }
        public Guid? DeletedBy { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public Guid CreatedBy { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }
        public Guid? UpdatedBy { get; set; }
        public int AdoptedReleaseVersion { get; set; }
        public string AdoptedReleaseFingerprint { get; set; } = "";
    }

    private sealed class GrantProjection
    {
        public string PermissionScope { get; set; } = "";
        public string PermissionKey { get; set; } = "";
    }

    private sealed class MembershipProjection
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public Guid? OrganizationId { get; set; }
        public Guid UserId { get; set; }
        public string Status { get; set; } = "";
        public long Version { get; set; }
        public DateTimeOffset? DeletedAt { get; set; }
        public Guid? DeletedBy { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public Guid CreatedBy { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }
        public Guid? UpdatedBy { get; set; }
    }

    private sealed class ProfileProjection
    {
        public string? DisplayName { get; set; }
        public string? PreferredLocale { get; set; }
    }

    private sealed class AssignmentProjection
    {
        public Guid RoleId { get; set; }
        public bool RoleIsValid { get; set; }
    }
}
