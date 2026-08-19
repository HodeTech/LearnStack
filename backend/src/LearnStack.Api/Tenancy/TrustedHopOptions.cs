using System.Net;

namespace LearnStack.Api.Tenancy;

/// <summary>
/// Who may state the visitor's host on the API's behalf, per
/// <see href="../../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036
/// § Effective host and the trusted hop</see>.
/// </summary>
/// <remarks>
/// <b>Both conditions, never either.</b> Network position alone fails on a
/// Docker bridge or a pod CIDR, where everything in the mesh is the gateway's
/// neighbour. A secret alone fails if it leaks into a bundle or a log. No
/// single mistake is a tenant boundary — ADR-0003's posture, applied to the
/// input rather than to the path.
/// </remarks>
public sealed class TrustedHopOptions
{
    public const string SectionName = "Tenancy:TrustedHop";

    /// <summary>
    /// The header the hop states the visitor's host in. It names a
    /// <b>host</b>, never a tenant: the value is a lookup key whose codomain is
    /// the contents of <c>platform_host_to_tenant</c>, and LearnStack still
    /// performs the lookup.
    /// </summary>
    public const string HostHeaderName = "X-LearnStack-Host";

    /// <summary>
    /// The shared secret header. Named <c>-Secret</c>, not <c>-Key</c>:
    /// <c>SensitiveTokenCatalog</c> carries <c>secret</c> as a redaction token
    /// and does not carry bare <c>key</c>, so Serilog and the error tracker
    /// already redact this one for free.
    /// </summary>
    public const string SecretHeaderName = "X-LearnStack-Hop-Secret";

    /// <summary>
    /// The minimum length of a hop secret. Short enough to type is short
    /// enough to guess offline.
    /// </summary>
    public const int MinimumSecretLength = 32;

    /// <summary>
    /// CIDR networks whose socket peers may be the hop. Compared against the
    /// <b>socket</b> peer, never against a forwarded client address.
    /// </summary>
    public IReadOnlyList<string> Networks { get; init; } = [];

    /// <summary>
    /// Accepted secrets. A list rather than one value so a rotation can
    /// overlap: the new secret is added, deployed, then the old one removed.
    /// </summary>
    public IReadOnlyList<string> Secrets { get; init; } = [];

    /// <summary>
    /// True when a hop could be recognised at all. A host with no networks and
    /// no secrets simply has no trusted hop, which is the correct state for a
    /// deployment where nothing fronts the API.
    /// </summary>
    public bool IsConfigured => Networks.Count > 0 && Secrets.Count > 0;

    /// <summary>
    /// Parses <see cref="Networks"/> once, dropping entries that are not CIDR.
    /// Returns the reason for each rejection so the composition root can refuse
    /// to start rather than silently trusting a shorter list than the operator
    /// wrote.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        foreach (var network in Networks)
        {
            if (!IPNetwork.TryParse(network, out _))
            {
                problems.Add($"'{network}' is not a CIDR network.");
            }
        }

        foreach (var secret in Secrets)
        {
            if (string.IsNullOrWhiteSpace(secret) || secret.Length < MinimumSecretLength)
            {
                problems.Add(
                    $"a hop secret is shorter than {MinimumSecretLength} characters.");
            }
        }

        return problems;
    }
}
