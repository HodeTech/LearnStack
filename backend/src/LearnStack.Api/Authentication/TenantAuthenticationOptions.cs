using LearnStack.SharedKernel.Hosting;

namespace LearnStack.Api.Authentication;

internal sealed class TenantAuthenticationOptions
{
    public const string IssuerConfigurationKey = "Authentication:Tenant:Issuer";
    public const string Audience = "learnstack-api";
    public const string ClientId = "learnstack-web";
    public const int MaxTokenBytes = 16 * 1024;

    public static TimeSpan ClockSkew { get; } = TimeSpan.FromSeconds(30);

    private TenantAuthenticationOptions(string issuer)
    {
        Issuer = issuer;
        MetadataAddress = issuer + "/.well-known/openid-configuration";
    }

    public string Issuer { get; }
    public string MetadataAddress { get; }

    public static TenantAuthenticationOptions FromConfiguration(
        IConfiguration configuration,
        DeploymentMode deploymentMode
    ) => Create(configuration[IssuerConfigurationKey], deploymentMode);

    public static TenantAuthenticationOptions Create(string? issuer, DeploymentMode deploymentMode)
    {
        if (
            string.IsNullOrEmpty(issuer)
            || !Uri.TryCreate(issuer, UriKind.Absolute, out var address)
            || !address.IsWellFormedOriginalString()
            || !string.Equals(address.AbsoluteUri, issuer, StringComparison.Ordinal)
            || address.UserInfo.Length != 0
            || address.Query.Length != 0
            || address.Fragment.Length != 0
            || !address.AbsolutePath.EndsWith("/realms/learnstack", StringComparison.Ordinal)
            || !IsAllowedTransport(address, deploymentMode)
        )
        {
            throw new InvalidOperationException(
                "Authentication:Tenant:Issuer must be a canonical absolute HTTPS tenant-realm "
                    + "URL without userinfo, query or fragment. Loopback HTTP is allowed only "
                    + "in Development."
            );
        }

        return new TenantAuthenticationOptions(issuer);
    }

    private static bool IsAllowedTransport(Uri address, DeploymentMode deploymentMode) =>
        address.Scheme == Uri.UriSchemeHttps
        || (
            address.Scheme == Uri.UriSchemeHttp
            && address.IsLoopback
            && deploymentMode == DeploymentMode.Development
        );
}
