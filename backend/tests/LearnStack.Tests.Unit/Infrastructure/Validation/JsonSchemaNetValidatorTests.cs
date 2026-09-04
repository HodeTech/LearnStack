using FluentAssertions;
using LearnStack.Infrastructure.Validation;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Validation;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Validation;

/// <summary>
/// The four gates of
/// <see href="../../../../docs/decisions/0043-customization-payload-validation.md">ADR-0043
/// § 2</see>, and the profile clauses the evaluator does not enforce.
/// </summary>
/// <remarks>
/// <para>
/// Every profile clause here exists because the library's behaviour without it was
/// measured, so every clause gets a test that fails when the clause is removed —
/// and, where the point is that the library would <i>accept</i> the document, a
/// companion assertion that it is the profile refusing rather than a gate that
/// would have refused anyway.
/// </para>
/// <para>
/// The instance-validation half matters more than its size suggests: § 8.1 makes
/// the write path the only gate, so a payload this method admits is a payload the
/// read path will trust forever.
/// </para>
/// </remarks>
public sealed class JsonSchemaNetValidatorTests
{
    private const string Dialect = "https://json-schema.org/draft/2020-12/schema";

    // Deliberately the port, not the adapter: these tests assert the contract a
    // caller meets. CA1859 wants the concrete type for a virtual-dispatch saving
    // that is meaningless in a test and would let an adapter-only member slip into
    // an assertion.
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1859:Use concrete types when possible for improved performance",
        Justification = "The port is the contract under test.")]
    private readonly IJsonSchemaValidator _validator = new JsonSchemaNetValidator();

    /// <summary>A minimal admissible schema: the dialect line and one property.</summary>
    private static string Schema(string properties = "\"word\":{\"type\":\"string\"}", string extra = "") =>
        "{\"$schema\":\"" + Dialect + "\"," + extra + "\"type\":\"object\",\"properties\":{" + properties + "}}";

    // ── Gate 1: it is JSON ──────────────────────────────────────────────────

    [Theory]
    [InlineData("{")]
    [InlineData("")]
    [InlineData("{\"a\": }")]
    public void A_document_that_is_not_json_is_refused(string document)
    {
        Refusal(_validator.AdmitSchema(document))
            .Should().ContainKey("").WhoseValue.Should()
            .ContainSingle(m => m.Key == "lockey_schema_not_well_formed_json");
    }

    [Fact]
    public void A_document_nested_past_the_readers_ceiling_is_refused_as_malformed()
    {
        // System.Text.Json refuses beyond 64 levels, and its JsonReaderException is
        // internal — a `catch (JsonReaderException)` does not compile, so the
        // adapter catches JsonException. This is what proves it catches the right
        // one: the depth failure must not escape as an unhandled exception.
        var deep = string.Concat(Enumerable.Repeat("{\"a\":", 100))
            + "1" + new string('}', 100);

        var act = () => _validator.AdmitSchema(deep);

        act.Should().NotThrow();
        _validator.AdmitSchema(deep).IsSuccess.Should().BeFalse();
    }

    // ── Gate 2: the LearnStack profile ──────────────────────────────────────

    [Fact]
    public void A_schema_with_no_dialect_line_is_refused()
    {
        // The library's default dialect is not 2020-12, and pinning it per call
        // does not override an in-document $schema — so requiring the line is the
        // only defence that holds.
        Refusal(_validator.AdmitSchema("{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"string\"}}}"))
            .Should().ContainKey("/$schema").WhoseValue.Should()
            .ContainSingle(m => m.Key == "lockey_schema_dialect_required");
    }

    [Theory]
    [InlineData("http://json-schema.org/draft-07/schema#")]
    [InlineData("https://json-schema.org/draft/2019-09/schema")]
    [InlineData("https://json-schema.org/v1/2026")]
    [InlineData("https://example.com/my-own-dialect")]
    [InlineData("not-a-uri")]
    public void A_schema_declaring_another_dialect_is_refused(string dialect)
    {
        // draft-07 and 2019-09 BUILD, and the meta-schema calls every one of these
        // valid — it types $schema as format:"uri" and format is an annotation by
        // default, so it accepts even `not-a-uri`. Only the profile refuses them.
        var document = "{\"$schema\":\"" + dialect + "\",\"type\":\"object\",\"properties\":{}}";

        Refusal(_validator.AdmitSchema(document))
            .Should().ContainKey("/$schema").WhoseValue.Should()
            .ContainSingle(m => m.Key == "lockey_schema_dialect_not_supported");
    }

    [Fact]
    public void A_draft_07_schema_would_otherwise_pass_every_other_gate()
    {
        // The measurement behind the clause above: with the dialect line changed to
        // 2020-12 and nothing else, the same document is admitted. So gate 2 is the
        // only thing standing between a tenant and a schema whose 2020-12 keywords
        // are silently inert.
        _validator.AdmitSchema(Schema("\"a\":{\"type\":\"string\"}")).IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    public void A_root_that_is_not_an_object_is_refused(string root)
    {
        // `true` and `false` are valid 2020-12 schemas and pass gates 3 and 4.
        // `true` accepts every instance, which silently turns validation off.
        Refusal(_validator.AdmitSchema(root))
            .Should().ContainKey("").WhoseValue.Should()
            .ContainSingle(m => m.Key == "lockey_schema_root_must_be_an_object");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"type\":\"object\"}")]
    public void A_root_that_declares_no_properties_is_refused(string document)
    {
        // Also accepts every instance, and § 8.1's read-time structural pass walks
        // "the schema's field list" — of which there is none.
        Refusal(_validator.AdmitSchema(document))
            .Should().ContainKey("").WhoseValue.Should()
            .Contain(m => m.Key == "lockey_schema_root_must_declare_properties");
    }

    [Theory]
    [InlineData("\"a\":{\"type\":\"string\",\"pattern\":\"^(a+)+$\"}", "/properties/a/pattern")]
    [InlineData("\"a\":{\"type\":\"object\",\"patternProperties\":{\"^x\":{}}}", "/properties/a/patternProperties")]
    [InlineData("\"a\":{\"type\":\"object\",\"propertyNames\":{\"maxLength\":3}}", "/properties/a/propertyNames")]
    public void A_tenant_authored_regular_expression_is_refused(string properties, string at)
    {
        // The evaluator builds every tenant pattern with an infinite MatchTimeout
        // and exposes no way to bound it; `^(a+)+$` against 32 characters took 30
        // seconds on one core, in one property.
        Refusal(_validator.AdmitSchema(Schema(properties)))
            .Should().ContainKey(at).WhoseValue.Should()
            .ContainSingle(m => m.Key == "lockey_schema_regex_not_permitted");
    }

    [Theory]
    [InlineData("\"$id\":\"https://tenant-a.example/s\",", "/$id")]
    [InlineData("\"$anchor\":\"here\",", "/$anchor")]
    [InlineData("\"$dynamicAnchor\":\"node\",", "/$dynamicAnchor")]
    [InlineData("\"$dynamicRef\":\"#node\",", "/$dynamicRef")]
    public void A_schema_identity_keyword_is_refused(string extra, string at)
    {
        // $id is not a reference — it declares an identity the evaluator records.
        // Under a shared registry the first writer of an $id wins and every later
        // build of it raises, so one tenant can lock another out of saving.
        Refusal(_validator.AdmitSchema(Schema(extra: extra)))
            .Should().ContainKey(at).WhoseValue.Should()
            .ContainSingle(m => m.Key == "lockey_schema_identity_not_permitted");
    }

    [Theory]
    [InlineData("https://example.com/evil.json")]
    [InlineData("http://10.255.255.1/s.json")]
    [InlineData("other.json")]
    public void A_reference_outside_the_document_is_refused(string reference)
    {
        var document = Schema("\"a\":{\"$ref\":\"" + reference + "\"}");

        Refusal(_validator.AdmitSchema(document))
            .Should().ContainKey("/properties/a/$ref").WhoseValue.Should()
            .ContainSingle(m => m.Key == "lockey_schema_reference_must_be_local");
    }

    [Fact]
    public void A_fragment_reference_into_the_same_document_is_admitted()
    {
        var document = "{\"$schema\":\"" + Dialect + "\",\"$defs\":{\"band\":{\"type\":\"string\"}},"
            + "\"type\":\"object\",\"properties\":{\"level\":{\"$ref\":\"#/$defs/band\"}}}";

        _validator.AdmitSchema(document).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void A_fragment_reference_that_names_nothing_is_refused()
    {
        // Measured: this BUILDS. RefResolutionException arrives only when an entry
        // is validated against it — so without this clause the author's mistake
        // surfaces on someone else's save.
        var document = Schema("\"a\":{\"$ref\":\"#/$defs/missing\"}");

        Refusal(_validator.AdmitSchema(document))
            .Should().ContainKey("/properties/a/$ref").WhoseValue.Should()
            .ContainSingle(m => m.Key == "lockey_schema_reference_unresolvable");
    }

    [Theory]
    [InlineData("\"n\":{\"$ref\":\"#/$defs/n\"}")]
    [InlineData("\"a\":{\"$ref\":\"#/$defs/b\"},\"b\":{\"$ref\":\"#/$defs/a\"}")]
    [InlineData("\"n\":{\"$ref\":\"#/$defs/n\",\"type\":\"object\"}")]
    public void A_non_productive_reference_cycle_is_refused(string defs)
    {
        // This is the sharpest thing the profile does. Measured: such a document
        // BUILDS, and evaluating an entry against it overflows the stack — 4426
        // frames of RefKeyword.Evaluate — which .NET cannot catch. One tenant's
        // schema would end the process for every tenant the pod serves. The
        // builder's own cycle detection only fires from the root, which is not
        // where a tenant writes one. The third case has a sibling keyword, which
        // does not make the hop productive: in 2020-12 `$ref` applies alongside its
        // siblings and re-enters at the same instance location.
        var target = defs.StartsWith("\"n\"", StringComparison.Ordinal) ? "n" : "a";
        var document = "{\"$schema\":\"" + Dialect + "\",\"$defs\":{" + defs + "},"
            + "\"type\":\"object\",\"properties\":{\"x\":{\"$ref\":\"#/$defs/" + target + "\"}}}";

        Refusal(_validator.AdmitSchema(document)).Values
            .SelectMany(messages => messages)
            .Should().Contain(m => m.Key == "lockey_schema_reference_cycles");
    }

    [Fact]
    public void Productive_recursion_stays_legal_because_it_terminates()
    {
        // Each hop sits under `properties`, so it consumes one instance level and
        // the instance's own nesting ceiling bounds it. Measured at depth 20 in
        // under a millisecond. Refusing this would cost a shape the draft permits
        // for no safety gained.
        var document = "{\"$schema\":\"" + Dialect + "\","
            + "\"$defs\":{\"n\":{\"type\":\"object\",\"properties\":{\"next\":{\"$ref\":\"#/$defs/n\"}}}},"
            + "\"type\":\"object\",\"properties\":{\"a\":{\"$ref\":\"#/$defs/n\"}}}";

        _validator.AdmitSchema(document).IsSuccess.Should().BeTrue();
        _validator.ValidateInstance(document, "{\"a\":{\"next\":{\"next\":{}}}}")
            .IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(6, false)]
    public void Nesting_is_admitted_to_the_declared_depth_and_no_further(int levels, bool admitted)
    {
        // § 8.4 declares five SCHEMA levels; one schema level costs two JSON levels
        // (`properties`, then the name), so the boundary is exercised in the unit
        // the corpus states rather than in the unit the walk counts.
        var document = Schema(NestedProperty(levels));

        _validator.AdmitSchema(document).IsSuccess.Should().Be(admitted);

        if (!admitted)
        {
            Refusal(_validator.AdmitSchema(document)).Values
                .SelectMany(messages => messages)
                .Should().Contain(m => m.Key == "lockey_schema_too_deep");
        }
    }

    /// <summary>
    /// <c>levels</c> nested object schemas, innermost carrying a string field.
    /// </summary>
    private static string NestedProperty(int levels)
    {
        var body = "\"leaf\":{\"type\":\"string\"}";

        for (var i = 0; i < levels - 1; i++)
        {
            body = $"\"n{i}\":{{\"type\":\"object\",\"properties\":{{{body}}}}}";
        }

        return body;
    }

    [Fact]
    public void A_schema_with_more_properties_than_the_ceiling_is_refused()
    {
        var many = string.Join(",", Enumerable.Range(0, 101)
            .Select(i => $"\"p{i}\":{{\"type\":\"string\"}}"));

        Refusal(_validator.AdmitSchema(Schema(many)))
            .Should().ContainKey("/properties").WhoseValue.Should()
            .ContainSingle(m => m.Key == "lockey_schema_too_many_properties");
    }

    [Fact]
    public void Exactly_the_property_ceiling_is_admitted()
    {
        var many = string.Join(",", Enumerable.Range(0, 100)
            .Select(i => $"\"p{i}\":{{\"type\":\"string\"}}"));

        _validator.AdmitSchema(Schema(many)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void A_schema_larger_than_the_row_cap_is_refused()
    {
        var padding = new string('x', 300 * 1024);
        var document = Schema($"\"a\":{{\"type\":\"string\",\"description\":\"{padding}\"}}");

        Refusal(_validator.AdmitSchema(document))
            .Should().ContainKey("").WhoseValue.Should()
            .ContainSingle(m => m.Key == "lockey_schema_too_large");
    }

    // ── Gate 3: the meta-schema ─────────────────────────────────────────────

    [Theory]
    [InlineData("\"a\":{\"type\":42}")]
    [InlineData("\"a\":{\"required\":\"word\"}")]
    [InlineData("\"a\":{\"minLength\":-3}")]
    public void A_document_that_is_not_a_valid_schema_is_refused(string properties)
    {
        _validator.AdmitSchema(Schema(properties)).IsSuccess.Should().BeFalse();
    }

    // ── Gate 4 admits what the corpus's own examples declare ────────────────

    [Fact]
    public void The_corpus_worked_example_is_admitted()
    {
        // 32-tenant-customization-model.md § 3 Example A, with the $schema line the
        // profile requires. If this ever stops being admissible, either the profile
        // or the published example is wrong, and a reader will hit it first.
        var vocabularyCard = "{\"$schema\":\"" + Dialect + "\",\"type\":\"object\","
            + "\"required\":[\"word\",\"definition\"],\"properties\":{"
            + "\"word\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":100},"
            + "\"pronunciation\":{\"type\":\"string\"},"
            + "\"definition\":{\"type\":\"string\",\"format\":\"markdown\"},"
            + "\"example_sentences\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"maxItems\":5},"
            + "\"audio_url\":{\"type\":\"string\",\"format\":\"uri\",\"x-renderer\":\"audio\"},"
            + "\"level\":{\"type\":\"string\",\"x-taxonomy\":\"cefr\"}}}";

        _validator.AdmitSchema(vocabularyCard).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void An_extension_keyword_passes_because_the_pinned_dialect_admits_it()
    {
        // Under the library's DEFAULT dialect this raises
        // "Unknown keywords (x-taxonomy) are disallowed for this dialect" — so this
        // asserts the adapter pins 2020-12 per call, not that draft 2020-12 is
        // tolerant.
        _validator.AdmitSchema(Schema("\"level\":{\"type\":\"string\",\"x-taxonomy\":\"cefr\"}"))
            .IsSuccess.Should().BeTrue();
    }

    // ── Instance validation ─────────────────────────────────────────────────

    [Fact]
    public void An_instance_that_matches_its_schema_is_accepted()
    {
        var schema = Schema("\"word\":{\"type\":\"string\",\"minLength\":1}");

        _validator.ValidateInstance(schema, "{\"word\":\"ephemeral\"}").IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void An_instance_that_violates_its_schema_is_refused_at_the_offending_pointer()
    {
        var schema = "{\"$schema\":\"" + Dialect + "\",\"type\":\"object\",\"required\":[\"word\"],"
            + "\"properties\":{\"word\":{\"type\":\"string\",\"minLength\":1}},"
            + "\"additionalProperties\":false}";

        var refusal = Refusal(_validator.ValidateInstance(schema, "{\"word\":\"\",\"extra\":1}"));

        refusal.Keys.Should().Contain("/word", "the pointer § 8.1 requires Problem Details to name");
        refusal.Values.SelectMany(messages => messages)
            .Should().Contain(m => m.Key == "lockey_instance_does_not_match_schema");
    }

    [Fact]
    public void An_instance_that_is_not_json_is_refused_rather_than_throwing()
    {
        var act = () => _validator.ValidateInstance(Schema(), "{");

        act.Should().NotThrow();
        _validator.ValidateInstance(Schema(), "{").IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void An_invalid_instance_always_reports_at_least_one_reason()
    {
        // A refusal whose details are empty is a 400 with no reason, which is worse
        // than a 500 — the author is told no and not told why.
        var schema = Schema("\"n\":{\"type\":\"integer\",\"minimum\":10}");

        Refusal(_validator.ValidateInstance(schema, "{\"n\":1}")).Should().NotBeEmpty();
    }

    [Fact]
    public void A_stored_schema_that_does_not_build_is_a_broken_invariant_not_a_tenant_error()
    {
        // Only AdmitSchema writes that column. If a migration, a repair script or a
        // future importer put something else there, the read path has been trusting
        // an unvalidated row — that is not a 400.
        var act = () => _validator.ValidateInstance("{\"type\": 42}", "{}");

        act.Should().Throw<InvalidOperationException>().WithMessage("*only sanctioned writer*");
    }

    // ── Isolation ───────────────────────────────────────────────────────────

    [Fact]
    public void One_schemas_identifier_never_reaches_another_build()
    {
        // The library's default registry is process-global: the first writer of an
        // $id wins and every later build of it raises for the life of the process.
        // The profile refuses $id outright, and the adapter builds against a fresh
        // registry — so neither half can be reached. This proves the second half by
        // admitting the same document twice, which a shared registry would refuse.
        var document = Schema("\"a\":{\"type\":\"string\"}");

        _validator.AdmitSchema(document).IsSuccess.Should().BeTrue();
        _validator.AdmitSchema(document).IsSuccess.Should().BeTrue();

        var second = new JsonSchemaNetValidator();
        second.AdmitSchema(document).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void The_validator_holds_no_state_between_calls()
    {
        // No cache: compiling is measured cheaper than evaluating, so ADR-0043 § 6
        // deletes the one § 8.2 mandated. A validator that memoised would have to
        // answer when a revision changes underneath it.
        _validator.GetType().GetFields(
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public)
            .Should().BeEmpty("the adapter compiles per call and remembers nothing");
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<LocalizedMessage>> Refusal(
        Result<None> result)
    {
        result.IsSuccess.Should().BeFalse("the document under test must be refused");
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("validation_failed",
            "a tenant's authoring mistake is a 400, and the status map is a closed table");
        result.Error.Details.Should().NotBeNull("a refusal names where it refused");
        return result.Error.Details!;
    }
}
