using Microsoft.Extensions.Configuration;

namespace LearnStack.SharedKernel.Secrets;

/// <summary>
/// Default <see cref="ISecretProvider"/> that delegates to
/// <see cref="IConfiguration"/>. Phase 02a Packet 3 ships this as the
/// composition-root default for every <c>DeploymentMode</c>. Per ADR-0035 the
/// Dapr/Vault-backed implementation is demand-gated to Phase 11, triggered by
/// secrets needing rotation without a redeploy or a non-development deployment.
/// </summary>
/// <remarks>
/// The configuration layer already merges environment variables, user
/// secrets, and <c>appsettings.{env}.json</c>, so the default covers
/// developer workstations and CI without spinning up Vault. Production-
/// grade deployments override the registration in the composition root.
/// </remarks>
public sealed class ConfigurationSecretProvider(IConfiguration configuration) : ISecretProvider
{
    private readonly IConfiguration _configuration = configuration
        ?? throw new ArgumentNullException(nameof(configuration));

    public string? GetSecret(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var value = _configuration[key];
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
