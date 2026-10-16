using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>Closed API composition of the one process gate, routing store and runtime.</summary>
public static class BackgroundJobRuntimeRegistration
{
    public static IServiceCollection AddLearnStackBackgroundJobRuntime(this IServiceCollection services,
        BackgroundJobPhysicalPoolSettings poolSettings)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(poolSettings);
        services.TryAddSingleton(poolSettings);
        services.TryAddSingleton<HangfireBoundedLogProvider>();
        services.TryAddSingleton(_ => BackgroundJobRecurrenceRegistry.CreateEmpty());
        services.TryAddSingleton(CreateGate);
        services.TryAddSingleton(CreateRoutingStore);
        services.TryAddSingleton(CreateRuntime);
        return services;
    }

    private static BackgroundJobRuntimeGate CreateGate(IServiceProvider services) =>
        new(services.GetRequiredService<BackgroundJobDefinitionRegistry>().Infrastructure);

    private static PostgresBackgroundJobRoutingStore CreateRoutingStore(IServiceProvider services) => new(
        services.GetRequiredKeyedService<PrivilegedDataSource>(OutboxDispatcherDataSource.Key),
        services.GetRequiredService<BackgroundJobDefinitionRegistry>(),
        services.GetRequiredService<BackgroundJobRuntimeOptions>(),
        services.GetRequiredService<IGuidFactory>(),
        services.GetRequiredService<BackgroundJobRuntimeGate>());

    private static BackgroundJobRuntime CreateRuntime(IServiceProvider services) => new(
        services.GetRequiredKeyedService<PrivilegedDataSource>(OutboxDispatcherDataSource.Key),
        services.GetRequiredService<BackgroundJobDefinitionRegistry>(),
        services.GetRequiredService<BackgroundJobRecurrenceRegistry>(),
        services.GetRequiredService<JobExecutor>(),
        services.GetRequiredService<PostgresBackgroundJobRoutingStore>(),
        services.GetRequiredService<BackgroundJobRuntimeGate>(),
        services.GetRequiredService<BackgroundJobRuntimeOptions>(),
        services.GetRequiredService<IGuidFactory>(),
        services.GetRequiredService<TimeProvider>(),
        services.GetRequiredService<BackgroundJobPhysicalPoolSettings>(),
        services.GetRequiredService<HangfireBoundedLogProvider>());
}
