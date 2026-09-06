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
    /// keeps the over-approximation from refusing what § 8.4 permits: measured, the
    /// corpus's own <c>guided-sequence</c> example reaches six, and five nested
    /// object schemas below the root are admitted while six are refused.
    /// </para>
    /// </remarks>
    internal const int MaxDepth = 12;

    /// <summary>§ 8.4's property ceiling for one content type.</summary>
    internal const int MaxProperties = 100;

    /// <summary>§ 8.4's per-row size cap, in bytes of UTF-8.</summary>
    internal const int MaxBytes = 256 * 1024;

    /// <summary>
    /// Subschema visits one document's reference graph may cost when expanded.
    /// </summary>
    /// <remarks>
    /// Generous for authoring and far below what hurts: a content type at § 8.4's
    /// hundred-property ceiling, every property referencing one shared <c>$defs</c>
    /// entry, costs about two hundred. Measured, an acyclic graph of twenty
    /// doubling <c>$defs</c> entries — 1,401 bytes — costs 2^20 and takes 5.7 GB to
    /// evaluate, so the bound is what stands between a kilobyte of JSON and a pod.
    /// </remarks>
    internal const long MaxExpansions = 1_000;

    /// <summary>
    /// Keywords that run a tenant-authored regular expression. Refused until the
    /// evaluator lets a caller bound one — ADR-0043 § 5 names the trigger.
    /// </summary>
    private static readonly string[] RegexKeywords = ["pattern", "patternProperties", "propertyNames"];

    /// <summary>
    /// Keywords that declare or resolve a schema identity outside this document.
    /// </summary>
    private static readonly string[] IdentityKeywords = ["$id", "$anchor", "$dynamicAnchor", "$dynamicRef"];

    /// <summary>
    /// Keywords whose value is a map from an <b>author-chosen name</b> to a
    /// subschema. The names inside one are field names, not keywords.
    /// </summary>
    /// <remarks>
    /// Without this, a tenant could not declare a field called <c>pattern</c> — a
    /// knitting school's content type, say — because the walk would read the field
    /// name as the keyword it bans. Measured: <c>properties/pattern</c>,
    /// <c>properties/propertyNames</c>, <c>properties/$id</c> and
    /// <c>properties/$schema</c> were all refused for being what they were named.
    /// <para>
    /// Missing an entry from this list makes the profile <i>stricter</i> — it
    /// refuses a legal field name — rather than weaker, which is the direction a
    /// list like this must fail in.
    /// </para>
    /// <para>
    /// <c>patternProperties</c> is on the list and cannot be reached: the keyword
    /// itself is banned, so no admitted schema contains one. It is here for the day
    /// <see href="../../../docs/decisions/0043-customization-payload-validation.md">ADR-0043
    /// § 5</see>'s trigger fires and the regex keywords are admitted — at which
    /// point its keys become author-chosen and a list that had quietly dropped it
    /// would start refusing them.
    /// </para>
    /// </remarks>
    private static readonly string[] NameMapKeywords =
        ["properties", "$defs", "patternProperties", "dependentSchemas", "dependentRequired"];

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
        Walk(root, "", 1, failures, references, namesAreAuthored: false);
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

        if (count == 0)
        {
            // Semantically the bare `{}` the clause above refuses: it accepts 42,
            // a string and every object alike, and § 8.1's read-time structural
            // pass has no field list to walk.
            failures.Add("/properties", "lockey_schema_root_must_declare_properties");
            return;
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
        List<(string Pointer, string Target)> references,
        bool namesAreAuthored)
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

                    // Inside a name map the key is the author's, so it is not read
                    // as a keyword — and the value under it is a schema again, so
                    // its own keys are.
                    if (!namesAreAuthored)
                    {
                        CheckKeyword(property, child, failures, references);
                    }

                    Walk(
                        property.Value,
                        child,
                        depth + 1,
                        failures,
                        references,
                        namesAreAuthored: !namesAreAuthored
                            && Array.IndexOf(NameMapKeywords, property.Name) >= 0);
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
                        references,
                        namesAreAuthored: false);
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
            return;
        }

        if (string.Equals(property.Name, "$schema", StringComparison.Ordinal)
            && !string.Equals(pointer, "/$schema", StringComparison.Ordinal))
        {
            // The root's own line is checked by CheckDialect; anywhere else it is a
            // second dialect declaration inside one document. Measured: a draft-07
            // line under `properties/a` makes `prefixItems` inert for that subschema
            // only, so the same schema text accepts and rejects the same instance —
            // which is the failure the root clause exists to prevent, reproduced one
            // level down.
            failures.Add(pointer, "lockey_schema_dialect_not_at_root");
        }
    }

    /// <summary>
    /// Builds the document's reference graph and refuses it if any edge dangles,
    /// any edge names something that is not a schema, the graph has a cycle, or
    /// expanding it costs more than <see cref="MaxExpansions"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every cycle, not only a "pure indirection" one.</b> An earlier form of
    /// this check followed a <c>$ref</c> only while the node it landed on carried
    /// a <c>$ref</c> of its own, on the theory that anything else consumed an
    /// instance level and therefore terminated. That theory is wrong, and wrong in
    /// the direction that kills the process: <c>allOf</c>, <c>anyOf</c>,
    /// <c>oneOf</c>, <c>not</c> and <c>if</c> all re-enter at the <i>same</i>
    /// instance location without the node carrying a top-level <c>$ref</c>.
    /// Measured — <c>{"$defs":{"a":{"allOf":[{"$ref":"#/$defs/a"}]}},…}</c>, 161
    /// bytes, was admitted, built, and ended the process with SIGABRT on the first
    /// entry validated against it.
    /// </para>
    /// <para>
    /// The fix is not a longer list of in-place applicators. Getting such a list
    /// wrong costs a process, so the rule refuses <b>every</b> cycle and gives up
    /// recursive schemas — a shape no document in the corpus uses, whose
    /// entry-to-entry cousin § 8.3 already caps at depth two. Nothing here depends
    /// on classifying a keyword.
    /// </para>
    /// <para>
    /// <b>Acyclic is not enough.</b> Measured: twenty <c>$defs</c> entries of the
    /// form <c>{"allOf":[{"$ref":"#/$defs/next"},{"$ref":"#/$defs/next"}]}</c> —
    /// 1,401 bytes, no cycle at all — evaluate to 2^20 visits of one instance
    /// location: 7.2 seconds and 5.7 GB of resident memory. So the graph is also
    /// costed, memoized, and refused past a bound. The same count bails out early
    /// on a long chain, which is what stops the walk itself from being the
    /// expensive part.
    /// </para>
    /// </remarks>
    private static void CheckReferences(
        JsonElement root,
        List<(string Pointer, string Target)> references,
        ProfileFailures failures)
    {
        if (references.Count == 0)
        {
            return;
        }

        var costs = new Dictionary<string, long>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var reported = false;

        foreach (var (pointer, target) in references)
        {
            var cost = Expand(root, target, costs, onStack, failures, pointer, ref reported);

            if (reported || cost > MaxExpansions)
            {
                if (!reported)
                {
                    failures.Add(pointer, "lockey_schema_reference_graph_too_large");
                }

                return;
            }
        }
    }

    /// <summary>
    /// The number of subschema visits one <c>$ref</c> costs, memoized per target.
    /// </summary>
    /// <remarks>
    /// A node's cost is one plus the cost of every <c>$ref</c> in its own subtree.
    /// <c>$defs</c> subtrees are skipped: <c>$defs</c> is a container, not an
    /// applicator, so what is inside it is paid for by whoever references it and
    /// counting it here would charge a document twice for the same subschema.
    /// </remarks>
    private static long Expand(
        JsonElement root,
        string target,
        Dictionary<string, long> costs,
        HashSet<string> onStack,
        ProfileFailures failures,
        string pointer,
        ref bool reported)
    {
        if (costs.TryGetValue(target, out var memo))
        {
            return memo;
        }

        if (!onStack.Add(target))
        {
            failures.Add(pointer, "lockey_schema_reference_cycles");
            reported = true;
            return 0;
        }

        try
        {
            if (!TryResolve(root, target, out var node))
            {
                failures.Add(pointer, "lockey_schema_reference_unresolvable");
                reported = true;
                return 0;
            }

            if (node.ValueKind is not (JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False))
            {
                // A fragment may name any JSON value; only a schema may be applied.
                // Measured: without this, the builder raises a bare
                // ArgumentException out of AdmitSchema — a 500 from a port whose
                // contract is that nothing throws.
                failures.Add(pointer, "lockey_schema_reference_not_a_schema");
                reported = true;
                return 0;
            }

            var cost = 1L;

            foreach (var next in ReferencesWithin(node))
            {
                cost += Expand(root, next, costs, onStack, failures, pointer, ref reported);

                if (reported || cost > MaxExpansions)
                {
                    return cost;
                }
            }

            costs[target] = cost;
            return cost;
        }
        finally
        {
            onStack.Remove(target);
        }
    }

    /// <summary>
    /// Every <c>$ref</c> string lexically inside <paramref name="node"/>, not
    /// descending into a <c>$defs</c> container.
    /// </summary>
    private static IEnumerable<string> ReferencesWithin(JsonElement node)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in node.EnumerateObject())
                {
                    if (string.Equals(property.Name, "$defs", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (string.Equals(property.Name, "$ref", StringComparison.Ordinal)
                        && property.Value.ValueKind == JsonValueKind.String)
                    {
                        yield return property.Value.GetString()!;
                        continue;
                    }

                    foreach (var nested in ReferencesWithin(property.Value))
                    {
                        yield return nested;
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray())
                {
                    foreach (var nested in ReferencesWithin(item))
                    {
                        yield return nested;
                    }
                }

                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Resolves a fragment-only reference against the document, per RFC 6901.
    /// </summary>
    /// <remarks>
    /// Fragment-only by the time it is reached: <see cref="CheckKeyword"/> refuses
    /// anything else, and the target may name any JSON value — which is why the
    /// caller checks that what comes back is a schema.
    /// </remarks>
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
            var token = Uri.UnescapeDataString(raw)
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
    /// The evaluator's own message, when there is one, carried under the parameter
    /// name <c>evaluatorMessage</c>.
    /// <para>
    /// <b>It is deliberately untranslated, and that is the trade.</b> It is
    /// attached only where the pointer is the document itself — a schema that does
    /// not build, a reference that does not resolve, text that is not JSON — and
    /// there the localized key alone ("not buildable") is a 400 the author cannot
    /// act on. The message names a position in the author's own document and
    /// echoes no value. A caller that wants a fully localized surface renders the
    /// key and drops the parameter.
    /// </para>
    /// </param>
    internal void Add(string pointer, string localizationKey, string? detail = null)
    {
        if (Count >= MaxReported)
        {
            return;
        }

        if (!_byPointer.TryGetValue(pointer, out var messages))
        {
            messages = [];
            _byPointer[pointer] = messages;
        }

        // One reason per pointer. Two branches of one `anyOf` can both fail at the
        // same location, and telling an author the same thing twice about the same
        // field is noise in a body three sinks read.
        if (messages.Any(existing => string.Equals(existing.Key, localizationKey, StringComparison.Ordinal)))
        {
            return;
        }

        Count++;

        messages.Add(detail is null
            ? new LocalizedMessage(localizationKey)
            : new LocalizedMessage(
                localizationKey,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["evaluatorMessage"] = detail }));
    }

    internal IReadOnlyDictionary<string, IReadOnlyList<LocalizedMessage>> ToDetails() =>
        _byPointer.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<LocalizedMessage>)pair.Value.ToArray(),
            StringComparer.Ordinal);
}
