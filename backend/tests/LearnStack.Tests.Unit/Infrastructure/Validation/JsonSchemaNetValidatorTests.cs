using FluentAssertions;
using LearnStack.Infrastructure.Validation;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Validation;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Validation;

/// <summary>
/// The four gates of
/// <see href="../../../../../docs/decisions/0043-customization-payload-validation.md">ADR-0043
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
    public void Recursion_is_refused_even_where_it_would_terminate()
    {
        // A $ref under `properties` consumes an instance level per hop, so this
        // one does terminate — and it is refused anyway. Distinguishing it from
        // the shapes that do NOT terminate means classifying every in-place
        // applicator, and the cost of getting that list wrong is a process, not a
        // wrong answer. Recursive schemas are a shape no document in the corpus
        // uses, whose entry-to-entry cousin section 8.3 already caps at depth two.
        var document = "{\"$schema\":\"" + Dialect + "\","
            + "\"$defs\":{\"n\":{\"type\":\"object\",\"properties\":{\"next\":{\"$ref\":\"#/$defs/n\"}}}},"
            + "\"type\":\"object\",\"properties\":{\"a\":{\"$ref\":\"#/$defs/n\"}}}";

        Refusal(_validator.AdmitSchema(document)).Values
            .SelectMany(messages => messages)
            .Should().Contain(m => m.Key == "lockey_schema_reference_cycles");
    }

    [Theory]
    [InlineData("\"allOf\":[{\"$ref\":\"#/$defs/a\"}]")]
    [InlineData("\"anyOf\":[{\"$ref\":\"#/$defs/a\"}]")]
    [InlineData("\"oneOf\":[{\"$ref\":\"#/$defs/a\"}]")]
    [InlineData("\"not\":{\"$ref\":\"#/$defs/a\"}")]
    [InlineData("\"if\":{\"$ref\":\"#/$defs/a\"}")]
    [InlineData("\"dependentSchemas\":{\"k\":{\"$ref\":\"#/$defs/a\"}}")]
    [InlineData("\"properties\":{\"deeper\":{\"$ref\":\"#/$defs/a\"}}")]
    public void A_cycle_wrapped_in_any_applicator_is_refused(string body)
    {
        // The first six are why the earlier rule was replaced. It followed a $ref
        // only while the landed node carried a $ref of its own, on the theory that
        // anything else consumed an instance level. Every one of these re-enters at
        // the SAME instance location without a top-level $ref — measured, all six
        // were admitted, built, and ended the process with SIGABRT (exit 134) on
        // the first entry validated against them, from 157 bytes of JSON.
        var document = "{\"$schema\":\"" + Dialect + "\",\"$defs\":{\"a\":{" + body + "}},"
            + "\"type\":\"object\",\"properties\":{\"x\":{\"$ref\":\"#/$defs/a\"}}}";

        Refusal(_validator.AdmitSchema(document)).Values
            .SelectMany(messages => messages)
            .Should().Contain(m => m.Key == "lockey_schema_reference_cycles");
    }

    [Fact]
    public void An_acyclic_reference_graph_that_expands_exponentially_is_refused()
    {
        // No cycle anywhere. Twenty $defs entries, each referencing the next twice,
        // give 2^20 visits of ONE instance location — measured at 7.2 seconds and
        // 5.7 GB of resident memory from 1,401 bytes of JSON. Refusing cycles alone
        // does not reach this; the graph has to be costed.
        var defs = Enumerable.Range(0, 20)
            .Select(i => $"\"a{i}\":{{\"allOf\":[{{\"$ref\":\"#/$defs/a{i + 1}\"}},{{\"$ref\":\"#/$defs/a{i + 1}\"}}]}}")
            .Append("\"a20\":{\"type\":\"object\"}");
        var document = "{\"$schema\":\"" + Dialect + "\",\"$defs\":{" + string.Join(",", defs) + "},"
            + "\"type\":\"object\",\"properties\":{\"x\":{\"$ref\":\"#/$defs/a0\"}}}";

        Refusal(_validator.AdmitSchema(document)).Values
            .SelectMany(messages => messages)
            .Should().Contain(m => m.Key == "lockey_schema_reference_graph_too_large");
    }

    [Fact]
    public void A_long_reference_chain_is_refused_without_paying_for_it()
    {
        // Two thousand links, 64 KB, inside every other declared limit. Before the
        // graph was costed this was ADMITTED after 6.7 seconds of the profile's own
        // walking — LearnStack's code, not the library's. The bound makes it bail.
        var defs = Enumerable.Range(0, 2000)
            .Select(i => $"\"a{i}\":{{\"$ref\":\"#/$defs/a{i + 1}\"}}")
            .Append("\"a2000\":{\"type\":\"object\"}");
        var document = "{\"$schema\":\"" + Dialect + "\",\"$defs\":{" + string.Join(",", defs) + "},"
            + "\"type\":\"object\",\"properties\":{\"x\":{\"$ref\":\"#/$defs/a0\"}}}";

        var clock = System.Diagnostics.Stopwatch.StartNew();
        Refusal(_validator.AdmitSchema(document)).Values
            .SelectMany(messages => messages)
            .Should().Contain(m => m.Key == "lockey_schema_reference_graph_too_large");
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2),
            "the bound has to stop the walk, not merely report on it afterwards");
    }

    [Fact]
    public void A_reference_that_lands_on_something_that_is_not_a_schema_is_refused()
    {
        // A fragment may name any JSON value. Measured: without this clause the
        // builder raised a bare ArgumentException — "Schemas may only booleans or
        // objects. Received String" — straight out of AdmitSchema, which is a 500
        // from a port whose contract is that nothing throws.
        var document = Schema("\"a\":{\"type\":\"string\"},\"b\":{\"$ref\":\"#/properties/a/type\"}");

        Refusal(_validator.AdmitSchema(document))
            .Should().ContainKey("/properties/b/$ref").WhoseValue.Should()
            .ContainSingle(m => m.Key == "lockey_schema_reference_not_a_schema");
    }

    [Theory]
    [InlineData("pattern")]
    [InlineData("patternProperties")]
    [InlineData("propertyNames")]
    [InlineData("$id")]
    [InlineData("$anchor")]
    [InlineData("$dynamicRef")]
    [InlineData("$schema")]
    [InlineData("$ref")]
    public void A_field_may_be_named_after_a_keyword_the_profile_bans(string fieldName)
    {
        // The bans read keywords, and inside `properties` the key is the author's.
        // A knitting school's content type has a `pattern` field; refusing it for
        // its name would be the profile mistaking data for schema. Measured: every
        // one of these was refused before the walk learned where it was.
        var document = Schema("\"" + fieldName + "\":{\"type\":\"string\"}");

        _validator.AdmitSchema(document).IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData("\"$defs\":{\"pattern\":{\"type\":\"string\"}},\"properties\":{\"a\":{\"$ref\":\"#/$defs/pattern\"}}")]
    [InlineData("\"properties\":{\"a\":{\"type\":\"object\",\"dependentSchemas\":{\"$id\":{\"type\":\"object\",\"properties\":{\"z\":{\"type\":\"string\"}}}}}}")]
    [InlineData("\"properties\":{\"a\":{\"type\":\"object\",\"dependentRequired\":{\"pattern\":[\"b\"]}}}")]
    public void Every_name_map_keyword_shields_the_names_inside_it(string body)
    {
        // One case per REACHABLE entry of the name-map list. Only `properties` was
        // covered, so the others could be dropped and nothing failed — and dropping
        // one refuses a legal document rather than admitting an illegal one, which
        // is a failure only a test will notice. `patternProperties` has no case
        // because the keyword itself is banned, so no admitted schema contains one;
        // its entry is defence for the day ADR-0043 § 5's trigger fires.
        var document = "{\"$schema\":\"" + Dialect + "\",\"type\":\"object\"," + body + "}";

        _validator.AdmitSchema(document).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void A_schema_of_exactly_the_row_cap_is_admitted_and_one_byte_more_is_not()
    {
        // Only the refusal was pinned, so the cap could be widened or narrowed and
        // nothing failed. Both edges are asserted here, and the size is built from
        // the literal 256 KB § 8.4 publishes rather than from the constant under
        // test — reading the constant would move the input with it.
        const int cap = 256 * 1024;
        var shell = Schema("\"a\":{\"type\":\"string\",\"description\":\"\"}");
        var padding = cap - System.Text.Encoding.UTF8.GetByteCount(shell);

        var atCap = Schema($"\"a\":{{\"type\":\"string\",\"description\":\"{new string('x', padding)}\"}}");
        var overCap = Schema($"\"a\":{{\"type\":\"string\",\"description\":\"{new string('x', padding + 1)}\"}}");

        System.Text.Encoding.UTF8.GetByteCount(atCap).Should().Be(cap);
        _validator.AdmitSchema(atCap).IsSuccess.Should().BeTrue();
        Refusal(_validator.AdmitSchema(overCap))
            .Should().ContainKey("").WhoseValue.Should()
            .ContainSingle(m => m.Key == "lockey_schema_too_large");
    }

    [Theory]
    [InlineData("\"type\":\"string\"", true)]
    [InlineData("\"not\":{\"type\":\"string\"}", false)]
    public void Nesting_is_admitted_to_the_ceiling_exactly(string leaf, bool admitted)
    {
        // Two documents nested identically, differing by exactly ONE raw JSON
        // level: `not` takes a schema directly, so it adds one object where
        // `properties` adds two. The boundary falls between them, so moving
        // MaxDepth in either direction fails one of the two. The first pair moved
        // in steps of two — a `properties` level costs two JSON levels — and could
        // not see a one-step drift.
        //
        // The second pair used `enum:["x"]`, whose array element also sits one
        // level down. That worked, and it was measuring the wrong tree: an enum
        // holds an INSTANCE, and § 8.4's depth is the schema's. The walk no longer
        // descends there, so the pair had to move to a keyword that really does
        // carry a subschema.
        var body = leaf;

        for (var i = 0; i < 5; i++)
        {
            body = "\"properties\":{\"n" + i + "\":{" + body + "}}";
        }

        var document = "{\"$schema\":\"" + Dialect + "\",\"type\":\"object\"," + body + "}";

        _validator.AdmitSchema(document).IsSuccess.Should().Be(admitted);
    }

    [Theory]
    [InlineData(998, true)]
    [InlineData(999, false)]
    public void A_reference_graph_is_admitted_to_the_bound_exactly(int links, bool admitted)
    {
        // 998 links plus the terminal node plus the root cost exactly 1000; 999
        // links cost 1001. One link apart, so raising the bound by one fails the
        // second case — the first pair was a thousand apart and did not.
        //
        // The root is in the count because the bound is the DOCUMENT's, which is
        // what one evaluation costs. Priced per reference instead, this pair still
        // passed while a hundred references to one 256-cost target were admitted at
        // fifty times the price — the case below.
        var defs = Enumerable.Range(0, links)
            .Select(i => $"\"a{i}\":{{\"$ref\":\"#/$defs/a{i + 1}\"}}")
            .Append($"\"a{links}\":{{\"type\":\"object\"}}");
        var document = "{\"$schema\":\"" + Dialect + "\",\"$defs\":{" + string.Join(",", defs) + "},"
            + "\"type\":\"object\",\"properties\":{\"x\":{\"$ref\":\"#/$defs/a0\"}}}";

        _validator.AdmitSchema(document).IsSuccess.Should().Be(admitted);
    }

    [Theory]
    [InlineData("\"default\":{\"x\":{\"pattern\":\"^safe$\"}}")]
    [InlineData("\"const\":{\"x\":{\"$id\":\"https://example.test\"}}")]
    [InlineData("\"enum\":[{\"x\":{\"propertyNames\":{}}}]")]
    public void A_reference_may_not_reach_into_a_literal(string leaf)
    {
        // The other edge of "an instance is not a schema". Once the walk stopped
        // reading a literal as one, a `$ref` INTO that literal made it a schema
        // again — applied by the evaluator, and carrying whatever the profile
        // bans, because nothing had checked it. Measured: admitted, with the
        // banned `pattern` live.
        //
        // A pointer alone cannot tell the two apart, so the walk records which
        // positions it reached AS schemas and a target must be one of them.
        var pointer = leaf.StartsWith("\"enum\"", System.StringComparison.Ordinal)
            ? "#/properties/a/enum/0/x"
            : "#/properties/a/" + leaf[1..leaf.IndexOf('"', 1)] + "/x";

        var document = "{\"$schema\":\"" + Dialect + "\",\"type\":\"object\",\"properties\":{"
            + "\"a\":{\"type\":\"object\"," + leaf + "},"
            + "\"b\":{\"$ref\":\"" + pointer + "\"}}}";

        Refusal(_validator.AdmitSchema(document))
            .Values.SelectMany(reasons => reasons).Select(reason => reason.Key)
            .Should().Contain("lockey_schema_reference_not_a_schema");
    }

    [Fact]
    public void A_field_a_tenant_named_dollar_defs_does_not_leave_the_graph()
    {
        // The graph traversal skipped every property named `$defs`, wherever it
        // sat. Inside `properties` that name is the author's, so renaming a field
        // from `inner` to `$defs` took its edges out of the cycle check —
        // measured, the same cycle went from refused to admitted, and ADR-0043
        // § Context measures an admitted cycle ending the process at evaluation.
        var document = "{\"$schema\":\"" + Dialect + "\",\"type\":\"object\",\"properties\":{"
            + "\"a\":{\"allOf\":[{\"$ref\":\"#/$defs/loop\"}]}},"
            + "\"$defs\":{\"loop\":{\"type\":\"object\",\"properties\":{"
            + "\"$defs\":{\"allOf\":[{\"$ref\":\"#/$defs/loop\"}]}}}}}";

        Refusal(_validator.AdmitSchema(document))
            .Values.SelectMany(reasons => reasons).Select(reason => reason.Key)
            .Should().Contain("lockey_schema_reference_cycles");
    }

    [Fact]
    public void A_literal_reference_stays_literal_beside_a_real_one()
    {
        // The graph read a `$ref`-shaped key inside `const` as an edge. Nothing
        // showed it while the document had no real reference — the loop returned
        // early — so adding one elsewhere turned the tenant's own literal into an
        // unresolvable reference.
        var document = "{\"$schema\":\"" + Dialect + "\",\"type\":\"object\",\"properties\":{"
            + "\"a\":{\"const\":{\"$ref\":\"#/nope\"}},"
            + "\"b\":{\"$ref\":\"#/$defs/ok\"}},\"$defs\":{\"ok\":{\"type\":\"string\"}}}";

        _validator.AdmitSchema(document).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void A_percent_encoded_fragment_resolves_the_pointer_it_names()
    {
        // RFC 6901 § 6 decodes the fragment as a whole and then splits it, so
        // `#%2F$defs%2Ftext` is `#/$defs/text`. Splitting first left one token that
        // matched nothing, and the evaluator — which resolves it — would then have
        // been handed a schema this gate called unresolvable.
        var document = "{\"$schema\":\"" + Dialect + "\",\"type\":\"object\",\"properties\":{"
            + "\"a\":{\"$ref\":\"#%2F$defs%2Ftext\"}},\"$defs\":{\"text\":{\"type\":\"string\"}}}";

        _validator.AdmitSchema(document).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void A_reference_graph_is_costed_across_the_whole_document()
    {
        // A hundred sites referencing one target that costs 256 each. No single
        // reference is near the bound; the document is fifty times over it.
        // Measured before this was costed: admitted at 3,400 bytes, and one
        // instance validation against it took 147 ms and allocated 125 MB — while
        // a 698-byte document with ONE reference was refused.
        var defs = Enumerable.Range(0, 9)
            .Select(i => i < 8
                ? $"\"d{i}\":{{\"allOf\":[{{\"$ref\":\"#/$defs/d{i + 1}\"}},{{\"$ref\":\"#/$defs/d{i + 1}\"}}]}}"
                : $"\"d{i}\":{{\"type\":\"string\"}}");

        var properties = Enumerable.Range(0, 100)
            .Select(i => $"\"p{i}\":{{\"$ref\":\"#/$defs/d0\"}}");

        var document = "{\"$schema\":\"" + Dialect + "\",\"type\":\"object\",\"properties\":{"
            + string.Join(",", properties) + "},\"$defs\":{" + string.Join(",", defs) + "}}";

        Refusal(_validator.AdmitSchema(document))
            .Values.SelectMany(reasons => reasons).Select(reason => reason.Key)
            .Should().Contain("lockey_schema_reference_graph_too_large");
    }

    [Fact]
    public void The_shape_ADR_0043_prices_at_101_is_still_admitted()
    {
        // The other edge of the same bound, and the one the ADR names: a hundred
        // properties applying one shared `$defs` shape "sees a cost of 101, because
        // the cost model counts reference expansion and this is reference reuse".
        // A document bound that refused this would be refusing what § 8.4 declares.
        var properties = Enumerable.Range(0, 100)
            .Select(i => $"\"p{i}\":{{\"$ref\":\"#/$defs/shared\"}}");

        var document = "{\"$schema\":\"" + Dialect + "\",\"type\":\"object\",\"properties\":{"
            + string.Join(",", properties) + "},\"$defs\":{\"shared\":{\"type\":\"string\"}}}";

        _validator.AdmitSchema(document).IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData("\"const\":{\"$ref\":\"#/nope\"}")]
    [InlineData("\"enum\":[{\"pattern\":\"x\"}]")]
    [InlineData("\"default\":{\"$id\":\"https://example.test\"}")]
    [InlineData("\"examples\":[{\"propertyNames\":{}}]")]
    public void A_literal_that_looks_like_a_keyword_is_still_a_literal(string leaf)
    {
        // The tenant's DATA, not its schema. All four were refused before the walk
        // stopped at an instance-valued keyword — as an unresolvable reference, a
        // banned regex, a banned identity — for the shape of a value the schema
        // exists to constrain rather than to interpret. It is the same lesson the
        // name-map list records, one level further in.
        var document = "{\"$schema\":\"" + Dialect + "\",\"type\":\"object\","
            + "\"properties\":{\"a\":{" + leaf + "}}}";

        _validator.AdmitSchema(document).IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData("#/properties/a/oneOf/0", true)]
    [InlineData("#/properties/a/oneOf/1", true)]
    [InlineData("#/properties/a/oneOf/2", false)]
    [InlineData("#/properties/a~1b", false)]
    public void A_reference_may_index_an_array_and_may_not_run_off_its_end(string reference, bool admitted)
    {
        // Index 2 on a two-element array is the case that discriminates: an
        // off-by-one in the bound admits it, and an index far past the end does
        // not. The array branch had no test at all before.
        var document = "{\"$schema\":\"" + Dialect + "\",\"type\":\"object\",\"properties\":{"
            + "\"a\":{\"oneOf\":[{\"type\":\"string\"},{\"type\":\"integer\"}]},"
            + "\"b\":{\"$ref\":\"" + reference + "\"}}}";

        _validator.AdmitSchema(document).IsSuccess.Should().Be(admitted);
    }

    [Fact]
    public void A_pointer_token_is_percent_decoded_before_it_is_unescaped()
    {
        // RFC 3986 first, then RFC 6901 — and ~0 after ~1, so an encoded tilde in a
        // field name does not turn into a slash. The percent-decode had no case at
        // all, and the order had none that discriminated.
        var document = "{\"$schema\":\"" + Dialect + "\",\"$defs\":{"
            + "\"a/b\":{\"type\":\"string\"},"      // reached as ~1
            + "\"c~1d\":{\"type\":\"integer\"},"    // reached as ~01, NOT as a slash
            + "\"e f\":{\"type\":\"boolean\"}"      // reached as %20
            + "},\"type\":\"object\",\"properties\":{"
            + "\"x\":{\"$ref\":\"#/$defs/a~1b\"},"
            + "\"y\":{\"$ref\":\"#/$defs/c~01d\"},"
            + "\"z\":{\"$ref\":\"#/$defs/e%20f\"}}}";

        _validator.AdmitSchema(document).IsSuccess.Should().BeTrue();
    }


    [Fact]
    public void A_banned_keyword_is_still_refused_one_level_below_an_authored_name()
    {
        // The other half: the VALUE under an authored name is a schema again, so
        // its own keys are keywords. A rule that stopped checking after the first
        // name map would let every ban be bypassed by one level of nesting.
        Refusal(_validator.AdmitSchema(Schema("\"pattern\":{\"type\":\"string\",\"pattern\":\"^(a+)+$\"}")))
            .Should().ContainKey("/properties/pattern/pattern").WhoseValue.Should()
            .ContainSingle(m => m.Key == "lockey_schema_regex_not_permitted");
    }

    [Fact]
    public void A_dialect_line_below_the_root_is_refused()
    {
        // The root clause reads root["$schema"] and saw nothing here. Measured: a
        // draft-07 line under properties/a makes prefixItems inert for that
        // subschema alone, so the same schema text accepts and rejects the same
        // instance — the exact failure the root clause exists to prevent, one
        // level down.
        var document = Schema(
            "\"a\":{\"$schema\":\"http://json-schema.org/draft-07/schema#\",\"prefixItems\":[{\"type\":\"integer\"}]}");

        Refusal(_validator.AdmitSchema(document))
            .Should().ContainKey("/properties/a/$schema").WhoseValue.Should()
            .ContainSingle(m => m.Key == "lockey_schema_dialect_not_at_root");
    }

    [Fact]
    public void An_empty_properties_object_is_refused_like_the_bare_object_it_equals()
    {
        // properties:{} satisfies "declares properties" and accepts 42, a string
        // and every object alike — semantically the {} the clause above refuses.
        Refusal(_validator.AdmitSchema(Schema("")))
            .Should().ContainKey("/properties").WhoseValue.Should()
            .ContainSingle(m => m.Key == "lockey_schema_root_must_declare_properties");
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

    // ── The column, not only the grammar ────────────────────────────────────

    [Theory]
    [InlineData("\"default\":\"\\u0000\"", "/properties/a/default", "lockey_schema_text_not_storable")]
    [InlineData("\"const\":\"\\ud800\"", "/properties/a/const", "lockey_schema_text_not_storable")]
    [InlineData("\"examples\":[\"\\udc00\"]", "/properties/a/examples/0", "lockey_schema_text_not_storable")]
    [InlineData("\"default\":1e1000000", "/properties/a/default", "lockey_schema_number_not_storable")]
    public void A_document_the_column_cannot_hold_is_refused_at_the_value(
        string extra, string at, string reason)
    {
        // Measured on PostgreSQL 18.6, as jsonb: `\u0000` is 22P05, an unpaired
        // surrogate is 22P02, and 1e1000000 is 22003 — while JsonDocument.Parse
        // accepts all three. Without this clause the tenant's mistake arrived as a
        // 500 from the INSERT, which is the one answer § 8.1 rules out.
        //
        // All four literals sit under keywords the schema walk deliberately does
        // NOT descend into. That is the point: a `default` is stored in the same
        // column as the schema around it.
        Refusal(_validator.AdmitSchema(Schema("\"a\":{\"type\":\"string\"," + extra + "}")))
            .Should().ContainKey(at).WhoseValue.Should().ContainSingle(m => m.Key == reason);
    }

    [Fact]
    public void A_member_name_the_column_cannot_hold_is_refused_at_its_object()
    {
        // The object's pointer, not the member's: a pointer built from a name
        // carrying a NUL would put that NUL into a Problem Details key, an audit
        // row and a log line.
        Refusal(_validator.AdmitSchema(
                Schema("\"a\":{\"type\":\"object\",\"default\":{\"\\u0000\":1}}")))
            .Should().ContainKey("/properties/a/default").WhoseValue.Should()
            .ContainSingle(m => m.Key == "lockey_schema_text_not_storable");
    }

    [Theory]
    [InlineData("1e131071", true)]
    [InlineData("1e131072", false)]
    [InlineData("12e131071", false)]
    [InlineData("1e-16383", true)]
    [InlineData("1e-16384", false)]
    public void The_numeric_bound_is_where_postgresql_measured_it(string number, bool admitted)
    {
        // numeric holds 131,072 digits before the point and 16,383 after, and one
        // more of either is 22003. The boundary is asserted on both sides because a
        // guard that refused 1e131071 as well would be refusing what the column
        // stores — which no failing INSERT would ever reveal.
        _validator.AdmitSchema(Schema("\"a\":{\"type\":\"number\",\"default\":" + number + "}"))
            .IsSuccess.Should().Be(admitted);
    }

    [Theory]
    [InlineData("\"\\ud83d\\ude00\"")]
    [InlineData("\"\\\\u0000\"")]
    public void What_the_column_does_hold_is_still_admitted(string literal)
    {
        // A correctly paired surrogate stores, and so does the six-character
        // literal `\u0000` written with an escaped backslash — measured. Reading
        // the parsed value rather than scanning the document text is what tells
        // those two apart from the escapes above.
        _validator.AdmitSchema(Schema("\"a\":{\"type\":\"string\",\"default\":" + literal + "}"))
            .IsSuccess.Should().BeTrue();
    }

    // ── LearnStack extensions: reported, not resolved ───────────────────────

    [Theory]
    [InlineData("x-renderer", "audio")]
    [InlineData("x-taxonomy", "cefr")]
    [InlineData("x-language", "python")]
    public void An_extension_is_reported_with_the_pointer_that_locates_it(
        string keyword, string value)
    {
        // ADR-0043 § 4: the pinned dialect admits these keywords and the validator
        // has no opinion about them, but § 8.1 requires each to resolve to a
        // registry entry ON SAVING. The module owns the registries; only this walk
        // knows where the keyword sits, so it reports and the module decides.
        Extensions(Schema($"\"a\":{{\"type\":\"string\",\"{keyword}\":\"{value}\"}}"))
            .Should().ContainSingle()
            .Which.Should().BeEquivalentTo(
                new SchemaExtensionReference($"/properties/a/{keyword}", keyword, value));
    }

    [Theory]
    [InlineData("x-renderer")]
    [InlineData("x-taxonomy")]
    [InlineData("x-language")]
    public void A_field_a_tenant_named_after_an_extension_is_still_a_field(string keyword)
    {
        // The same trap `properties/pattern` was: inside a name map the key is the
        // author's. Reporting it would refuse the tenant's own field for naming
        // something the platform cannot resolve — and there is nothing to resolve,
        // because it is a field name.
        Extensions(Schema($"\"{keyword}\":{{\"type\":\"string\"}}"))
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData("const")]
    [InlineData("default")]
    [InlineData("examples")]
    public void An_extension_inside_a_literal_is_the_tenants_own_data(string keyword)
    {
        // One level further in: the value of an instance-valued keyword is data the
        // schema constrains, so an `x-renderer` there is a key of the tenant's own
        // object and names no renderer.
        var literal = keyword == "examples"
            ? "[{\"x-renderer\":\"unicorn\"}]"
            : "{\"x-renderer\":\"unicorn\"}";

        Extensions(Schema($"\"a\":{{\"type\":\"object\",\"{keyword}\":{literal}}}"))
            .Should().BeEmpty();
    }

    [Fact]
    public void A_non_string_extension_is_reported_as_the_json_it_is()
    {
        // Carried rather than refused here: the registries are the authority on
        // what resolves, and `3` naming no renderer is the same answer a misspelled
        // key gets. A nullable value would be a second failure mode for every
        // caller to handle.
        Extensions(Schema("\"a\":{\"type\":\"string\",\"x-renderer\":3}"))
            .Should().ContainSingle().Which.Value.Should().Be("3");
    }

    [Fact]
    public void An_extension_value_is_not_read_as_a_schema()
    {
        // It names a registry entry, so its contents are not keywords. Without the
        // stop, this document is refused for a banned regex keyword the tenant
        // never wrote as a keyword — the defect `const` and `enum` already had.
        Extensions(Schema("\"a\":{\"type\":\"string\",\"x-renderer\":{\"pattern\":\"(\"}}"))
            .Should().ContainSingle().Which.Value.Should().Be("{\"pattern\":\"(\"}");
    }

    [Fact]
    public void A_document_the_gates_refuse_reports_no_extensions()
    {
        // The list is an admission's payload. A caller cannot reach it without a
        // success, and this is what says so where a reader looks for it.
        _validator.AdmitSchema("{\"type\":\"object\"}").IsSuccess.Should().BeFalse();
    }

    // ── Gate 3: the meta-schema ─────────────────────────────────────────────

    [Theory]
    [InlineData("\"a\":{\"type\":42}", "/properties/a/type")]
    [InlineData("\"a\":{\"required\":\"word\"}", "/properties/a/required")]
    [InlineData("\"a\":{\"minLength\":-3}", "/properties/a/minLength")]
    [InlineData("\"a\":{\"type\":\"object\",\"properties\":{\"b\":{\"maxItems\":-1}}}",
        "/properties/a/properties/b/maxItems")]
    public void A_document_that_is_not_a_valid_schema_is_refused_at_the_offending_keyword(
        string properties, string at)
    {
        // The POINTER is the assertion, not merely the refusal. Gate 4 refuses all
        // four of these too, with a message and no location — so a test that only
        // checked IsSuccess would pass with gate 3 deleted, and gate 3 is the gate
        // that exists to name where the author went wrong.
        Refusal(_validator.AdmitSchema(Schema(properties)))
            .Should().ContainKey(at).WhoseValue.Should()
            .ContainSingle(m => m.Key == "lockey_schema_not_valid_json_schema");
    }

    [Fact]
    public void A_refusal_names_only_the_keyword_that_failed()
    {
        // OutputFormat.List reports the losing branches of an anyOf that
        // ultimately succeeded. Measured: the meta-schema types `type` as
        // anyOf[simpleType, array-of-simpleType], so a schema whose only fault is
        // minLength also reported `/type` — telling the author a correct field is
        // wrong.
        Refusal(_validator.AdmitSchema(Schema("\"a\":{\"minLength\":-3}")))
            .Keys.Should().NotContain("/type");
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

    [Theory]
    [InlineData("{\"type\": 42}")]
    [InlineData("{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"type\":\"object\",\"properties\":{\"a\":{\"$ref\":\"https://example.com/x.json\"}}}")]
    [InlineData("{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"type\":\"object\",\"properties\":{\"a\":{\"$ref\":\"#/$defs/nope\"}}}")]
    [InlineData("{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"string\"},\"b\":{\"$ref\":\"#/properties/a/type\"}}}")]
    public void A_stored_schema_the_gate_never_admitted_is_a_broken_invariant_not_a_tenant_error(
        string storedSchema)
    {
        // Only AdmitSchema writes that column, so any of these means a row was
        // written past the gate — a 500, not a 400. Measured before this was
        // sealed: the last three escaped RAW, as Json.Schema.RefResolutionException
        // and System.ArgumentException, past a port whose contract names exactly
        // one exception. Two of them surface from Evaluate and never from FromText,
        // which is why the evaluation is inside the try.
        var act = () => _validator.ValidateInstance(storedSchema, "{\"a\":\"x\"}");

        act.Should().Throw<InvalidOperationException>().WithMessage("*only sanctioned writer*");
    }

    [Fact]
    public void An_instance_larger_than_the_declared_entry_cap_is_refused()
    {
        // The port's own bound. The HTTP body limit caps a request; it does not cap
        // the seeder, nor the bulk importer Phase 04 brings, and both reach here.
        // Measured at the cap: a hundred properties each applying one shared shape
        // against a 1 MiB instance costs 742 ms and 1.6 GB of allocation in one
        // call, so the cap is what the cost is bounded by.
        var oversized = "{\"a\":\"" + new string('x', 1024 * 1024) + "\"}";

        Refusal(_validator.ValidateInstance(Schema("\"a\":{\"type\":\"string\"}"), oversized))
            .Should().ContainKey("").WhoseValue.Should()
            .ContainSingle(m => m.Key == "lockey_instance_too_large");
    }

    [Fact]
    public void A_stored_schema_larger_than_the_row_cap_is_refused()
    {
        var padding = new string('x', 300 * 1024);
        var oversized = Schema($"\"a\":{{\"type\":\"string\",\"description\":\"{padding}\"}}");

        Refusal(_validator.ValidateInstance(oversized, "{\"a\":\"x\"}"))
            .Should().ContainKey("").WhoseValue.Should()
            .ContainSingle(m => m.Key == "lockey_schema_too_large");
    }

    // ── Isolation ───────────────────────────────────────────────────────────

    [Fact]
    public void Each_build_gets_its_own_registry_so_one_identifier_cannot_lock_another_out()
    {
        // The library's default registry is process-global: the first writer of an
        // $id wins, and every later build of that $id raises "Overwriting
        // registered schemas is not permitted" for the life of the process — one
        // tenant locking another out of saving.
        //
        // Gate 2 refuses $id, so this cannot be reached through AdmitSchema, and a
        // test that went through it would pass with the argument deleted. It is
        // observable through ValidateInstance, which builds whatever schema text it
        // is handed: with a fresh registry per build the same $id builds twice;
        // with the process-global default the second raises.
        var withIdentifier = "{\"$schema\":\"" + Dialect + "\",\"$id\":\"https://tenant-a.example/s\","
            + "\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"string\"}}}";

        _validator.ValidateInstance(withIdentifier, "{\"a\":\"x\"}").IsSuccess.Should().BeTrue();
        _validator.ValidateInstance(withIdentifier, "{\"a\":\"y\"}").IsSuccess.Should().BeTrue();

        // A second instance of the adapter shares the process, so it shares the
        // default registry too — which is the half that would actually bite.
        new JsonSchemaNetValidator()
            .ValidateInstance(withIdentifier, "{\"a\":\"z\"}").IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Each_build_pins_the_dialect_so_a_document_without_the_line_still_evaluates()
    {
        // The library's default dialect is https://json-schema.org/v1/2026, whose
        // AllowUnknownKeywords is false — an x- extension raises there. Gate 2
        // requires the $schema line, so this too is unobservable through
        // AdmitSchema and a test that went through it would pass with the pin
        // deleted. ValidateInstance builds what it is handed, so the pin is what
        // stands between it and a JsonSchemaException on a stored schema.
        var noDialectLine = "{\"type\":\"object\",\"properties\":"
            + "{\"level\":{\"type\":\"string\",\"x-taxonomy\":\"cefr\"}}}";

        var act = () => _validator.ValidateInstance(noDialectLine, "{\"level\":\"b2\"}");

        act.Should().NotThrow(
            "the adapter pins draft 2020-12 per call rather than taking the library's default");
        _validator.ValidateInstance(noDialectLine, "{\"level\":\"b2\"}").IsSuccess.Should().BeTrue();
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

    /// <summary>The extensions an admitted document declares.</summary>
    private IReadOnlyList<SchemaExtensionReference> Extensions(string document)
    {
        var result = _validator.AdmitSchema(document);

        result.IsSuccess.Should().BeTrue("the document under test must be admitted");

        return result.Value!;
    }

    /// <remarks>
    /// Generic over the payload because the two members answer with different
    /// ones — an admission carries the extensions LearnStack has to resolve — while
    /// a refusal is the same shape from both.
    /// </remarks>
    private static IReadOnlyDictionary<string, IReadOnlyList<LocalizedMessage>> Refusal<T>(
        Result<T> result)
    {
        result.IsSuccess.Should().BeFalse("the document under test must be refused");
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("validation_failed",
            "a tenant's authoring mistake is a 400, and the status map is a closed table");
        result.Error.Details.Should().NotBeNull("a refusal names where it refused");
        return result.Error.Details!;
    }
}
