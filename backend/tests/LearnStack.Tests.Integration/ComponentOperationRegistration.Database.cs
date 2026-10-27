using System.Diagnostics.Metrics;
using LearnStack.Api.Composition;
using LearnStack.Application.Pipeline;
using LearnStack.Infrastructure.Caching;
using LearnStack.Infrastructure.MultiTenancy;
using LearnStack.Infrastructure.Validation;
using LearnStack.Modules.Customization.Application.Authorization;
using LearnStack.Modules.Education.Application.Authorization;
using LearnStack.Modules.Identity.Infrastructure.Authorization;
using LearnStack.Modules.Tenancy.Application.Authorization;
using LearnStack.SharedKernel.Caching;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using LearnStack.SharedKernel.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LearnStack.Tests.Integration;

internal static partial class ComponentOperationRegistration
{
    /// <summary>
    /// Database component composition through actual persistence registrations and all
    /// canonical behaviors. It is neither Program nor the Seeder entry authority.
    /// The caller owns the source and explicitly supplies each component capability.
    /// </summary>
    internal static IServiceCollection CreateDatabaseServices(NpgsqlDataSource dataSource, ITenantContext? context)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IGuidFactory, SystemGuidFactory>();
        services.AddSingleton(dataSource);
        services.AddSingleton(new Lazy<NpgsqlDataSource>(() => dataSource));
        services.AddSingleton<ITenantContextAccessor>(new StaticTenantContextAccessor(context));
        services.AddTransient<ITenantContext>(provider =>
            provider.GetRequiredService<ITenantContextAccessor>().Current ?? UnresolvedTenantContext.Instance);
        // The real canonical registration supplies contexts, scoped transaction/audit
        // state, interceptors, stores and app-role writers. Its unused credential owners
        // remain lazy; all component data access borrows the caller-owned source above.
        services.AddLearnStackPersistence(new ConfigurationBuilder().Build());
        services.AddSingleton<ICacheService>(provider => new InMemoryCacheService(
            provider.GetRequiredService<IClock>(), provider.GetRequiredService<IMeterFactory>()));
        services.AddSingleton<IJsonSchemaValidator, JsonSchemaNetValidator>();
        services.AddSingleton<IOrganizationScopeValidator, OrganizationScopeValidator>();
        services.AddSingleton<IReservedHostRegistry>(NoReservedHosts.Instance);
        services.AddSingleton<IHostResolutionInvalidator>(NullHostResolutionInvalidator.Instance);
        services.AddIdentityOperationPolicies();
        services.AddSingleton<IOperationPolicySource, TenancyOperationPolicySource>();
        services.AddSingleton<IOperationPolicySource, CustomizationOperationPolicySource>();
        services.AddSingleton<IOperationPolicySource, EducationOperationPolicySource>();
        services.AddLearnStackMediatRPipeline(
            typeof(LearnStack.Modules.Tenancy.Application.AssemblyMarker).Assembly,
            typeof(LearnStack.Modules.Customization.Application.AssemblyMarker).Assembly,
            typeof(LearnStack.Modules.Education.Application.AssemblyMarker).Assembly,
            typeof(LearnStack.Modules.Identity.Application.AssemblyMarker).Assembly);
        return services;
    }
}
