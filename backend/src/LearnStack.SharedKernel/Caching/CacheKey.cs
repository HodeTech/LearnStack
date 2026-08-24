namespace LearnStack.SharedKernel.Caching;

/// <summary>
/// Builds and validates the one cache-key shape
/// <see href="../../../../docs/standards/20-infrastructure-stack.md">Standards 20
/// § Cache</see> admits: <c>{tenant_id}:{module}:{logical-name}</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The tenant segment is mandatory, and that is the whole point.</b> A cache is
/// a lookup keyed by a string, so a key that omits the tenant is a key two tenants
/// can both compute — and the second one reads the first one's value. There is no
/// query filter and no RLS policy in front of a dictionary; the key is the entire
/// isolation boundary, which is why it is validated here rather than left to each
/// call site to remember.
/// </para>
/// <para>
/// A platform-wide value uses the <see cref="PlatformTenant"/> sentinel rather
/// than omitting the segment. "No tenant" and "every tenant" then look different
/// in a key dump, and the rule stays one rule.
/// </para>
/// </remarks>
public static class CacheKey
{
    /// <summary>The tenant segment a platform-wide value carries.</summary>
    public const string PlatformTenant = "platform";

    /// <summary>The separator between the three segments.</summary>
    public const char Separator = ':';

    /// <summary>Composes a key for a tenant-owned value.</summary>
    public static string For(Guid tenantId, string module, string logicalName) =>
        For(tenantId.ToString(), module, logicalName);

    /// <summary>Composes a key for a platform-wide value.</summary>
    public static string ForPlatform(string module, string logicalName) =>
        For(PlatformTenant, module, logicalName);

    /// <summary>
    /// Throws when a key does not carry three non-empty segments.
    /// </summary>
    /// <remarks>
    /// Every <see cref="ICacheService"/> implementation calls this. It lives here
    /// rather than in one of them because the rule belongs to the contract: an
    /// adapter that forgot it would not fail its own tests, it would quietly widen
    /// the key space of a system whose isolation the key IS.
    /// </remarks>
    public static void EnsureValid(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var segments = key.Split(Separator);
        if (segments.Length < 3 || segments.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                $"'{key}' is not a cache key. Standards 20 fixes the shape as "
                + $"'{{tenant_id}}{Separator}{{module}}{Separator}{{logical-name}}', and the "
                + $"tenant segment is mandatory even for a platform-wide value — use the "
                + $"'{PlatformTenant}' sentinel rather than omitting it. A key without a "
                + "tenant is a key two tenants can both compute.",
                nameof(key));
        }
    }

    private static string For(string tenant, string module, string logicalName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(module);
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);

        // A separator inside a segment would let two different (tenant, module,
        // name) triples produce the same key — the ambiguity a delimiter always
        // has when a component can contain it.
        foreach (var segment in (string[])[tenant, module, logicalName])
        {
            if (segment.Contains(Separator, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"A cache-key segment may not contain '{Separator}': '{segment}'.");
            }
        }

        return $"{tenant}{Separator}{module}{Separator}{logicalName}";
    }
}
