using FluentAssertions;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>Supporting structural controls for ADR-0032 Amendment 6's compiler contract.</summary>
public sealed class AnalyzerWiringTests
{
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
        }
    }

    [Fact]
    public void The_Evaluated_Wiring_Control_Rejects_A_Library_Or_Duplicate_Reference()
    {
        var analyzer = AnalyzerProjectWiring.AnalyzerPath;
        var good = new AnalyzerProjectWiring.Reference(analyzer, "Analyzer", "false");
        var evaluation = new AnalyzerProjectWiring.Evaluation([good], false, true, ["LS0001", "LS0002"]);
        evaluation.HasAnalyzer(analyzer).Should().BeTrue();
        (evaluation with { References = [] }).HasAnalyzer(analyzer).Should().BeFalse();
        (evaluation with { References = [good, good] }).HasAnalyzer(analyzer).Should().BeFalse();
        (evaluation with { References = [good, good with { OutputItemType = "" }] }).HasAnalyzer(analyzer).Should().BeFalse();
        (evaluation with { References = [good with { OutputItemType = "" }] }).HasAnalyzer(analyzer).Should().BeFalse();
        (evaluation with { References = [good with { ReferenceOutputAssembly = "true" }] }).HasAnalyzer(analyzer).Should().BeFalse();
    }
}
