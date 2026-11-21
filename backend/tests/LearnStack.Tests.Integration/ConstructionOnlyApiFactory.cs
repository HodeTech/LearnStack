using LearnStack.Api.Messaging;
using LearnStack.Api.Jobs;
using LearnStack.Api.Authentication;
using LearnStack.Tests.Fixtures;
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
        InvitationEmailFixtureConfiguration.AddTo(builder);
        // Development configuration carries an operator issuer for the real developer stack
        // (ADR-0101 § 12). A test host starts without one, so the tenant boundary is judged on its
        // own; a case about the operator surface sets the issuer itself, after this line.
        builder.UseSetting("PlatformOperator:Issuer", string.Empty);
        // Development also grants the exported demo operator, which the grant validator refuses
        // outside Development, and these hosts run under several modes. Pointing the one entry at a
        // subject nobody holds keeps the grant list valid and matches no one.
        builder.UseSetting("PlatformOperator:Grants:0:Subject", "test-host-matches-no-operator");
        builder.UseSetting("PlatformOperator:Grants:0:Permissions:0", "platform.killswitch.write");
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
                     is { } implementation && (implementation == typeof(TenantIdentityAuthorityStartupService)
                         || implementation == typeof(TenantAuthorizationSourcesStartupService))).ToArray())
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
            || implementation == typeof(BackgroundJobHostedService)
            || implementation?.FullName == "LearnStack.Api.Identity.ProviderEventMirrorService";
    }
}
