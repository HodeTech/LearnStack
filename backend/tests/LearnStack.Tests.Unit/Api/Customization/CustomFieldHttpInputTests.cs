using System.Text;
using System.Text.Json;
using LearnStack.Api.Customization;
using LearnStack.Infrastructure.Validation;
using LearnStack.Modules.Customization.Application.Contracts.CustomFields;
using LearnStack.Modules.Customization.Application.CustomFields;
using LearnStack.SharedKernel.DataProtection;
using Microsoft.AspNetCore.Http;
using Xunit;
namespace LearnStack.Tests.Unit.Api.Customization;

public sealed class CustomFieldHttpInputTests
{
    private const string Schema = "{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"number\",\"minimum\":1e-200,\"maximum\":1e-100}},\"required\":[\"value\"],\"additionalProperties\":false}";
    private static string Input => "{\"targetEntity\":\"Membership\",\"key\":\"contact-preference\",\"schemaVersion\":1,\"schema\":" + Schema + ",\"labels\":{\"en\":\"Answer\"},\"piiCategory\":\"None\",\"isRequired\":false,\"sort\":0,\"purposeKey\":null,\"notices\":null,\"formLabels\":null}";
    [Fact]
    public async Task Definition_wire_retains_numeric_lexemes_and_requires_explicit_classification()
    {
        using var parsed = await Read(Input);
        Assert.NotNull(parsed); Assert.True(CustomFieldHttpInput.TryDefinition(parsed.RootElement, out var input));
        Assert.Contains("1e-200", input!.JsonSchema, StringComparison.Ordinal);
        using var missing = await Read(Input.Replace(",\"piiCategory\":\"None\"", "", StringComparison.Ordinal));
        Assert.Null(missing);
        using var wrong = await Read(Input.Replace("\"None\"", "0", StringComparison.Ordinal));
        Assert.NotNull(wrong); Assert.False(CustomFieldHttpInput.TryDefinition(wrong.RootElement, out _));
    }
    [Theory]
    [InlineData("\"key\":\"different\",")]
    [InlineData("\"k\\u0065y\":\"different\",")]
    [InlineData("\"unexpected\":false,")]
    [InlineData("\"invalid\\uD800\":false,")]
    public async Task Unknown_duplicate_and_escaped_duplicate_names_never_become_input(string prefix)
    { using var parsed = await Read("{" + prefix + Input[1..]); Assert.Null(parsed); }
    [Theory]
    [InlineData("\\uD800")]
    [InlineData("\\u0000")]
    public async Task Invalid_decoded_text_is_a_closed_refusal(string text)
    {
        using var parsed = await Read(Input.Replace("Answer", text, StringComparison.Ordinal));
        Assert.Null(parsed);
    }
    [Fact]
    public async Task Parser_bounds_actual_bytes_and_refuses_malformed_utf8_before_interpretation()
    {
        using var oversized = await Read(Input + new string(' ', 4096)); Assert.Null(oversized);
        var http = Context([0x7b, 0x22, 0xc0, 0xaf, 0x22, 0x3a, 0x31, 0x7d]);
        using var invalid = await CustomFieldHttpInput.ReadAsync(http, false, CustomFieldHttpInput.DefinitionFields); Assert.Null(invalid);
    }
    [Fact]
    public void Stored_jsonb_exponent_expansion_is_normalized_before_wire_editor_budget()
    {
        var physical = Schema.Replace("1e-200", "0." + new string('0', 199) + "1", StringComparison.Ordinal);
        var d = new CustomFieldDefinitionDto(Guid.CreateVersion7(), "Membership", "answer", 1, "Active", 0, false, physical,
            new Dictionary<string, string> { { "en", "Answer" } }, PersonalDataCategory.None, false, 0, null, null, 32);
        var normalized = new CustomFieldFormProfile(new JsonSchemaNetValidator()).NormalizeStored(d.JsonSchema);
        Assert.True(normalized.IsSuccess);
        var projected = CustomFieldResponseProjection.Definition(d with { JsonSchema = normalized.Value });
        Assert.True(Encoding.UTF8.GetByteCount(projected.Schema.GetRawText()) <= 2048);
        Assert.Contains("E-200", projected.Schema.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0d, projected.Schema.GetProperty("properties").GetProperty("value").GetProperty("minimum").GetDouble() - 1e-200);
    }
    private static async Task<JsonDocument?> Read(string input) => await CustomFieldHttpInput.ReadAsync(Context(Encoding.UTF8.GetBytes(input)), false, CustomFieldHttpInput.DefinitionFields);
    private static DefaultHttpContext Context(byte[] bytes)
    { var http = new DefaultHttpContext(); http.Request.ContentType = "application/json"; http.Request.Body = new MemoryStream(bytes); return http; }
}
