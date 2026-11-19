using LearnStack.SharedKernel.Hosting;

namespace LearnStack.Api.Authentication;

/// <summary>
/// The validated, immutable operator-scheme configuration; absent when the issuer is not
/// configured, in which case no operator scheme and no operator route exists (ADR-0101 § 2, § 4).
/// </summary>
internal sealed class PlatformOperatorSettings
{
    internal const string SectionName = "PlatformOperator";
    internal const string Audience = "learnstack-platform";
    internal const string RoutePrefix = "/api/platform";
    internal const string Executor = "operator.api";

    internal static TimeSpan ClockSkew { get; } = TimeSpan.FromSeconds(30);

    /// <summary>The longest <c>exp - iat</c> a token may carry.</summary>
    internal static TimeSpan MaxTokenLifetime { get; } = TimeSpan.FromMinutes(15);

    private PlatformOperatorSettings(
        string issuer, HashSet<string> acceptedAcr, HashSet<string> authorizedParties, TimeSpan maxAuthenticationAge)
    {
        Issuer = issuer;
        MetadataAddress = issuer + "/.well-known/openid-configuration";
        AcceptedAcr = acceptedAcr;
        AuthorizedParties = authorizedParties;
        MaxAuthenticationAge = maxAuthenticationAge;
    }

    internal string Issuer { get; }
    internal string MetadataAddress { get; }
    internal IReadOnlySet<string> AcceptedAcr { get; }
    internal IReadOnlySet<string> AuthorizedParties { get; }
    internal TimeSpan MaxAuthenticationAge { get; }

    /// <summary>Returns null when no issuer is configured; throws when the configuration is unsafe.</summary>
    internal static PlatformOperatorSettings? FromConfiguration(IConfiguration configuration, DeploymentMode deploymentMode)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(SectionName);
        var issuer = section["Issuer"];
        if (string.IsNullOrWhiteSpace(issuer))
        {
            return null;
        }

        if (!Uri.TryCreate(issuer, UriKind.Absolute, out var address)
            || !address.IsWellFormedOriginalString()
            || !string.Equals(address.AbsoluteUri, issuer, StringComparison.Ordinal)
            || address.UserInfo.Length != 0
            || address.Query.Length != 0
            || address.Fragment.Length != 0
            || !(address.Scheme == Uri.UriSchemeHttps
                 || (address.Scheme == Uri.UriSchemeHttp && address.IsLoopback
                     && deploymentMode == DeploymentMode.Development)))
        {
            throw new InvalidOperationException(
                "PlatformOperator:Issuer must be a canonical absolute HTTPS URL without userinfo, query or "
                + "fragment. Loopback HTTP is allowed only in Development.");
        }

        var acr = ReadSet(section.GetSection("AcceptedAcr"));
        var parties = ReadSet(section.GetSection("AuthorizedParties"));
        if (acr.Count == 0 || parties.Count == 0)
        {
            throw new InvalidOperationException(
                "PlatformOperator needs at least one AcceptedAcr and one AuthorizedParties value when "
                + "an Issuer is configured; assurance and client are never inferred.");
        }

        var ageText = section["MaxAuthenticationAge"];
        var age = TimeSpan.FromMinutes(15);
        if (ageText is not null && (!TimeSpan.TryParse(ageText, System.Globalization.CultureInfo.InvariantCulture, out age)
            || age <= TimeSpan.Zero || age > TimeSpan.FromHours(1)))
        {
            throw new InvalidOperationException("PlatformOperator:MaxAuthenticationAge must be between 0 and 1 hour.");
        }

        return new PlatformOperatorSettings(issuer, acr, parties, age);
    }

    private static HashSet<string> ReadSet(IConfigurationSection section) =>
        section.GetChildren().Select(child => child.Value).Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!).ToHashSet(StringComparer.Ordinal);
}
