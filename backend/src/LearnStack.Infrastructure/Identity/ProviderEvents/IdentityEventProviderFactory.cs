using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Resilience;
using LearnStack.SharedKernel.Secrets;

namespace LearnStack.Infrastructure.Identity.ProviderEvents;

public static class IdentityEventProviderFactory
{
    public static IIdentityEventProvider Create(IdentityEventProviderOptions options, ISecretProvider secrets,
        IProviderResilience<IIdentityEventProvider> resilience) => new KeycloakIdentityEventProvider(options, secrets, resilience);
}
