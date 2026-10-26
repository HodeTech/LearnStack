using LearnStack.Api.Authentication;
using LearnStack.Api.Tenancy;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LearnStack.Tests.Integration;

/// <summary>The production Program and stock bearer handler; only persistence ports are replaced.</summary>
internal sealed class TenantAuthenticationApiFactory(TenantAuthenticationIssuer issuer,
    ITenantIdentityBindingResolver? bindings = null) : ConstructionOnlyApiFactory
{
    internal ReconciliationPorts Ports { get; } = new();
    internal ReconciliationRecorder Recorder =>
        (ReconciliationRecorder)Services.GetRequiredService<ITenantAssertionRecorder>();

    protected override void ConfigureTestHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting(TenantAuthenticationOptions.IssuerConfigurationKey, issuer.Issuer);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(bindings ?? (ITenantIdentityBindingResolver)new FixtureTenantIdentityBindingResolver(
                issuer.Issuer, ["opaque-keycloak-subject"]));
            services.AddSingleton(new PlatformHostOptions
            {
                Hosts = [TenantAuthenticationReconciliationHost.PlatformHost],
            });
            services.AddSingleton<IHostToTenantResolver>(Ports);
            services.AddSingleton<ITenantMembershipReader>(Ports);
            services.AddSingleton<IOrganizationScopeValidator>(Ports);
            services.AddSingleton<ITenantAssertionRecorder>(provider => new ReconciliationRecorder(
                provider.GetRequiredService<LoggingTenantAssertionRecorder>()));
        });
    }
}

internal static class TenantAuthenticationHttpRequest
{
    internal const string AccountPath = "/api/v1/account/site";
    internal const string PublicPath = "/api/v1/public/site";

    internal static async Task<HttpResponseMessage> Send(HttpClient client, string host, string path = AccountPath,
        string? token = null, string? authorization = null, string? tenantAssertion = null,
        string? organizationAssertion = null, string method = "GET")
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Host = host;
        if (authorization is not null || token is not null)
            request.Headers.TryAddWithoutValidation("Authorization", authorization ?? "Bearer " + token);
        if (tenantAssertion is not null)
            request.Headers.TryAddWithoutValidation(TenantAssertionMiddleware.TenantHeaderName, tenantAssertion);
        if (organizationAssertion is not null)
            request.Headers.TryAddWithoutValidation(TenantAssertionMiddleware.OrganizationHeaderName, organizationAssertion);
        return await client.SendAsync(request);
    }
}
