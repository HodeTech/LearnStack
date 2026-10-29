using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using LearnStack.Tools.Seeder;
using LearnStack.Modules.Customization.Application.Contracts.Customization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;
using Mono.Cecil.Cil;
using OriginManifestCensus = LearnStack.Tests.Architecture.TrustedOperationSourceConventionTests.Census;

namespace LearnStack.Tests.Architecture;

public sealed partial class DomainGenericityTests
{
    /// <summary>Phase 02d G20's exact-literal fence, not a proof against computed identities.</summary>
    [Fact]
    public void Production_Code_Does_Not_Branch_On_Demo_Tenant_Literals()
    {
        var vocabulary = DemoIdentities.Create();
        vocabulary.Values.Should().NotBeEmpty();
        using var manifestCensus = OriginManifestCensus.Read();
        FixedOriginManifestReferenceOffenders(manifestCensus).Should().BeEmpty(
            "the closed topology manifest cannot become a symbolic demo identity source for ordinary production code");
        var allFiles = DemoProductionSourceCensus();
        var files = allFiles
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

        values.Should().Contain(new DemoIdentity("content-type-key", BuiltInCustomizations.Card.Key))
            .And.Contain(new DemoIdentity("taxonomy-key", BuiltInCustomizations.Plain.Key));
        foreach (var band in BuiltInCustomizations.Plain.Bands)
        {
            values.Should().Contain(new DemoIdentity("band-key", band.Key));
            foreach (var label in band.DisplayName.Values)
                values.Should().Contain(new DemoIdentity("band-label", label));
        }

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
            if (tenant.Curriculum is not { } curriculum) continue;
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
        foreach (var identity in SeedData.IdentityUsers)
        {
            values.Should().Contain(new DemoIdentity("id", identity.UserId.Value.ToString("D")));
            values.Should().Contain(new DemoIdentity("identity-username", identity.Username));
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
        var username = SeedData.IdentityUsers[0].Username;
        var actor = SeedData.IdentityUsers[0].UserId.Value.ToString("D");
        foreach (var value in new[] { identity, key, username, actor,
            Guid.Parse(id).ToString("B").ToUpperInvariant(), Guid.Parse(id).ToString("N") })
            DemoLiteralViolations("Probe.cs", $"class Probe {{ string Value = {JsonSerializer.Serialize(value)}; }}", vocabulary)
                .Should().ContainSingle("each backend identity/customization/alternate UUID spelling must be seen");
        var project = Path.GetFullPath("/repo/backend/src/Probe");
        var generated = new[] { Path.Combine(project, "bin"), Path.Combine(project, "obj") };
        foreach (var directory in new[] { "bin", "obj" })
        {
            BackendDemoSourceKind(Path.Combine(project, "Features", directory, "Probe.cs"), generated)
                .Should().Be(1, "only actual project outputs are excluded");
            BackendDemoSourceKind(Path.Combine(project, directory, "Generated.cs"), generated).Should().Be(0);
        }
        foreach (var name in new[] { ".env.local", ".DS_Store", "Thumbs.db", "editor.swp", "runtime.log" })
        {
            var local = Path.Combine(RepositoryPaths.BackendSrc(), "LearnStack.Api", name);
            IsDemoLocalArtifact(local).Should().BeTrue();
            OmitIgnoredDemoLocalFile(local).Should().BeTrue("the actual Git rules omit known machine files");
        }
        foreach (var name in new[] { ".env.hidden.cs", ".env.hidden.json", "hidden.resx", "Probe.csproj.user" })
            IsDemoLocalArtifact(Path.Combine(project, name)).Should().BeFalse("ignored source/unknown formats are not machine-file exemptions");
        // MSBuild automatically imports .csproj.user, so ignore rules cannot exempt it.
        OmitIgnoredDemoLocalFile(Path.Combine(RepositoryPaths.BackendSrc(), "LearnStack.Api", "Probe.csproj.user"))
            .Should().BeFalse();
        Action ignoredBuildHook = () => _ = BackendDemoSourceKind(Path.Combine(project, "Probe.csproj.user"), generated);
        ignoredBuildHook.Should().Throw<InvalidOperationException>();
        Action unknownFormat = () => _ = BackendDemoSourceKind(Path.Combine(project, "Probe.resx"), generated);
        unknownFormat.Should().Throw<InvalidOperationException>();
        Action linkedInput = () => RequireCompileCoverage([Path.Combine(project, "Probe.cs")], ["/repo/shared/Linked.cs"]);
        linkedInput.Should().Throw<InvalidOperationException>();
        RequireCompileCoverage([Path.Combine(project, "Probe.cs")], [Path.Combine(project, "Probe.cs")]);
        Action dynamicCompile = () => RefuseExecutionTimeCompile("<Project><Target Name=\"Add\"><ItemGroup><Compile Include=\"../shared/Probe.cs\" /></ItemGroup></Target></Project>");
        dynamicCompile.Should().Throw<InvalidOperationException>();
        RefuseExecutionTimeCompile("<Project><ItemGroup><Compile Include=\"Probe.cs\" /></ItemGroup></Project>");
        Action importedTarget = () => RefuseExecutionTimeCompile("<Project><Import Project=\"../shared.targets\" /></Project>");
        importedTarget.Should().Throw<InvalidOperationException>();
        Action customSdk = () => RefuseExecutionTimeCompile("<Project Sdk=\"Custom.Sdk\" />");
        customSdk.Should().Throw<InvalidOperationException>();
        Action implicitImport = () => RefuseExecutionTimeCompile("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><DirectoryBuildTargetsPath>../../tests/CensusInjection.targets</DirectoryBuildTargetsPath></PropertyGroup></Project>");
        implicitImport.Should().Throw<InvalidOperationException>();
        RefuseExecutionTimeCompile("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        Action externalProject = () => RequireProductionProjectCoverage([Path.Combine(project, "Probe.csproj")], ["/repo/backend/tests/RuntimeHelper/RuntimeHelper.csproj"]);
        externalProject.Should().Throw<InvalidOperationException>();
        RequireProductionProjectCoverage([Path.Combine(project, "Probe.csproj")], [Path.Combine(project, "Probe.csproj")]);
        var escaped = $"\\u{(int)identity[0]:x4}{identity[1..]}";
        DemoLiteralViolations("Probe.cs", "class Probe { string Value = \"" + escaped + "\"; }", vocabulary)
            .Should().ContainSingle("Roslyn decodes escaped strings");
        DemoLiteralViolations("Probe.cs", "class Probe { string Value = \"\"\"" + identity + "\"\"\"; }", vocabulary)
            .Should().ContainSingle("raw literals are values too");
        DemoLiteralViolations("Probe.cs", "class Probe { string Value = $\"" + identity + "\"; }", vocabulary)
            .Should().ContainSingle("constant templates are complete literals");
        DemoLiteralViolations("Probe.cs", "class Probe { string Value = \"{\\\"name\\\":\\\"" + identity + "\\\"}\"; }", vocabulary)
            .Should().ContainSingle("embedded JSON values must not hide identities");
        var json = JsonSerializer.Serialize(new { name = identity });
        foreach (var literal in new[] {
            JsonSerializer.Serialize(identity) + "u8",
            "\"\"\"" + identity + "\"\"\"u8",
            "\"" + escaped + "\"u8",
            JsonSerializer.Serialize(json) + "u8",
            "\"\"\"" + json + "\"\"\"u8",
            "\"{\\\"name\\\":\\\"" + escaped + "\\\"}\"u8" })
            DemoLiteralViolations("Probe.cs", "class Probe { System.ReadOnlySpan<byte> Value => " + literal + "; }", vocabulary)
                .Should().Equal([identity], "UTF-8 literals decode ordinary, raw, escaped and embedded JSON values too");
        var declaration = "class Probe { string Value = " + JsonSerializer.Serialize(identity) + "; }";
        foreach (var source in new[] {
            "#if NET\n" + declaration + "\n#endif",
            "#if NET10_0\r\n" + declaration + "\r\n#endif",
            "#if FUTURE_TARGET\n" + declaration + "\n#endif",
            "#if NET\nclass Probe {}\n#else\n" + declaration + "\n#endif",
            "#if NET\nclass Probe {}\n#elif FUTURE_TARGET\n" + declaration + "\n#endif",
            "#if NET\n#if NET10_0\nclass Probe {}\n#elif FUTURE_TARGET\n" + declaration + "\n#else\nclass Probe {}\n#endif\n#endif",
            "#if NET\n#if NET10_0\nclass Probe {}\n#else\n" + declaration + "\n#endif\n#endif" })
        {
            DemoLiteralViolations("Probe.cs", source, vocabulary)
                .Should().Equal([identity], "every feasible conditional configuration is scanned independently of build symbols");
            DemoLiteralViolations("Probe.cs", source.Replace(identity, "generic", StringComparison.Ordinal), vocabulary)
                .Should().BeEmpty("mutually exclusive declarations need only be syntactically valid for the source scan");
        }
        DemoLiteralViolations("Probe.cs", "#if NET\n// " + identity + "\n#elif NET10_0\n/* " + identity
            + " */\n#else\nclass Probe { string Value = \"generic\"; }\n#endif", vocabulary)
            .Should().BeEmpty("comments remain excluded in every conditional branch");
        var directiveText = "#if NET\n" + identity + "\n#endif";
        CSharpValues("class Probe { string Value = \"\"\"\n" + directiveText + "\n\"\"\"; }")
            .Should().Equal([directiveText], "directive-looking text inside a raw literal must remain unchanged");
        var splitComment = "#if FOO\n/*\n#endif\n" + declaration + "\n#if FOO\n*/\n#endif";
        DemoLiteralViolations("Probe.cs", splitComment, vocabulary).Should().Equal([identity]);
        DemoLiteralViolations("Probe.cs", splitComment.Replace(identity, "generic", StringComparison.Ordinal), vocabulary)
            .Should().BeEmpty();
        DemoLiteralViolations("Probe.cs", "#define FOO\n" + splitComment, vocabulary).Should().BeEmpty();
        var lateSymbol = "#if FOO\n/*\n#endif\n#if LATE_SYMBOL\n" + declaration
            + "\n#endif\n#if FOO\n*/\n#endif";
        DemoLiteralViolations("Probe.cs", lateSymbol, vocabulary).Should().Equal([identity],
            "symbols exposed by another lexical configuration must enter the fixed-point census");
        var excessive = string.Join("\n", Enumerable.Range(0, 9).Select(index => $"#if SYMBOL{index}\nclass Probe{index} {{}}\n#endif"));
        Action tooManySymbols = () => _ = DemoLiteralViolations("Probe.cs", excessive, vocabulary);
        tooManySymbols.Should().Throw<InvalidOperationException>("an excessive census must fail rather than omit configurations");
        DemoLiteralViolations("Probe.json", JsonSerializer.Serialize(new { value = identity }), vocabulary).Should().ContainSingle();
        BackendDemoSourceKind(Path.Combine(project, "Embedded.sql"), generated).Should().Be(1);
        DemoLiteralViolations("Probe.sql", "SELECT '" + identity.Replace("'", "''", StringComparison.Ordinal) + "';", vocabulary)
            .Should().ContainSingle("embedded SQL must not bypass the production literal census");
        DemoLiteralViolations("Probe.sql", "SELECT $value$" + identity + "$value$;", vocabulary).Should().ContainSingle();
        DemoLiteralViolations("Probe.sql", "DO $$ BEGIN PERFORM '" + id + "'; END $$;", vocabulary).Should().ContainSingle();
        DemoLiteralViolations("Probe.sql", "SELECT '" + json.Replace("'", "''", StringComparison.Ordinal) + "';", vocabulary)
            .Should().ContainSingle("JSON inside a SQL string remains source data");
        DemoLiteralViolations("Probe.sql", "-- '" + identity + "'\n/* '" + identity + "' */ SELECT 'generic';", vocabulary)
            .Should().BeEmpty("ordinary SQL comments remain outside the exact-literal policy");
        Action escapedSql = () => _ = DemoLiteralViolations("Probe.sql", "SELECT E'\\u0041';", vocabulary);
        escapedSql.Should().Throw<InvalidOperationException>("unmodeled SQL escape decoding must fail instead of omitting values");
        DemoLiteralViolations("Probe.cs", "// " + identity + "\nclass Probe { string Value = \"generic\"; }", vocabulary).Should().BeEmpty();
        foreach (var value in vocabulary.PlatformValues)
        {
            DemoLiteralViolations("Probe.cs", $"class Probe {{ string Value = {JsonSerializer.Serialize(value)}; }}", vocabulary).Should().BeEmpty();
            DemoLiteralViolations("Probe.cs", $"class Probe {{ System.ReadOnlySpan<byte> Value => {JsonSerializer.Serialize(value)}u8; }}", vocabulary)
                .Should().BeEmpty("canonical platform values remain allowed as UTF-8 literals");
        }
        var testData = DemoLiteralViolations("Probe.cs", $"class Probe {{ string Value = {JsonSerializer.Serialize(identity + " suffix")}; }}", vocabulary);
        testData.Should().BeEmpty("matching is complete ordinal equality, never a substring");
        Action broken = () => _ = DemoLiteralViolations("Probe.json", "{", vocabulary);
        broken.Should().Throw<JsonException>();
        Action brokenCode = () => _ = DemoLiteralViolations("Probe.cs", "class {", vocabulary);
        brokenCode.Should().Throw<InvalidOperationException>();
        Action brokenConditional = () => _ = DemoLiteralViolations("Probe.cs", "#if NET10_0\nclass {\n#endif", vocabulary);
        brokenConditional.Should().Throw<InvalidOperationException>("unreadable branches must not silently disappear");
        Action brokenDirective = () => _ = DemoLiteralViolations("Probe.cs", "#if NET10_0\nclass Probe {}", vocabulary);
        brokenDirective.Should().Throw<InvalidOperationException>("unbalanced directives must remain syntax failures");
        AssertFixedOriginManifestDataFence(vocabulary);
        AssertFixedOriginManifestCallerFence();
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
        "course-key", "course-slug", "course-title", "lesson-slug", "lesson-title", "property-name", "field-label",
        "identity-username"
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
            .Except(vocabulary.PlatformValues, StringComparer.Ordinal)
            .Concat(SeedData.All.Select(tenant => "manage." + tenant.Host)).ToHashSet(StringComparer.Ordinal);
        return (Path.GetExtension(path) switch
        {
            ".json" => JsonValues(source),
            ".sql" => SqlLiteralValues(source),
            _ => CSharpValues(source, IsFixedOriginManifestPath(path)),
        })
            .Where(value => strings.Contains(value) || Guid.TryParse(value, out var id) && ids.Contains(id))
            .Distinct(StringComparer.Ordinal).ToArray();
    }

    // This exact-literal fence is deliberately not a SQL validator or arbitrary
    // dataflow analysis. The real migrations validate syntax. Dollar-quoted
    // function bodies are both literal values and recursively inspected SQL;
    // escape-string forms fail closed until their decoding is modeled.
    private static IEnumerable<string> SqlLiteralValues(string source)
    {
        foreach (Match match in SqlSourceTokens().Matches(source))
        {
            if (match.Groups["escape"].Success)
                throw new InvalidOperationException("Unmodeled SQL escape string; Fix: extend the decoded literal census before using it.");
            string value;
            if (match.Groups["dollar"].Success)
            {
                value = match.Groups["dollar"].Value;
                foreach (var nested in SqlLiteralValues(value)) yield return nested;
            }
            else if (match.Groups["value"].Success)
                value = match.Groups["value"].Value.Replace("''", "'", StringComparison.Ordinal);
            else continue;
            yield return value;
            string[] json;
            try { json = value.TrimStart().StartsWith('{') || value.TrimStart().StartsWith('[') ? JsonValues(value).ToArray() : []; }
            catch (JsonException) { json = []; }
            foreach (var nested in json) yield return nested;
        }
    }

    [GeneratedRegex(@"--[^\r\n]*|/\*[\s\S]*?\*/|(?<escape>\b[Ee]|[Uu]&)?'(?<value>(?:''|[^'])*)'|\$(?<tag>[A-Za-z_][A-Za-z0-9_]*|)\$(?<dollar>[\s\S]*?)\$\k<tag>\$", RegexOptions.CultureInvariant)]
    private static partial Regex SqlSourceTokens();

    private static IEnumerable<string> CSharpValues(string source, bool fixedOriginManifest = false)
    {
        foreach (var syntax in CSharpConditionalConfigurations(source))
        {
            var root = syntax.GetRoot();
            var declared = fixedOriginManifest ? FixedOriginManifestDataLiterals(root) : [];
            foreach (var node in root.DescendantNodes())
            {
                if (declared.Contains(node)) continue;
                var value = node switch
                {
                    LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression)
                        || literal.IsKind(SyntaxKind.Utf8StringLiteralExpression) => literal.Token.ValueText,
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
    }

    private const string FixedOriginManifestRelativePath =
        "Modules/Tenancy/LearnStack.Modules.Tenancy.Application.Contracts/Seeding/SeedAdministrationOriginManifest.cs";
    private const string FixedOriginManifestType =
        "LearnStack.Modules.Tenancy.Application.Contracts.Seeding.SeedAdministrationOriginManifest";

    private static bool IsFixedOriginManifestPath(string path) => string.Equals(Path.GetFullPath(path),
        Path.GetFullPath(Path.Combine(RepositoryPaths.BackendSrc(), FixedOriginManifestRelativePath)), StringComparison.Ordinal);

    // ADR-0079's fixed topology registry and ADR-0081's independently checked
    // source are data-only security declarations, not business specialization.
    // The production file stays in the census. Only these six validated syntax
    // nodes are omitted from literal matching; every other node remains a subject.
    private static HashSet<SyntaxNode> FixedOriginManifestDataLiterals(SyntaxNode root)
    {
        if (root is not CompilationUnitSyntax unit || unit.ContainsDirectives || unit.Externs.Count != 0
            || unit.AttributeLists.Count != 0 || unit.Members.Count != 1
            || !unit.Usings.Select(value => value.Name?.ToString() ?? string.Empty).Order(StringComparer.Ordinal)
                .SequenceEqual(new[] { "LearnStack.SharedKernel.Identifiers", "System.Collections.Immutable" }, StringComparer.Ordinal)
            || unit.Usings.Any(value => value.Alias is not null || value.StaticKeyword.RawKind != 0 || value.GlobalKeyword.RawKind != 0)
            || unit.Members[0] is not FileScopedNamespaceDeclarationSyntax space
            || space.Name.ToString() != "LearnStack.Modules.Tenancy.Application.Contracts.Seeding"
            || space.Members.Count != 1 || space.Usings.Count != 0 || space.Externs.Count != 0
            || space.Members[0] is not ClassDeclarationSyntax type || type.Identifier.ValueText != "SeedAdministrationOriginManifest"
            || type.Modifiers.Count != 2 || !type.Modifiers.Any(SyntaxKind.PublicKeyword) || !type.Modifiers.Any(SyntaxKind.StaticKeyword)
            || type.AttributeLists.Count != 0 || type.TypeParameterList is not null || type.BaseList is not null
            || type.ConstraintClauses.Count != 0 || type.ParameterList is not null || type.Members.Count != 2
            || type.Members[0] is not PropertyDeclarationSyntax all || all.Identifier.ValueText != "All"
            || !SyntaxFactory.AreEquivalent(all.WithInitializer(null).WithSemicolonToken(default), SyntaxFactory.ParseMemberDeclaration(
                "public static ImmutableArray<SeedAdministrationOriginDeclaration> All { get; }"))
            || all.Initializer?.Value is not CollectionExpressionSyntax values || values.Elements.Count != 2
            || type.Members[1] is not MethodDeclarationSyntax find
            || !SyntaxFactory.AreEquivalent(find, SyntaxFactory.ParseMemberDeclaration("""
                public static SeedAdministrationOriginDeclaration? Find(TenantId tenantId) =>
                    All.SingleOrDefault(declaration => declaration.TenantId == tenantId);
                """)))
            throw InvalidFixedOriginManifest();

        var expected = SeedData.All.ToArray();
        if (expected.Length != 2) throw InvalidFixedOriginManifest();
        var admitted = new HashSet<SyntaxNode>();
        for (var index = 0; index < expected.Length; index++)
        {
            if (values.Elements[index] is not ExpressionElementSyntax element
                || element.Expression is not ImplicitObjectCreationExpressionSyntax declaration
                || declaration.Initializer is not null || declaration.ArgumentList.Arguments.Count != 3
                || declaration.ArgumentList.Arguments.Any(argument => argument.NameColon is not null || argument.RefKindKeyword.RawKind != 0)
                || !FixedOriginInvocationArgument(declaration.ArgumentList.Arguments[0].Expression, "TenantId", "From", out var from)
                || !FixedOriginInvocationArgument(from!, "Guid", "Parse", out var parsed)
                || parsed is not LiteralExpressionSyntax id || !id.IsKind(SyntaxKind.StringLiteralExpression)
                || declaration.ArgumentList.Arguments[1].Expression is not LiteralExpressionSyntax slug
                || !slug.IsKind(SyntaxKind.StringLiteralExpression)
                || declaration.ArgumentList.Arguments[2].Expression is not LiteralExpressionSyntax host
                || !host.IsKind(SyntaxKind.StringLiteralExpression)
                || id.Token.ValueText != expected[index].TenantId.Value.ToString("D")
                || slug.Token.ValueText != expected[index].Slug || host.Token.ValueText != "manage." + expected[index].Host)
                throw InvalidFixedOriginManifest();
            admitted.Add(id);
            admitted.Add(slug);
            admitted.Add(host);
        }
        return admitted;
    }

    private static bool FixedOriginInvocationArgument(ExpressionSyntax expression, string receiver, string name,
        out ExpressionSyntax? argument)
    {
        argument = null;
        if (expression is not InvocationExpressionSyntax invocation
            || invocation.Expression is not MemberAccessExpressionSyntax member
            || member.Expression is not IdentifierNameSyntax owner || owner.Identifier.ValueText != receiver
            || member.Name is not IdentifierNameSyntax method || method.Identifier.ValueText != name
            || invocation.ArgumentList.Arguments.Count != 1 || invocation.ArgumentList.Arguments[0].NameColon is not null
            || invocation.ArgumentList.Arguments[0].RefKindKeyword.RawKind != 0) return false;
        argument = invocation.ArgumentList.Arguments[0].Expression;
        return true;
    }

    private static InvalidOperationException InvalidFixedOriginManifest() => new(
        "The fixed origin manifest must retain only its two exact data declarations and generic lookup; review ADR-0079/0081 before extending it.");

    private static void AssertFixedOriginManifestDataFence(DemoIdentities vocabulary)
    {
        var path = Path.Combine(RepositoryPaths.BackendSrc(), FixedOriginManifestRelativePath);
        var source = File.ReadAllText(path);
        var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
        var type = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Single();
        var all = type.Members.OfType<PropertyDeclarationSyntax>().Single();
        var values = (CollectionExpressionSyntax)all.Initializer!.Value;
        var firstId = SeedData.All[0].TenantId.Value.ToString("D");
        DemoLiteralViolations(path, source, vocabulary).Should().BeEmpty();
        DemoLiteralViolations(path, source + "\n// extra comment only", vocabulary).Should().BeEmpty();
        DemoLiteralViolations("CopiedManifest.cs", source, vocabulary).Should().NotBeEmpty("data admission is confined to the exact production path");
        var foreign = source.Replace(firstId, "01930000-0000-7000-8000-000000000099", StringComparison.Ordinal);
        var computed = source.Replace("Guid.Parse(\"" + firstId + "\")", "Guid.Parse(\"" + firstId + "\".ToString())", StringComparison.Ordinal);
        var extra = root.ReplaceNode(values, values.AddElements(values.Elements[0])).ToFullString();
        var branch = source.Replace("All.SingleOrDefault(declaration => declaration.TenantId == tenantId)",
            "tenantId == All[0].TenantId ? All[0] : All.SingleOrDefault(declaration => declaration.TenantId == tenantId)", StringComparison.Ordinal);
        var extraMember = root.ReplaceNode(type, type.AddMembers(SyntaxFactory.ParseMemberDeclaration(
            "public static bool Select(TenantId tenant) => tenant == All[0].TenantId;")!)).ToFullString();
        var extraLiteral = root.ReplaceNode(type, type.AddMembers(SyntaxFactory.ParseMemberDeclaration(
            "public static string Host => " + JsonSerializer.Serialize("manage." + SeedData.All[0].Host) + ";")!)).ToFullString();
        foreach (var dirty in new[] { foreign, computed, extra, branch, extraMember, extraLiteral,
            "#if FIXED_ORIGIN_CONTROL\n" + source + "\n#else\n" + extraMember + "\n#endif" })
        {
            dirty.Should().NotBe(source, "each planted control must change its actual input");
            Action inspect = () => _ = DemoLiteralViolations(path, dirty, vocabulary);
            inspect.Should().Throw<InvalidOperationException>("only the six verified data slots are admitted");
        }
        DemoLiteralViolations("OriginBranch.cs", "class Probe { bool Match(string host) => host == "
            + JsonSerializer.Serialize("manage." + SeedData.All[0].Host) + "; }", vocabulary).Should().ContainSingle(
                "the admitted management-host data cannot become a branch literal elsewhere");
    }

    private static string[] FixedOriginManifestReferenceOffenders(OriginManifestCensus census)
    {
        var observed = new List<string>();
        foreach (var method in census.Methods)
            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.Operand is not Mono.Cecil.MethodReference target || target.DeclaringType.FullName != FixedOriginManifestType) continue;
                var owner = census.Owner(method);
                // This one generated lambda implements the runner's fixed-selection
                // Any predicate. Count it with RunAsync; additional calls still fail.
                if (method.DeclaringType.FullName == "LearnStack.Tools.Seeder.SeedAdministrationOriginRunner/<>c"
                    && method.Name.StartsWith("<RunAsync>b__", StringComparison.Ordinal))
                    owner = "LearnStack.Tools.Seeder.SeedAdministrationOriginRunner::RunAsync";
                observed.Add(owner + "|" + target.Name + "|" + instruction.OpCode.Code);
            }
        var expected = new[]
        {
            FixedOriginManifestType + "::Find|get_All|Call",
            "LearnStack.Modules.Tenancy.Infrastructure.Persistence.SeedAdministrationOriginDdl::Install|get_All|Call",
            "LearnStack.Modules.Tenancy.Infrastructure.Persistence.SeedAdministrationOriginDdl::InstallQualifiedCompletion|get_All|Call",
            "LearnStack.Tools.Seeder.SeedAdministrationOriginRunner::ReadSelectionAsync|get_All|Call",
            "LearnStack.Modules.Tenancy.Application.Seeding.AdoptSeedAdministrationOriginCommandHandler::Handle|Find|Call",
            "LearnStack.Tools.Seeder.FixedSeedCompletionSource::Matches|Find|Call",
            "LearnStack.Tools.Seeder.FixedSeedAdministrationOriginAdmission::ForTenant|Find|Call",
            "LearnStack.Tools.Seeder.SeedAdministrationOriginRunner::RunAsync|Find|Call",
            "LearnStack.Tools.Seeder.SeedAdministrationOriginRunner::RunAsync|Find|Call",
            "LearnStack.Tools.Seeder.SeedAdministrationOriginRunner::ReadSelectionAsync|Find|Call",
        };
        return observed.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal)
            ? [] : ["Fixed origin All/Find callers, call kind or multiplicity changed: " + string.Join(", ", observed)];
    }

    private static void AssertFixedOriginManifestCallerFence()
    {
        using var census = OriginManifestCensus.Read();
        FixedOriginManifestReferenceOffenders(census).Should().BeEmpty();
        var api = census.Type("LearnStack.Api.Composition.PersistenceCompositionExtensions");
        foreach (var member in new[] { "get_All", "Find" })
            foreach (var opcode in new[] { OpCodes.Call, OpCodes.Ldftn })
            {
                var method = new Mono.Cecil.MethodDefinition("UnownedFixedOriginControl",
                    Mono.Cecil.MethodAttributes.Private | Mono.Cecil.MethodAttributes.Static, api.Module.TypeSystem.Void);
                method.Body.Instructions.Add(Instruction.Create(opcode, api.Module.ImportReference(census.Method(FixedOriginManifestType, member))));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                api.Methods.Add(method);
                try { FixedOriginManifestReferenceOffenders(census).Should().NotBeEmpty("ordinary API code cannot read or alias fixed origin pins"); }
                finally { api.Methods.Remove(method); }
                FixedOriginManifestReferenceOffenders(census).Should().BeEmpty();
            }
    }

    private static List<SyntaxTree> CSharpConditionalConfigurations(string source)
    {
        // Parse configurations independently: flattening branches can let an inactive
        // comment or raw literal swallow code compiled in another configuration.
        const int maximumSymbols = 8;
        var symbols = new SortedSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var names = symbols.ToArray();
            var trees = new List<SyntaxTree>();
            for (var mask = 0; mask < (1 << names.Length); mask++)
            {
                var defined = names.Where((_, index) => (mask & (1 << index)) != 0);
                var tree = CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithPreprocessorSymbols(defined));
                if (tree.GetDiagnostics().Any(row => row.Severity == DiagnosticSeverity.Error))
                    throw new InvalidOperationException("Unreadable production C#; Fix: restore syntax in every preprocessor configuration.");
                trees.Add(tree);
                foreach (var directive in tree.GetRoot().DescendantTrivia(descendIntoTrivia: true)
                    .Select(trivia => trivia.GetStructure()).OfType<DirectiveTriviaSyntax>())
                {
                    ExpressionSyntax? condition = directive switch
                    {
                        IfDirectiveTriviaSyntax value => value.Condition,
                        ElifDirectiveTriviaSyntax value => value.Condition,
                        _ => null
                    };
                    if (condition is not null)
                        foreach (var identifier in condition.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
                            symbols.Add(identifier.Identifier.ValueText);
                }
                if (symbols.Count > maximumSymbols)
                    throw new InvalidOperationException("Preprocessor census exceeds eight symbols; Fix: review this source explicitly before widening the bounded scan.");
            }
            // A configuration can expose directives hidden inside another branch's
            // comment. Discover symbols to a fixed point before accepting the census.
            if (names.Length == symbols.Count) return trees;
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
