using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Education.Domain;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace LearnStack.Modules.Education.Infrastructure.Persistence;

/// <summary>Education persistence on the ambient unit of work's connection.</summary>
public sealed class EducationDbContext(
    DbContextOptions<EducationDbContext> options, ITenantContextAccessor accessor)
    : TenantScopedDbContext(options, accessor)
{
    public DbSet<Course> Courses => Set<Course>();

    public DbSet<Lesson> Lessons => Set<Lesson>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(EducationDbContext).Assembly);

        // Satellites are ordinary mapped entities, so this sweep gives them their
        // own tenant/organization filter despite their lack of an auditable base.
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplySnakeCaseNames();
    }
}
