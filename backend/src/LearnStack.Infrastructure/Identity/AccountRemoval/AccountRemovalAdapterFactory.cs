using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Resilience;
using LearnStack.SharedKernel.Secrets;

namespace LearnStack.Infrastructure.Identity.AccountRemoval;

/// <summary>
/// The composition root's only entry to the account-removal adapter (ADR-0107 § 7). It returns the port, never the concrete
/// type, so nothing outside this namespace can name the confined client or its transport.
/// </summary>
public static class AccountRemovalAdapterFactory
{
    /// <summary>The fixed provider-resilience port name: <c>Resilience:accountRemoval:</c>.</summary>
    public const string PortName = "accountRemoval";

    /// <summary>The adapter's whole-call ceiling in seconds, token and deletion together; the composition root fixes the pipeline timeout to it.</summary>
    public const double CallCeilingSeconds = 5;

    public static IIdentityProviderAccountRemoval Create(KeycloakAccountRemovalOptions options, ISecretProvider secrets,
        IProviderResilience<IIdentityProviderAccountRemoval> resilience) => new KeycloakAccountRemoval(options, secrets, resilience);

    /// <summary>The secret key the composition root checks before selecting the real adapter.</summary>
    public static string SecretKey => KeycloakAccountRemoval.SecretKey;
}
