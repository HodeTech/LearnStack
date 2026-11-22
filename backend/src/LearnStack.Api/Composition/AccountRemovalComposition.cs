using LearnStack.Infrastructure.Identity.AccountRemoval;
using LearnStack.Infrastructure.Resilience;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Resilience;
using LearnStack.SharedKernel.Secrets;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LearnStack.Api.Composition;

/// <summary>
/// ADR-0107 § 7 composition: the confined Keycloak account-removal adapter replaces the fail-closed
/// <see cref="RefusingIdentityProviderAccountRemoval"/> only when the confined client's secret is configured
/// (<see cref="AccountRemovalAdapterFactory.SecretKey"/>). Otherwise the port keeps its default and every call is refused
/// without any network effect. The Development check that admits a loopback HTTP issuer is made here, never in a module;
/// modules never read DeploymentMode.
/// </summary>
internal static class AccountRemovalComposition
{
    internal const string ConfigurationSection = "Resilience:" + AccountRemovalAdapterFactory.PortName;

    internal static IServiceCollection AddLearnStackAccountRemovalAdapter(this IServiceCollection services, IConfiguration configuration)
    {
        var defaults = new Dictionary<string, string?>
        {
            [ConfigurationSection + ":Retry:Enabled"] = "false",
            [ConfigurationSection + ":Retry:MaxRetryAttempts"] = "0",
            [ConfigurationSection + ":Timeout:Enabled"] = "true",
            [ConfigurationSection + ":Timeout:TotalSeconds"] = "5",
            [ConfigurationSection + ":CircuitBreaker:Enabled"] = "true",
            [ConfigurationSection + ":Bulkhead:MaxConcurrency"] = "1",
            [ConfigurationSection + ":Bulkhead:QueueLength"] = "0",
        };
        var policyConfiguration = new ConfigurationBuilder().AddInMemoryCollection(defaults).AddConfiguration(configuration).Build();
        var policy = policyConfiguration.GetSection(ConfigurationSection).Get<ResilienceOptions>()!;
        // The shape is fixed, not a range. Retry is off: the closure's Finalize is the repeatable, operator-visible retry, and an
        // automatic repeat after a lost answer would hide that answer behind a quiet "absent". The timeout is the adapter's own
        // call ceiling (token and deletion together), the breaker is on within the federation range, and the bulkhead admits
        // one call with no queue: the sole caller is a deliberate, serialized operator act, and a second concurrent call is
        // refused as unavailable and repeated. Any other value refuses at startup.
        if (policy.Retry.Enabled || policy.Retry.MaxRetryAttempts != 0 || !policy.Timeout.Enabled
            || policy.Timeout.TotalSeconds != AccountRemovalAdapterFactory.CallCeilingSeconds
            || !policy.CircuitBreaker.Enabled || !FederationComposition.BreakerInRange(policy.CircuitBreaker)
            || policy.Bulkhead is not { MaxConcurrency: 1, QueueLength: 0 })
            throw new InvalidOperationException("Account removal requires finite provider resilience without retries.");
        services.AddProviderResilience<IIdentityProviderAccountRemoval>(policyConfiguration, AccountRemovalAdapterFactory.PortName);

        services.Replace(ServiceDescriptor.Singleton<IIdentityProviderAccountRemoval>(provider =>
        {
            var secrets = provider.GetRequiredService<ISecretProvider>();
            if (string.IsNullOrEmpty(secrets.GetSecret(AccountRemovalAdapterFactory.SecretKey))) return new RefusingIdentityProviderAccountRemoval();
            var development = provider.GetRequiredService<IHostEnvironment>().IsDevelopment();
            var options = new KeycloakAccountRemovalOptions(configuration["Authentication:Tenant:Issuer"] ?? "", development);
            return AccountRemovalAdapterFactory.Create(options, secrets,
                provider.GetRequiredService<IProviderResilience<IIdentityProviderAccountRemoval>>());
        }));
        return services;
    }
}
