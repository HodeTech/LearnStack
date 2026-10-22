using System.Text.Json;
using LearnStack.Modules.Customization.Application.Contracts.Customization;

namespace LearnStack.Tools.Seeder;

/// <summary>Read-only, typed G20 vocabulary. SeedData is its only tenant-data source.</summary>
public sealed record DemoIdentities(IReadOnlyList<DemoIdentity> Values, IReadOnlyList<string> PlatformValues)
{
    public static DemoIdentities Create()
    {
        var values = new HashSet<DemoIdentity>();
        void Add(string kind, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("Empty demo identity.");
            values.Add(new(kind, value));
        }
        void Id(Guid id) => Add("id", id.ToString("D"));
        void Labels(string kind, IReadOnlyDictionary<string, string> labels)
        {
            if (labels.Count == 0) throw new InvalidOperationException("Missing demo labels.");
            foreach (var label in labels.Values) Add(kind, label);
        }
        foreach (var tenant in SeedData.All)
        {
            Id(tenant.TenantId.Value);
            Add("tenant-slug", tenant.Slug);
            Add("tenant-name", tenant.DisplayName);
            Add("host", tenant.Host);
            foreach (var organization in new[] { tenant.DefaultOrganization, tenant.SecondOrganization })
            {
                Id(organization.OrganizationId.Value);
                Add("organization-slug", organization.Slug);
                Add("organization-name", organization.DisplayName);
            }
            foreach (var type in SeedData.ContentTypes(tenant))
            {
                Id(type.Id);
                Add("content-type-key", type.Key);
                Labels("definition-label", type.DisplayName);
                foreach (var identity in SchemaValues(type.JsonSchema)) values.Add(identity);
            }
            foreach (var taxonomy in SeedData.Taxonomies(tenant))
            {
                Id(taxonomy.Id);
                Add("taxonomy-key", taxonomy.Key);
                Labels("definition-label", taxonomy.DisplayName);
                foreach (var band in taxonomy.Bands)
                {
                    Add("band-key", band.Key);
                    Labels("band-label", band.DisplayName);
                }
            }
            if (tenant.Curriculum is not { } curriculum) continue;
            Id(curriculum.Theme.Id);
            foreach (var course in curriculum.Courses)
            {
                Id(course.Id);
                Add("course-key", course.SlugKey);
                foreach (var translation in course.Translations)
                {
                    Add("course-slug", translation.Slug);
                    Add("course-title", translation.Title);
                }
                foreach (var lesson in course.Lessons)
                {
                    Id(lesson.Id);
                    foreach (var translation in lesson.Translations)
                    {
                        Add("lesson-slug", translation.Slug);
                        Add("lesson-title", translation.Title);
                    }
                }
            }
        }
        foreach (var user in SeedData.IdentityUsers)
        {
            Id(user.UserId.Value);
            Add("identity-username", user.Username);
        }
        // Built-ins retain their semantic vocabulary; their tenant-owned identities
        // are generated at initialization and are not demo literals.
        Add("content-type-key", BuiltInCustomizations.Card.Key);
        Labels("definition-label", BuiltInCustomizations.Card.DisplayName);
        foreach (var identity in SchemaValues(BuiltInCustomizations.Card.JsonSchema)) values.Add(identity);
        Add("taxonomy-key", BuiltInCustomizations.Plain.Key);
        Labels("definition-label", BuiltInCustomizations.Plain.DisplayName);
        foreach (var band in BuiltInCustomizations.Plain.Bands)
        {
            Add("band-key", band.Key);
            Labels("band-label", band.DisplayName);
        }
        var platform = new HashSet<string>(StringComparer.Ordinal)
        {
            BuiltInCustomizations.Card.Key, BuiltInCustomizations.Card.RendererKey, BuiltInCustomizations.Plain.Key
        };
        platform.UnionWith(BuiltInCustomizations.Card.DisplayName.Values);
        platform.UnionWith(BuiltInCustomizations.Plain.DisplayName.Values);
        platform.UnionWith(SchemaValues(BuiltInCustomizations.Card.JsonSchema).Select(identity => identity.Value));
        foreach (var band in BuiltInCustomizations.Plain.Bands)
        {
            platform.Add(band.Key);
            platform.UnionWith(band.DisplayName.Values);
        }
        return new([.. values.OrderBy(identity => identity.Kind, StringComparer.Ordinal)
            .ThenBy(identity => identity.Value, StringComparer.Ordinal)], [.. platform.Order(StringComparer.Ordinal)]);
    }

    private static DemoIdentity[] SchemaValues(string schema)
    {
        using var document = JsonDocument.Parse(schema);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Demo schema must be an object.");
        return Walk(document.RootElement).ToArray();

        static IEnumerable<DemoIdentity> Walk(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in node.EnumerateArray())
                    foreach (var value in Walk(element)) yield return value;
            }
            if (node.ValueKind != JsonValueKind.Object) yield break;
            foreach (var property in node.EnumerateObject())
            {
                if (property.Name == "properties")
                    foreach (var field in property.Value.EnumerateObject())
                        yield return new("property-name", field.Name);
                if (property.Name == "x-fields")
                    foreach (var field in property.Value.EnumerateArray())
                    {
                        yield return new("property-name", field.GetProperty("name").GetString()
                            ?? throw new InvalidOperationException("Missing descriptor name."));
                        foreach (var label in field.GetProperty("label").EnumerateObject())
                            yield return new("field-label", label.Value.GetString()
                                ?? throw new InvalidOperationException("Missing descriptor label."));
                    }
                foreach (var value in Walk(property.Value)) yield return value;
            }
        }
    }
}

public sealed record DemoIdentity(string Kind, string Value);
