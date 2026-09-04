using System.Text;
using System.Text.Json;
using Json.Schema;
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
    public Result<None> AdmitSchema(string jsonSchema)
    {
        // Gate 1 — it is JSON. JsonException, whose reader-depth subtype
        // JsonReaderException is internal and cannot be named in a catch.
        if (!TryParse(jsonSchema, out var document, out var malformed))
        {
            return malformed;
        }

        using (document)
        {
            if (Encoding.UTF8.GetByteCount(jsonSchema) > JsonSchemaProfile.MaxBytes)
            {
                return Fail("", "lockey_schema_too_large");
            }

            // Gate 2 — the LearnStack profile, which the library does not provide.
            var failures = new ProfileFailures();
            JsonSchemaProfile.Check(document.RootElement, failures);

            if (failures.Any)
            {
                return Fail(failures);
            }

            // Gate 3 — the meta-schema, which is where a structural mistake gets a
            // location. Authoritative about structure and silent about dialect,
            // which is gate 2's job.
            var meta = MetaSchemas.Draft202012.Evaluate(document.RootElement, Located);

            if (!meta.IsValid)
            {
                return Fail(Collect(meta, "lockey_schema_not_valid_json_schema"));
            }

            // Gate 4 — it builds. Cycle detection lives here.
            try
            {
                JsonSchema.FromText(jsonSchema, BuildOptions());
            }
            catch (RefResolutionException exception)
            {
                // First, because it derives from JsonSchemaException and the
                // broader clause below would otherwise swallow it. Gate 2 refuses a
                // non-fragment `$ref`, so reaching this means a fragment that names
                // nothing in its own document.
                return Fail("", "lockey_schema_reference_unresolvable", exception.Message);
            }
            catch (JsonSchemaException exception)
            {
                return Fail("", "lockey_schema_not_buildable", exception.Message);
            }
            catch (ArgumentException exception)
            {
                // The builder raises a bare ArgumentException for shapes it will
                // not accept — measured, "Schemas may only booleans or objects" for
                // a $ref landing on a string. Gate 2 refuses that one now, and this
                // clause exists so the next such shape is a 400 rather than the 500
                // the port's own contract says it will never produce.
                return Fail("", "lockey_schema_not_buildable", exception.Message);
            }
        }

        return Result.Ok(None.Value);
    }

    /// <inheritdoc />
    public Result<None> ValidateInstance(string admittedSchema, string instanceJson)
    {
        if (!TryParse(instanceJson, out var document, out var malformed))
        {
            return malformed;
        }

        using (document)
        {
            JsonSchema schema;

            try
            {
                schema = JsonSchema.FromText(admittedSchema, BuildOptions());
            }
            catch (Exception exception) when (exception is JsonSchemaException or JsonException)
            {
                // The caller read this from a column only AdmitSchema writes, so a
                // failure here is a broken invariant rather than a tenant's mistake
                // — and it is the one ADR-0043's Driver 2 names: something wrote a
                // row without passing this gate.
                throw new InvalidOperationException(
                    "The stored schema does not build. It was written without passing "
                    + "IJsonSchemaValidator.AdmitSchema, which is the only sanctioned "
                    + "writer of that column.",
                    exception);
            }

            var evaluation = schema.Evaluate(document.RootElement, Located);

            return evaluation.IsValid
                ? Result.Ok(None.Value)
                : Fail(Collect(evaluation, "lockey_instance_does_not_match_schema"));
        }
    }

    /// <remarks>
    /// A fresh registry per build. Both arguments are load-bearing; see the
    /// remarks on the class.
    /// </remarks>
    private static BuildOptions BuildOptions() =>
        new() { Dialect = Dialect.Draft202012, SchemaRegistry = new SchemaRegistry() };

    private static bool TryParse(string json, out JsonDocument document, out Result<None> failure)
    {
        try
        {
            document = JsonDocument.Parse(json);
            failure = default!;
            return true;
        }
        catch (JsonException exception)
        {
            document = default!;
            failure = Fail("", "lockey_schema_not_well_formed_json", exception.Message);
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

    private static Result<None> Fail(ProfileFailures failures) =>
        Result.Fail<None>(new Error(new LocalizedMessage(ValidationFailedKey), failures.ToDetails()));

    private static Result<None> Fail(string pointer, string localizationKey, string? detail = null)
    {
        var failures = new ProfileFailures();
        failures.Add(pointer, localizationKey, detail);
        return Fail(failures);
    }
}
