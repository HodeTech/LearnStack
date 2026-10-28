using LearnStack.Modules.Tenancy.Application.Contracts.Account;
using LearnStack.Modules.Tenancy.Application.Contracts.PublicReads;
using LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LearnStack.Modules.Tenancy.Infrastructure;

/// <summary>Shared site-read composition for the API and Seeder.</summary>
public static class TenantSiteRegistration
{
    public static IServiceCollection AddTenantSiteReads(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(new ExplicitReadOnlyRequest(typeof(GetAccountSiteQuery)));
        services.TryAddScoped<IPublicTenantConfigurationReader, PublicTenantConfigurationReader>();
        services.TryAddScoped<IOwnMembershipScopeLabels, PreContextSelfDiscoveryScopeReader>();
        return services;
    }
}
