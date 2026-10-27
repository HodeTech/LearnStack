using LearnStack.Modules.Identity.Application.Contracts.Authorization;
using LearnStack.Modules.Identity.Application;
using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Authorization;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.Permissions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LearnStack.Modules.Identity.Infrastructure.Authorization;

public static class IdentityAuthorizationRegistration
{
    /// <summary>Registers the confined reader; source qualification and production admission are separate composition acts.</summary>
    public static IServiceCollection AddIdentityAuthorizationReader(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<IIdentityAuthorizationSessionFactory, PreContextIdentityAuthorizationSessionFactory>();
        services.TryAddScoped<IIdentityAuthorizationReader, CommittedIdentityAuthorizationReader>();
        services.TryAddScoped<Func<IIdentityAuthorizationReader>>(provider => () => provider.GetRequiredService<IIdentityAuthorizationReader>());
        return services;
    }

    /// <summary>Staged internal policy and directory adapters; installs no HTTP or native operation.</summary>
    public static IServiceCollection AddIdentityOperationPolicies(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOperationPolicySource, IdentityOperationPolicySource>());
        services.TryAddScoped<IProtectedOperationAuthorizer>(provider => new IdentityOperationAuthorizer(
            () => provider.GetRequiredService<IIdentityAuthorizationReader>(), provider.GetRequiredService<IOperationPolicyCatalog>()));
        services.TryAddScoped<RoleGrantAuthorization>();
        services.TryAddScoped<Func<RoleGrantAuthorization>>(provider => () => provider.GetRequiredService<RoleGrantAuthorization>());
        services.TryAddScoped<IMembershipDirectoryReader, MembershipDirectoryReader>();
        return services;
    }
}
