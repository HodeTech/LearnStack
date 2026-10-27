using LearnStack.Application.Audit;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Permissions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LearnStack.Application.Authorization;

/// <summary>Joint composition of the frozen policy catalogue, actual invocation owner and mandatory evidence barrier.</summary>
public static class OperationAuthorizationRegistration
{
    public static IServiceCollection AddLearnStackOperationAuthorization(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOperationPolicySource, BackgroundJobOperationPolicySource>());
        services.TryAddSingleton<IOperationPolicyCatalog>(provider =>
            new OperationPolicyCatalog(provider.GetServices<IOperationPolicySource>()));
        services.TryAddScoped<OperationAuthorizationCoordinator>();
        services.TryAddScoped<AuthorizationEvidenceCoordinator>();
        services.TryAddScoped<Func<IOperationInvocationContext>>(provider =>
            () => provider.GetRequiredService<IOperationInvocationContext>());
        services.TryAddScoped<IOperationInvocationContext>(provider => provider.GetRequiredService<OperationAuthorizationCoordinator>());
        services.TryAddScoped<IAuthorizationRefusalReporter>(provider => provider.GetRequiredService<AuthorizationEvidenceCoordinator>());
        return services;
    }
}
