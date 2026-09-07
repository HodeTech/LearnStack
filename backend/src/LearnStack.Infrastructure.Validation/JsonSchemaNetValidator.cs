using System.Text;
using System.Text.Json;
using Json.Schema;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Validation;

namespace LearnStack.Infrastructure.Validation;

/// <summary>
/// The <see cref="IJsonSchemaValidator"/> adapter, and the only type in the
/// repository that touches <c>Json.Schema</c>.
/// </summary>
/// <remarks>
/// <para>
/// Implements the four gates of
/// <see href="../../../docs/decisions/0043-customization-payload-validation.md">ADR-0043
/// § 2</see>, in the order that ADR fixes: JSON, the LearnStack profile, the
/// draft 2020-12 meta-schema, then the build. The order is not arbitrary — the
/// gates that can name a location run before the one that cannot, because
/// <c>JsonSchemaException</c> carries a message and no pointer.
/// </para>
/// <para>
/// <b>Never the library's defaults.</b> Every build passes
/// <c>BuildOptions { Dialect = Draft202012, SchemaRegistry = new SchemaRegistry() }</c>.
/// Measured on the pinned version: the default dialect is
/// <c>https://json-schema.org/v1/2026</c>, whose <c>AllowUnknownKeywords</c> is
/// false, so a schema carrying <c>x-taxonomy</c> and no <c>$schema</c> raises
/// rather than passing; and the default registry is process-global, where the
/// first writer of an <c>$id</c> wins and a second build of it raises
/// <c>Overwriting registered schemas is not permitted</c> for the life of the
/// process — one tenant locking another out of saving.
/// <c>BuildOptions.Default</c> is a cached singleton and is never mutated to
/// achieve this: that is process-global state shared with anything else in the
/// host.
/// </para>
/// <para>
/// <b>Nothing is cached.</b> Compiling is measured cheaper than evaluating at
/// every realistic schema size — 0.5× at § 8.4's hundred-property ceiling — so
/// ADR-0043 § 6 deletes the compiled-validator cache § 8.2 used to mandate, and
/// with it the question of whether an additive revision may change a published
/// schema.
/// </para>
/// </remarks>
public sealed class JsonSchemaNetValidator : IJsonSchemaValidator
{
    private const string ValidationFailedKey = "lockey_validation_failed";

    /// <remarks>
    /// <c>List</c> rather than the default <c>Flag</c>, which returns pass/fail and
    /// no location at all — and § 8.1 requires Problem Details naming the offending
    /// JSON pointer. The cost is real and is stated where it belongs: <c>Flag</c>
    /// short-circuits on first failure and <c>List</c> does not.
    /// </remarks>
    private static readonly EvaluationOptions Located =
        new() { OutputFormat = OutputFormat.List };

    /// <inheritdoc />
    public Result<IReadOnlyList<SchemaExtensionReference>> AdmitSchema(string jsonSchema)
    {
        // The size cap before the parse, not after. It names no location, so ADR-0043
        // § 2's ordering — the gates that CAN name one run first — has nothing to say
        // about where it sits; what does is that parsing is the work the cap exists
        // to refuse, and the callers no request-body limit bounds (the seeder, the
        // Hub adapter, Phase 04's bulk importer) all reach this method.
        if (Encoding.UTF8.GetByteCount(jsonSchema) > JsonSchemaProfile.MaxBytes)
        {
            return Fail<IReadOnlyList<SchemaExtensionReference>>("", "lockey_schema_too_large");
        }

        // Gate 1 — it is JSON. JsonException, whose reader-depth subtype
        // JsonReaderException is internal and cannot be named in a catch.
        if (!TryParse(jsonSchema, out var document, out var malformed))
        {
            return Fail<IReadOnlyList<SchemaExtensionReference>>(malformed);
        }

        IReadOnlyList<SchemaExtensionReference> extensions;

        using (document)
        {
            // Gate 2 — the LearnStack profile, which the library does not provide.
            // It is also the only walk that can say WHERE an x-renderer sits, so the
            // extension occurrences LearnStack has to resolve leave from here.
            var failures = new ProfileFailures();
            extensions = JsonSchemaProfile.Check(document.RootElement, failures);

            if (failures.Any)
            {
                return Fail<IReadOnlyList<SchemaExtensionReference>>(failures);
            }

            // Gate 3 — the meta-schema, which is where a structural mistake gets a
            // location. Authoritative about structure and silent about dialect,
            // which is gate 2's job.
            var meta = MetaSchemas.Draft202012.Evaluate(document.RootElement, Located);

            if (!meta.IsValid)
            {
                return Fail<IReadOnlyList<SchemaExtensionReference>>(
                    Collect(meta, "lockey_schema_not_valid_json_schema"));
            }

            // Gate 4 — it builds. Both catches below are **defensive at a third-party
            // boundary and no longer reachable from a document gates 1-3 admit**:
            // searched on 2026-09-07 across `$ref` onto a non-schema, a `$ref` into a
            // `properties` slot, `contentSchema`, `unevaluatedProperties`,
            // `prefixItems`, a pruned `$vocabulary` and a malformed
            // `dependentRequired`, and gate 2 or gate 3 refused every one first. They
            // stay because the builder is a pinned dependency rather than a rule this
            // repository owns — the same reason ADR-0043 gives for the
            // ArgumentException arm, which a measured shape DID reach before gate 2
            // grew the clause that now refuses it.
            //
            // NOT where cycles are caught either: gate 2 refuses every
            // one, because the builder detects only a cycle reachable from the root
            // and one reached through `properties` builds and then ends the process
            // during evaluation (ADR-0043 Amendments 1 and 2).
            try
            {
                JsonSchema.FromText(jsonSchema, BuildOptions());
            }
            catch (JsonSchemaException exception)
            {
                return Fail<IReadOnlyList<SchemaExtensionReference>>(
                    "", "lockey_schema_not_buildable", exception.Message);
            }
            catch (ArgumentException exception)
            {
                // The builder raises a bare ArgumentException for shapes it will
                // not accept — measured, "Schemas may only booleans or objects" for
                // a $ref landing on a string. Gate 2 refuses that one now, and this
                // clause exists so the next such shape is a 400 rather than the 500
                // the port's own contract says it will never produce.
                return Fail<IReadOnlyList<SchemaExtensionReference>>(
                    "", "lockey_schema_not_buildable", exception.Message);
            }
        }

        return Result.Ok(extensions);
    }

    /// <inheritdoc />
    public Result<None> ValidateInstance(string admittedSchema, string instanceJson)
    {
        // Both caps before either parse, for the reason AdmitSchema states.
        if (Encoding.UTF8.GetByteCount(instanceJson) > MaxInstanceBytes)
        {
            return Fail<None>("", "lockey_instance_too_large");
        }

        if (Encoding.UTF8.GetByteCount(admittedSchema) > JsonSchemaProfile.MaxBytes)
        {
            return Fail<None>("", "lockey_schema_too_large");
        }

        if (!TryParse(instanceJson, out var document, out var malformed))
        {
            return Fail<None>(malformed);
        }

        using (document)
        {
            // An entry is stored in a `jsonb` column too, and `U+0000`, an unpaired
            // surrogate and a number outside `numeric` all pass every schema — so
            // without this the entry is admitted here and refused by the INSERT, as
            // the 500 ADR-0043 Amendment 4 removed from the schema path.
            var unstorable = new ProfileFailures();

            foreach (var (location, fault) in JsonValue.Unstorable(document.RootElement))
            {
                unstorable.Add(
                    location,
                    fault == JsonStorageFault.Number
                        ? "lockey_instance_number_not_storable"
                        : "lockey_instance_text_not_storable");
            }

            if (unstorable.Any)
            {
                return Fail<None>(unstorable);
            }

            try
            {
                var schema = JsonSchema.FromText(admittedSchema, BuildOptions());
                var evaluation = schema.Evaluate(document.RootElement, Located);

                return evaluation.IsValid
                    ? Result.Ok(None.Value)
                    : Fail<None>(Collect(evaluation, "lockey_instance_does_not_match_schema"));
            }
            catch (Exception exception)
                when (exception is JsonSchemaException or JsonException or ArgumentException)
            {
                // Everything the library raises for a schema it will not accept,
                // and the evaluation is inside the try because that is where two of
                // them actually surface: measured, an unresolvable or external
                // `$ref` raises RefResolutionException from Evaluate and never from
                // FromText, and a `$ref` onto a string raises a bare
                // ArgumentException. Both used to escape raw, past a port whose
                // contract names exactly one exception.
                //
                // The caller read this schema from a column only AdmitSchema
                // writes, so any of them means a row was written past the gate —
                // ADR-0043's Driver 2, and a 500 rather than a tenant's 400.
                throw new InvalidOperationException(
                    "The stored schema does not build, or does not evaluate. It was "
                    + "written without passing IJsonSchemaValidator.AdmitSchema, which "
                    + "is the only sanctioned writer of that column.",
                    exception);
            }
        }
    }

    /// <summary>
    /// The largest content entry this validator will evaluate, per
    /// <see href="../../../docs/architecture/32-tenant-customization-model.md">§ 8.4</see>.
    /// </summary>
    private const int MaxInstanceBytes = 1024 * 1024;

    /// <remarks>
    /// A fresh registry per build. Both arguments are load-bearing; see the
    /// remarks on the class.
    /// </remarks>
    private static BuildOptions BuildOptions() =>
        new() { Dialect = Dialect.Draft202012, SchemaRegistry = new SchemaRegistry() };

    /// <remarks>
    /// Hands back the failure as <see cref="ProfileFailures"/> rather than as a
    /// <c>Result</c>, because the two callers answer with different payload types
    /// and the reason for the refusal is the same either way.
    /// </remarks>
    /// <summary>
    /// Gate 1's parse, with duplicate members refused.
    /// </summary>
    /// <remarks>
    /// <b>Because the readers disagree about which one wins.</b> Measured on .NET
    /// 10: a keyed lookup returns the <i>last</i> member of a duplicated pair and
    /// an enumeration yields both — so the profile audits a member <c>jsonb</c>
    /// then discards, and a duplicated <c>$schema</c> is read by this gate and by
    /// the builder with no guarantee they agree. Refusing the document is one
    /// option flag, and it raises <c>JsonException</c>, which gate 1 already turns
    /// into a 400 naming the position.
    /// <para>
    /// <b>Here and not in <c>JsonValue</c>.</b> PostgreSQL does store a duplicated
    /// member — it keeps the last — so "JSON a <c>jsonb</c> column takes" stays true
    /// of one, and that general guard also runs on <c>LocalizedText.FromJson</c>,
    /// which reads a stored column back and would then refuse a value on its way
    /// out. What is specific to this gate is that <b>LearnStack reads the document
    /// twice under different rules</b>: the profile enumerates, and the dialect
    /// check looks up by key.
    /// </para>
    /// </remarks>
    private static readonly JsonDocumentOptions NoDuplicates = new() { AllowDuplicateProperties = false };

    private static bool TryParse(string json, out JsonDocument document, out ProfileFailures malformed)
    {
        try
        {
            document = JsonDocument.Parse(json, NoDuplicates);
            malformed = default!;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            // InvalidOperationException is not a parser error in general — it is what
            // duplicate detection raises when it has to READ a member name to compare
            // it, and the name carries an unpaired surrogate escape ("Cannot read
            // incomplete UTF-16 JSON text…", measured). It arrives only from this
            // call and means exactly what JsonException means here: the text is not
            // a document this platform can read.
            document = default!;
            malformed = new ProfileFailures();
            malformed.Add("", "lockey_schema_not_well_formed_json", exception.Message);
            return false;
        }
    }

    /// <summary>
    /// The genuinely failing nodes of an evaluation, as pointer-keyed details.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>OutputFormat.List</c> is <b>flat</b> — every node is a direct child of
    /// the root — so a losing branch cannot be pruned by walking a tree. Each node
    /// does carry its own <c>EvaluationPath</c>, and that is the discriminator: a
    /// node whose path has a <b>passing</b> prefix belongs to a branch of an
    /// applicator that ultimately succeeded.
    /// </para>
    /// <para>
    /// Measured, on a schema whose only fault is <c>minLength: -3</c>: the node at
    /// instance location <c>/type</c> carries evaluation path
    /// <c>/allOf/3/$ref/properties/type/anyOf/1</c>, and the node at
    /// <c>/allOf/3/$ref/properties/type</c> is valid — the meta-schema types
    /// <c>type</c> as <c>anyOf[simpleType, array-of-simpleType]</c> and the array
    /// branch loses on <c>"object"</c> while the keyword passes. Reporting that
    /// pointer tells an author a correct field is wrong, which is worse than
    /// reporting one pointer fewer.
    /// </para>
    /// <para>
    /// Bounded by <see cref="ProfileFailures"/> for the reason stated there — a
    /// pathological document fails at every node, and three sinks read
    /// <c>Details</c>.
    /// </para>
    /// </remarks>
    private static ProfileFailures Collect(EvaluationResults results, string localizationKey)
    {
        var nodes = Flatten(results).ToList();

        var passing = nodes
            .Where(node => node.IsValid)
            .Select(node => node.EvaluationPath.ToString())
            .ToHashSet(StringComparer.Ordinal);

        var failures = new ProfileFailures();

        foreach (var node in nodes)
        {
            if (node.IsValid
                || node.Errors is not { Count: > 0 }
                || HasPassingAncestor(node.EvaluationPath.ToString(), passing))
            {
                continue;
            }

            failures.Add(node.InstanceLocation.ToString(), localizationKey);
        }

        if (!failures.Any)
        {
            // Every located failure was pruned, or the evaluator reported none at
            // all. A refusal with no reason tells the author no without telling
            // them why, which is the one outcome worse than a wrong pointer.
            failures.Add("", localizationKey);
        }

        return failures;
    }

    /// <summary>
    /// Whether a proper prefix of <paramref name="evaluationPath"/>, at a segment
    /// boundary, is the path of a node that passed.
    /// </summary>
    private static bool HasPassingAncestor(string evaluationPath, HashSet<string> passing)
    {
        for (var cut = evaluationPath.LastIndexOf('/'); cut > 0; cut = evaluationPath.LastIndexOf('/', cut - 1))
        {
            if (passing.Contains(evaluationPath[..cut]))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<EvaluationResults> Flatten(EvaluationResults results)
    {
        yield return results;

        if (results.Details is null)
        {
            yield break;
        }

        foreach (var child in results.Details)
        {
            foreach (var descendant in Flatten(child))
            {
                yield return descendant;
            }
        }
    }

    private static Result<T> Fail<T>(ProfileFailures failures) =>
        Result.Fail<T>(new Error(new LocalizedMessage(ValidationFailedKey), failures.ToDetails()));

    private static Result<T> Fail<T>(string pointer, string localizationKey, string? detail = null)
    {
        var failures = new ProfileFailures();
        failures.Add(pointer, localizationKey, detail);
        return Fail<T>(failures);
    }
}
