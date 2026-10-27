using LearnStack.Modules.Identity.Application.Contracts.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LearnStack.Modules.Identity.Infrastructure.Authorization;

public static class IdentityAuthorizationRegistration
{
    /// <summary>Registers the confined reader; source qualification and production admission are separate composition acts.</summary>
    public static IServiceCollection AddIdentityAuthorizationReader(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<IIdentityAuthorizationReader, PreContextIdentityAuthorizationReader>();
        return services;
    }
}
