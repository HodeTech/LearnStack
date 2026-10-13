using LearnStack.Api.Messaging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LearnStack.Tests.Integration;

/// <summary>
/// Request/composition proofs own no background dispatcher. Production startup
/// proofs use an ordinary factory and must supply its confined credential.
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
        builder.ConfigureTestServices(RemoveDispatcher);
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
                     && descriptor.ImplementationType == typeof(OutboxProcessor)).ToArray())
        {
            services.Remove(descriptor);
        }
    }
}
