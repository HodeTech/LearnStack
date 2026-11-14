using LearnStack.Infrastructure.Identity.Federation;
using LearnStack.Infrastructure.Resilience;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Resilience;
using LearnStack.SharedKernel.Secrets;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LearnStack.Api.Composition;

/// <summary>
/// ADR-0099 composition: the confined Keycloak adapter and the guarded egress fetcher replace the fail-closed defaults
/// only when the federation admin client's secret is configured (<see cref="FederationAdapterFactory.SecretKey"/>). Otherwise both ports keep
/// their unconfigured defaults and every federation operation is unavailable without any network effect. The Development
/// check that admits a loopback HTTP issuer is made here, never in a module; modules never read DeploymentMode.
/// </summary>
internal static class FederationComposition
{
    internal const string AllowedHostsKey = "Federation:Egress:AllowedHosts";

    internal static IServiceCollection AddLearnStackFederationAdapter(this IServiceCollection services, IConfiguration configuration)
    {
        var defaults = new Dictionary<string, string?>
        {
            ["Resilience:federation:Retry:Enabled"] = "false",
            ["Resilience:federation:Retry:MaxRetryAttempts"] = "0",
            ["Resilience:federation:Timeout:Enabled"] = "true",
            ["Resilience:federation:Timeout:TotalSeconds"] = "2.5",
            ["Resilience:federation:CircuitBreaker:Enabled"] = "true",
            ["Resilience:federation:Bulkhead:MaxConcurrency"] = "4",
            ["Resilience:federation:Bulkhead:QueueLength"] = "0",
        };
        var policyConfiguration = new ConfigurationBuilder().AddInMemoryCollection(defaults).AddConfiguration(configuration).Build();
        var policy = policyConfiguration.GetSection("Resilience:federation").Get<ResilienceOptions>()!;
        // Provider writes are not safe to retry automatically (ADR-0099): a retried PUT can land after its outcome settled.
        // The shape is fixed, not a range: retry off, a 2.5-second timeout (the adapter's own call ceiling), the breaker on
        // and a bulkhead of exactly four with no queue. Any other value refuses at startup.
        if (policy.Retry.Enabled || policy.Retry.MaxRetryAttempts != 0 || !policy.Timeout.Enabled
            || policy.Timeout.TotalSeconds != 2.5
            || !policy.CircuitBreaker.Enabled || policy.Bulkhead is not { MaxConcurrency: 4, QueueLength: 0 })
            throw new InvalidOperationException("Federation requires finite provider resilience without write retries.");
        services.AddProviderResilience<IFederationProvider>(policyConfiguration, "federation");

        var egress = new FederationEgressOptions(configuration.GetSection(AllowedHostsKey).Get<string[]>() ?? []);
        services.TryAddSingleton(egress);
        services.Replace(ServiceDescriptor.Singleton<IFederationProvider>(provider =>
        {
            var secrets = provider.GetRequiredService<ISecretProvider>();
            if (!Configured(secrets)) return new UnconfiguredFederationProvider();
            var development = provider.GetRequiredService<IHostEnvironment>().IsDevelopment();
            var options = new KeycloakFederationOptions(configuration["Authentication:Tenant:Issuer"] ?? "", development);
            return FederationAdapterFactory.CreateProvider(options, secrets, provider.GetRequiredService<IProviderResilience<IFederationProvider>>());
        }));
        services.Replace(ServiceDescriptor.Singleton<IFederationEgressFetcher>(provider =>
            Configured(provider.GetRequiredService<ISecretProvider>())
                ? FederationAdapterFactory.CreateEgressFetcher(provider.GetRequiredService<FederationEgressOptions>())
                : new UnconfiguredFederationEgressFetcher()));
        return services;
    }

    private static bool Configured(ISecretProvider secrets) =>
        !string.IsNullOrEmpty(secrets.GetSecret(FederationAdapterFactory.SecretKey));
}
