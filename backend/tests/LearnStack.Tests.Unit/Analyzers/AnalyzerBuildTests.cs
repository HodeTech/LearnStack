using FluentAssertions;
using Xunit;

namespace LearnStack.Tests.Unit.Analyzers;

/// <summary>
/// Exercises the actual production props and compiler-visible LS0001 scope in disposable
/// builds. Every probe is a new project with no handwritten analyzer reference.
/// </summary>
public sealed class AnalyzerBuildTests
{
    private static readonly (string Layer, bool DomainDiagnostic)[] Layers =
    [
        ("Domain", true),
        ("Application", true),
        ("Api", false),
        ("Infrastructure", false),
        ("Application.Contracts", false),
        ("SharedKernel", false),
        ("Seeder", false),
    ];

    [Fact]
    public async Task New_Production_Projects_Receive_LS0002_And_Preserve_LS0001_Scope()
    {
        using var build = new AnalyzerBuildSandbox();

        foreach (var (layer, domainDiagnostic) in Layers)
        {
            var project = build.CreateProductionProject(layer);
            await build.RestoreAsync(project);

            var result = await build.BuildAsync(project);

            result.ExitCode.Should().Be(0, "the real CI build of {0} must succeed: {1}", layer, result.Summary);
            result.HasDiagnostic("warning", "LS0002").Should().BeTrue(
                "the new {0} project must automatically receive the analyzer: {1}", layer, result.Summary);
            result.HasDiagnostic("warning", "LS0001").Should().Be(domainDiagnostic,
                "the compiler-visible production scope must govern {0}: {1}", layer, result.Summary);
            result.HasDiagnostic("error", "LS0001").Should().BeFalse(result.Summary);
            result.HasDiagnostic("error", "LS0002").Should().BeFalse(result.Summary);
            result.HasDiagnostic("warning", "AD0001").Should().BeFalse(result.Summary);
            result.HasDiagnostic("error", "AD0001").Should().BeFalse(result.Summary);
        }
    }

    [Fact]
    public async Task CI_Build_Allows_LS0002_Warning_But_Rejects_An_Unrelated_Compiler_Warning()
    {
        using var build = new AnalyzerBuildSandbox();
        var project = build.CreateProductionProject("Api");
        await build.RestoreAsync(project);

        var baseline = await build.BuildAsync(project);

        baseline.ExitCode.Should().Be(0, "LS0002 must remain a Warning with CI=true: {0}", baseline.Summary);
        baseline.HasDiagnostic("warning", "LS0002").Should().BeTrue(baseline.Summary);

        AnalyzerBuildSandbox.AddOrdinaryCompilerWarning(project);
        var control = await build.BuildAsync(project);

        control.ExitCode.Should().NotBe(0, "an unrelated compiler warning must still fail CI: {0}", control.Summary);
        control.HasDiagnostic("error", "CS0219").Should().BeTrue(
            "the unused-local warning must be promoted by the actual CI props: {0}", control.Summary);
        control.HasDiagnostic("warning", "LS0002").Should().BeTrue(control.Summary);
        control.HasDiagnostic("error", "LS0002").Should().BeFalse(control.Summary);
    }

    [Fact]
    public async Task Clean_Production_And_Excluded_Project_Trees_Stay_Diagnostic_Free()
    {
        using var build = new AnalyzerBuildSandbox();
        var projects = new[]
        {
            build.CreateProductionProject("Api", violation: false),
            build.CreateExcludedProject("tests"),
            build.CreateExcludedProject("analyzers"),
        };
        foreach (var project in projects)
        {
            await build.RestoreAsync(project);
            var result = await build.BuildAsync(project);
            result.ExitCode.Should().Be(0, result.Summary);
            result.Diagnostics.Should().BeEmpty("clean production code and excluded trees must stay clean: {0}", result.Summary);
        }
    }
}
