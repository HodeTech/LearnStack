using LearnStack.Api.Messaging;
using LearnStack.Api.Jobs;
using LearnStack.Api.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LearnStack.Tests.Integration;

/// <summary>
/// Request/composition proofs own no background dispatcher or identity authority.
/// Real identity derivatives explicitly retain qualification; production worker
/// proofs use an ordinary factory and supply their confined credentials.
/// </summary>
/// <remarks>
/// The retained private outbox health check intentionally remains Unhealthy when
/// its worker is removed. The static <c>/healthz</c> liveness endpoint is independent.
/// </remarks>
public class ConstructionOnlyApiFactory : WebApplicationFactory<Program>
{
    protected sealed override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ConfigureTestHost(builder);
        builder.ConfigureTestServices(services =>
        {
            RemoveDispatcher(services);
            if (!RetainIdentityQualification)
                RemoveIdentityQualification(services);
        });
    }

    /// <summary>Real provider/database derivatives retain the production startup qualification.</summary>
    protected virtual bool RetainIdentityQualification => false;

    internal static void RemoveIdentityQualification(IServiceCollection services)
    {
        foreach (var descriptor in services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                     && (descriptor.ImplementationType ?? descriptor.ImplementationFactory?.Method.ReturnType)
                     == typeof(TenantIdentityAuthorityStartupService)).ToArray())
            services.Remove(descriptor);
    }

    protected virtual void ConfigureTestHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
    }

    internal static void RemoveDispatcher(IServiceCollection services)
    {
        // Leave every other hosted service intact, including telemetry. This is
        // test composition, never a production flag or a blanket hosted-service ban.
        foreach (var descriptor in services.Where(descriptor =>
                     descriptor.ServiceType == typeof(IHostedService)
                     && IsDispatcher(descriptor)).ToArray())
        {
            services.Remove(descriptor);
        }
    }

    private static bool IsDispatcher(ServiceDescriptor descriptor)
    {
        var implementation = descriptor.ImplementationType ?? descriptor.ImplementationFactory?.Method.ReturnType;
        return implementation == typeof(OutboxProcessor) || implementation == typeof(DeliveryProcessor)
            || implementation == typeof(BackgroundJobHostedService);
    }
}
