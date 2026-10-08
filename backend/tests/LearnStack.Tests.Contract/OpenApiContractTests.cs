using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using LearnStack.Api.Common;
using LearnStack.Modules.Education.Application.Contracts.PublicReads;
using LearnStack.Modules.Tenancy.Application.Contracts.PublicReads;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Pagination;
using LearnStack.SharedKernel.Results;
using Xunit;

namespace LearnStack.Tests.Contract;

public sealed class OpenApiContractTests(DevelopmentWebApplicationFactory factory) : IClassFixture<DevelopmentWebApplicationFactory>
{
    private static readonly string[] Statuses = ["200", "400", "404", "405", "429", "500", "503"];
    private static readonly Dictionary<string, string[]> Operations = new(StringComparer.Ordinal)
    {
        ["/api/v1/public/site"] = ["GetPublicSite", "HeadPublicSite"],
        ["/api/v1/public/courses"] = ["GetPublicCourses", "HeadPublicCourses"],
        ["/api/v1/public/courses/{slug}"] = ["GetPublicCourse", "HeadPublicCourse"],
        ["/api/v1/public/courses/{slug}/lessons/{lessonSlug}"] = ["GetPublicLesson", "HeadPublicLesson"],
    };
    private static readonly Dictionary<string, (Type Type, string[] Fields)> Dtos = new(StringComparer.Ordinal)
    {
        ["PublicSite"] = (typeof(PublicSite), ["displayName", "enabledLocales", "defaultLocale", "theme", "showPlatformAttribution"]),
        ["PublicTheme"] = (typeof(PublicTheme), ["primary", "background", "foreground", "muted"]),
        ["PageInfo"] = (typeof(PageInfo), ["nextCursor", "previousCursor", "hasNext", "hasPrevious"]),
        ["PublicCourseCatalog"] = (typeof(PublicCourseCatalog), ["locale", "items", "pageInfo"]),
        ["PublicCourseSummary"] = (typeof(PublicCourseSummary), ["slug", "title", "summary", "contentAccess", "level"]),
        ["PublicLevel"] = (typeof(PublicLevel), ["state", "label"]),
        ["PublicLabel"] = (typeof(PublicLabel), ["value", "locale"]),
        ["PublicCourseDetail"] = (typeof(PublicCourseDetail), ["locale", "course", "alternates", "lessons"]),
        ["PublicCourseAlternate"] = (typeof(PublicCourseAlternate), ["locale", "slug"]),
        ["PublicCourseOutline"] = (typeof(PublicCourseOutline), ["items", "pageInfo"]),
        ["PublicOutlineItem"] = (typeof(PublicOutlineItem), ["slug", "title", "sort"]),
        ["PublicLessonDetail"] = (typeof(PublicLessonDetail), ["locale", "course", "lesson", "alternates", "content"]),
        ["PublicNamedResource"] = (typeof(PublicNamedResource), ["slug", "title"]),
        ["PublicLessonAlternate"] = (typeof(PublicLessonAlternate), ["locale", "courseSlug", "lessonSlug"]),
        ["PublicTextField"] = (typeof(PublicTextField), ["name", "label", "value"]),
        ["PublicLessonContent"] = (typeof(PublicLessonContent), []),
        ["PublicLessonContentPublicReadyContent"] = (typeof(PublicReadyContent), ["state", "rendererKey", "label", "fields"]),
        ["PublicLessonContentPublicUnavailableContent"] = (typeof(PublicUnavailableContent), ["state"]),
    };

    [Fact]
    public async Task PublicSurface_Contract_Matches_Served_OpenApi()
    {
        var served = await Served();
        var snapshot = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "backend/openapi/v1.json")))!;
        JsonNode.DeepEquals(served, snapshot).Should().BeTrue("only object key order may differ from the served production composition");
        ContractErrors(served).Should().BeEmpty();
    }

    [Fact]
    public async Task Openapi_contract_controls_detect_missing_operations_statuses_parameters_and_array_drift()
    {
        var clean = await Served();
        ContractErrors(clean).Should().BeEmpty();
        var missing = clean.DeepClone();
        missing["paths"]!["/api/v1/public/site"]!.AsObject().Remove("head");
        ContractErrors(missing).Should().Contain("operations");
        var extra = clean.DeepClone();
        extra["paths"]!["/api/v1/public/site"]!["post"] = extra["paths"]!["/api/v1/public/site"]!["get"]!.DeepClone();
        ContractErrors(extra).Should().Contain("operations");
        extra = clean.DeepClone();
        extra["paths"]!["/api/v1/public/extra"] = extra["paths"]!["/api/v1/public/site"]!.DeepClone();
        ContractErrors(extra).Should().Contain("operations");
        missing = clean.DeepClone();
        missing["paths"]!["/api/v1/public/courses"]!["get"]!["responses"]!.AsObject().Remove("503");
        ContractErrors(missing).Should().Contain("responses");
        missing = clean.DeepClone();
        missing["paths"]!["/api/v1/public/courses"]!["get"]!["parameters"]!.AsArray().Clear();
        ContractErrors(missing).Should().Contain("parameters");
        var reordered = new JsonObject(clean.AsObject().Reverse().Select(member => KeyValuePair.Create(member.Key, member.Value?.DeepClone())));
        JsonNode.DeepEquals(clean, reordered).Should().BeTrue();
        var arrayDrift = clean.DeepClone();
        var required = arrayDrift["components"]!["schemas"]!["PublicSite"]!["required"]!.AsArray();
        var first = required[0]!.DeepClone();
        required[0] = required[1]!.DeepClone(); required[1] = first;
        JsonNode.DeepEquals(clean, arrayDrift).Should().BeFalse("schema arrays are not ignored or sorted away");
        typeof(LocalizedMessage).GetProperty(nameof(LocalizedMessage.Params))!.PropertyType
            .Should().Be<IReadOnlyDictionary<string, string>>("the error parameter wire carrier holds string values");
        var parameters = clean["components"]!["schemas"]!["ProblemDetails"]!["properties"]!["errors"]!["additionalProperties"]!["items"]!["properties"]!["params"]!;
        foreach (var invalid in new JsonNode[] { new JsonObject(), new JsonObject { ["type"] = "number" }, new JsonObject { ["type"] = "object" } })
        {
            var malformed = clean.DeepClone();
            malformed["components"]!["schemas"]!["ProblemDetails"]!["properties"]!["errors"]!["additionalProperties"]!["items"]!["properties"]!["params"]!["additionalProperties"] = invalid;
            ContractErrors(malformed).Should().Contain("problem-params");
        }
        parameters["additionalProperties"]!["type"]!.GetValue<string>().Should().Be("string");
        var message = new LocalizedMessage("lockey_required", new Dictionary<string, string> { ["maxLength"] = "35" });
        var actualProblem = ProblemDetailsFactory.For(new Error(new LocalizedMessage("lockey_validation_failed"),
            new Dictionary<string, IReadOnlyList<LocalizedMessage>> { ["locale"] = [message] }));
        var actualWire = JsonSerializer.SerializeToNode(actualProblem, JsonSerializerOptions.Web)!;
        actualWire["errors"]!["locale"]![0]!["params"]!["maxLength"]!.GetValue<string>().Should().Be("35");
    }

    [Fact]
    public async Task PublicSurface_Response_Schemas_Exclude_Internal_Fields()
    {
        var served = await Served();
        SchemaErrors(served).Should().BeEmpty();
        foreach (var (name, descriptor) in Dtos)
        {
            ClrFields(descriptor.Type).Should().BeEquivalentTo(descriptor.Fields.Where(field => field != "state"
                || descriptor.Type == typeof(PublicLevel)), name);
            var leaves = descriptor.Type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(property => property.PropertyType).ToArray();
            if (descriptor.Type == typeof(PublicLessonContent) || descriptor.Type == typeof(PublicUnavailableContent))
                leaves.Should().BeEmpty("these discriminator-only shapes have no CLR payload properties");
            else leaves.Should().OnlyContain(type => AllowedType(type), "public DTO leaves are bounded primitives, declared display DTOs or their lists");
        }
        ClrFields(typeof(PublicLeakProbe)).Should().NotBeEquivalentTo(["title"]);
        AllowedType(typeof(Guid)).Should().BeFalse();
        AllowedType(typeof(JsonElement)).Should().BeFalse();
        AllowedType(typeof(Dictionary<string, object>)).Should().BeFalse();
    }

    [Fact]
    public async Task Public_schema_controls_detect_nested_internal_fields_raw_payloads_and_unavailable_expansion()
    {
        var clean = await Served();
        SchemaErrors(clean).Should().BeEmpty();
        foreach (var (name, field) in new[] { ("PublicLabel", "tenantId"), ("PublicTextField", "rawBody"), ("PublicLessonContentPublicUnavailableContent", "rendererKey") })
        {
            var leak = clean.DeepClone();
            leak["components"]!["schemas"]![name]!["properties"]![field] = new JsonObject { ["type"] = "string" };
            SchemaErrors(leak).Should().Contain(name);
        }
        var raw = clean.DeepClone();
        raw["components"]!["schemas"]!["PublicTextField"]!["properties"]!["value"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = true };
        SchemaErrors(raw).Should().Contain("raw-shape");
        raw = clean.DeepClone();
        raw["components"]!["schemas"]!["PublicTextField"]!["properties"]!["value"] = new JsonObject();
        SchemaErrors(raw).Should().Contain("raw-shape");
        raw = clean.DeepClone();
        raw["components"]!["schemas"]!["PublicTextField"]!["properties"]!["value"] = new JsonObject
        { ["type"] = "object", ["properties"] = new JsonObject { ["tenantId"] = new JsonObject { ["type"] = "string" } } };
        SchemaErrors(raw).Should().Contain("leaf-shape");
    }

    private async Task<JsonNode> Served()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private static List<string> ContractErrors(JsonNode document)
    {
        var errors = new List<string>();
        var paths = document["paths"]!.AsObject();
        if (!paths.Select(path => path.Key).ToHashSet(StringComparer.Ordinal).SetEquals(Operations.Keys)) errors.Add("operations");
        var count = 0;
        foreach (var (path, ids) in Operations)
        {
            if (paths[path] is not JsonObject methods) { errors.Add("operations"); continue; }
            if (!methods.Select(method => method.Key).ToHashSet(StringComparer.Ordinal).SetEquals(["get", "head"])) errors.Add("operations");
            foreach (var (index, method) in new[] { (0, "get"), (1, "head") })
            {
                if (methods[method] is not JsonObject operation) continue;
                count++;
                if (operation["operationId"]?.GetValue<string>() != ids[index]) errors.Add("operations");
                var expected = path.EndsWith("/site", StringComparison.Ordinal) ? Array.Empty<string>()
                    : path.EndsWith("/lessons/{lessonSlug}", StringComparison.Ordinal) ? new[] { "slug", "lessonSlug", "locale" }
                    : path.EndsWith("/{slug}", StringComparison.Ordinal) ? new[] { "slug", "locale", "lessonCursor", "lessonLimit" }
                    : ["locale", "cursor", "limit"];
                var parameters = operation["parameters"]?.AsArray() ?? new JsonArray();
                if (!parameters.Select(parameter => parameter!["name"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal).SetEquals(expected)
                    || parameters.Count != expected.Length
                    || parameters.Any(parameter => parameter!["name"]!.GetValue<string>() == "locale" && parameter["required"]?.GetValue<bool>() != true)) errors.Add("parameters");
                var responses = operation["responses"]!.AsObject();
                if (!responses.Select(response => response.Key).ToHashSet(StringComparer.Ordinal).SetEquals(Statuses)) errors.Add("responses");
                foreach (var (status, response) in responses)
                {
                    if (response!["headers"]?["Cache-Control"]?["schema"]?["enum"]?[0]?.GetValue<string>() != "no-store") errors.Add("headers");
                    if (method == "head") { if (response["content"] is not null) errors.Add("head-body"); }
                    else if (status != "200" && response["content"]?["application/problem+json"]?["schema"]?["$ref"]?.GetValue<string>() != "#/components/schemas/ProblemDetails") errors.Add("problem");
                    else if (status == "200")
                    {
                        var root = path.EndsWith("/site", StringComparison.Ordinal) ? "PublicSite"
                            : path.EndsWith("/lessons/{lessonSlug}", StringComparison.Ordinal) ? "PublicLessonDetail"
                            : path.EndsWith("/{slug}", StringComparison.Ordinal) ? "PublicCourseDetail" : "PublicCourseCatalog";
                        if (response["content"]!.AsObject().Count != 1
                            || response["content"]!["application/json"]?["schema"]?["$ref"]?.GetValue<string>() != "#/components/schemas/" + root) errors.Add("success-media");
                    }
                }
            }
        }
        if (count != 8) errors.Add("operations");
        if (document["servers"] is JsonArray { Count: > 0 }) errors.Add("servers");
        var problem = document["components"]!["schemas"]!["ProblemDetails"]!;
        if (!problem["required"]!.AsArray().Select(node => node!.GetValue<string>()).ToHashSet(StringComparer.Ordinal)
            .SetEquals(["type", "title", "status", "instance", "code", "messageKey", "correlationId"])) errors.Add("problem");
        var messages = problem["properties"]?["errors"]?["additionalProperties"];
        var localized = messages?["items"];
        if (problem["properties"]?["errors"]?["type"]?.GetValue<string>() != "object"
            || messages?["type"]?.GetValue<string>() != "array"
            || localized?["type"]?.GetValue<string>() != "object"
            || localized?["properties"]?["key"]?["type"]?.GetValue<string>() != "string"
            || localized?["required"] is not JsonArray messageRequired
            || !messageRequired.Select(node => node!.GetValue<string>()).SequenceEqual(["key"])) errors.Add("problem-messages");
        var problemParameters = localized?["properties"]?["params"];
        if (problemParameters?["type"]?.GetValue<string>() != "object"
            || problemParameters?["additionalProperties"]?["type"]?.GetValue<string>() != "string") errors.Add("problem-params");
        return errors;
    }

    private static List<string> SchemaErrors(JsonNode document)
    {
        var errors = new List<string>();
        var schemas = document["components"]!["schemas"]!.AsObject();
        var expectedNames = Dtos.Keys.Concat(["PublicCourseAccess", "PublicDisplayState", "ProblemDetails"]);
        if (!schemas.Select(schema => schema.Key).ToHashSet(StringComparer.Ordinal).SetEquals(expectedNames)) errors.Add("schema-census");
        foreach (var (name, descriptor) in Dtos)
        {
            if (schemas[name] is not JsonObject schema) { errors.Add(name); continue; }
            var properties = schema["properties"]?.AsObject() ?? new JsonObject();
            if (!properties.Select(property => property.Key).ToHashSet(StringComparer.Ordinal).SetEquals(descriptor.Fields)) errors.Add(name);
            var required = schema["required"]?.AsArray().Select(node => node!.GetValue<string>()).ToHashSet(StringComparer.Ordinal) ?? [];
            if (!required.SetEquals(descriptor.Type == typeof(PublicLessonContent) ? ["state"] : descriptor.Fields)) errors.Add("required-fields");
            foreach (var property in descriptor.Type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (!LeafShapeMatches(property.PropertyType, properties[JsonNamingPolicy.CamelCase.ConvertName(property.Name)])) errors.Add("leaf-shape");
            foreach (var node in Descendants(schema))
            {
                if (node["type"] is null && node["$ref"] is null && node["anyOf"] is null && node["oneOf"] is null) errors.Add("raw-shape");
                if (node["additionalProperties"] is JsonValue value && value.TryGetValue<bool>(out var additional) && additional) errors.Add("raw-shape");
                if (node["type"] is JsonValue type && type.TryGetValue<string>(out var kind) && kind == "object"
                    && node["properties"] is null && node["anyOf"] is null) errors.Add("raw-shape");
                if (node["$ref"] is JsonValue reference && !expectedNames.Select(expected => "#/components/schemas/" + expected).Contains(reference.GetValue<string>(), StringComparer.Ordinal)) errors.Add("reference");
            }
        }
        if (schemas["PublicCourseAccess"]?["type"]?.GetValue<string>() != "string"
            || !schemas["PublicCourseAccess"]!["enum"]!.AsArray().Select(node => node!.GetValue<string>()).SequenceEqual(["public", "enrollment_required"])) errors.Add("access-enum");
        if (!schemas["PublicDisplayState"]!["enum"]!.AsArray().Select(node => node!.GetValue<string>()).SequenceEqual(["ready", "unavailable"])) errors.Add("state-enum");
        foreach (var name in new[] { "PublicLessonContentPublicReadyContent", "PublicLessonContentPublicUnavailableContent" })
            if (!schemas[name]!["required"]!.AsArray().Select(node => node!.GetValue<string>()).Contains("state", StringComparer.Ordinal)) errors.Add("content-state");
        return errors;
    }
    private static IEnumerable<JsonObject> Descendants(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            yield return obj;
            if (obj["properties"] is JsonObject properties)
                foreach (var child in properties.Select(member => member.Value).Where(value => value is not null).SelectMany(value => Descendants(value!))) yield return child;
            foreach (var keyword in new[] { "items", "anyOf", "oneOf", "allOf", "additionalProperties" })
                if (obj[keyword] is JsonObject or JsonArray)
                    foreach (var child in Descendants(obj[keyword]!)) yield return child;
        }
        else if (node is JsonArray array)
            foreach (var child in array.Where(value => value is not null).SelectMany(value => Descendants(value!))) yield return child;
    }
    private static string[] ClrFields(Type type) => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name)).ToArray();
    private static bool LeafShapeMatches(Type type, JsonNode? schema)
    {
        if (schema is not JsonObject) return false;
        if (schema["oneOf"] is JsonArray alternatives)
        {
            var nonNull = alternatives.Where(node => node?["type"]?.ToJsonString() != "\"null\"").ToArray();
            return nonNull.Length == 1 && LeafShapeMatches(type, nonNull[0]);
        }
        var types = schema["type"] is JsonArray union
            ? union.Select(node => node!.GetValue<string>()).Where(value => value != "null").ToArray()
            : schema["type"] is JsonValue value ? [value.GetValue<string>()] : Array.Empty<string>();
        if (Nullable.GetUnderlyingType(type) is { } underlying) return LeafShapeMatches(underlying, schema);
        if (type == typeof(string) || type == typeof(bool) || type == typeof(int))
            return types.SequenceEqual([type == typeof(string) ? "string" : type == typeof(bool) ? "boolean" : "integer"]);
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
            return types.SequenceEqual(["array"]) && LeafShapeMatches(type.GenericTypeArguments[0], schema["items"]);
        var name = type == typeof(PublicCourseAccess) || type == typeof(PublicDisplayState) ? type.Name
            : Dtos.Single(dto => dto.Value.Type == type).Key;
        return schema["$ref"]?.GetValue<string>() == "#/components/schemas/" + name;
    }
    private static bool AllowedType(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } underlying) return AllowedType(underlying);
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)) return AllowedType(type.GenericTypeArguments[0]);
        return type == typeof(string) || type == typeof(bool) || type == typeof(int)
            || type == typeof(PublicCourseAccess) || type == typeof(PublicDisplayState) || Dtos.Values.Any(dto => dto.Type == type);
    }
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "backend/LearnStack.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Cannot locate the contract snapshot repository root.");
    }
    private sealed record PublicLeakProbe(string Title, Guid TenantId);
}
