using LearnStack.Infrastructure.Persistence;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Api.Messaging;
using LearnStack.Api.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Time;
using LearnStack.SharedKernel.Jobs;
using LearnStack.Modules.Tenancy.Infrastructure.Jobs;
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
        services.TryAddSingleton<OutboxProcessor>();
        services.AddHostedService(ResolveOutboxHostedService);
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(OutboxHealthCheck)))
        {
            services.AddSingleton<OutboxHealthCheck>();
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
        services.TryAddSingleton<DeliveryProcessor>();
        services.AddHostedService(ResolveDeliveryHostedService);
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(DeliveryHealthCheck)))
        {
            services.AddSingleton<DeliveryHealthCheck>();
            services.AddHealthChecks().AddCheck<DeliveryHealthCheck>(DeliveryHealthCheck.Name);
        }
        // Capture only nonsecret capacity settings from the same immutable input
        // as the existing lazy source; jobs never create a second source/pool.
        var parsed = new NpgsqlConnectionStringBuilder(connectionString);
        services.TryAddSingleton<BackgroundJobLifecycleProjection>(BackgroundJobLifecycleProjectionDeclaration.Create());
        services.AddLearnStackBackgroundJobRuntime(new(parsed.Pooling, parsed.MaxPoolSize));
        services.TryAddSingleton<BackgroundJobHostedService>();
        services.AddHostedService(ResolveJobHostedService);
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(BackgroundJobHealthCheck)))
        {
            services.AddSingleton<BackgroundJobHealthCheck>();
            services.AddHealthChecks().AddCheck<BackgroundJobHealthCheck>(BackgroundJobHealthCheck.Name);
        }
        return services;
    }

    private static OutboxProcessor ResolveOutboxHostedService(IServiceProvider services) =>
        services.GetRequiredService<OutboxProcessor>();

    private static DeliveryProcessor ResolveDeliveryHostedService(IServiceProvider services) =>
        services.GetRequiredService<DeliveryProcessor>();

    private static BackgroundJobHostedService ResolveJobHostedService(IServiceProvider services) =>
        services.GetRequiredService<BackgroundJobHostedService>();
}
