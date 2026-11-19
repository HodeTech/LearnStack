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
                + "an Issuer is configured, as indexed keys such as PlatformOperator__AcceptedAcr__0. "
                + "Assurance and client are never inferred.");
        }

        // OpenID Connect reserves 0 for authentication no stronger than a single factor, and
        // many issuers emit 1 for a password: an accepted acr that is one of those is a
        // misconfiguration that would turn the multi-factor requirement into a formality.
        if (acr.Any(value => value is "0" or "1"))
        {
            throw new InvalidOperationException(
                "PlatformOperator:AcceptedAcr must name a multi-factor assurance value the issuer emits; "
                + "'0' and '1' are single-factor levels and are refused.");
        }

        var tenantIssuer = configuration[TenantAuthenticationOptions.IssuerConfigurationKey];
        if (!string.IsNullOrWhiteSpace(tenantIssuer)
            && string.Equals(tenantIssuer.TrimEnd('/'), issuer.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "PlatformOperator:Issuer must not be the tenant issuer: operators authenticate at a "
                + "deployment-controlled issuer that tenant administrators cannot influence.");
        }

        var ageText = section["MaxAuthenticationAge"];
        var age = TimeSpan.FromMinutes(15);
        if (ageText is not null && (!TimeSpan.TryParse(ageText, System.Globalization.CultureInfo.InvariantCulture, out age)
            || age <= TimeSpan.Zero || age > TimeSpan.FromHours(1)))
        {
            throw new InvalidOperationException(
                "PlatformOperator:MaxAuthenticationAge must be a time span between 0 and 1 hour, "
                + "for example 00:15:00.");
        }

        return new PlatformOperatorSettings(issuer, acr, parties, age);
    }

    private static HashSet<string> ReadSet(IConfigurationSection section) =>
        section.GetChildren().Select(child => child.Value).Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!).ToHashSet(StringComparer.Ordinal);
}
