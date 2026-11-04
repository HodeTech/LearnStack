using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

/// <summary>Checks required retained identities and current local administration without profile/provider values.</summary>
public sealed class FixedSeedIdentityCompletionReader(IdentityDbContext db, IUnitOfWork unit)
    : ISeedIdentityCompletionReader
{
    public Task<bool> VerifyAsync(SeedCompletionReadWitness witness, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(witness);
        return ContextualIdentityStorageControl.ReadAsync(() => ReadAsync(witness, cancellationToken));
    }

    private async Task<bool> ReadAsync(SeedCompletionReadWitness witness, CancellationToken ct)
    {
        RequireCurrent(witness);
        var declaration = witness.GetDeclaration<SeedIdentityCompletionDeclaration>();
        if (!DeclarationIsValid(declaration, witness.TenantId)) return false;
        ContextualIdentityStorageControl.EnrollCurrentTenantOwner(db, unit);
        if (!ReferenceEquals(db.Database.CurrentTransaction?.GetDbTransaction(), unit.Transaction))
            throw new InvalidOperationException("Fixed identity completion requires its enlisted verification owner.");
        var previousTimeout = db.Database.GetCommandTimeout();
        db.Database.SetCommandTimeout(10);
        try
        {
            // Required roots are retained even when terminal. Extra selected roots
            // are only current local-admin candidates and their own graph dependencies.
            var members = await Query<MembershipMetadata>("""
                SELECT m.id AS "Id", m.tenant_id AS "TenantId", m.organization_id AS "OrganizationId", m.user_id AS "UserId",
                       m.status AS "Status", m.row_version AS "Version", m.created_at AS "CreatedAt", m.created_by AS "CreatedBy",
                       m.updated_at AS "UpdatedAt", m.updated_by AS "UpdatedBy", m.deleted_at AS "DeletedAt", m.deleted_by AS "DeletedBy"
                  FROM public.memberships AS m
                 WHERE m.tenant_id = @tenant AND (m.id = ANY(@ids)
                    OR m.organization_id IS NULL AND m.status = 'Active' AND m.deleted_at IS NULL AND m.deleted_by IS NULL
                       AND EXISTS (SELECT 1 FROM public.membership_role_assignments AS a
                             JOIN public.roles AS r ON r.tenant_id = a.tenant_id AND r.id = a.role_id
                            WHERE a.tenant_id = m.tenant_id AND a.membership_id = m.id AND a.organization_id IS NULL
                              AND r.built_in_key = 'TenantAdmin' AND r.permission_scope = 'Tenant'
                              AND r.deleted_at IS NULL AND r.deleted_by IS NULL))
                 ORDER BY m.id
                """, witness, declaration.Memberships.Select(pin => pin.Id).ToArray()).ToListAsync(ct);
            RequireCurrent(witness);
            if (!MembershipsAreValid(members, declaration)) return false;
            var memberIds = members.Select(member => member.Id).ToArray();
            var profiles = await Query<ProfileMetadata>("""
                SELECT membership_id AS "MembershipId", organization_id AS "OrganizationId"
                  FROM public.membership_profiles WHERE tenant_id = @tenant AND membership_id = ANY(@ids)
                 ORDER BY membership_id
                """, witness, memberIds).ToListAsync(ct);
            RequireCurrent(witness);
            if (profiles.Count != members.Count || !members.All(member => profiles.Count(profile =>
                    profile.MembershipId == member.Id && profile.OrganizationId == member.OrganizationId) == 1)) return false;
            var assignments = await Query<AssignmentMetadata>("""
                SELECT membership_id AS "MembershipId", organization_id AS "OrganizationId", role_id AS "RoleId"
                  FROM public.membership_role_assignments WHERE tenant_id = @tenant AND membership_id = ANY(@ids)
                 ORDER BY membership_id, role_id
                """, witness, memberIds).ToListAsync(ct);
            RequireCurrent(witness);
            var roleIds = declaration.Roles.Select(pin => pin.Id).Concat(assignments.Select(row => row.RoleId)).Distinct().ToArray();
            var roles = await Query<RoleMetadata>("""
                SELECT id AS "Id", tenant_id AS "TenantId", built_in_key AS "BuiltInKey", permission_scope AS "Scope",
                       row_version AS "Version", created_at AS "CreatedAt", created_by AS "CreatedBy",
                       updated_at AS "UpdatedAt", updated_by AS "UpdatedBy", deleted_at AS "DeletedAt", deleted_by AS "DeletedBy",
                       adopted_release_version AS "ReleaseVersion", adopted_release_fingerprint AS "ReleaseFingerprint"
                  FROM public.roles WHERE tenant_id = @tenant AND id = ANY(@ids) ORDER BY id
                """, witness, roleIds).ToListAsync(ct);
            RequireCurrent(witness);
            if (roles.Count != roleIds.Length || roles.Select(row => row.Id).Distinct().Count() != roles.Count) return false;
            var grants = await Query<GrantMetadata>("""
                SELECT role_id AS "RoleId", permission_scope AS "Scope", permission_key AS "PermissionKey"
                  FROM public.role_permission_grants WHERE tenant_id = @tenant AND role_id = ANY(@ids)
                 ORDER BY role_id, permission_scope, permission_key
                """, witness, roleIds).ToListAsync(ct);
            RequireCurrent(witness);
            return RolesAreValid(roles, grants, declaration) && AssignmentsAreValid(assignments, members, roles)
                && members.Any(member => member.OrganizationId is null && member.Status == "Active"
                    && member.DeletedAt is null && member.DeletedBy is null
                    && assignments.Any(assignment => assignment.MembershipId == member.Id
                        && roles.Any(role => role.Id == assignment.RoleId && role.BuiltInKey == nameof(BuiltInRoleKey.TenantAdmin)
                            && role.Scope == nameof(PermissionScope.Tenant) && role.DeletedAt is null && role.DeletedBy is null)));
        }
        finally { db.Database.SetCommandTimeout(previousTimeout); }
    }

    private IQueryable<T> Query<T>(string sql, SeedCompletionReadWitness witness, Guid[] ids) =>
        db.Database.SqlQueryRaw<T>(sql, new NpgsqlParameter("tenant", witness.TenantId.Value),
            new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids });

    private void RequireCurrent(SeedCompletionReadWitness witness)
    {
        witness.RequireCurrent();
        if (unit is not IOperationReadScopeView { IsTenantWideRead: true }
            || !unit.HasActiveTransaction || unit.IsRollbackOnly || unit.Mode != TransactionMode.ReadWrite
            || unit is IGlobalIdentityUnitOfWork { IsGlobalIdentityTransaction: true }
            || unit.Transaction is not { } transaction
            || unit.GetTenantContextAnnouncement(transaction) is not { Generation: > 0 } announcement
            || announcement.TenantId != witness.TenantId || announcement.OrganizationId is not null
            || witness.OrganizationId is not null || !witness.IsBoundTo(unit, transaction, announcement.Generation)
            || db.CurrentTenantId != witness.TenantId || db.CurrentOrganizationId is not null || !db.CurrentTenantWideRead
            || !ReferenceEquals(db.Database.GetDbConnection(), unit.Connection))
            throw new InvalidOperationException("Fixed identity completion requires its active verification owner.");
    }

    private static bool DeclarationIsValid(SeedIdentityCompletionDeclaration declaration, TenantId tenant)
    {
        var manifest = IdentityFoundationManifest.Version1;
        return declaration.TenantId == tenant && declaration.ManifestVersion == manifest.Version
            && declaration.ManifestFingerprint == manifest.Fingerprint && declaration.Roles.Length == manifest.Templates.Length
            && declaration.Roles.Select(pin => pin.Id).Distinct().Count() == declaration.Roles.Length
            && declaration.Roles.Select(pin => (pin.BuiltInKey, pin.Scope)).Distinct().Count() == declaration.Roles.Length
            && declaration.Roles.All(pin => pin.Id != Guid.Empty && pin.Id.Version == 7
                && manifest.FindTemplate(pin.BuiltInKey, pin.Scope) is { } template
                && pin.Grants.Length == template.Grants.Length && pin.Grants.ToHashSet().SetEquals(template.Grants))
            && !declaration.Memberships.IsDefaultOrEmpty
            && declaration.Memberships.Select(pin => pin.Id).Distinct().Count() == declaration.Memberships.Length
            && declaration.Memberships.Count(pin => pin.IsFirstAdmin) == 1
            && declaration.Memberships.All(pin => pin.Id != Guid.Empty && pin.Id.Version == 7
                && pin.UserId.IsInitialized() && pin.UserId.Value != Guid.Empty && pin.UserId.Value.Version == 7
                && pin.UserId != UserId.SystemActor && declaration.Roles.Any(role => role.Id == pin.RoleId
                    && role.Scope == (pin.OrganizationId is null ? PermissionScope.Tenant : PermissionScope.Organization)
                    && (!pin.IsFirstAdmin || pin.OrganizationId is null && role.BuiltInKey == BuiltInRoleKey.TenantAdmin)));
    }

    private static bool MembershipsAreValid(List<MembershipMetadata> rows, SeedIdentityCompletionDeclaration declaration)
    {
        if (rows.Select(row => row.Id).Distinct().Count() != rows.Count
            || declaration.Memberships.Any(pin => !rows.Any(row => row.Id == pin.Id))) return false;
        foreach (var row in rows)
        {
            var required = declaration.Memberships.SingleOrDefault(pin => pin.Id == row.Id);
            if (row.TenantId != declaration.TenantId.Value || row.Id == Guid.Empty || row.Id.Version != 7
                || row.UserId == Guid.Empty || row.UserId.Version != 7 || row.UserId == UserId.SystemActor.Value
                || row.OrganizationId == Guid.Empty || !ValidStamps(row.CreatedAt, row.CreatedBy, row.UpdatedAt,
                    row.UpdatedBy, row.DeletedAt, row.DeletedBy, row.Version, required is not null)
                || !(row.Status is "Active" or "Suspended" && row.DeletedAt is null && row.DeletedBy is null
                    || row.Status == "Revoked" && row.DeletedAt is not null && row.DeletedBy is not null)
                || required is not null && (row.Version <= 0 || row.OrganizationId != required.OrganizationId?.Value
                    || row.UserId != required.UserId.Value)) return false;
        }
        // Profile values deliberately remain unselected; admitted writers and
        // existing storage guards retain their value-validity responsibility.
        return true;
    }

    private static bool RolesAreValid(List<RoleMetadata> roles, List<GrantMetadata> grants,
        SeedIdentityCompletionDeclaration declaration)
    {
        var manifest = IdentityFoundationManifest.Version1;
        var release = IdentityRoleRelease.Version2;
        if (grants.Select(row => (row.RoleId, row.Scope, row.PermissionKey)).Distinct().Count() != grants.Count) return false;
        foreach (var row in roles)
        {
            var required = declaration.Roles.SingleOrDefault(pin => pin.Id == row.Id);
            if (row.TenantId != declaration.TenantId.Value || row.Id == Guid.Empty || row.Id.Version != 7
                || !Enum.TryParse<BuiltInRoleKey>(row.BuiltInKey, out var key)
                || !Enum.TryParse<PermissionScope>(row.Scope, out var scope)
                || row.BuiltInKey != key.ToString() || row.Scope != scope.ToString()
                || manifest.FindTemplate(key, scope) is not { } template
                || !ValidStamps(row.CreatedAt, row.CreatedBy, row.UpdatedAt, row.UpdatedBy, row.DeletedAt, row.DeletedBy,
                    row.Version, required is not null)
                || required is not null && (key != required.BuiltInKey || scope != required.Scope)
                || !(row.ReleaseVersion == manifest.Version && row.ReleaseFingerprint == manifest.Fingerprint
                    || key == BuiltInRoleKey.TenantAdmin && scope == PermissionScope.Tenant && row.Version > 0
                        && release.IsExact(row.ReleaseVersion, row.ReleaseFingerprint))) return false;

            var values = new List<PermissionDefinition>();
            foreach (var grant in grants.Where(value => value.RoleId == row.Id))
            {
                if (!Enum.TryParse<PermissionScope>(grant.Scope, out var grantScope) || grant.Scope != grantScope.ToString()
                    || grantScope != scope
                    || !release.IsActive(grantScope, grant.PermissionKey)
                    || grant.PermissionKey == IdentityFoundationManifest.RoleAdmin && key != BuiltInRoleKey.TenantAdmin) return false;
                values.Add(new(grantScope, grant.PermissionKey));
            }
            if (key == BuiltInRoleKey.TenantAdmin && !manifest.TenantAdminKernel.All(values.Contains)
                || row.Version == 0 && (row.ReleaseVersion != manifest.Version
                    || values.Count != template.Grants.Length || !values.ToHashSet().SetEquals(template.Grants))) return false;
        }
        return declaration.Roles.All(pin => roles.Any(row => row.Id == pin.Id));
    }

    private static bool AssignmentsAreValid(List<AssignmentMetadata> assignments, List<MembershipMetadata> members,
        List<RoleMetadata> roles) => assignments.Select(row => (row.MembershipId, row.RoleId)).Distinct().Count() == assignments.Count
        && assignments.All(assignment => members.SingleOrDefault(member => member.Id == assignment.MembershipId) is { } member
            && assignment.OrganizationId == member.OrganizationId && roles.SingleOrDefault(role => role.Id == assignment.RoleId) is { } role
            && role.Scope == (member.OrganizationId is null ? nameof(PermissionScope.Tenant) : nameof(PermissionScope.Organization))
            && (member.Status == "Revoked" || role.DeletedAt is null && role.DeletedBy is null));

    private static bool ValidStamps(DateTimeOffset createdAt, Guid createdBy, DateTimeOffset? updatedAt, Guid? updatedBy,
        DateTimeOffset? deletedAt, Guid? deletedBy, long version, bool fixedCreation) => createdAt != default
        && createdBy != Guid.Empty && (!fixedCreation || createdBy == UserId.SystemActor.Value) && version >= 0
        && (version == 0 ? updatedAt is null && updatedBy is null
            : updatedAt is { } updated && updated != default && updatedBy is { } editor && editor != Guid.Empty)
        && (deletedAt is null ? deletedBy is null : version > 0 && deletedAt is { } deleted && deleted != default
            && deletedBy is { } deleting && deleting != Guid.Empty && updatedAt == deletedAt && updatedBy == deletedBy);

    private sealed class RoleMetadata
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public string BuiltInKey { get; set; } = "";
        public string Scope { get; set; } = "";
        public long Version { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public Guid CreatedBy { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }
        public Guid? UpdatedBy { get; set; }
        public DateTimeOffset? DeletedAt { get; set; }
        public Guid? DeletedBy { get; set; }
        public int ReleaseVersion { get; set; }
        public string ReleaseFingerprint { get; set; } = "";
    }

    private sealed class GrantMetadata
    {
        public Guid RoleId { get; set; }
        public string Scope { get; set; } = "";
        public string PermissionKey { get; set; } = "";
    }

    private sealed class MembershipMetadata
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public Guid? OrganizationId { get; set; }
        public Guid UserId { get; set; }
        public string Status { get; set; } = "";
        public long Version { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public Guid CreatedBy { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }
        public Guid? UpdatedBy { get; set; }
        public DateTimeOffset? DeletedAt { get; set; }
        public Guid? DeletedBy { get; set; }
    }

    private sealed class ProfileMetadata
    {
        public Guid MembershipId { get; set; }
        public Guid? OrganizationId { get; set; }
    }

    private sealed class AssignmentMetadata
    {
        public Guid MembershipId { get; set; }
        public Guid? OrganizationId { get; set; }
        public Guid RoleId { get; set; }
    }
}
