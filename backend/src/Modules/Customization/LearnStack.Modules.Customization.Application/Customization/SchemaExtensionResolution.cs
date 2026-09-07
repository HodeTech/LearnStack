using LearnStack.Modules.Customization.Application.Abstractions;
using LearnStack.Modules.Customization.Domain;
using LearnStack.SharedKernel.Validation;

namespace LearnStack.Modules.Customization.Application.Customization;

/// <summary>
/// Resolves the LearnStack extensions a tenant's schema declares, which is the
/// half of the write-path gate the validator deliberately does not do.
/// </summary>
/// <remarks>
/// <para>
/// <see href="../../../../../../docs/architecture/32-tenant-customization-model.md">§
/// 8.1</see> requires every <c>x-renderer</c> / <c>x-taxonomy</c> /
/// <c>x-language</c> to resolve to a registry entry <b>on saving</b>, and
/// <see href="../../../../../../docs/decisions/0043-customization-payload-validation.md">ADR-0043
/// § 4</see> says who does it: the four gates admit unknown keywords because the
/// pinned dialect admits them, and resolution belongs to the module that owns the
/// registries. Without this, a content type naming a renderer or a taxonomy that
/// does not exist is stored, published and frozen — and the read path is designed
/// to trust what is stored, so nothing downstream can repair it.
/// </para>
/// <para>
/// <b><c>x-language</c> is accepted, because its registry does not exist yet.</b>
/// The primitive and composite renderer sets are ADR-0018's, and a taxonomy is a
/// tenant's own row; the set of languages a <c>code</c> field may declare is
/// neither, and nothing in the corpus decides it. It is owed by
/// <see href="../../../../../../docs/roadmap/phase-04-cms-media-pages.md">Phase
/// 04</see>, which owns the closed set of built-in primitive field types the
/// <c>code</c> field belongs to and the Studio editor that offers them. Refusing
/// the keyword until then would make the corpus's own worked example unsavable
/// while deciding nothing.
/// </para>
/// </remarks>
internal static class SchemaExtensionResolution
{
    internal const string RendererKeyword = "x-renderer";
    internal const string TaxonomyKeyword = "x-taxonomy";

    /// <summary>
    /// The occurrences that name nothing, in document order.
    /// </summary>
    /// <remarks>
    /// Every distinct taxonomy key costs one query and a repeated one costs none:
    /// a content type at § 8.4's hundred-property ceiling may reference one
    /// vocabulary a hundred times, and the answer cannot differ between them
    /// inside a transaction that holds one connection.
    /// </remarks>
    internal static async Task<IReadOnlyList<SchemaExtensionReference>> UnresolvedAsync(
        IReadOnlyList<SchemaExtensionReference> extensions,
        ITenantLevelTaxonomyCatalog taxonomies,
        CancellationToken cancellationToken)
    {
        var missingTaxonomies = await MissingTaxonomiesAsync(extensions, taxonomies, cancellationToken);
        var unresolved = new List<SchemaExtensionReference>();

        foreach (var extension in extensions)
        {
            var resolves = extension.Keyword switch
            {
                RendererKeyword => PrimitiveRendererKey.IsKnown(extension.Value),
                TaxonomyKeyword => !missingTaxonomies.Contains(extension.Value),

                // x-language, and any extension a later release adds to the
                // validator's list before this one learns to resolve it. Accepting
                // is the direction that fails safe: the alternative refuses a
                // document for a keyword nobody has decided about yet.
                _ => true,
            };

            if (!resolves)
            {
                unresolved.Add(extension);
            }
        }

        return unresolved;
    }

    private static async Task<HashSet<string>> MissingTaxonomiesAsync(
        IReadOnlyList<SchemaExtensionReference> extensions,
        ITenantLevelTaxonomyCatalog taxonomies,
        CancellationToken cancellationToken)
    {
        var missing = new HashSet<string>(StringComparer.Ordinal);
        var asked = new HashSet<string>(StringComparer.Ordinal);

        foreach (var extension in extensions)
        {
            if (!string.Equals(extension.Keyword, TaxonomyKeyword, StringComparison.Ordinal)
                || !asked.Add(extension.Value))
            {
                continue;
            }

            if (!await taxonomies.ContainsAsync(extension.Value, cancellationToken))
            {
                missing.Add(extension.Value);
            }
        }

        return missing;
    }
}
