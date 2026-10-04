using System.Text.Json.Nodes;
using FluentAssertions;
using LearnStack.Infrastructure.Validation;
using LearnStack.Modules.Customization.Application.Customization;
using Xunit;

namespace LearnStack.Tests.Unit.Modules.Customization;

public sealed class TextCardPresentationTests
{
    private readonly JsonSchemaNetValidator _validator = new();
    private const string Schema = """
        {"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object",
         "properties":{"first":{"type":"string","minLength":1},"second":{"type":"string"}},
         "additionalProperties":false,"required":["first"],
         "x-fields":[{"name":"second","label":{"TR-tr":"İkinci","en":"Second"}},
                     {"name":"first","label":{"en":"First"}}]}
        """;

    [Fact]
    public void Descriptors_preserve_array_order_and_canonical_localized_fallback()
    {
        _validator.AdmitSchema(Schema).IsSuccess.Should().BeTrue();
        var result = TextCardPresentation.Resolve(Schema, "default-card");
        result.IsSuccess.Should().BeTrue();
        result.Value.Select(field => field.Name).Should().Equal("second", "first");
        result.Value.Select(field => field.IsRequired).Should().Equal(false, true);
        result.Value[0].Label.Locales.Should().Equal("en", "tr-TR");
        result.Value[1].Label.Resolve("tr-TR").Should().Be("First");
        _validator.ValidateInstance(Schema, """{"first":""}""").IsFailure.Should().BeTrue();
        _validator.ValidateInstance(Schema, """{"first":"https://example.com/<script>"}""").IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData("{")]
    [InlineData("")]
    [InlineData("{\"properties\":")]
    public void Invalid_json_syntax_returns_a_located_validation_failure(string schema)
    {
        var result = TextCardPresentation.Resolve(schema, "default-card");
        result.Error!.Code.Should().Be("validation_failed");
        result.Error.Details.Should().ContainSingle().Which.Key.Should().Be("");
        result.Error.Details![""].Should().ContainSingle().Which.Key.Should().Be("lockey_schema_not_well_formed_json");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("42")]
    [InlineData("\"schema\"")]
    [InlineData("[]")]
    [InlineData("{\"x-fields\":[]}")]
    [InlineData("{\"x-fields\":[],\"properties\":null}")]
    [InlineData("{\"x-fields\":[],\"properties\":[]}")]
    [InlineData("{\"x-fields\":[],\"properties\":true}")]
    [InlineData("{\"x-fields\":[],\"properties\":42}")]
    [InlineData("{\"x-fields\":[],\"properties\":\"fields\"}")]
    public void Malformed_root_or_properties_refuses_the_location_instead_of_throwing(string schema)
    {
        var result = TextCardPresentation.Resolve(schema, "default-card");
        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("validation_failed");
        result.Error.Details.Should().ContainSingle().Which.Key.Should().Be("/properties");
    }

    [Theory]
    [InlineData("[]", "/x-fields")]
    [InlineData("{}", "/x-fields")]
    [InlineData("[42]", "/x-fields/0")]
    [InlineData("[{\"name\":\"first\",\"label\":{\"en\":\"A\"}}]", "/properties/second")]
    [InlineData("[{\"name\":\"third\",\"label\":{\"en\":\"A\"}}]", "/x-fields/0/name")]
    [InlineData("[{\"name\":\"first\",\"label\":{\"en\":\"A\"}},{\"name\":\"first\",\"label\":{\"en\":\"B\"}}]", "/x-fields/1/name")]
    [InlineData("[{\"name\":\"first\"}]", "/x-fields/0/label")]
    [InlineData("[{\"label\":{\"en\":\"A\"}}]", "/x-fields/0/name")]
    [InlineData("[{\"name\":1,\"label\":{\"en\":\"A\"}}]", "/x-fields/0/name")]
    [InlineData("[{\"name\":\"first\",\"label\":{\"en\":\"A\"},\"layout\":\"html\"}]", "/x-fields/0/layout")]
    [InlineData("[{\"name\":\"first\",\"name\":\"second\",\"label\":{\"en\":\"A\"}}]", "/x-fields/0/name")]
    [InlineData("[{\"name\":\"first\",\"label\":{}}]", "/x-fields/0/label")]
    [InlineData("[{\"name\":\"first\",\"label\":{\"en\":\"\"}}]", "/x-fields/0/label")]
    [InlineData("[{\"name\":\"first\",\"label\":{\"en\":1}}]", "/x-fields/0/label")]
    [InlineData("[{\"name\":\"first\",\"label\":{\"en_US\":\"A\"}}]", "/x-fields/0/label")]
    [InlineData("[{\"name\":\"first\",\"label\":{\"en\":\"A\",\"EN\":\"B\"}}]", "/x-fields/0/label")]
    public void Invalid_descriptors_and_labels_fail_with_bounded_pointers(string fields, string location)
    {
        var schema = JsonNode.Parse(Schema)!;
        schema["x-fields"] = JsonNode.Parse(fields);
        var result = TextCardPresentation.Resolve(schema.ToJsonString(), "default-card");
        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("validation_failed");
        result.Error.Details.Should().ContainKey(location);
        result.Error.Details!.Count.Should().BeLessThanOrEqualTo(25);
    }

    [Theory]
    [InlineData("{\"type\":\"object\"}", "/type")]
    [InlineData("{\"type\":[\"string\",\"null\"]}", "/type")]
    [InlineData("{\"type\":\"string\",\"format\":\"uri\"}", "/format")]
    [InlineData("{\"type\":\"string\",\"enum\":[\"x\"]}", "/enum")]
    [InlineData("{\"type\":\"string\",\"const\":\"x\"}", "/const")]
    [InlineData("{\"type\":\"string\",\"$ref\":\"#/$defs/text\"}", "/$ref")]
    [InlineData("{\"type\":\"string\",\"allOf\":[{\"type\":\"string\"}]}", "/allOf")]
    [InlineData("{\"type\":\"string\",\"x-renderer\":\"text\"}", "/x-renderer")]
    [InlineData("{\"type\":\"string\",\"x-custom\":\"markup\"}", "/x-custom")]
    public void Opted_in_fields_refuse_unsupported_shapes_and_annotations(string field, string suffix)
    {
        var schema = JsonNode.Parse(Schema)!;
        schema["properties"]!["first"] = JsonNode.Parse(field);
        var result = TextCardPresentation.Resolve(schema.ToJsonString(), "default-card");
        result.IsFailure.Should().BeTrue();
        result.Error!.Details.Should().ContainKey("/properties/first" + suffix);
    }

    [Fact]
    public void Opted_in_root_and_composite_are_closed_but_legacy_schemas_are_unchanged()
    {
        var schema = JsonNode.Parse(Schema)!;
        schema["additionalProperties"] = true;
        TextCardPresentation.Resolve(schema.ToJsonString(), "default-card").IsFailure.Should().BeTrue();
        TextCardPresentation.Resolve(Schema, "guided-sequence").IsFailure.Should().BeTrue();
        schema["additionalProperties"] = false;
        schema["anyOf"] = JsonNode.Parse("[{\"type\":\"object\"}]");
        TextCardPresentation.Resolve(schema.ToJsonString(), "default-card").Error!.Details.Should().ContainKey("/anyOf");
        schema.AsObject().Remove("x-fields");
        _validator.AdmitSchema(schema.ToJsonString()).IsSuccess.Should().BeTrue();
        TextCardPresentation.Resolve(schema.ToJsonString(), "guided-sequence").Value.Should().BeEmpty();
    }

    [Fact]
    public void Metadata_is_not_a_reference_graph_but_real_nested_keywords_remain_checked()
    {
        var schema = JsonNode.Parse(Schema)!;
        schema["x-fields"] = JsonNode.Parse("[{\"name\":\"first\",\"label\":{\"$ref\":\"#/nope\"}}]");
        _validator.AdmitSchema(schema.ToJsonString()).IsSuccess.Should().BeTrue();
        TextCardPresentation.Resolve(schema.ToJsonString(), "default-card").IsFailure.Should().BeTrue();
        schema["properties"]!["first"]!["x-fields"] = JsonNode.Parse("[{\"name\":\"a\",\"label\":{\"en\":\"A\"}}]");
        var admission = _validator.AdmitSchema(schema.ToJsonString());
        admission.Error!.Details.Should().ContainKey("/properties/first/x-fields");
        schema["properties"]!["first"]!.AsObject().Remove("x-fields");
        schema["properties"]!["first"]!["$ref"] = "#/x-fields/0/label";
        _validator.AdmitSchema(schema.ToJsonString()).IsFailure.Should().BeTrue();
    }
}
