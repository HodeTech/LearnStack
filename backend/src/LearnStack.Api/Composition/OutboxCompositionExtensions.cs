using LearnStack.Infrastructure.Persistence;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Api.Messaging;
using LearnStack.Infrastructure.Messaging;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Time;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace LearnStack.Api.Composition;

/// <summary>Registers producer/subscriber dispatch and its API-owned dispatcher credential.</summary>
public static class OutboxCompositionExtensions
{
    public static IServiceCollection AddLearnStackOutboxDispatcher(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString(OutboxDispatcherDataSource.ConnectionName);
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            OutboxDispatcherDataSource.Validate(connectionString);
        }

        // The keyed source is container-owned. Lazy defers construction while
        // construction-only test hosts remove the production worker; the worker's
        // startup must resolve and open it, including when configuration is absent.
        services.TryAddKeyedSingleton<PrivilegedDataSource>(
            OutboxDispatcherDataSource.Key,
            (_, _) => new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(connectionString)));

        services.TryAddSingleton(new OutboxDispatchOptions());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<OutboxEnvelopeReader>();
        services.TryAddSingleton<OutboxObservation>();
        services.TryAddSingleton(provider => new OutboxDispatchStore(
            provider.GetRequiredKeyedService<PrivilegedDataSource>(OutboxDispatcherDataSource.Key),
            provider.GetRequiredService<OutboxEventRegistry>(),
            provider.GetRequiredService<OutboxDispatchOptions>(),
            provider.GetRequiredService<IGuidFactory>(),
            provider.GetRequiredService<IClock>(),
            provider.GetRequiredService<IServiceScopeFactory>()));
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(OutboxProcessor)))
        {
            services.AddHostedService<OutboxProcessor>();
            services.AddHealthChecks().AddCheck<OutboxHealthCheck>(OutboxHealthCheck.Name);
        }
        services.TryAddSingleton(new DeliveryDispatchOptions());
        services.TryAddSingleton<DeliveryExecutor>();
        services.TryAddSingleton<DeliveryObservation>();
        services.TryAddSingleton(provider => new DeliveryDispatchStore(
            provider.GetRequiredKeyedService<PrivilegedDataSource>(OutboxDispatcherDataSource.Key),
            provider.GetRequiredService<OutboxEventRegistry>(),
            provider.GetRequiredService<DeliveryDispatchOptions>(),
            provider.GetRequiredService<IGuidFactory>(),
            provider.GetRequiredService<IClock>(),
            provider.GetRequiredService<IServiceScopeFactory>()));
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(DeliveryProcessor)))
        {
            services.AddHostedService<DeliveryProcessor>();
            services.AddHealthChecks().AddCheck<DeliveryHealthCheck>(DeliveryHealthCheck.Name);
        }
        services.TryAddSingleton(provider => new PostgresBackgroundJobRoutingStore(
            provider.GetRequiredKeyedService<PrivilegedDataSource>(OutboxDispatcherDataSource.Key),
            provider.GetRequiredService<BackgroundJobDefinitionRegistry>(),
            provider.GetRequiredService<BackgroundJobRuntimeOptions>(),
            provider.GetRequiredService<IGuidFactory>()));
        return services;
    }
}
