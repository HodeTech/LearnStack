using LearnStack.Application.Audit;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Permissions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LearnStack.Application.Authorization;

/// <summary>Staged joint composition of actual policy ownership and its mandatory evidence barrier.</summary>
public static class OperationAuthorizationRegistration
{
    public static IServiceCollection AddLearnStackOperationAuthorization(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<OperationAuthorizationCoordinator>();
        services.TryAddScoped<AuthorizationEvidenceCoordinator>();
        services.TryAddScoped<Func<IOperationInvocationContext>>(provider =>
            () => provider.GetRequiredService<IOperationInvocationContext>());
        services.Replace(ServiceDescriptor.Scoped<IOperationInvocationContext>(provider => provider.GetRequiredService<OperationAuthorizationCoordinator>()));
        services.Replace(ServiceDescriptor.Scoped<IAuthorizationRefusalReporter>(provider => provider.GetRequiredService<AuthorizationEvidenceCoordinator>()));
        return services;
    }
}
