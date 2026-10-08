using System.Text.Json.Nodes;
using LearnStack.SharedKernel.Localization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace LearnStack.Api.PublicReads;

/// <summary>Documents the public wire boundary, including parameters read without MVC binding.</summary>
internal sealed class PublicReadDocumentTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.Paths.Keys.Any(path => path.Contains("/public/", StringComparison.Ordinal))) return Task.CompletedTask;
        document.Components ??= new OpenApiComponents();
        document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
        document.Components.Schemas["ProblemDetails"] = Problem();
        foreach (var (path, item) in document.Paths)
        {
            if (!path.Contains("/public/", StringComparison.Ordinal)) continue;
            foreach (var (method, operation) in item.Operations ?? [])
            {
                operation.Parameters ??= [];
                foreach (var parameter in operation.Parameters.OfType<OpenApiParameter>().Where(parameter => parameter.In == ParameterLocation.Path))
                {
                    parameter.Schema = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 160 };
                    parameter.Description = "Lowercase ASCII slug with single interior hyphens; UUID-shaped slugs are refused.";
                }
                if (!path.EndsWith("/site", StringComparison.Ordinal))
                {
                    operation.Parameters.Add(Query("locale", true, new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 2, MaxLength = LocaleTag.MaxLength },
                        "Exactly one well-formed locale, canonicalized by case without trimming. Headers do not select content; absent/disabled locales give unsupported_locale."));
                    if (!path.EndsWith("/lessons/{lessonSlug}", StringComparison.Ordinal))
                    {
                        var outline = path.EndsWith("/{slug}", StringComparison.Ordinal);
                        operation.Parameters.Add(Query(outline ? "lessonCursor" : "cursor", false,
                            new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 1024 },
                            "Canonical unpadded base64url v1 seek cursor, bound to host, scope, locale, endpoint and order. Not authority or a snapshot; malformed or mismatched values give validation_failed."));
                        operation.Parameters.Add(Query(outline ? "lessonLimit" : "limit", false,
                            new OpenApiSchema { Type = JsonSchemaType.String },
                            "Positive ASCII decimal, including leading zeros. Default 20; values above 100 clamp to 100. Zero, signs, whitespace and repeats are refused."));
                    }
                }
                operation.Description = "Anonymous host-scoped read through the read-only pipeline. Unknown query keys and repeated single-valued keys are refused. No response caching or validators.";
                operation.Responses ??= [];
                foreach (var (status, description) in Errors)
                    operation.Responses[status] = new OpenApiResponse
                    {
                        Description = status == "400" && path.EndsWith("/site", StringComparison.Ordinal)
                            ? "validation_failed; the site bootstrap accepts no query parameters." : description,
                        Content = method == HttpMethod.Head ? null : new Dictionary<string, OpenApiMediaType>
                        { ["application/problem+json"] = new() { Schema = new OpenApiSchemaReference("ProblemDetails", document) } }
                    };
                foreach (var response in operation.Responses.Values.OfType<OpenApiResponse>())
                {
                    response.Headers ??= new Dictionary<string, IOpenApiHeader>();
                    response.Headers["Cache-Control"] = new OpenApiHeader
                    {
                        Required = true,
                        Schema = new OpenApiSchema
                        { Type = JsonSchemaType.String, Enum = [JsonValue.Create("no-store")!] }
                    };
                    if (method == HttpMethod.Head) response.Content = null;
                }
                // The controller formatter emits JSON; MVC's default text alternatives are not this contract.
                if (method == HttpMethod.Get && operation.Responses["200"] is OpenApiResponse success && success.Content is { } content)
                    success.Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = content["application/json"] };
            }
        }
        foreach (var (name, value) in document.Components.Schemas)
        {
            if (value is not OpenApiSchema schema) continue;
            if (name is "PublicCourseAccess" or "PublicDisplayState") schema.Type = JsonSchemaType.String;
            if (name == "PublicOutlineItem" && schema.Properties?["sort"] is OpenApiSchema sort)
                sort.Type = JsonSchemaType.Integer; // MVC accepts numeric input strings, but this response always writes a number.
            if (name.StartsWith("PublicLessonContentPublic", StringComparison.Ordinal))
            {
                schema.Type = JsonSchemaType.Object;
                schema.Required ??= new HashSet<string>();
                schema.Required.Add("state");
            }
        }
        return Task.CompletedTask;
    }

    private static OpenApiParameter Query(string name, bool required, OpenApiSchema schema, string description) => new()
    { Name = name, In = ParameterLocation.Query, Required = required, Schema = schema, Description = description, Style = ParameterStyle.Form, Explode = false };

    private static readonly (string Status, string Description)[] Errors =
    [ ("400", "validation_failed or unsupported_locale; bounded localizable field errors where applicable."),
      ("404", "not_found; unavailable host or hidden/untranslated/ineligible resource, without disclosure."),
      ("405", "method_not_allowed from routing."), ("429", "rate_limited from the anonymous limiter."),
      ("500", "internal_error from unexpected failures; no exception or query details."),
      ("503", "dependency_unavailable from invalid stored public configuration.") ];

    private static OpenApiSchema Text() => new() { Type = JsonSchemaType.String };
    private static OpenApiSchema Problem() => new()
    {
        Type = JsonSchemaType.Object,
        Required = new HashSet<string> { "type", "title", "status", "instance", "code", "messageKey", "correlationId" },
        Properties = new Dictionary<string, IOpenApiSchema>
        {
            ["type"] = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uri" },
            ["title"] = Text(),
            ["status"] = new OpenApiSchema { Type = JsonSchemaType.Integer, Minimum = "400", Maximum = "599" },
            ["instance"] = Text(),
            ["code"] = Text(),
            ["messageKey"] = Text(),
            ["correlationId"] = Text(),
            ["errors"] = new OpenApiSchema
            {
                Type = JsonSchemaType.Object,
                AdditionalProperties = new OpenApiSchema
                {
                    Type = JsonSchemaType.Array,
                    Items = new OpenApiSchema
                    {
                        Type = JsonSchemaType.Object,
                        Required = new HashSet<string> { "key" },
                        Properties = new Dictionary<string, IOpenApiSchema>
                        { ["key"] = Text(), ["params"] = new OpenApiSchema { Type = JsonSchemaType.Object, AdditionalProperties = Text() } }
                    }
                }
            },
        },
    };
}
