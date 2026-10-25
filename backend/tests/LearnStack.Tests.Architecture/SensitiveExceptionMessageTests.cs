using FluentAssertions;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>Production regression controls for ADR-0032 Amendment 6's bounded LS0002 rule.</summary>
public sealed class SensitiveExceptionMessageTests
{
    [Theory]
    [InlineData("Debug")]
    [InlineData("Release")]
    public async Task Production_Exception_Messages_Do_Not_Contain_Sensitive_References(string configuration)
    {
        var projects = Directory.GetFiles(RepositoryPaths.BackendSrc(), "*.csproj", SearchOption.AllDirectories);
        projects.Should().NotBeEmpty("the production corpus must be discovered");
        var findings = new List<string>();
        foreach (var project in projects.Order(StringComparer.Ordinal))
        {
            var reports = await AnalyzerReport.SensitiveAsync(project, configuration);
            findings.AddRange(reports.Select(report => Path.GetFileName(project) + ": " + report));
        }

        findings.Should().BeEmpty(
            "Fix: replace sensitive constructor strings with fixed messages; suppressing LS0002 does not remove the value");
    }

    [Theory]
    [InlineData("Debug", "DEBUG")]
    [InlineData("Release", "NETCOREAPP")]
    public async Task The_Sensitive_Report_Sees_Suppressed_And_Conditional_Violations(string configuration, string symbol)
    {
        var source = $$"""
            namespace AnalyzerProof;
            internal static class Probe
            {
                internal static void Direct(string accessToken) => throw new System.Exception(accessToken);
                internal static void Silenced(string accessToken)
                {
            #pragma warning disable LS0002
                    throw new System.Exception(accessToken);
            #pragma warning restore LS0002
                }
            #if {{symbol}}
                internal static void Conditional(string accessToken) => throw new System.Exception($"{accessToken}");
            #endif
                internal static void Clean(string className) => throw new System.Exception(className);
                internal static System.Exception Factory(string accessToken) => new System.Exception(accessToken);
            }
            """;
        var project = Path.Combine(RepositoryPaths.BackendSrc(), "LearnStack.Api", "LearnStack.Api.csproj");
        var findings = (await AnalyzerReport.SensitiveAsync(project, configuration, source))
            .Where(finding => Path.GetFileName(finding.File) == "Planted0.cs").ToArray();

        findings.Select(finding => finding.Member).Should().BeEquivalentTo(["Direct", "Silenced", "Conditional"]);
        findings.Single(finding => finding.Member == "Silenced").Suppressed.Should().BeTrue();
    }

}
