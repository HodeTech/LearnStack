using System.Text.Json;
using LearnStack.SharedKernel.Localization;

namespace LearnStack.Infrastructure.Validation;

/// <summary>
/// Gate 2 — the clauses the evaluator does not enforce, walked over the raw
/// document before it is built.
/// </summary>
/// <remarks>
/// <para>
/// Every clause is here because the behaviour without it was measured on the
/// pinned version, and
/// <see href="../../../docs/decisions/0043-customization-payload-validation.md">ADR-0043
/// § 3 and § Context</see> record what each one prevents. None of them is a
/// stylistic preference and none is enforceable by the meta-schema, which types
/// <c>$schema</c> as <c>format: "uri"</c> and — <c>format</c> being an annotation
/// by default — accepts the string <c>not-a-uri</c> there.
/// </para>
/// <para>
/// It walks the <see cref="JsonElement"/> tree rather than the built schema,
/// because a document that violates the profile must be refused with a JSON
/// pointer, and the builder reports neither a location nor, for a foreign
/// dialect, a failure at all.
/// </para>
/// </remarks>
internal static class JsonSchemaProfile
{
    /// <summary>The one dialect a tenant schema may declare.</summary>
    internal const string Dialect = "https://json-schema.org/draft/2020-12/schema";

    /// <summary>
    /// <see href="../../../docs/architecture/32-tenant-customization-model.md">§ 8.4</see>'s
    /// nesting bound, expressed in raw JSON levels.
    /// </summary>
    /// <remarks>
    /// <para>
    /// § 8.4 declares <b>five</b>, and it means five <i>schema</i> levels — its
    /// stated reason is that "JSON Schema validators are recursive", and it is a
    /// schema level that costs a frame. One schema level costs <b>two</b> JSON
    /// levels, because a subschema is reached through an applicator keyword and
    /// then a name: <c>properties</c> then <c>word</c>. So five schema levels is
    /// the root plus ten, and one more for the keyword value at the bottom —
    /// twelve.
    /// </para>
    /// <para>
    /// Counted on raw JSON rather than on schema positions deliberately. Counting
    /// schema positions means enumerating every applicator keyword, and a keyword
    /// missed from that list is a hole in the bound rather than a stricter bound;
    /// raw depth over-approximates, which fails safe. The arithmetic above is what
    /// keeps the over-approximation from refusing what § 8.4 permits — the corpus's
    /// own <c>guided-sequence</c> example reaches ten.
    /// </para>
    /// </remarks>
    internal const int MaxDepth = 12;

    /// <summary>§ 8.4's property ceiling for one content type.</summary>
    internal const int MaxProperties = 100;

    /// <summary>§ 8.4's per-row size cap, in bytes of UTF-8.</summary>
    internal const int MaxBytes = 256 * 1024;

    /// <summary>
    /// Keywords that run a tenant-authored regular expression. Refused until the
    /// evaluator lets a caller bound one — ADR-0043 § 5 names the trigger.
    /// </summary>
    private static readonly string[] RegexKeywords = ["pattern", "patternProperties", "propertyNames"];

    /// <summary>
    /// Keywords that declare or resolve a schema identity outside this document.
    /// </summary>
    private static readonly string[] IdentityKeywords = ["$id", "$anchor", "$dynamicAnchor", "$dynamicRef"];

    internal static void Check(JsonElement root, ProfileFailures failures)
    {
        ArgumentNullException.ThrowIfNull(failures);

        if (root.ValueKind != JsonValueKind.Object)
        {
            // `true`, `false` and a bare `{}` are all valid 2020-12 schemas that
            // pass gates 3 and 4. Two of the three switch validation off for every
            // entry of the type, and § 8.1's read-time structural pass has no field
            // list to walk without `properties`.
            failures.Add("", "lockey_schema_root_must_be_an_object");
            return;
        }

        CheckDialect(root, failures);
        CheckRootProperties(root, failures);

        var references = new List<(string Pointer, string Target)>();
        Walk(root, "", 1, failures, references);
        CheckReferences(root, references, failures);
    }

    private static void CheckDialect(JsonElement root, ProfileFailures failures)
    {
        if (!root.TryGetProperty("$schema", out var declared))
        {
            // Not injected. The evaluator's default dialect is not 2020-12, and
            // pinning it per call does NOT override an in-document `$schema` —
            // measured — so requiring the line is the only defence that holds.
            failures.Add("/$schema", "lockey_schema_dialect_required");
            return;
        }

        if (declared.ValueKind != JsonValueKind.String
            || !string.Equals(declared.GetString(), Dialect, StringComparison.Ordinal))
        {
            failures.Add("/$schema", "lockey_schema_dialect_not_supported");
        }
    }

    private static void CheckRootProperties(JsonElement root, ProfileFailures failures)
    {
        if (!root.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
        {
            failures.Add("", "lockey_schema_root_must_declare_properties");
            return;
        }

        var count = 0;

        foreach (var _ in properties.EnumerateObject())
        {
            count++;
        }

        if (count > MaxProperties)
        {
            failures.Add("/properties", "lockey_schema_too_many_properties");
        }
    }

    /// <summary>
    /// Walks every object and array in the document, applying the keyword clauses
    /// and the depth bound.
    /// </summary>
    /// <remarks>
    /// Depth is counted on the syntax tree, which is what § 8.4's limit measures.
    /// A reference <b>cycle</b> needs no bound of its own: the builder raises
    /// <c>Cycle detected starting with a reference to …</c> at gate 4, measured.
    /// </remarks>
    private static void Walk(
        JsonElement element,
        string pointer,
        int depth,
        ProfileFailures failures,
        List<(string Pointer, string Target)> references)
    {
        if (depth > MaxDepth)
        {
            failures.Add(pointer, "lockey_schema_too_deep");
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var child = Pointer(pointer, property.Name);
                    CheckKeyword(property, child, failures, references);
                    Walk(property.Value, child, depth + 1, failures, references);
                }

                break;

            case JsonValueKind.Array:
                var index = 0;

                foreach (var item in element.EnumerateArray())
                {
                    Walk(
                        item,
                        Pointer(pointer, index.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        depth + 1,
                        failures,
                        references);
                    index++;
                }

                break;

            default:
                break;
        }
    }

    private static void CheckKeyword(
        JsonProperty property,
        string pointer,
        ProfileFailures failures,
        List<(string Pointer, string Target)> references)
    {
        if (Array.IndexOf(RegexKeywords, property.Name) >= 0)
        {
            failures.Add(pointer, "lockey_schema_regex_not_permitted");
            return;
        }

        if (Array.IndexOf(IdentityKeywords, property.Name) >= 0)
        {
            failures.Add(pointer, "lockey_schema_identity_not_permitted");
            return;
        }

        if (string.Equals(property.Name, "$ref", StringComparison.Ordinal)
            && (property.Value.ValueKind != JsonValueKind.String
                || property.Value.GetString() is not { } reference
                || !reference.StartsWith('#')))
        {
            // Only a fragment. An absolute `$ref` is a resolution attempt against
            // an address the author chose; the evaluator performs no network I/O
            // today, which makes this a correctness rule rather than a last line
            // of defence — the author's mistake would otherwise surface as a 500
            // on a reader's request.
            failures.Add(pointer, "lockey_schema_reference_must_be_local");
            return;
        }

        if (string.Equals(property.Name, "$ref", StringComparison.Ordinal))
        {
            references.Add((pointer, property.Value.GetString()!));
        }
    }

    /// <summary>
    /// Resolves every <c>$ref</c> inside the document and refuses one that names
    /// nothing, or one whose chain returns to itself without consuming an instance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both cases are measured, and neither is caught by any other gate.</b> A
    /// <c>$ref</c> naming a missing fragment <i>builds</i> and raises
    /// <c>RefResolutionException</c> only when an entry is validated against it —
    /// so the author's mistake would surface on someone else's save.
    /// </para>
    /// <para>
    /// <b>A non-productive cycle is worse than a refusal: it is a process kill.</b>
    /// <c>{"$defs":{"n":{"$ref":"#/$defs/n"}}}</c> reached through
    /// <c>properties</c> builds without complaint and then overflows the stack
    /// during evaluation — measured, 4426 frames of
    /// <c>RefKeyword.Evaluate</c> — and a .NET stack overflow cannot be caught. One
    /// tenant's schema would end the process for every tenant it serves. The
    /// builder's own cycle detection fires only for a cycle reached from the root,
    /// which is not where a tenant writes one.
    /// </para>
    /// <para>
    /// <b>Productive recursion stays legal</b>, because it terminates: a
    /// <c>$ref</c> under <c>properties</c> consumes one instance level per hop, and
    /// the instance is bounded by the reader's own nesting ceiling. Measured at
    /// depth 20 in under a millisecond. The chain walk therefore follows a
    /// <c>$ref</c> only while the node it lands on carries another <c>$ref</c> —
    /// pure indirection — and stops as soon as a node says anything about an
    /// instance.
    /// </para>
    /// </remarks>
    private static void CheckReferences(
        JsonElement root,
        List<(string Pointer, string Target)> references,
        ProfileFailures failures)
    {
        foreach (var (pointer, target) in references)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var hop = target;

            while (true)
            {
                if (!visited.Add(hop))
                {
                    failures.Add(pointer, "lockey_schema_reference_cycles");
                    break;
                }

                if (!TryResolve(root, hop, out var node))
                {
                    failures.Add(pointer, "lockey_schema_reference_unresolvable");
                    break;
                }

                // Following happens whenever the node carries a `$ref` at all, even
                // beside other keywords: in 2020-12 `$ref` applies alongside its
                // siblings and re-enters at the same instance location, so siblings
                // do not make the hop productive.
                if (node.ValueKind != JsonValueKind.Object
                    || !node.TryGetProperty("$ref", out var next)
                    || next.ValueKind != JsonValueKind.String)
                {
                    break;
                }

                hop = next.GetString()!;

                if (!hop.StartsWith('#'))
                {
                    // Reported where it was written; nothing further to follow.
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Resolves a fragment-only reference against the document, per RFC 6901.
    /// </summary>
    private static bool TryResolve(JsonElement root, string reference, out JsonElement node)
    {
        node = root;

        if (reference.Length == 0 || reference[0] != '#')
        {
            return false;
        }

        var fragment = reference[1..];

        if (fragment.Length == 0)
        {
            // `#` is the document itself.
            return true;
        }

        if (fragment[0] != '/')
        {
            // A plain-name fragment resolves through `$anchor`, which the profile
            // refuses outright, so there is nothing here to resolve to.
            return false;
        }

        foreach (var raw in fragment[1..].Split('/'))
        {
            var token = raw
                .Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);

            if (node.ValueKind == JsonValueKind.Object)
            {
                if (!node.TryGetProperty(token, out node))
                {
                    return false;
                }

                continue;
            }

            if (node.ValueKind == JsonValueKind.Array
                && int.TryParse(token, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var index)
                && index < node.GetArrayLength())
            {
                node = node[index];
                continue;
            }

            return false;
        }

        return true;
    }

    /// <summary>RFC 6901: <c>~</c> and <c>/</c> are escaped inside a token.</summary>
    private static string Pointer(string parent, string token) =>
        parent + "/" + token.Replace("~", "~0", StringComparison.Ordinal)
            .Replace("/", "~1", StringComparison.Ordinal);
}

/// <summary>
/// Collects gate failures as (JSON pointer, localization key) pairs.
/// </summary>
/// <remarks>
/// Bounded deliberately. A pathological document can fail on every node, and the
/// Problem Details body, the audit row and the structured log all carry
/// <c>Details</c> — an unbounded list turns one bad save into three unbounded
/// sinks. The first failures are the ones an author can act on.
/// </remarks>
internal sealed class ProfileFailures
{
    internal const int MaxReported = 25;

    private readonly Dictionary<string, List<LocalizedMessage>> _byPointer = new(StringComparer.Ordinal);

    internal int Count { get; private set; }

    internal bool Any => Count > 0;

    /// <param name="detail">
    /// The evaluator's own message, when there is one. It names a position in the
    /// author's document and does not echo a value, and without it a
    /// "not buildable" refusal is a 400 the author cannot act on. Carried as a
    /// message parameter rather than as prose, because
    /// <see cref="LocalizedMessage"/>'s key is the contract and its params are the
    /// substitution.
    /// </param>
    internal void Add(string pointer, string localizationKey, string? detail = null)
    {
        if (Count >= MaxReported)
        {
            return;
        }

        Count++;

        if (!_byPointer.TryGetValue(pointer, out var messages))
        {
            messages = [];
            _byPointer[pointer] = messages;
        }

        messages.Add(detail is null
            ? new LocalizedMessage(localizationKey)
            : new LocalizedMessage(
                localizationKey,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["detail"] = detail }));
    }

    internal IReadOnlyDictionary<string, IReadOnlyList<LocalizedMessage>> ToDetails() =>
        _byPointer.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<LocalizedMessage>)pair.Value.ToArray(),
            StringComparer.Ordinal);
}
