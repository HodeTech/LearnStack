using LearnStack.Api.Messaging;
using LearnStack.Api.Jobs;
using LearnStack.Api.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LearnStack.Tests.Contract;

/// <summary>
/// Pins the environment to <c>Development</c>. <c>WebApplicationFactory</c>
/// inherits <c>ASPNETCORE_ENVIRONMENT</c> from the test host process, which
/// defaults to <c>Production</c> under <c>dotnet test</c>
/// (<c>launchSettings.json</c> is only read by <c>dotnet run</c>).
/// </summary>
/// <remarks>
/// This used to be load-bearing for a different reason: <c>Program.cs</c>
/// gated <c>MapOpenApi()</c> on <c>IsDevelopment()</c>, so the document 404'd
/// in CI without it. Packet 4 removed that gate — the document is the contract
/// the SDK generates from and CI diffs, so it is served in every environment —
/// and the environment override keeps the contract suite on one known environment.
/// This served-contract fixture removes dispatcher and identity startup workers because
/// it owns no database. Its retained private outbox and delivery health checks
/// intentionally remain Unhealthy; the static <c>/healthz</c> liveness endpoint stays independent.
/// </remarks>
public sealed class DevelopmentWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        // This served-contract proof owns no database. Remove only the named
        // startup workers; production startup tests retain mandatory registration.
        builder.ConfigureTestServices(services =>
        {
            foreach (var descriptor in services.Where(descriptor =>
                         descriptor.ServiceType == typeof(IHostedService)
                         && IsDatabaseStartupWorker(descriptor)).ToArray())
                services.Remove(descriptor);
        });
    }

    private static bool IsDatabaseStartupWorker(ServiceDescriptor descriptor)
    {
        var implementation = descriptor.ImplementationType ?? descriptor.ImplementationFactory?.Method.ReturnType;
        return implementation == typeof(OutboxProcessor) || implementation == typeof(DeliveryProcessor)
            || implementation == typeof(BackgroundJobHostedService)
            || implementation == typeof(TenantIdentityAuthorityStartupService);
    }
}
