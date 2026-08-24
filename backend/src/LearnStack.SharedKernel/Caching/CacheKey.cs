namespace LearnStack.SharedKernel.Caching;

/// <summary>
/// Builds and validates the one cache-key shape
/// <see href="../../../../docs/standards/20-infrastructure-stack.md">Standards 20
/// § Cache</see> admits: <c>{tenant_id}:{module}:{logical-name}</c>, or
/// <c>{tenant_id}:{organization_id}:{module}:{logical-name}</c> for a value scoped
/// to one organization.
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

    /// <summary>
    /// Composes a key for a value scoped to one organization within a tenant:
    /// <c>{tenant_id}:{organization_id}:{module}:{logical-name}</c>.
    /// </summary>
    /// <remarks>
    /// The same argument as the tenant segment, one level down. Organizations are
    /// a scope in their own right
    /// (<see href="../../../../docs/decisions/0017-tenant-organization-hierarchy.md">ADR-0017</see>),
    /// so a roster cached as <c>{tenant}:education:roster</c> is a key two
    /// organizations of one tenant both compute. <see cref="EnsureValid"/> cannot
    /// catch that — an organization-scoped value and a tenant-wide one are
    /// indistinguishable as strings — which is exactly why the composition exists
    /// rather than being left to each call site to spell.
    /// </remarks>
    public static string ForOrganization(
        Guid tenantId, Guid organizationId, string module, string logicalName) =>
        For(tenantId.ToString(), organizationId.ToString(), module, logicalName);

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
        var wellFormed = segments.Length >= 3
            && !segments.Any(string.IsNullOrWhiteSpace)
            && IsTenantSegment(segments[0]);

        if (!wellFormed)
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

    /// <summary>
    /// Whether the first segment is a tenant identifier or the platform sentinel.
    /// </summary>
    /// <remarks>
    /// Counting segments is not enough, and the first version of this guard did
    /// only that: <c>hub:entitlement:{tenant_id}</c> has three segments and puts
    /// the module first, so it passed a check whose own error message says the
    /// tenant segment is mandatory. A guard that admits the shape it exists to
    /// reject is worse than none — it makes the rule look enforced.
    /// </remarks>
    private static bool IsTenantSegment(string segment) =>
        segment.Equals(PlatformTenant, StringComparison.Ordinal) || Guid.TryParse(segment, out _);

    private static string For(string tenant, string module, string logicalName) =>
        Compose([tenant, module, logicalName]);

    private static string For(string tenant, string org, string module, string logicalName) =>
        Compose([tenant, org, module, logicalName]);

    private static string Compose(string[] segments)
    {
        foreach (var segment in segments)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(segment);
        }

        // A separator inside a segment would let two different segment tuples
        // produce the same key — the ambiguity a delimiter always has when a
        // component can contain it.
        foreach (var segment in segments)
        {
            if (segment.Contains(Separator, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"A cache-key segment may not contain '{Separator}': '{segment}'.");
            }
        }

        return string.Join(Separator, segments);
    }
}
