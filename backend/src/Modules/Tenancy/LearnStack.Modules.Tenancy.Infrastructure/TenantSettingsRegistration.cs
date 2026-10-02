using LearnStack.Modules.Tenancy.Application.Contracts.Settings;
using LearnStack.Modules.Tenancy.Application.Settings;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LearnStack.Modules.Tenancy.Infrastructure;

public static class TenantSettingsRegistration
{
    public static IServiceCollection AddTenantSettingsReads(this IServiceCollection services)
    {
        services.TryAddSingleton(TenantSettingRegistry.Default);
        services.TryAddScoped<ITenantSettingsAccessor, TenantSettingsAccessor>();
        return services;
    }
}
