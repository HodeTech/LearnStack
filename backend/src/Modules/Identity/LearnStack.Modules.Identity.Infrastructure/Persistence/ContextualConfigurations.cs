using LearnStack.Infrastructure.DataProtection;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

internal static class ContextualIdentityMapping
{
    internal static void Tenant<TEntity>(this EntityTypeBuilder<TEntity> builder) where TEntity : class, ITenantOwned
        => builder.Property(entity => entity.TenantId)
            .HasConversion<TenantId.EfCoreValueConverter, TenantId.EfCoreValueComparer>();

    internal static void Scope<TEntity>(this EntityTypeBuilder<TEntity> builder, string table) where TEntity : class, IOrganizationScoped
    {
        builder.Tenant();
        builder.Property(entity => entity.OrganizationId)
            .HasConversion<OrganizationId.EfCoreValueConverter, OrganizationId.EfCoreValueComparer>();
        builder.HasIndex(entity => new { entity.TenantId, entity.OrganizationId })
            .HasDatabaseName($"ix_{table}_tenant_id_organization_id");
    }
}

internal sealed class MembershipConfiguration : IEntityTypeConfiguration<Membership>
{
    public void Configure(EntityTypeBuilder<Membership> builder)
    {
        builder.ToTable("memberships", table =>
        {
            table.HasCheckConstraint("ck_memberships_status", "status IN ('Active','Suspended','Revoked')");
            table.HasCheckConstraint("ck_memberships_lifecycle", "(status IN ('Active','Suspended') AND deleted_at IS NULL AND deleted_by IS NULL) OR (status = 'Revoked' AND deleted_at IS NOT NULL AND deleted_by IS NOT NULL)");
        });
        builder.HasKey(entity => entity.Id).HasName("pk_memberships");
        builder.Property(entity => entity.Id).HasConversion<MembershipId.EfCoreValueConverter, MembershipId.EfCoreValueComparer>().ValueGeneratedNever();
        builder.Scope("memberships");
        builder.HasAlternateKey(entity => new { entity.TenantId, entity.Id }).HasName("ux_memberships_tenant_id_id");
        builder.Property(entity => entity.UserId).HasConversion<UserId.EfCoreValueConverter, UserId.EfCoreValueComparer>();
        builder.Property(entity => entity.Status).HasEnumAsText();
        builder.HasOne<User>().WithMany().HasForeignKey(entity => entity.UserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_memberships_user");
        builder.HasIndex(entity => entity.UserId).HasDatabaseName("ix_memberships_user_id");
        builder.HasIndex(entity => new { entity.TenantId, entity.UserId, entity.OrganizationId }).IsUnique()
            .AreNullsDistinct(false).HasFilter("deleted_at IS NULL").HasDatabaseName("ux_memberships_current_scope");
        builder.HasOne(entity => entity.Profile).WithOne(entity => entity.Membership)
            .HasForeignKey<MembershipProfile>(entity => new { entity.TenantId, entity.MembershipId })
            .HasPrincipalKey<Membership>(entity => new { entity.TenantId, entity.Id })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_membership_profiles_membership");
        builder.HasMany(entity => entity.RoleAssignments).WithOne(entity => entity.Membership)
            .HasForeignKey(entity => new { entity.TenantId, entity.MembershipId })
            .HasPrincipalKey(entity => new { entity.TenantId, entity.Id })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_membership_role_assignments_membership");
        builder.Navigation(entity => entity.RoleAssignments).HasField("_roleAssignments").UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.MapAuditColumns<Membership, MembershipId>();
        builder.DeclarePersonalData(PersonalDataCategory.Identity,
            "Contextual person membership, immutable scope and admission lifecycle.",
            nameof(Membership.Id), nameof(Membership.TenantId), nameof(Membership.OrganizationId), nameof(Membership.UserId), nameof(Membership.Status));
    }
}

internal sealed class MembershipProfileConfiguration : IEntityTypeConfiguration<MembershipProfile>
{
    public void Configure(EntityTypeBuilder<MembershipProfile> builder)
    {
        builder.ToTable("membership_profiles");
        builder.HasKey(entity => new { entity.TenantId, entity.MembershipId }).HasName("pk_membership_profiles");
        builder.Scope("membership_profiles");
        builder.Property(entity => entity.MembershipId).HasConversion<MembershipId.EfCoreValueConverter, MembershipId.EfCoreValueComparer>().ValueGeneratedNever();
        builder.Property(entity => entity.DisplayName).HasMaxLength(128);
        builder.Property(entity => entity.PreferredLocale).HasMaxLength(35);
        builder.DeclarePersonalData(PersonalDataCategory.Identity,
            "Contained contextual profile identity, scope and optional personal display and locale preferences.",
            nameof(MembershipProfile.TenantId), nameof(MembershipProfile.OrganizationId), nameof(MembershipProfile.MembershipId),
            nameof(MembershipProfile.DisplayName), nameof(MembershipProfile.PreferredLocale));
    }
}

internal sealed class MembershipRoleAssignmentConfiguration : IEntityTypeConfiguration<MembershipRoleAssignment>
{
    public void Configure(EntityTypeBuilder<MembershipRoleAssignment> builder)
    {
        builder.ToTable("membership_role_assignments");
        builder.HasKey(entity => new { entity.TenantId, entity.MembershipId, entity.RoleId }).HasName("pk_membership_role_assignments");
        builder.Scope("membership_role_assignments");
        builder.Property(entity => entity.MembershipId).HasConversion<MembershipId.EfCoreValueConverter, MembershipId.EfCoreValueComparer>().ValueGeneratedNever();
        builder.Property(entity => entity.RoleId).HasConversion<RoleId.EfCoreValueConverter, RoleId.EfCoreValueComparer>().ValueGeneratedNever();
        builder.HasOne<Role>().WithMany().HasForeignKey(entity => new { entity.TenantId, entity.RoleId })
            .HasPrincipalKey(entity => new { entity.TenantId, entity.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_membership_role_assignments_role");
        builder.HasIndex(entity => new { entity.TenantId, entity.RoleId }).HasDatabaseName("ix_membership_role_assignments_tenant_id_role_id");
        builder.DeclarePersonalData(PersonalDataCategory.Identity,
            "Contained person's contextual role assignment and exact Membership scope.",
            nameof(MembershipRoleAssignment.TenantId), nameof(MembershipRoleAssignment.OrganizationId),
            nameof(MembershipRoleAssignment.MembershipId), nameof(MembershipRoleAssignment.RoleId));
    }
}

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("roles");
        builder.HasKey(entity => entity.Id).HasName("pk_roles");
        builder.Property(entity => entity.Id).HasConversion<RoleId.EfCoreValueConverter, RoleId.EfCoreValueComparer>().ValueGeneratedNever();
        builder.Tenant();
        builder.HasAlternateKey(entity => new { entity.TenantId, entity.Id }).HasName("ux_roles_tenant_id_id");
        builder.Property(entity => entity.BuiltInKey).HasEnumAsText();
        builder.Property(entity => entity.PermissionScope).HasEnumAsText();
        builder.HasIndex(entity => new { entity.TenantId, entity.PermissionScope, entity.BuiltInKey }).IsUnique()
            .HasFilter("deleted_at IS NULL").HasDatabaseName("ux_roles_current_builtin_scope");
        builder.HasMany(entity => entity.Grants).WithOne(entity => entity.Role)
            .HasForeignKey(entity => new { entity.TenantId, entity.RoleId })
            .HasPrincipalKey(entity => new { entity.TenantId, entity.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_role_permission_grants_role");
        builder.Navigation(entity => entity.Grants).HasField("_grants").UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.MapAuditColumns<Role, RoleId>();
        builder.DeclarePersonalData(PersonalDataCategory.None,
            "Institution-owned built-in authorization template identity and capability scope, without person fields.",
            nameof(Role.Id), nameof(Role.TenantId), nameof(Role.BuiltInKey), nameof(Role.PermissionScope));
    }
}

internal sealed class RolePermissionGrantConfiguration : IEntityTypeConfiguration<RolePermissionGrant>
{
    public void Configure(EntityTypeBuilder<RolePermissionGrant> builder)
    {
        builder.ToTable("role_permission_grants");
        builder.HasKey(entity => new { entity.TenantId, entity.RoleId, entity.PermissionScope, entity.PermissionKey }).HasName("pk_role_permission_grants");
        builder.Tenant();
        builder.Property(entity => entity.RoleId).HasConversion<RoleId.EfCoreValueConverter, RoleId.EfCoreValueComparer>().ValueGeneratedNever();
        builder.Property(entity => entity.PermissionScope).HasEnumAsText();
        builder.Property(entity => entity.PermissionKey).HasMaxLength(128);
        builder.DeclarePersonalData(PersonalDataCategory.None,
            "Contained finite template capability grant, without a person assignment.",
            nameof(RolePermissionGrant.TenantId), nameof(RolePermissionGrant.RoleId), nameof(RolePermissionGrant.PermissionScope), nameof(RolePermissionGrant.PermissionKey));
    }
}
