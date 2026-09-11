using System.Text;
using System.Text.Json;
using FluentAssertions;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Secrets;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Audit;

/// <summary>
/// What the audit write path puts in the three <c>jsonb</c> columns.
/// </summary>
public sealed class AuditJsonTests
{
    [Fact]
    public void A_null_value_renders_as_the_JSON_null_and_not_an_absent_key()
    {
        // The distinction the column contract turns on: a CLR null is the JSON `null`,
        // which is a value, rather than a missing property — so a reader can tell "was
        // cleared" from "was not part of this change".
        AuditJson.Render(null).Should().Be("null");
    }

    [Theory]
    // Every slot holds JSON TEXT, not a rendered value, which is why 42 and "42" have to
    // stay distinguishable. A slot that held a rendered value would make `changes`
    // unparseable by the two readers that consume it.
    [InlineData(42, "42")]
    [InlineData("42", "\"42\"")]
    [InlineData(true, "true")]
    [InlineData("", "\"\"")]
    public void A_scalar_renders_as_its_own_JSON_type(object value, string expected)
    {
        AuditJson.Render(value).Should().Be(expected);
    }

    [Fact]
    public void A_jsonb_column_passes_through_rather_than_being_escaped_again()
    {
        // Re-encoding a document would put the whole thing in the diff as one long escaped
        // string, which is legal JSON and useless to read.
        const string schema = """{"type":"object","properties":{"a":{"type":"string"}}}""";

        AuditJson.Render(schema, storedAsJson: true).Should().Be(schema);
    }

    [Theory]
    // The passthrough is decided by the COLUMN, never by the value. Deciding from the value
    // was wrong twice: it retyped ordinary text whose content parsed — a display name of
    // `[1,2,3]` became a JSON array, contradicting this file's own rule that 42 and "42"
    // are different values — and it emitted text jsonb refuses, failing the INSERT inside
    // the business transaction.
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("123")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"a":1}""")]
    [InlineData("not json at all")]
    public void Text_from_a_non_json_column_is_quoted_however_it_reads(string value)
    {
        AuditJson.Render(value).Should().Be(JsonSerializer.Serialize(value));
    }

    [Fact]
    public void The_escapes_PostgreSQL_refuses_never_reach_a_jsonb_parameter_from_a_text_column()
    {
        // Measured on PostgreSQL 18.6: `select '{"a": "\u0000"}'::jsonb` raises 22P05, and
        // `select '{"a": 1e400000}'::jsonb` raises 22003 — both of which System.Text.Json
        // parses without complaint. Under the old value-based passthrough a display name
        // containing either was emitted verbatim into a jsonb parameter and failed the
        // audit INSERT, on the path whose whole job is that the record survives.
        const string hostile = """{"a":"\u0000"}""";

        AuditJson.Render(hostile).Should().Be(JsonSerializer.Serialize(hostile));
        AuditJson.Render(hostile).Should().NotBe(hostile);
    }

    [Theory]
    // The three closed-set columns beside these store the member NAME. A snapshot storing
    // the ordinal would make the two halves of one record disagree — and change meaning
    // silently the day a member is inserted into the middle of the enum.
    [InlineData(AuditOutcome.Success, "\"Success\"")]
    [InlineData(AuditOutcome.Indeterminate, "\"Indeterminate\"")]
    [InlineData(OperationClass.Must, "\"Must\"")]
    public void An_enum_renders_as_its_name(object member, string expected)
    {
        AuditJson.Render(member).Should().Be(expected);
    }

    [Fact]
    public void A_value_under_the_cap_is_returned_unchanged()
    {
        var json = Document(AuditJson.MaxBytes - 64);

        AuditJson.CapObject(json).Should().BeSameAs(json);
        AuditJson.CapArray(json).Should().BeSameAs(json);
    }

    [Fact]
    public void A_value_over_the_cap_becomes_an_elision_record_that_keeps_the_column_type()
    {
        // The two binding cases ADR-0044 § 8 names are the ones either side of the cap:
        // a payload just under it and one just over it must deserialise through the same
        // contract. `before_state` and `after_state` are objects on both sides; `changes`
        // is an array on both sides, so its elision is wrapped in a one-element array
        // rather than left as a bare object.
        var json = Document(AuditJson.MaxBytes + 64);

        using var elidedObject = JsonDocument.Parse(AuditJson.CapObject(json));
        elidedObject.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
        elidedObject.RootElement.GetProperty("_elided").GetBoolean().Should().BeTrue();
        elidedObject.RootElement.GetProperty("bytes").GetInt32()
            .Should().Be(Encoding.UTF8.GetByteCount(json));
        elidedObject.RootElement.GetProperty("sha256").GetString()
            .Should().MatchRegex("^[0-9a-f]{64}$");

        using var elidedArray = JsonDocument.Parse(AuditJson.CapArray(json));
        elidedArray.RootElement.ValueKind.Should().Be(JsonValueKind.Array,
            "changes is an array on both sides of the cap, or a reader has to branch on "
            + "the shape before it can tell what it is looking at");
        elidedArray.RootElement.GetArrayLength().Should().Be(1);
        elidedArray.RootElement[0].GetProperty("_elided").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Two_elisions_of_the_same_payload_carry_the_same_digest()
    {
        // What makes the record useful rather than merely honest: a reader can tell a
        // repeated value from a changed one without the value.
        var json = Document(AuditJson.MaxBytes + 1024);

        AuditJson.CapObject(json).Should().Be(AuditJson.CapObject(json));
        AuditJson.CapObject(json).Should().NotBe(AuditJson.CapObject(Document(AuditJson.MaxBytes + 2048)));
    }

    [Fact]
    public void The_cap_is_measured_in_bytes_of_UTF8_and_not_in_characters()
    {
        // A jsonb column's cost is bytes, and a stored document reaches the cap
        // unescaped — Render passes it through rather than re-encoding it, which is the
        // whole point of that branch. The emoji below is four bytes and one char in .NET
        // terms two chars, so a cap counting characters would let roughly twice the
        // payload through. Measured against Encoding.UTF8 rather than assumed.
        var padding = string.Concat(Enumerable.Repeat("😀", (AuditJson.MaxBytes / 4) + 8));
        var json = $$"""{"v":"{{padding}}"}""";

        json.Length.Should().BeLessThan(AuditJson.MaxBytes,
            "in chars the document fits");
        Encoding.UTF8.GetByteCount(json).Should().BeGreaterThan(AuditJson.MaxBytes,
            "in bytes it does not, which is the measure that matters");

        AuditJson.Render(json, storedAsJson: true).Should().BeSameAs(json,
            "a jsonb column passes through");
        AuditJson.CapObject(json).Should().Contain("_elided");
    }

    [Fact]
    public void A_letter_outside_ASCII_is_written_as_itself_and_counts_as_itself()
    {
        // The default encoder wrote every one of these as a six-byte \uXXXX escape, and the
        // cap is measured on the written text — so a value in Turkish reached it three
        // times sooner than its bytes did and was elided (the fifth review of Packet 9).
        AuditJson.Render("Şule Çağrı 日本").Should().Be("\"Şule Çağrı 日本\"");

        // 100 000 × 'ş' is 200 000 bytes of UTF-8 — under the 256 KiB cap — and 600 000
        // escaped, which is over it.
        var name = AuditJson.Render(new string('ş', 100_000));
        AuditJson.CapObject($$"""{"v":{{name}}}""").Should().NotContain("_elided");

        // What the default escaped beyond that, it still does.
        AuditJson.Render("<b>&").Should().Be("\"\\u003Cb\\u003E\\u0026\"");
    }

    [Fact]
    public void The_redaction_sentinel_enters_quoted()
    {
        // It is a JSON string in a JSON slot, so it arrives as "***REDACTED***" and not
        // as a bare token the parse would reject.
        AuditJson.Quote(SensitiveTokenCatalog.RedactedValue)
            .Should().Be("\"***REDACTED***\"");
    }

    [Theory]
    // A NUL cannot live in a PostgreSQL text column at all, and the jsonb input function
    // refuses the escape that spells one even though System.Text.Json emits it happily —
    // measured: 22P05 unsupported Unicode escape sequence. So a value carrying one is a
    // value NO column holds; the business write carrying it fails too. What must not
    // happen is the AUDIT write failing with it, because the audit write is what records
    // that the business write failed.
    [InlineData("Foo\0Bar")]
    [InlineData("\0")]
    [InlineData("trailing\0")]
    public void A_value_PostgreSQL_cannot_hold_becomes_an_explicit_marker(string value)
    {
        var rendered = AuditJson.Render(value);

        using var document = JsonDocument.Parse(rendered);

        document.RootElement.GetProperty("_unstorable").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("chars").GetInt32().Should().Be(value.Length);
        rendered.Should().NotContain(
            "\\u0000",
            "the marker replaces the value rather than escaping it, because the escape is "
            + "exactly what jsonb refuses");
    }

    [Fact]
    public void An_unpaired_surrogate_is_treated_the_same_way()
    {
        // The other half of what a text column cannot represent. Reachable from any string
        // a caller built by slicing one.
        AuditJson.Render("ok\ud800").Should().Contain("_unstorable");
        AuditJson.Render("\udc00ok").Should().Contain("_unstorable");

        // A well-formed pair is ordinary text and stays it.
        AuditJson.Render("ok\U0001F600").Should().Be(JsonSerializer.Serialize("ok\U0001F600"));
    }

    [Fact]
    public void A_jsonb_document_carrying_one_is_marked_rather_than_passed_through()
    {
        // The passthrough branch answers the same question by PARSING, which is what tells
        // an escape from the six characters that spell one.
        var hostile = "{\"a\":\"\0\"}";
        var spelled = "{\"a\":\"\\\\u0000\"}";

        AuditJson.Render(hostile, storedAsJson: true).Should().Contain("_unstorable");
        AuditJson.Render(spelled, storedAsJson: true).Should().Be(
            spelled,
            "a backslash followed by u0000 is six ordinary characters and stores");
    }

    private static string Document(int approximateBytes)
    {
        var padding = new string('x', Math.Max(0, approximateBytes - 10));
        return $$"""{"v":"{{padding}}"}""";
    }
}
