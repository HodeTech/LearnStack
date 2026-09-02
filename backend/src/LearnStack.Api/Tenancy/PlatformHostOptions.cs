using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Api.Tenancy;

/// <summary>
/// The short static list of hosts that map to <b>no</b> tenant —
/// <c>Tenancy:PlatformHosts</c>.
/// </summary>
/// <remarks>
/// <para>
/// The Studio / Portal entry host, and in development <c>localhost</c>. A request
/// arriving on one of these is classified <see cref="HostClass.Platform"/> and
/// never reaches the resolver, so it costs no database round trip.
/// </para>
/// <para>
/// <b>Not a second mapping authority.</b> <c>platform_host_to_tenant</c> remains
/// the only answer to "which tenant is this host?"; this list only names hosts
/// that have no answer, which is why it can be configuration rather than data
/// (ADR-0036 § Neutral).
/// </para>
/// <para>
/// <b>Every entry is validated at startup</b>, and the host refuses to boot on one
/// that <c>EffectiveHost.Normalize</c> does not return unchanged. The comparison
/// downstream is ordinal against an already-normalized effective host, so an entry
/// spelled <c>LocalHost</c> or <c>app.learnstack.dev.</c> would silently match
/// nothing — a platform host that quietly becomes an unknown host is a 404 on the
/// operator's own entry point, discovered in production.
/// </para>
/// </remarks>
public sealed class PlatformHostOptions
{
    /// <summary>The configuration section this binds to.</summary>
    public const string SectionName = "Tenancy:PlatformHosts";

    /// <summary>The configured hosts, empty when the section is absent.</summary>
    public IReadOnlyList<string> Hosts { get; init; } = [];

    /// <summary>
    /// Throws when any entry is not a host <c>EffectiveHost.Normalize</c> returns
    /// unchanged.
    /// </summary>
    /// <returns>The validated set, for ordinal lookup.</returns>
    public HashSet<string> Validate()
    {
        // The null check is not redundant with the comparison below it. Measured:
        // a JSON `null` element normalizes to null, so `null != null` is false and
        // the entry sails through into the set — while "" and "   " are both
        // refused. The entry is inert at request time, because Contains on a
        // non-null host never matches it; but a configuration typo that every
        // other spelling refuses at boot should not be the one that passes.
        var offenders = Hosts
            .Where(host => host is null || EffectiveHost.Normalize(host) != host)
            .Select(host => host ?? "<null>")
            .ToList();

        if (offenders.Count > 0)
        {
            throw new InvalidOperationException(
                $"{SectionName} contains {offenders.Count} entry/entries that are not "
                + $"normalized effective hosts: {string.Join(", ", offenders)}. "
                + "Each must be lowercase, punycoded, without a port and without a trailing "
                + "dot — the form EffectiveHost.Normalize produces — because the runtime "
                + "comparison is ordinal against an already-normalized host. An entry in any "
                + "other spelling matches nothing and turns the platform entry point into a "
                + "404 that only production reveals.");
        }

        return new HashSet<string>(Hosts, StringComparer.Ordinal);
    }
}
