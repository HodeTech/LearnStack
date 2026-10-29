using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

/// <summary>Checks a finite installation prefix on the existing owner's temporary read scope.</summary>
/// <remarks>Only retained metadata is selected. Ordinary seed queries retain their organization predicate.</remarks>
public sealed class FixedSeedIdentityPrerequisiteReader(IdentityDbContext db, IUnitOfWork unit)
    : IFixedSeedIdentityPrerequisiteReader, ISeedPrerequisiteReadOperation
{
    public Task<bool> ExecuteAsync(SeedPrerequisiteReadWitness witness, CancellationToken cancellationToken = default) =>
        VerifyAsync(witness, cancellationToken);

    public Task<bool> VerifyAsync(SeedPrerequisiteReadWitness witness, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(witness);
        return ContextualIdentityStorageControl.ReadAsync(() => ReadAsync(witness, cancellationToken));
    }

    private async Task<bool> ReadAsync(SeedPrerequisiteReadWitness witness, CancellationToken ct)
    {
        RequireCurrent(witness);
        var declaration = witness.GetDeclaration<FixedSeedPrerequisiteDeclaration>();
        if (declaration.TenantId != witness.TenantId || declaration.ManifestVersion != IdentityFoundationManifest.Version1.Version
            || declaration.ManifestFingerprint != IdentityFoundationManifest.Version1.Fingerprint
            || declaration.Roles.Length != IdentityFoundationManifest.Version1.Templates.Length
            || declaration.Memberships.IsEmpty || !Enum.IsDefined(declaration.Stage)) return false;
        ContextualIdentityStorageControl.EnrollCurrentTenantOwner(db, unit);
        var previousTimeout = db.Database.GetCommandTimeout();
        db.Database.SetCommandTimeout(10);
        try
        {
            // All retained roots participate: a terminal or unknown identity is never absence.
            var roles = await Query<RoleMetadata>("""
                SELECT id AS "Id", tenant_id AS "TenantId", built_in_key AS "BuiltInKey", permission_scope AS "Scope",
                       row_version AS "Version", created_at AS "CreatedAt", created_by AS "CreatedBy",
                       updated_at AS "UpdatedAt", updated_by AS "UpdatedBy", deleted_at AS "DeletedAt", deleted_by AS "DeletedBy",
                       adopted_release_version AS "ReleaseVersion", adopted_release_fingerprint AS "ReleaseFingerprint"
                  FROM public.roles WHERE tenant_id = @tenant ORDER BY id
                """, witness).Take(declaration.Roles.Length + 1).ToListAsync(ct);
            RequireCurrent(witness);
            if (!RolesArePristine(roles, declaration)) return false;
            var grants = await Query<GrantMetadata>("""
                SELECT role_id AS "RoleId", permission_scope AS "Scope", permission_key AS "PermissionKey"
                  FROM public.role_permission_grants WHERE tenant_id = @tenant ORDER BY role_id, permission_scope, permission_key
                """, witness).Take(declaration.Roles.Sum(role => role.Grants.Length) + 1).ToListAsync(ct);
            RequireCurrent(witness);
            if (!GrantsArePristine(grants, roles, declaration)) return false;
            var members = await Query<MembershipMetadata>("""
                SELECT id AS "Id", tenant_id AS "TenantId", organization_id AS "OrganizationId", user_id AS "UserId",
                       status AS "Status", row_version AS "Version", created_at AS "CreatedAt", created_by AS "CreatedBy",
                       updated_at AS "UpdatedAt", updated_by AS "UpdatedBy", deleted_at AS "DeletedAt", deleted_by AS "DeletedBy"
                  FROM public.memberships WHERE tenant_id = @tenant ORDER BY id
                """, witness).Take(declaration.Memberships.Length + 1).ToListAsync(ct);
            RequireCurrent(witness);
            if (!MembershipsArePristine(members, declaration)) return false;
            var profiles = await Query<ProfileMetadata>("""
                SELECT membership_id AS "MembershipId", organization_id AS "OrganizationId"
                  FROM public.membership_profiles WHERE tenant_id = @tenant ORDER BY membership_id
                """, witness).Take(declaration.Memberships.Length + 1).ToListAsync(ct);
            RequireCurrent(witness);
            var assignments = await Query<AssignmentMetadata>("""
                SELECT membership_id AS "MembershipId", organization_id AS "OrganizationId", role_id AS "RoleId"
                  FROM public.membership_role_assignments WHERE tenant_id = @tenant ORDER BY membership_id, role_id
                """, witness).Take(declaration.Memberships.Length + 1).ToListAsync(ct);
            RequireCurrent(witness);
            return profiles.Count == members.Count && assignments.Count == members.Count
                && members.All(member => profiles.Count(profile => profile.MembershipId == member.Id
                        && profile.OrganizationId == member.OrganizationId) == 1
                    && assignments.Count(assignment => assignment.MembershipId == member.Id
                        && assignment.OrganizationId == member.OrganizationId
                        && assignment.RoleId == declaration.Memberships.Single(pin => pin.Id == member.Id).RoleId) == 1);
        }
        finally { db.Database.SetCommandTimeout(previousTimeout); }
    }

    private IQueryable<T> Query<T>(string sql, SeedPrerequisiteReadWitness witness) =>
        db.Database.SqlQueryRaw<T>(sql, new NpgsqlParameter("tenant", witness.TenantId.Value));

    private void RequireCurrent(SeedPrerequisiteReadWitness witness)
    {
        witness.RequireCurrent();
        if (unit is not IOperationReadScopeView { IsTenantWideRead: true }
            || !unit.HasActiveTransaction || unit.IsRollbackOnly || unit.Mode != TransactionMode.ReadWrite
            || unit.Transaction is not { } transaction
            || unit.GetTenantContextAnnouncement(transaction) is not { Generation: > 0 } announcement
            || announcement.TenantId != witness.TenantId || announcement.OrganizationId != witness.OrganizationId
            || !witness.IsBoundTo(unit, transaction, announcement.Generation)
            || db.CurrentTenantId != witness.TenantId || db.CurrentOrganizationId != witness.OrganizationId)
            throw new InvalidOperationException("Fixed seed metadata requires its active verification owner.");
    }

    private static bool RolesArePristine(List<RoleMetadata> rows, FixedSeedPrerequisiteDeclaration declaration)
    {
        var count = declaration.Stage switch
        {
            FixedSeedPrerequisiteStage.Tenancy => 0,
            FixedSeedPrerequisiteStage.Role => declaration.TargetIndex,
            _ => declaration.Roles.Length,
        };
        if (count < 0 || count > declaration.Roles.Length || rows.Count != count) return false;
        return declaration.Roles.Take(count).All(pin => rows.SingleOrDefault(row => row.Id == pin.Id) is { } row
            && row.TenantId == declaration.TenantId.Value && row.BuiltInKey == pin.BuiltInKey.ToString() && row.Scope == pin.Scope.ToString()
            && row.Version == 0 && row.CreatedAt != default && row.CreatedBy == UserId.SystemActor.Value
            && row.UpdatedAt is null && row.UpdatedBy is null && row.DeletedAt is null && row.DeletedBy is null
            && row.ReleaseVersion == declaration.ManifestVersion && row.ReleaseFingerprint == declaration.ManifestFingerprint);
    }

    private static bool GrantsArePristine(List<GrantMetadata> rows, List<RoleMetadata> roles,
        FixedSeedPrerequisiteDeclaration declaration)
    {
        var expected = declaration.Roles.Where(pin => roles.Any(role => role.Id == pin.Id))
            .SelectMany(pin => pin.Grants.Select(grant => (RoleId: pin.Id, Scope: grant.Scope.ToString(), grant.Key))).ToArray();
        return rows.Count == expected.Length
            && expected.All(pin => rows.Count(row => row.RoleId == pin.RoleId && row.Scope == pin.Scope
                && row.PermissionKey == pin.Key) == 1);
    }

    private static bool MembershipsArePristine(List<MembershipMetadata> rows, FixedSeedPrerequisiteDeclaration declaration)
    {
        var count = declaration.Stage switch
        {
            FixedSeedPrerequisiteStage.Tenancy or FixedSeedPrerequisiteStage.Role => 0,
            FixedSeedPrerequisiteStage.Membership => declaration.TargetIndex,
            _ => declaration.Memberships.Length,
        };
        if (count < 0 || count > declaration.Memberships.Length || rows.Count != count
            || declaration.Stage == FixedSeedPrerequisiteStage.Profile
                && (declaration.TargetIndex < 0 || declaration.TargetIndex >= declaration.Memberships.Length)) return false;
        for (var index = 0; index < count; index++)
        {
            var pin = declaration.Memberships[index];
            var row = rows.SingleOrDefault(value => value.Id == pin.Id);
            var initialized = declaration.Stage == FixedSeedPrerequisiteStage.Profile && index < declaration.TargetIndex;
            if (row is null || row.TenantId != declaration.TenantId.Value || row.OrganizationId != pin.OrganizationId?.Value
                || row.UserId != pin.UserId.Value || row.Status != "Active" || row.Version != (initialized ? 1 : 0)
                || row.CreatedAt == default || row.CreatedBy != UserId.SystemActor.Value
                || row.DeletedAt is not null || row.DeletedBy is not null
                || (initialized ? row.UpdatedAt is null || row.UpdatedAt == default || row.UpdatedBy != UserId.SystemActor.Value
                    : row.UpdatedAt is not null || row.UpdatedBy is not null)) return false;
        }
        return true;
    }

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
