using System.Text.Json;
using FluentAssertions;
using LearnStack.Infrastructure.Validation;
using LearnStack.Modules.Customization.Application.Contracts.CustomFields;
using LearnStack.Modules.Customization.Application.CustomFields;
using LearnStack.SharedKernel.DataProtection;
using Xunit;

namespace LearnStack.Tests.Unit.Customization;

public sealed class CustomFieldFormProfileTests
{
    private readonly CustomFieldFormProfile _profile = new(new JsonSchemaNetValidator());
    private static string Envelope(string field) => """
        {"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","properties":{"value":
        """ + field + "},\"required\":[\"value\"],\"additionalProperties\":false}";
    private const string Text = """{"type":"string","minLength":1,"maxLength":20}""";
    private static CustomFieldDefinitionDto Definition(string schema) => new(Guid.CreateVersion7(), "Membership", "field", 1,
        "Active", 0, false, schema, new Dictionary<string, string> { ["en"] = "Label" }, PersonalDataCategory.None, false, 0, null, null, 242);

    [Theory]
    [InlineData("{\"type\":\"boolean\"}")]
    [InlineData("{\"type\":\"integer\",\"minimum\":-9007199254740991,\"maximum\":9007199254740991}")]
    [InlineData("{\"type\":\"number\",\"minimum\":1e-200,\"maximum\":1e-100}")]
    [InlineData("{\"type\":\"string\",\"minLength\":1,\"maxLength\":3,\"enum\":[\"a\",\"界\",\"😀\"]}")]
    [InlineData("{\"type\":\"array\",\"items\":{\"type\":\"boolean\"},\"minItems\":1,\"maxItems\":10}")]
    public void Admitted_fields_have_a_real_witness_validated_by_the_existing_port(string field)
    {
        var admitted = _profile.Admit(Envelope(field));
        admitted.IsSuccess.Should().BeTrue();
        new JsonSchemaNetValidator().ValidateInstance(admitted.Value!.Schema, admitted.Value!.Witness).IsSuccess.Should().BeTrue();
        _profile.ValidateAndNormalize(Definition(admitted.Value!.Schema), admitted.Value!.Witness).IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData("format", "\"date\"")]
    [InlineData("default", "\"safe\"")]
    [InlineData("const", "\"safe\"")]
    [InlineData("description", "\"tenant canary\"")]
    [InlineData("pattern", "\".*\"")]
    [InlineData("x-unknown", "true")]
    public void Unsupported_keywords_refuse_even_if_the_general_dialect_ignores_them(string keyword, string value)
    {
        var field = Text[..^1] + "," + JsonSerializer.Serialize(keyword) + ":" + value + "}";
        _profile.Admit(Envelope(field)).IsFailure.Should().BeTrue();
    }

    [Theory]
    [InlineData("{\"type\":\"string\",\"minLength\":4,\"maxLength\":3}")]
    [InlineData("{\"type\":\"string\",\"minLength\":1,\"maxLength\":4096}")]
    [InlineData("{\"type\":\"string\",\"minLength\":1,\"maxLength\":3,\"enum\":[\"\"]}")]
    [InlineData("{\"type\":\"string\",\"minLength\":1,\"maxLength\":3,\"enum\":[\"a\",\"a\"]}")]
    [InlineData("{\"type\":\"number\",\"minimum\":2,\"maximum\":1}")]
    [InlineData("{\"type\":\"number\",\"minimum\":1e-999,\"maximum\":1}")]
    [InlineData("{\"type\":\"integer\",\"minimum\":9007199254740992,\"maximum\":9007199254740994}")]
    [InlineData("{\"type\":\"array\",\"items\":{\"type\":\"boolean\"},\"minItems\":2,\"maxItems\":1}")]
    public void Contradictory_or_unrepresentable_nodes_refuse_constructive_admission(string field) =>
        _profile.Admit(Envelope(field)).IsFailure.Should().BeTrue();

    [Fact]
    public void Container_depth_counts_value_nodes_and_optional_children_are_proved_nonempty()
    {
        string Object(string child) => "{\"type\":\"object\",\"properties\":{\"child\":" + child + "},\"required\":[],\"additionalProperties\":false}";
        string Array(string child) => "{\"type\":\"array\",\"items\":" + child + ",\"minItems\":0,\"maxItems\":1}";
        _profile.Admit(Envelope(Object(Array(Text)))).IsSuccess.Should().BeTrue();
        _profile.Admit(Envelope(Object(Array(Object(Text))))).IsFailure.Should().BeTrue();
        _profile.Admit(Envelope(Object("""{"type":"string","minLength":4,"maxLength":3}"""))).IsFailure.Should().BeTrue();
        _profile.Admit(Envelope(Object(Text).Replace("child", "constructor", StringComparison.Ordinal))).IsFailure.Should().BeTrue();
    }

    [Theory]
    [InlineData("9007199254740991", true)]
    [InlineData("-9007199254740991", true)]
    [InlineData("9007199254740992", false)]
    [InlineData("9007199254740993", false)]
    [InlineData("-9007199254740992", false)]
    [InlineData("-9007199254740993", false)]
    [InlineData("9007199254740991e0", true)]
    [InlineData("0.1", true)]
    [InlineData("1e-300", true)]
    [InlineData("1e-999", false)]
    [InlineData("1e999", false)]
    [InlineData("null", false)]
    public void Nested_numbers_have_the_same_finite_binary64_guard_as_direct_values(string number, bool accepted)
    {
        var field = """{"type":"object","properties":{"amount":{"type":"number","minimum":-9007199254740991,"maximum":9007199254740991}},"required":["amount"],"additionalProperties":false}""";
        var schema = _profile.Admit(Envelope(field));
        schema.IsSuccess.Should().BeTrue();
        var dto = Definition(schema.Value!.Schema);
        var result = _profile.ValidateAndNormalize(dto, "{\"value\":{\"amount\":" + number + "}}");
        result.IsSuccess.Should().Be(accepted);
        if (accepted) _profile.ValidateAndNormalize(dto, result.Value!).Value!.Should().Be(result.Value!);
    }

    [Fact]
    public void Exact_envelope_duplicates_and_null_refuse_without_echoing_authored_material()
    {
        var schema = _profile.Admit(Envelope(Text)).Value!.Schema;
        foreach (var value in new[] { "\"a\"", "{\"value\":null}", "{\"value\":\"a\",\"extra\":\"canary\"}", "{\"value\":\"a\",\"value\":\"canary\"}" })
        {
            var result = _profile.ValidateAndNormalize(Definition(schema), value);
            result.IsFailure.Should().BeTrue();
            JsonSerializer.Serialize(result.Error).Should().NotContain("canary");
        }
        var escaped = "\"\\\"\\\\界😀";
        _profile.ValidateAndNormalize(Definition(schema), JsonSerializer.Serialize(new { value = escaped })).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Structural_labels_keep_their_nodes_after_jsonb_property_reordering_and_enum_choices_keep_array_order()
    {
        var authored = Envelope("""{"type":"object","properties":{"z-last":{"type":"boolean"},"a-first":{"type":"string","minLength":1,"maxLength":3,"enum":["one","two"]}},"required":[],"additionalProperties":false}""");
        var reordered = Envelope("""{"additionalProperties":false,"required":[],"properties":{"a-first":{"enum":["one","two"],"maxLength":3,"minLength":1,"type":"string"},"z-last":{"type":"boolean"}},"type":"object"}""");
        var first = _profile.Admit(authored); var second = _profile.Admit(reordered);
        first.IsSuccess.Should().BeTrue(); second.IsSuccess.Should().BeTrue();
        CustomFieldFormProfile.DescribeControls(first.Value!.Schema).Should().Equal(CustomFieldFormProfile.DescribeControls(second.Value!.Schema));
        var root = new Dictionary<string, string> { ["en"] = "Root" };
        var select = new Dictionary<string, string> { ["en"] = "Select" };
        var flag = new Dictionary<string, string> { ["en"] = "Flag" };
        CustomFieldControlLabels[] labels = [new("/0", select, [new Dictionary<string, string> { ["en"] = "First" }, new Dictionary<string, string> { ["en"] = "Second" }]), new("/1", flag)];
        CustomFieldFormProfile.ValidControlLabels(first.Value!.Schema, root, labels, "en", ["en"]).Should().BeTrue();
        CustomFieldFormProfile.ValidControlLabels(second.Value!.Schema, root, labels, "en", ["en"]).Should().BeTrue();
        CustomFieldFormProfile.ValidControlLabels(second.Value!.Schema, root, [labels[1], labels[1]], "en", ["en"]).Should().BeFalse();
        CustomFieldFormProfile.ValidControlLabels(second.Value!.Schema, root, [labels[0] with { Location = "/2" }, labels[1]], "en", ["en"]).Should().BeFalse();
        CustomFieldFormProfile.ValidControlLabels(second.Value!.Schema, root, [labels[0] with { Choices = [] }, labels[1]], "en", ["en"]).Should().BeFalse();
        CustomFieldFormProfile.ValidControlLabels(second.Value!.Schema, root, null, "en", ["en"]).Should().BeFalse();
        var restored = JsonSerializer.Deserialize<List<CustomFieldControlLabels>>(CustomFieldFormProfile.SerializeControlLabels(labels)!);
        CustomFieldFormProfile.ValidControlLabels(second.Value!.Schema, root, restored, "en", ["en"]).Should().BeTrue();
        CustomFieldFormProfile.ValidControlLabels(second.Value!.Schema, root, restored, "tr", ["en", "tr"]).Should().BeFalse();
    }

    [Fact]
    public void Root_select_uses_the_one_root_label_and_explicit_localized_choice_labels()
    {
        var admitted = _profile.Admit(Envelope("""{"type":"string","minLength":1,"maxLength":3,"enum":["one","two"]}"""));
        var root = new Dictionary<string, string> { ["en"] = "Select" };
        CustomFieldControlLabels[] labels = [new("/", null, [new Dictionary<string, string> { ["en"] = "First" }, new Dictionary<string, string> { ["en"] = "Second" }])];
        CustomFieldFormProfile.ValidControlLabels(admitted.Value!.Schema, root, labels, "en", ["en"]).Should().BeTrue();
        CustomFieldFormProfile.ValidControlLabels(admitted.Value!.Schema, root, [labels[0] with { Labels = root }], "en", ["en"]).Should().BeFalse();
        CustomFieldFormProfile.ValidControlLabels(admitted.Value!.Schema, root, [labels[0] with { Choices = [new Dictionary<string, string> { ["tr"] = "First" }, new Dictionary<string, string> { ["en"] = "Second" }] }], "en", ["en", "tr"]).Should().BeFalse();
    }

    [Fact]
    public void Canonical_whole_submission_bound_accounts_for_metadata_and_maximum_text_expansion()
    {
        // 256 operation bytes + 7 serialized key bytes + 256 pin bytes + maximum answer.
        CustomFieldFormProfile.FitsSubmission([("field", 3577)]).Should().BeTrue();
        CustomFieldFormProfile.FitsSubmission([("field", 3578)]).Should().BeFalse();
        var large = _profile.Admit(Envelope("""{"type":"string","minLength":1,"maxLength":256}""")).Value!;
        CustomFieldFormProfile.FitsSubmission([("field", large.MaximumValueBytes)]).Should().BeTrue();
        CustomFieldFormProfile.FitsSubmission(Enumerable.Range(0, 20).Select(index => ($"field-{index}", large.MaximumValueBytes))).Should().BeFalse();
    }
}
