using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>Supporting structural controls for ADR-0032 Amendment 6's compiler contract.</summary>
public sealed class AnalyzerWiringTests
{
    private static readonly string[] EditorConfigNames = [".editorconfig", ".globalconfig"];

    [Theory]
    [InlineData("Debug")]
    [InlineData("Release")]
    public async Task Production_Projects_Run_The_Bounded_Exception_Analyzers(string configuration)
    {
        var projects = Directory.GetFiles(RepositoryPaths.BackendSrc(), "*.csproj", SearchOption.AllDirectories);
        projects.Should().NotBeEmpty();
        projects.Should().Contain(path => Path.GetFileName(path) == "LearnStack.Api.csproj");
        projects.Should().Contain(path => Path.GetFileName(path) == "LearnStack.Infrastructure.csproj");
        var domainProjects = AnalyzerReport.Projects().ToHashSet(StringComparer.Ordinal);
        foreach (var project in projects.Order(StringComparer.Ordinal))
        {
            var wiring = await AnalyzerProjectWiring.ReadAsync(project, configuration);
            wiring.HasAnalyzer(AnalyzerProjectWiring.AnalyzerPath).Should().BeTrue(
                "{0} must load exactly one in-tree analyzer reference without a runtime dependency", project);
            wiring.DomainRule.Should().Be(domainProjects.Contains(project),
                "LS0001 must retain its existing Domain/Application project set: {0}", project);
            wiring.VisibleScope.Should().BeTrue("the compiler must pass the scope to the analyzer: {0}", project);
            wiring.Exemptions.Should().Contain(["LS0001", "LS0002"],
                "both rules remain visible Warnings until Phase 03 exit: {0}", project);
            wiring.Suppressions.Should().NotContain(id => id.Equals("LS0002", StringComparison.OrdinalIgnoreCase),
                "LS0002 must remain visible: {0}", project);
            wiring.RunsAnalyzers.Should().BeTrue("the production compiler must run its analyzers: {0}", project);
        }
    }

    [Fact]
    public void The_Evaluated_Wiring_Control_Rejects_A_Library_Or_Duplicate_Reference()
    {
        var analyzer = AnalyzerProjectWiring.AnalyzerPath;
        var good = new AnalyzerProjectWiring.Reference(analyzer, "Analyzer", "false");
        var evaluation = new AnalyzerProjectWiring.Evaluation([good], false, true, ["LS0001", "LS0002"], [], true);
        evaluation.HasAnalyzer(analyzer).Should().BeTrue();
        (evaluation with { References = [] }).HasAnalyzer(analyzer).Should().BeFalse();
        (evaluation with { References = [good, good] }).HasAnalyzer(analyzer).Should().BeFalse();
        (evaluation with { References = [good, good with { OutputItemType = "" }] }).HasAnalyzer(analyzer).Should().BeFalse();
        (evaluation with { References = [good with { OutputItemType = "" }] }).HasAnalyzer(analyzer).Should().BeFalse();
        (evaluation with { References = [good with { ReferenceOutputAssembly = "true" }] }).HasAnalyzer(analyzer).Should().BeFalse();
    }

    [Fact]
    public void Production_Analyzer_Reference_Is_Unconditional_And_Cannot_Be_Silenced_By_Build_Settings()
    {
        var backend = Path.Combine(RepositoryPaths.RepoRoot(), "backend");
        AnalyzerProjectWiring.HasUnconditionalReference(XDocument.Load(Path.Combine(backend, "src", "Directory.Build.props")))
            .Should().BeTrue("Fix: keep the shared production analyzer reference and its ancestors unconditional");

        var settings = Directory.EnumerateFiles(RepositoryPaths.BackendSrc(), "*", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(backend, "*", SearchOption.TopDirectoryOnly))
            .Where(path => !Path.GetRelativePath(backend, path).Split(Path.DirectorySeparatorChar)
                .Any(segment => segment is "bin" or "obj"))
            .Where(path => Path.GetExtension(path) is ".csproj" or ".props" or ".targets"
                || Path.GetFileName(path) is ".editorconfig" or ".globalconfig")
            .Concat(EditorConfigNames
                .Select(name => Path.Combine(RepositoryPaths.RepoRoot(), name)).Where(File.Exists)).ToArray();
        settings.Should().NotBeEmpty();
        settings.Where(path => Path.GetExtension(path) is ".csproj" or ".props" or ".targets"
                ? AnalyzerProjectWiring.SuppressesSensitiveRule(XDocument.Load(path))
                : AnalyzerProjectWiring.SuppressesSensitiveRule(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(RepositoryPaths.RepoRoot(), path))
            .Order(StringComparer.Ordinal).Should().BeEmpty(
                "Fix: retain LS0002 as a visible Warning and keep production analyzer execution enabled (ADR-0032 Amendment 6)");
    }

    [Theory]
    [InlineData("<ItemGroup>{0}</ItemGroup>", true)]
    [InlineData("<ItemGroup Condition=\"'$(OS)' == 'Unix'\">{0}</ItemGroup>", false)]
    [InlineData("<ItemGroup Condition=\"'$(CI)' == 'true'\">{0}</ItemGroup>", false)]
    [InlineData("<Choose><When Condition=\"'$(CI)' == 'true'\"><ItemGroup>{0}</ItemGroup></When></Choose>", false)]
    public void The_Unconditional_Reference_Control_Rejects_Environment_Branches(string wrapper, bool expected)
    {
        const string reference = "<ProjectReference Include=\"../analyzers/LearnStack.Analyzers/LearnStack.Analyzers.csproj\" OutputItemType=\"Analyzer\" ReferenceOutputAssembly=\"false\" />";
        var xml = "<Project>" + wrapper.Replace("{0}", reference, StringComparison.Ordinal) + "</Project>";
        AnalyzerProjectWiring.HasUnconditionalReference(XDocument.Parse(xml)).Should().Be(expected);
        AnalyzerProjectWiring.HasUnconditionalReference(XDocument.Parse(xml.Replace("OutputItemType=",
            "Condition=\"'$(OS)' == 'Unix'\" OutputItemType=", StringComparison.Ordinal))).Should().BeFalse();
    }

    [Theory]
    [InlineData("<PropertyGroup><NoWarn>$(NoWarn);LS0002</NoWarn></PropertyGroup>")]
    [InlineData("<Target Name=\"Hide\" BeforeTargets=\"CoreCompile\"><PropertyGroup><RunAnalyzers>false</RunAnalyzers></PropertyGroup></Target>")]
    [InlineData("<PropertyGroup Condition=\"'$(CI)' == 'true'\"><RunAnalyzersDuringBuild>false</RunAnalyzersDuringBuild></PropertyGroup>")]
    [InlineData("<Target Name=\"Hide\" BeforeTargets=\"CoreCompile\"><ItemGroup><Analyzer Remove=\"@(Analyzer)\" /></ItemGroup></Target>")]
    [InlineData("<ItemGroup><Analyzer Remove=\"**/LearnStack.*.dll\" /></ItemGroup>")]
    [InlineData("<Target Name=\"Hide\" BeforeTargets=\"AssignProjectConfiguration\"><ItemGroup><ProjectReference Remove=\"**/LearnStack.Analyzers.csproj\" /></ItemGroup></Target>")]
    [InlineData("<Target Name=\"Hide\" BeforeTargets=\"AssignProjectConfiguration\"><ItemGroup><ProjectReference Remove=\"**/*.csproj\" /></ItemGroup></Target>")]
    [InlineData("<Target Name=\"Hide\" BeforeTargets=\"AssignProjectConfiguration\"><ItemGroup><ProjectReference Remove=\"$(MSBuildProjectDirectory)/../../analyzers/LearnStack.Analyzers/LearnStack.Analyzers.csproj\" /></ItemGroup></Target>")]
    [InlineData("<Target Name=\"Hide\" BeforeTargets=\"AssignProjectConfiguration\"><ItemGroup><ProjectReference Update=\"../analyzers/LearnStack.Analyzers/LearnStack.Analyzers.csproj\" OutputItemType=\"\" /></ItemGroup></Target>")]
    [InlineData("<Target Name=\"Hide\" BeforeTargets=\"AssignProjectConfiguration\"><ItemGroup><ProjectReference Remove=\"@(ProjectReference)\" Condition=\"'%(ProjectReference.Filename)' == 'LearnStack.Analyzers'\" /></ItemGroup></Target>")]
    [InlineData("<Target Name=\"Hide\" BeforeTargets=\"AssignProjectConfiguration\"><ItemGroup><ProjectReference Update=\"@(ProjectReference)\" Condition=\"'%(ProjectReference.Filename)' == 'LearnStack.Analyzers'\"><OutputItemType /></ProjectReference></ItemGroup></Target>")]
    public void The_Build_Setting_Control_Rejects_LS0002_Suppression(string body) =>
        AnalyzerProjectWiring.SuppressesSensitiveRule(XDocument.Parse("<Project>" + body + "</Project>")).Should().BeTrue();

    [Fact]
    public void Unrelated_Analyzer_And_Warning_Settings_Are_Allowed() =>
        AnalyzerProjectWiring.SuppressesSensitiveRule(XDocument.Parse("""
            <Project>
                <PropertyGroup><RunAnalyzers>true</RunAnalyzers><NoWarn>CA1707</NoWarn></PropertyGroup>
                <ItemGroup><Analyzer Remove="Unrelated.Analyzer.dll" /></ItemGroup>
                <ItemGroup><ProjectReference Remove="Unrelated.csproj" /><ProjectReference Update="Ordinary.csproj" PrivateAssets="all" /></ItemGroup>
                <ItemGroup><ProjectReference Remove="$(MSBuildProjectDirectory)/Unrelated.csproj" /></ItemGroup>
            </Project>
            """)).Should().BeFalse();

    [Theory]
    [InlineData("dotnet_diagnostic.LS0002.severity = none", true)]
    [InlineData("dotnet_diagnostic.LS0002.severity = silent", true)]
    [InlineData("dotnet_analyzer_diagnostic.category-Security.severity = none", true)]
    [InlineData("dotnet_analyzer_diagnostic.severity = none", true)]
    [InlineData("dotnet_diagnostic.LS0002.severity = warning", false)]
    [InlineData("# dotnet_diagnostic.LS0002.severity = none", false)]
    [InlineData("dotnet_diagnostic.CA1707.severity = none", false)]
    public void The_EditorConfig_Control_Rejects_LS0002_Suppression(string setting, bool expected) =>
        AnalyzerProjectWiring.SuppressesSensitiveRule("[*.cs]\n" + setting).Should().Be(expected);
}
