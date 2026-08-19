using FluentAssertions;
using LearnStack.Api.Tenancy;
using LearnStack.SharedKernel.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LearnStack.Tests.Unit.Api;

/// <summary>
/// <c>Deployment:Mode</c> has no default, per
/// <see href="../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036
/// § There is no Development override</see>.
/// </summary>
public sealed class DeploymentModeConfigurationTests
{
    [Fact]
    public void An_Absent_Mode_Refuses_To_Start()
    {
        // The defect this replaces: the key shipped as "Development" in
        // appsettings.json — the file that goes to every environment — with the
        // same value as the code default, while appsettings.Development.json
        // set none. Every Development-guarded mechanism was therefore on by
        // default in a deployment that never configured it, and no amount of
        // guarding on the value could have caught it.
        var configuration = new ConfigurationBuilder().Build();

        var act = () => configuration.RequireDeploymentMode();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Deployment:Mode*not configured*");
    }

    [Fact]
    public void An_Unknown_Mode_Refuses_To_Start_And_Names_The_Valid_Ones()
    {
        var configuration = Build("Sass");

        var act = () => configuration.RequireDeploymentMode();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*SaaS*", "the message has to make the typo obvious");
    }

    [Theory]
    [InlineData("Development", DeploymentMode.Development)]
    [InlineData("saas", DeploymentMode.SaaS)]
    [InlineData("SELFHOSTEDAIRGAPPED", DeploymentMode.SelfHostedAirGapped)]
    public void A_Configured_Mode_Parses_Case_Insensitively(string raw, DeploymentMode expected) =>
        Build(raw).RequireDeploymentMode().Should().Be(expected);

    private static IConfiguration Build(string mode) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [TenancyCompositionExtensions.DeploymentModeKey] = mode,
            })
            .Build();
}
