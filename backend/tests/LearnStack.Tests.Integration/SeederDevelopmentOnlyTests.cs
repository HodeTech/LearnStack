using System.Diagnostics;
using FluentAssertions;
using LearnStack.Tools.Seeder;
using Xunit;

namespace LearnStack.Tests.Integration;

/// <summary>
/// The seeder is a development capability (ADR-0102 § 10): started anywhere but a Development
/// deployment it refuses before it reads a credential or opens a connection.
/// </summary>
/// <remarks>
/// No Docker and no database: the process is given no connection string at all, so a run that did anything
/// but refuse would exit with the missing-credential code instead.
/// </remarks>
public sealed class SeederDevelopmentOnlyTests
{
    [Theory]
    [InlineData("Production")]
    [InlineData("Dedicated")]
    [InlineData("SelfHosted")]
    [InlineData("development")]
    [InlineData("")]
    [InlineData(null)]
    public async Task The_Seeder_Refuses_Outside_A_Development_Deployment_Before_Reading_A_Credential(string? mode)
    {
        var (exit, errors) = await RunAsync(mode);

        exit.Should().Be(2);
        errors.Should().Contain("only when Deployment__Mode is Development").And.Contain("operator surface");
    }

    [Fact]
    public async Task In_A_Development_Deployment_The_Refusal_Is_Not_The_Answer()
    {
        // With the mode right and no credential the seeder reaches its own missing-credential exit, which is
        // how this case shows the refusal is the mode's and not a blanket one.
        var (exit, errors) = await RunAsync("Development");

        exit.Should().Be(2);
        errors.Should().NotContain("only when Deployment__Mode is Development");
        errors.Should().Contain("ConnectionStrings__Default");
    }

    private static async Task<(int Exit, string Errors)> RunAsync(string? mode)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add("--runtimeconfig");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "LearnStack.Tests.Integration.runtimeconfig.json"));
        start.ArgumentList.Add("--depsfile");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "LearnStack.Tests.Integration.deps.json"));
        start.ArgumentList.Add(typeof(SeedRunner).Assembly.Location);
        start.Environment.Remove("ConnectionStrings__Default");
        start.Environment.Remove("Deployment__Mode");
        if (mode is not null)
        {
            start.Environment["Deployment__Mode"] = mode;
        }

        using var process = new Process { StartInfo = start };
        process.Start().Should().BeTrue();
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }

        await Task.WhenAll(output, errors);
        return (process.ExitCode, await errors);
    }
}
