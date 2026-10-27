using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

public sealed class IdentityDbContextFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public const string HistoryTable = "__ef_migrations_history_identity";

    public IdentityDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Migration");
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("ConnectionStrings__Migration is required for Identity migrations.");
        return new IdentityDbContext(new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(connection, provider => provider.MigrationsHistoryTable(HistoryTable)).Options,
            StaticTenantContextAccessor.Unresolved, EmptyIdentityScope.Instance);
    }
}

/// <summary>Design/model inspection supplies no verified subject or runtime authority.</summary>
public sealed class EmptyIdentityScope : IGlobalIdentityScopeAccessor
{
    public static EmptyIdentityScope Instance { get; } = new();
    private EmptyIdentityScope() { }
    public GlobalIdentityContextAnnouncement? Current => null;
}
