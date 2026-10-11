using System.Text.Json;
using FluentAssertions;
using LearnStack.Tools.Seeder;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed partial class DomainGenericityTests
{
    /// <summary>Phase 02d G20's exact-literal fence, not a proof against computed identities.</summary>
    [Fact]
    public void Production_Code_Does_Not_Branch_On_Demo_Tenant_Literals()
    {
        var vocabulary = DemoIdentities.Create();
        vocabulary.Values.Should().NotBeEmpty();
        var files = ProductionSourceRoots.SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .Where(path => Path.GetExtension(path) is ".cs" or ".json")
            // Sole authoritative tenant declaration, never the whole Seeder project.
            .Where(path => Path.GetRelativePath(RepositoryPaths.BackendSrc(), path).Replace('\\', '/')
                != "LearnStack.Tools.Seeder/SeedData.cs").Order(StringComparer.Ordinal).ToArray();
        files.Length.Should().BeGreaterThan(100, "the production census must not disappear");
        files.Select(Path.GetFileName).Should().Contain("SeedRunner.cs").And.Contain("Program.cs")
            .And.Contain("appsettings.json").And.Contain("PublicEducationDisplay.cs");
        files.SelectMany(path => DemoLiteralViolations(path, File.ReadAllText(path), vocabulary)
                .Select(value => $"{Path.GetRelativePath(RepositoryPaths.BackendSrc(), path)}: {value}"))
            .Should().BeEmpty("G20: demo identities are data; Fix: remove the production literal or branch");
    }

    [Fact]
    public void Demo_Identity_Projection_Is_Complete_And_The_Frontend_Snapshot_Matches()
    {
        var vocabulary = DemoIdentities.Create();
        var values = vocabulary.Values;
        values.Select(row => row.Kind).Distinct().Should().BeEquivalentTo(IdentityKinds);

        foreach (var tenant in SeedData.All)
        {
            values.Should().Contain(new DemoIdentity("id", tenant.TenantId.Value.ToString("D")));
            values.Should().Contain(new DemoIdentity("host", tenant.Host));
            values.Should().Contain(new DemoIdentity("tenant-slug", tenant.Slug))
                .And.Contain(new DemoIdentity("tenant-name", tenant.DisplayName));
            foreach (var organization in new[] { tenant.DefaultOrganization, tenant.SecondOrganization })
            {
                values.Should().Contain(new DemoIdentity("id", organization.OrganizationId.Value.ToString("D")));
                values.Should().Contain(new DemoIdentity("organization-slug", organization.Slug))
                    .And.Contain(new DemoIdentity("organization-name", organization.DisplayName));
            }
            foreach (var type in SeedData.ContentTypes(tenant))
            {
                values.Should().Contain(new DemoIdentity("id", type.Id.ToString("D")));
                values.Should().Contain(new DemoIdentity("content-type-key", type.Key));
                foreach (var label in type.DisplayName.Values)
                    values.Should().Contain(new DemoIdentity("definition-label", label));
                using var schema = JsonDocument.Parse(type.JsonSchema);
                foreach (var property in schema.RootElement.GetProperty("properties").EnumerateObject())
                    values.Should().Contain(new DemoIdentity("property-name", property.Name));
                if (schema.RootElement.TryGetProperty("x-fields", out var descriptors))
                    foreach (var descriptor in descriptors.EnumerateArray())
                        foreach (var label in descriptor.GetProperty("label").EnumerateObject())
                            values.Should().Contain(new DemoIdentity("field-label", label.Value.GetString()!));
            }
            foreach (var taxonomy in SeedData.Taxonomies(tenant))
            {
                values.Should().Contain(new DemoIdentity("id", taxonomy.Id.ToString("D")));
                values.Should().Contain(new DemoIdentity("taxonomy-key", taxonomy.Key));
                foreach (var label in taxonomy.DisplayName.Values)
                    values.Should().Contain(new DemoIdentity("definition-label", label));
                foreach (var band in taxonomy.Bands)
                {
                    values.Should().Contain(new DemoIdentity("band-key", band.Key));
                    foreach (var label in band.DisplayName.Values)
                        values.Should().Contain(new DemoIdentity("band-label", label));
                }
            }
            var curriculum = tenant.Curriculum!;
            values.Should().Contain(new DemoIdentity("id", curriculum.Theme.Id.ToString("D")));
            foreach (var course in curriculum.Courses)
            {
                values.Should().Contain(new DemoIdentity("id", course.Id.ToString("D")));
                values.Should().Contain(new DemoIdentity("course-key", course.SlugKey));
                foreach (var translation in course.Translations)
                    values.Should().Contain(new DemoIdentity("course-title", translation.Title))
                        .And.Contain(new DemoIdentity("course-slug", translation.Slug));
                foreach (var lesson in course.Lessons)
                {
                    values.Should().Contain(new DemoIdentity("id", lesson.Id.ToString("D")));
                    foreach (var translation in lesson.Translations)
                        values.Should().Contain(new DemoIdentity("lesson-title", translation.Title))
                            .And.Contain(new DemoIdentity("lesson-slug", translation.Slug));
                }
            }
        }
        values.Where(row => row.Kind == "id").Select(row => row.Value).Should().OnlyHaveUniqueItems();
        values.Should().NotContain(row => row.Value == "Published" || row.Value == "enrollment_required" || row.Value == "en");
        var fixture = Path.Combine(RepositoryPaths.RepoRoot(), "frontend/apps/web/src/test/fixtures/demo-identities.json");
        JsonSerializer.Serialize(JsonSerializer.Deserialize<DemoIdentities>(File.ReadAllText(fixture), JsonSerializerOptions.Web), JsonSerializerOptions.Web)
            .Should().Be(JsonSerializer.Serialize(vocabulary, JsonSerializerOptions.Web),
            "Fix: regenerate the derived fixture with the Seeder --demo-identities command after SeedData changes");
    }

    [Fact]
    public void Demo_Literal_Fence_Detects_Decoded_Identities_And_Admits_Platform_Data()
    {
        var vocabulary = DemoIdentities.Create();
        var identity = vocabulary.Values.First(row => row.Kind == "tenant-name").Value;
        var id = vocabulary.Values.First(row => row.Kind == "id").Value;
        var key = vocabulary.Values.First(row => row.Kind == "content-type-key" && !vocabulary.PlatformValues.Contains(row.Value)).Value;
        foreach (var value in new[] { identity, key, Guid.Parse(id).ToString("B").ToUpperInvariant(), Guid.Parse(id).ToString("N") })
            DemoLiteralViolations("Probe.cs", $"class Probe {{ string Value = {JsonSerializer.Serialize(value)}; }}", vocabulary)
                .Should().ContainSingle("each backend identity/customization/alternate UUID spelling must be seen");
        var escaped = $"\\u{(int)identity[0]:x4}{identity[1..]}";
        DemoLiteralViolations("Probe.cs", "class Probe { string Value = \"" + escaped + "\"; }", vocabulary)
            .Should().ContainSingle("Roslyn decodes escaped strings");
        DemoLiteralViolations("Probe.cs", "class Probe { string Value = \"\"\"" + identity + "\"\"\"; }", vocabulary)
            .Should().ContainSingle("raw literals are values too");
        DemoLiteralViolations("Probe.cs", "class Probe { string Value = $\"" + identity + "\"; }", vocabulary)
            .Should().ContainSingle("constant templates are complete literals");
        DemoLiteralViolations("Probe.cs", "class Probe { string Value = \"{\\\"name\\\":\\\"" + identity + "\\\"}\"; }", vocabulary)
            .Should().ContainSingle("embedded JSON values must not hide identities");
        DemoLiteralViolations("Probe.json", JsonSerializer.Serialize(new { value = identity }), vocabulary).Should().ContainSingle();
        DemoLiteralViolations("Probe.cs", "// " + identity + "\nclass Probe { string Value = \"generic\"; }", vocabulary).Should().BeEmpty();
        foreach (var value in vocabulary.PlatformValues)
            DemoLiteralViolations("Probe.cs", $"class Probe {{ string Value = {JsonSerializer.Serialize(value)}; }}", vocabulary).Should().BeEmpty();
        var testData = DemoLiteralViolations("Probe.cs", $"class Probe {{ string Value = {JsonSerializer.Serialize(identity + " suffix")}; }}", vocabulary);
        testData.Should().BeEmpty("matching is complete ordinal equality, never a substring");
        Action broken = () => _ = DemoLiteralViolations("Probe.json", "{", vocabulary);
        broken.Should().Throw<JsonException>();
        Action brokenCode = () => _ = DemoLiteralViolations("Probe.cs", "class {", vocabulary);
        brokenCode.Should().Throw<InvalidOperationException>();
    }

    /// <summary>G20 also bans symbolic access to the Seeder from every production project.</summary>
    [Fact]
    public void Production_Projects_Do_Not_Reference_The_Seeder()
    {
        var projects = ProductionSourceRoots.SelectMany(root => Directory.GetFiles(root, "*.csproj", SearchOption.AllDirectories))
            .Where(path => Path.GetFileNameWithoutExtension(path) != SeederAssembly).ToArray();
        projects.Length.Should().BeGreaterThan(20);
        projects.Select(Path.GetFileNameWithoutExtension).Should().Contain("LearnStack.Api").And.Contain("LearnStack.SharedKernel");
        foreach (var project in projects)
        {
            var name = Path.GetFileNameWithoutExtension(project);
            var assembly = System.Reflection.Assembly.Load(name);
            assembly.GetReferencedAssemblies().Select(row => row.Name).Should().NotContain(SeederAssembly,
                $"{name}: Fix: depend on application contracts, never demo SeedData");
            var assets = Path.Combine(Path.GetDirectoryName(project)!, "obj/project.assets.json");
            File.Exists(assets).Should().BeTrue("restore-resolved references must exist");
            ModuleDependencyTests.RestoredProjectReferences(File.ReadAllText(assets)).Where(IsSeederReference)
                .Should().BeEmpty($"{name}: Fix: remove the production Seeder project reference");
        }
    }

    [Fact]
    public void Seeder_Reference_Fence_Detects_Compiled_And_Restored_Edges()
    {
        typeof(DemoIdentities).Assembly.GetName().Name.Should().Be(SeederAssembly);
        typeof(DomainGenericityTests).Assembly.GetReferencedAssemblies().Select(row => row.Name)
            .Should().Contain(SeederAssembly, "the test assembly is an actual compiled planted reference");
        var graph = """
            {"project":{"restore":{"frameworks":{"net10.0":{"projectReferences":{
            "C:\\repo\\LearnStack.Tools.Seeder.csproj":{},"/repo/LearnStack.SharedKernel.csproj":{}}}}}}}
            """;
        ModuleDependencyTests.RestoredProjectReferences(graph).Where(IsSeederReference).Should().ContainSingle();
        IsSeederReference("/repo/LearnStack.Tools.SeederCopy.csproj").Should().BeFalse();
    }

    private static readonly string[] IdentityKinds = [
        "id", "tenant-slug", "tenant-name", "host", "organization-slug", "organization-name",
        "content-type-key", "taxonomy-key", "definition-label", "band-key", "band-label",
        "course-key", "course-slug", "course-title", "lesson-slug", "lesson-title", "property-name", "field-label"
    ];
    private static readonly string[] ProductionSourceRoots = [RepositoryPaths.BackendSrc(),
        Path.Combine(RepositoryPaths.RepoRoot(), "backend/analyzers")];

    private const string SeederAssembly = "LearnStack.Tools.Seeder";
    private static bool IsSeederReference(string path) =>
        Path.GetFileNameWithoutExtension(path.Replace('\\', '/').Split('/')[^1]) == SeederAssembly;

    private static string[] DemoLiteralViolations(string path, string source, DemoIdentities vocabulary)
    {
        var ids = vocabulary.Values.Where(row => row.Kind == "id").Select(row => Guid.Parse(row.Value)).ToHashSet();
        var strings = vocabulary.Values.Where(row => row.Kind != "id").Select(row => row.Value)
            .Except(vocabulary.PlatformValues, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        return (Path.GetExtension(path) == ".json" ? JsonValues(source) : CSharpValues(source))
            .Where(value => strings.Contains(value) || Guid.TryParse(value, out var id) && ids.Contains(id))
            .Distinct(StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<string> CSharpValues(string source)
    {
        var syntax = CSharpSyntaxTree.ParseText(source);
        if (syntax.GetDiagnostics().Any(row => row.Severity == DiagnosticSeverity.Error))
            throw new InvalidOperationException("Unreadable production C#; Fix: restore syntax before scanning.");
        foreach (var node in syntax.GetRoot().DescendantNodes())
        {
            var value = node switch
            {
                LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) => literal.Token.ValueText,
                InterpolatedStringExpressionSyntax template when template.Contents.All(part => part is InterpolatedStringTextSyntax) =>
                    string.Concat(template.Contents.Cast<InterpolatedStringTextSyntax>().Select(part => part.TextToken.ValueText)),
                _ => null
            };
            if (value is null) continue;
            yield return value;
            // JSON embedded in C# is a source of complete decoded values too.
            string[] nested;
            try { nested = value.TrimStart().StartsWith('{') || value.TrimStart().StartsWith('[') ? JsonValues(value).ToArray() : []; }
            catch (JsonException) { nested = []; } // Ordinary text/SQL may start with a bracket.
            foreach (var inner in nested) yield return inner;
        }
    }

    private static string[] JsonValues(string source)
    {
        using var document = JsonDocument.Parse(source);
        return Walk(document.RootElement).ToArray();
        static IEnumerable<string> Walk(JsonElement node) => node.ValueKind switch
        {
            JsonValueKind.String => [node.GetString()!],
            JsonValueKind.Array => node.EnumerateArray().SelectMany(Walk),
            JsonValueKind.Object => node.EnumerateObject().SelectMany(row => Walk(row.Value)),
            _ => []
        };
    }
}
