using System.Collections.Frozen;
using LearnStack.SharedKernel;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using Vogen;

namespace LearnStack.Modules.Customization.Domain;

/// <summary>
/// Identifies one revision of a tenant-declared content shape.
/// </summary>
/// <remarks>
/// Module-local, like <c>TenantDomainId</c> and unlike <see cref="TenantId"/>:
/// nothing outside Customization holds one, so
/// <see href="../../../../../docs/decisions/0023-strongly-typed-id-source-generator.md">ADR-0023
/// Amendment 2</see>'s cross-cutting placement rule does not apply.
/// <para>
/// It identifies a <b>revision</b>, not a concept. The concept is
/// <c>(tenant_id, key)</c>; the row this id addresses is
/// <c>(tenant_id, key, schema_version)</c>, and a second revision of one key is a
/// second row with a second id.
/// </para>
/// </remarks>
[ValueObject<Guid>(LearnStackVogenDefaults.IdMask)]
public readonly partial record struct TenantContentTypeId : IStronglyTypedId<Guid>;

/// <summary>
/// Identifies one revision of a tenant's level or difficulty vocabulary.
/// </summary>
/// <remarks>
/// Same shape and same reasoning as <see cref="TenantContentTypeId"/>.
/// </remarks>
[ValueObject<Guid>(LearnStackVogenDefaults.IdMask)]
public readonly partial record struct TenantLevelTaxonomyId : IStronglyTypedId<Guid>;

/// <summary>
/// Guards a customization key against the shape its consumers assume.
/// </summary>
/// <remarks>
/// <para>
/// A key reaches a URL segment, a cache-key component and an
/// <c>x-taxonomy</c> reference inside a tenant-authored JSON Schema, so it is the
/// same lowercase-alphanumeric-with-single-interior-hyphens shape
/// <see cref="UrlSlug"/> already guards — which is what the corpus's own worked
/// examples use (<c>vocabulary-card</c>, <c>asana-pose</c>, <c>cefr</c>).
/// </para>
/// <para>
/// The cache-key component is not decoration: <c>CacheKey.EnsureValid</c> rejects
/// a <c>:</c> inside a component, because a separator that can appear inside one
/// makes two different key tuples collide. Refusing the whole shape here is
/// stricter than refusing that one character, and it is refused at the factory so
/// the failure names the field rather than surfacing three layers away.
/// </para>
/// </remarks>
public static class CustomizationKey
{
    /// <summary>
    /// The width the customization schema maps for a key.
    /// </summary>
    /// <remarks>
    /// Wider than <see cref="UrlSlug.MaxLength"/>, which is a DNS label's 63: a
    /// customization key is never a hostname. Named rather than written at each
    /// call site because the factories throw at it and the validators refuse at
    /// it, and one number is what keeps the two answers the same.
    /// </remarks>
    public const int MaxLength = 100;

    public static void EnsureValid(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        MappedLength.EnsureAtMost(value, MaxLength, parameterName);
        UrlSlug.EnsureUrlSafe(value, parameterName);
    }
}

/// <summary>
/// The closed set of composite renderers a tenant row may point at.
/// </summary>
/// <remarks>
/// <para>
/// Canonical in
/// <see href="../../../../../docs/architecture/32-tenant-customization-model.md">Tenant
/// Customization Model § 2</see>; this is the backend's copy, and it exists because
/// § 8.1 requires renderer resolution to be checked <b>on saving</b> — a stale
/// reference must fail the author's request, not render a fallback on a learner's
/// page. <c>Composite_Renderer_Keys_Match_The_Frontend_Registry</c> holds this copy
/// and <c>composites.ts</c> together; without it the two drift silently, which is
/// what the same document's primitive list did until Packet 8 reconciled it.
/// </para>
/// <para>
/// <b>Every key names a capability, never a domain.</b> A <c>cefr-level-badge</c>
/// or <c>asana-card</c> key would fail
/// <c>Core_Modules_HaveNo_DomainSpecific_Names</c>. The set is larger than what
/// <c>composites.ts</c> registers today: the five shells land with the phases that
/// render them, and a declared-but-unregistered key renders <c>UnknownBlock</c>
/// per <see href="../../../../../docs/decisions/0013-page-block-schema-versioning.md">ADR-0013</see>
/// rather than failing the save.
/// </para>
/// </remarks>
public static class CompositeRendererKey
{
    /// <remarks>
    /// A <c>FrozenSet</c> rather than a <c>HashSet</c> behind an
    /// <c>IReadOnlySet</c>: the latter is one cast away from being mutable, and a
    /// set the whole platform's genericity claim rests on should not be widened by
    /// anything but a release. Ordinal and case-sensitive — <c>Default-Card</c> is
    /// not this key, and admitting it would put a second spelling into a cache-key
    /// component.
    /// </remarks>
    public static readonly FrozenSet<string> All = new[]
    {
        "default-card",
        "content-list",
        "media-gallery",
        "rich-page",
        "lesson-shell",
        "quiz-shell",
        "placement-shell",
        "live-shell",
        "submission-shell",
    }.ToFrozenSet(StringComparer.Ordinal);

    public static bool IsKnown(string value) =>
        !string.IsNullOrWhiteSpace(value) && All.Contains(value);

    public static void EnsureKnown(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        if (!All.Contains(value))
        {
            throw new ArgumentException(
                $"'{value}' is not a composite renderer. The set is closed and adding to it "
                + $"is a LearnStack release: {string.Join(", ", All.Order(StringComparer.Ordinal))}.",
                parameterName);
        }
    }
}
