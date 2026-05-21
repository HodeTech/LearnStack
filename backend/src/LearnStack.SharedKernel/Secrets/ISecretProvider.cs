namespace LearnStack.SharedKernel.Secrets;

/// <summary>
/// Composition-root-resolved secret provider. Per
/// <see href="../../../docs/standards/20-infrastructure-stack.md">Standards 20 § ISecretProvider</see>
/// and ADR-0032 § Sub-decision 9, every secret-bearing value (Sentry DSN,
/// signed-license RSA key paths, provider API keys, …) is read through
/// this contract — modules never call <c>Environment.GetEnvironmentVariable</c>
/// or hand-roll their own Vault clients.
/// </summary>
/// <remarks>
/// <para>
/// Phase 02a Packet 3 ships the contract + the default
/// <see cref="ConfigurationSecretProvider"/> implementation that delegates
/// to <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>. Packet 5 adds the
/// <c>DaprSecretProvider</c> (Vault-backed) and the composition root branches by
/// <c>DeploymentMode</c>.
/// </para>
/// <para>
/// The interface is intentionally synchronous: most secret reads happen at
/// startup time, and Vault offers a synchronous fetch path. A future
/// async overload may land alongside the Dapr-backed implementation if a
/// hot-path use case appears.
/// </para>
/// </remarks>
public interface ISecretProvider
{
    /// <summary>
    /// Resolves the secret identified by <paramref name="key"/>. Returns
    /// <c>null</c> when the secret is not configured — callers decide
    /// whether to fail fast or fall back to a default.
    /// </summary>
    /// <param name="key">The secret path, e.g. <c>"ErrorTracking:Sentry:Dsn"</c>.</param>
    string? GetSecret(string key);
}
