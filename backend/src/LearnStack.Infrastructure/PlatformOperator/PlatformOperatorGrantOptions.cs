using LearnStack.SharedKernel.Hosting;
using LearnStack.SharedKernel.Platform;
using Microsoft.Extensions.Options;

namespace LearnStack.Infrastructure.PlatformOperator;

/// <summary>One operator and the Platform permissions LearnStack holds for them.</summary>
public sealed class PlatformOperatorGrant
{
    /// <summary>The operator's opaque <c>sub</c> at the configured issuer.</summary>
    public string? Subject { get; set; }

    /// <summary>The Platform permission keys granted, each registered in the catalogue.</summary>
    public IList<string> Permissions { get; init; } = [];
}

/// <summary>
/// The configuration the shipped grant adapter binds: the issuer and the held grants
/// (ADR-0101 § 6).
/// </summary>
public sealed class PlatformOperatorGrantOptions
{
    /// <summary>The configuration section both the scheme and the grants read.</summary>
    public const string SectionName = "PlatformOperator";

    /// <summary>
    /// The `sub` of the exported demo operator, pinned in the <c>learnstack-hub</c> realm
    /// export. A grant for it is valid in Development only.
    /// </summary>
    public const string DemoOperatorSubject = "5a3c8f0e-2b1d-4c6a-9e47-0d1f6a2b7c93";

    /// <summary>The pinned operator issuer; grants are meaningless without it.</summary>
    public string? Issuer { get; set; }

    /// <summary>The held grants.</summary>
    public IList<PlatformOperatorGrant> Grants { get; init; } = [];
}

/// <summary>Refuses to start on any grant that names an unregistered key or the demo operator outside Development.</summary>
public sealed class PlatformOperatorGrantOptionsValidator(DeploymentMode deploymentMode)
    : IValidateOptions<PlatformOperatorGrantOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, PlatformOperatorGrantOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Grants.Count == 0)
        {
            return ValidateOptionsResult.Success;
        }

        // Grants without an issuer are inert, not an error: with no issuer there is no operator
        // scheme and the grant source answers "not granted" for everyone, so the surface stays
        // closed. Every other rule below still applies to them.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var grant in options.Grants)
        {
            if (string.IsNullOrWhiteSpace(grant.Subject) || !seen.Add(grant.Subject))
            {
                return ValidateOptionsResult.Fail("A PlatformOperator grant needs a distinct, nonempty Subject.");
            }

            if (grant.Permissions.Count == 0 || grant.Permissions.Any(key => !PlatformPermissions.TryGet(key, out _)))
            {
                return ValidateOptionsResult.Fail(
                    "A PlatformOperator grant lists only registered Platform permission keys.");
            }

            if (string.Equals(grant.Subject, PlatformOperatorGrantOptions.DemoOperatorSubject, StringComparison.Ordinal)
                && deploymentMode != DeploymentMode.Development)
            {
                return ValidateOptionsResult.Fail(
                    "The exported demo operator may hold a grant in Development only.");
            }
        }

        return ValidateOptionsResult.Success;
    }
}
