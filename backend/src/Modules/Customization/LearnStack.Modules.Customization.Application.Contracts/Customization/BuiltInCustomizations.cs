namespace LearnStack.Modules.Customization.Application.Contracts.Customization;

/// <summary>
/// The two customizations a tenant starts with, so the runtime has something to
/// render before anyone has authored anything.
/// </summary>
/// <remarks>
/// <para>
/// <b>Data, not code, and that is the point being demonstrated.</b>
/// <see href="../../../../../../docs/decisions/0018-tenant-driven-customization-model.md">ADR-0018</see>
/// says a tenant's shapes are rows; these are the first rows, and they go in
/// through the same four commands a tenant admin uses. Nothing here is privileged
/// — a tenant may deprecate either one and publish its own successor on day one,
/// which is exactly what a language school replacing <c>plain</c> with CEFR does.
/// </para>
/// <para>
/// <b>Deliberately domain-neutral.</b> A `beginner / intermediate / advanced`
/// ladder belongs to no vertical, which is the property that makes it safe to
/// ship: a built-in that assumed a language school would be code the yoga studio
/// has to work around, and
/// <see href="../../../../../../docs/architecture/01-platform-vision.md">Platform
/// Vision § Genericity boundary</see> is where that line is drawn.
/// </para>
/// <para>
/// <b>Applied by whoever creates the tenant, never by provisioning itself.</b>
/// <see href="../../../../../../docs/decisions/0042-tenant-provisioning-cross-aggregate-transaction.md">ADR-0042</see>
/// keeps the cross-aggregate allow-list at one entry, and folding a customization
/// write into <c>ProvisionTenantCommand</c> would add a second aggregate to that
/// transaction. Today the caller is <c>LearnStack.Tools.Seeder</c>; the
/// self-service signup that will need the same four commands for a real tenant
/// lands with the Hub-side onboarding in
/// <see href="../../../../../../docs/roadmap/phase-02c-hub-foundation.md">Phase
/// 02c</see>, and reads this same declaration rather than a second copy of it.
/// </para>
/// </remarks>
public static class BuiltInCustomizations
{
    /// <summary>The one content type, rendered by the <c>default-card</c> composite.</summary>
    /// <remarks>
    /// Its schema is deliberately the smallest thing the four gates admit and the
    /// renderer can draw: a title and a body. It exists to prove the pipeline —
    /// authored shape to stored row to rendered page — not to be useful.
    /// </remarks>
    public static readonly BuiltInContentType Card = new(
        "card",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["en"] = "Card",
            ["tr"] = "Kart",
        },
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "title": { "type": "string", "maxLength": 200 },
            "body": { "type": "string" }
          },
          "required": ["title"]
        }
        """,
        "default-card");

    /// <summary>The one level taxonomy, and the ladder every tenant may replace.</summary>
    public static readonly BuiltInTaxonomy Plain = new(
        "plain",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["en"] = "Plain",
            ["tr"] = "Düz",
        },
        [
            new BuiltInBand("beginner", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["en"] = "Beginner",
                ["tr"] = "Başlangıç",
            }, 0),
            new BuiltInBand("intermediate", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["en"] = "Intermediate",
                ["tr"] = "Orta",
            }, 1),
            new BuiltInBand("advanced", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["en"] = "Advanced",
                ["tr"] = "İleri",
            }, 2),
        ]);

    /// <summary>
    /// The revision every built-in is created at.
    /// </summary>
    /// <remarks>
    /// One, always. A built-in that shipped a second version would have to decide
    /// what happens to a tenant that already deprecated the first, and
    /// <see href="../../../../../../docs/decisions/0013-page-block-schema-versioning.md">ADR-0013</see>
    /// gives that decision to the tenant rather than to the platform: the successor
    /// a tenant publishes is the tenant's, whatever it succeeds.
    /// </remarks>
    public const int SchemaVersion = 1;
}

/// <summary>A content type a tenant starts with.</summary>
public sealed record BuiltInContentType(
    string Key,
    IReadOnlyDictionary<string, string> DisplayName,
    string JsonSchema,
    string RendererKey);

/// <summary>A level taxonomy a tenant starts with.</summary>
public sealed record BuiltInTaxonomy(
    string Key,
    IReadOnlyDictionary<string, string> DisplayName,
    IReadOnlyList<BuiltInBand> Bands);

/// <summary>One band of a built-in taxonomy.</summary>
public sealed record BuiltInBand(
    string Key,
    IReadOnlyDictionary<string, string> DisplayName,
    short Sort);
