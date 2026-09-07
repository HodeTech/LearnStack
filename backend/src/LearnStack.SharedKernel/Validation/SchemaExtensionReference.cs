namespace LearnStack.SharedKernel.Validation;

/// <summary>
/// One LearnStack extension keyword a tenant's schema declares, and where it
/// declares it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reported by the validator, resolved by the module.</b>
/// <see href="../../../../docs/decisions/0043-customization-payload-validation.md">ADR-0043
/// § 4</see> puts the split in as many words: unknown keywords pass the gates
/// because the pinned dialect admits them, and "resolving those extensions
/// against the renderer, taxonomy and language registries is § 8.1's separate
/// requirement, performed by LearnStack after the four gates — never delegated to
/// the validator, which has no opinion about them". Two of the three registries
/// are closed sets a module owns and the third is a tenant's own rows; none is
/// something a JSON Schema evaluator could know.
/// </para>
/// <para>
/// <b>Why the occurrence list still comes from the validator.</b> The one thing a
/// caller cannot reconstruct is <i>where</i> a keyword sits. A field a tenant
/// named <c>x-renderer</c> under <c>properties</c> is a field name, and an
/// <c>x-renderer</c> inside <c>const</c> or <c>examples</c> is the tenant's own
/// data — neither is an extension, and only the walk that already separates
/// schema positions from instance literals can say so. A second walk written
/// outside it would be a second copy of that rule, and the copies would disagree.
/// </para>
/// </remarks>
/// <param name="Location">
/// The RFC 6901 JSON pointer to the keyword, so a refusal names the position in
/// the author's own document — <see href="../../../../docs/architecture/32-tenant-customization-model.md">§
/// 8.1</see>'s stated failure mode. Named for what it is rather than for its
/// notation: <c>Pointer</c> is a type name, and CA1720 refuses it on a public
/// surface.
/// </param>
/// <param name="Keyword">
/// The extension keyword: <c>x-renderer</c>, <c>x-taxonomy</c> or
/// <c>x-language</c>.
/// </param>
/// <param name="Value">
/// What it names — the string value, or the raw JSON text when the value is not a
/// string. A registry lookup then refuses <c>3</c> and <c>{"a":1}</c> by finding
/// nothing under them, which is the same answer a misspelled key gets and one
/// fewer failure mode than a nullable value would add.
/// </param>
public sealed record SchemaExtensionReference(string Location, string Keyword, string Value);
