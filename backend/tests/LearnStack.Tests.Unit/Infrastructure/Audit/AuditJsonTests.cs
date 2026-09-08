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
    public void A_stored_document_passes_through_rather_than_being_escaped_again()
    {
        // A jsonb column mapped to string. Re-encoding it would put the whole document in
        // the diff as one long escaped string, which is legal JSON and useless to read.
        const string schema = """{"type":"object","properties":{"a":{"type":"string"}}}""";

        AuditJson.Render(schema).Should().Be(schema);
    }

    [Theory]
    // A bare JSON scalar is indistinguishable from ordinary text somebody typed, so it is
    // quoted rather than passed through. Guessing from the first character would retype a
    // string column's value: a tenant whose display name is literally `null` must not
    // arrive in the diff as the JSON null.
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("123")]
    [InlineData("not json at all")]
    [InlineData("{ not: valid }")]
    public void Text_that_is_not_a_stored_document_is_quoted(string value)
    {
        AuditJson.Render(value).Should().Be(JsonSerializer.Serialize(value));
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
        // whole point of that branch and also the only path on which a character is more
        // than one byte. The emoji below is four bytes and one char in .NET terms two
        // chars, so a cap counting characters would let roughly twice the payload
        // through. Measured against Encoding.UTF8 rather than assumed.
        var padding = string.Concat(Enumerable.Repeat("😀", (AuditJson.MaxBytes / 4) + 8));
        var json = $$"""{"v":"{{padding}}"}""";

        json.Length.Should().BeLessThan(AuditJson.MaxBytes,
            "in chars the document fits");
        Encoding.UTF8.GetByteCount(json).Should().BeGreaterThan(AuditJson.MaxBytes,
            "in bytes it does not, which is the measure that matters");

        AuditJson.Render(json).Should().BeSameAs(json, "a stored document passes through");
        AuditJson.CapObject(json).Should().Contain("_elided");
    }

    [Fact]
    public void The_redaction_sentinel_enters_quoted()
    {
        // It is a JSON string in a JSON slot, so it arrives as "***REDACTED***" and not
        // as a bare token the parse would reject.
        AuditJson.Quote(SensitiveTokenCatalog.RedactedValue)
            .Should().Be("\"***REDACTED***\"");
    }

    private static string Document(int approximateBytes)
    {
        var padding = new string('x', Math.Max(0, approximateBytes - 10));
        return $$"""{"v":"{{padding}}"}""";
    }
}
