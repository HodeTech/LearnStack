using LearnStack.Infrastructure.DataProtection;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

/// <summary>One Identity model/chain for global bindings and later contextual membership.</summary>
public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options,
    ITenantContextAccessor tenantContext, IGlobalIdentityScopeAccessor identityScope)
    : TenantScopedDbContext(options, tenantContext)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<IdentityAuthority> IdentityAuthorities => Set<IdentityAuthority>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<MembershipProfile> MembershipProfiles => Set<MembershipProfile>();
    public DbSet<MembershipRoleAssignment> MembershipRoleAssignments => Set<MembershipRoleAssignment>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RolePermissionGrant> RolePermissionGrants => Set<RolePermissionGrant>();

    public IdentityAuthorityId CurrentIdentityAuthorityId => identityScope.Current is { } current
        && current.Generation > 0
        && current.Descriptor.ContextType == typeof(IdentityDbContext)
        && current.Descriptor.EntityType == typeof(User)
            ? current.Descriptor.Subject.AuthorityId : IdentityAuthorityId.From(Guid.Empty);

    public IdentitySubjectKey CurrentIdentitySubjectKey => identityScope.Current is { } current
        && current.Generation > 0
        && current.Descriptor.ContextType == typeof(IdentityDbContext)
        && current.Descriptor.EntityType == typeof(User)
            ? current.Descriptor.Subject.SubjectKey : IdentitySubjectKey.FromBytes(new byte[32]);

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.UsePersonalDataClassification();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(IdentityDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<User>().HasQueryFilter(user => user.AuthorityId == CurrentIdentityAuthorityId
            && user.SubjectKey == CurrentIdentitySubjectKey);
        modelBuilder.ApplySnakeCaseNames();
    }
}
