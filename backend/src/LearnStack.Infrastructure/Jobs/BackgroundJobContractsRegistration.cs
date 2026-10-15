using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>Shared API/Seeder registration of the empty closed job catalogues and authority seam.</summary>
public static class BackgroundJobContractsRegistration
{
    /// <summary>Registers contracts without activating a definition, scheduler, vendor or worker.</summary>
    public static IServiceCollection AddLearnStackBackgroundJobContracts(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(_ => BackgroundJobDefinitionRegistry.CreateEmpty());
        services.TryAddSingleton<IBackgroundJobDefinitionCatalog>(provider =>
            provider.GetRequiredService<BackgroundJobDefinitionRegistry>());
        services.TryAddSingleton<IBackgroundJobRequestCatalog>(provider =>
            provider.GetRequiredService<BackgroundJobDefinitionRegistry>());
        services.TryAddSingleton<BackgroundJobExecutionAdmission>();
        return services;
    }
}
