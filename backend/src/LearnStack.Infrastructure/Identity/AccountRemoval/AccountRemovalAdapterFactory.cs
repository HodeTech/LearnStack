using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Resilience;
using LearnStack.SharedKernel.Secrets;
using Microsoft.Extensions.Logging;

namespace LearnStack.Infrastructure.Identity.AccountRemoval;

/// <summary>
/// The composition root's only entry to the account-removal adapter (ADR-0107 § 7). It returns the port, never the concrete
/// type, so nothing outside this namespace can name the confined client or its transport.
/// </summary>
public static class AccountRemovalAdapterFactory
{
    /// <summary>The fixed provider-resilience port name: <c>Resilience:accountRemoval:</c>.</summary>
    public const string PortName = "accountRemoval";

    /// <summary>
    /// The whole-call ceiling in seconds, token and deletion together: the pipeline timeout the composition root fixes. The
    /// adapter's own cancellation is a backstop one second later, so the pipeline's timeout is what the breaker records.
    /// </summary>
    public const double CallCeilingSeconds = 5;

    public static IIdentityProviderAccountRemoval Create(KeycloakAccountRemovalOptions options, ISecretProvider secrets,
        IProviderResilience<IIdentityProviderAccountRemoval> resilience, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        return new KeycloakAccountRemoval(options, secrets, resilience, loggerFactory.CreateLogger<KeycloakAccountRemoval>());
    }

    /// <summary>The secret key the composition root checks before selecting the real adapter.</summary>
    public static string SecretKey => KeycloakAccountRemoval.SecretKey;
}
