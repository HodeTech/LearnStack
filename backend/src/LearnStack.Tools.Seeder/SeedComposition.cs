using LearnStack.Application.Pipeline;
using LearnStack.Infrastructure.MultiTenancy;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace LearnStack.Tools.Seeder;

/// <summary>
/// The seeder's service graph: one provider per act, around one shared data source.
/// </summary>
/// <remarks>
/// <para>
/// <b>One file, because the alternative was three copies.</b> The entry point and the
/// integration suite both need this graph, and a hand-maintained second copy is a second
/// thing to keep true — the copy is what drifts, and it had already drifted on the axis
/// that mattered: one built a data source per act where the other shared one.
/// </para>
/// <para>
/// <b>A provider per act, and no writable ambient accessor anywhere.</b> Writes to
/// <c>ITenantContextAccessor.Current</c> are a closed set of four
/// ([ADR-0036 Amendment 2](../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md)),
/// because a writer of it can make work run under a tenant nothing resolved. Composing the
/// context into a <see cref="StaticTenantContextAccessor"/> per act means the seeder
/// cannot move the ambient tenant at all — not that it promises not to.
/// </para>
/// <para>
/// <b>The data source is the caller's, not this method's.</b> A pool per act would leave
/// one idle connection behind per command against a server with a connection ceiling, and
/// <c>AddSingleton(instance)</c> does not dispose what it did not create — measured — so
/// nothing would ever reclaim them. The owner disposes it once, after the run.
/// </para>
/// </remarks>
public static class SeedComposition
{
    public static ServiceProvider Build(
        NpgsqlDataSource dataSource, ITenantContext? context, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var services = new ServiceCollection();

        services.AddSingleton(dataSource);
        services.AddSingleton(loggerFactory);
        services.AddLogging();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<ITenantContextAccessor>(new StaticTenantContextAccessor(context));
        services.AddTransient<ITenantContext>(provider =>
            provider.GetRequiredService<ITenantContextAccessor>().Current
            ?? UnresolvedTenantContext.Instance);

        services.AddScoped<IUnitOfWork, NpgsqlUnitOfWork>();
        services.AddModuleDbContext<TenancyDbContext>();
        services.AddScoped<ITenantWriteStore, TenantWriteStore>();
        services.AddScoped<IOrganizationWriteStore, OrganizationWriteStore>();
        services.AddScoped<IPlatformHostMappingStore, PlatformHostMappingStore>();

        // Its own short read-only transaction on its own connection, which is why it takes
        // a Lazy data source rather than the ambient unit of work: it answers "is this
        // organization one of this tenant's?" before the write that would depend on the
        // answer, and it must not be able to see uncommitted state from that write.
        services.AddSingleton(new Lazy<NpgsqlDataSource>(() => dataSource));
        services.AddSingleton<IOrganizationScopeValidator, OrganizationScopeValidator>();

        // The seeder reserves no hosts and fronts no cache: it is a one-shot process with
        // no configuration bound and nothing in memory to go stale. Both defaults answer
        // truthfully for that host rather than approximating the API's.
        services.AddSingleton<IReservedHostRegistry>(NoReservedHosts.Instance);
        services.AddSingleton<IHostResolutionInvalidator>(NullHostResolutionInvalidator.Instance);
        services.AddLearnStackMediatRPipeline(typeof(ITenantWriteStore).Assembly);

        return services.BuildServiceProvider();
    }
}

/// <summary>Source-generated logging, per the house CA1848 rule.</summary>
public static partial class SeedLog
{
    [LoggerMessage(EventId = 7001, Level = LogLevel.Error, Message = "Seeding failed.")]
    public static partial void Failed(ILogger logger, Exception exception);
}
