using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Resilience;
using LearnStack.SharedKernel.Secrets;

namespace LearnStack.Infrastructure.Identity.Federation;

/// <summary>
/// The composition root's only entry to the federation adapter and egress fetcher (ADR-0099). It returns the ports, never
/// the concrete types, so nothing outside this namespace can name the admin client or its transport.
/// </summary>
public static class FederationAdapterFactory
{
    public static IFederationProvider CreateProvider(KeycloakFederationOptions options, ISecretProvider secrets,
        IProviderResilience<IFederationProvider> resilience) => new KeycloakFederationProvider(options, secrets, resilience);

    /// <summary>The operator's adapter: the same client and routes under its own resilience pipeline (ADR-0099 Amendment 10).</summary>
    public static IFederationOperator CreateOperator(KeycloakFederationOptions options, ISecretProvider secrets,
        IProviderResilience<IFederationOperator> resilience) => new KeycloakFederationProvider(options, secrets, resilience);

    public static IFederationEgressFetcher CreateEgressFetcher(FederationEgressOptions options) => new FederationEgressFetcher(options);

    /// <summary>The secret key the composition root checks before selecting the real adapter.</summary>
    public static string SecretKey => KeycloakFederationProvider.SecretKey;
}
