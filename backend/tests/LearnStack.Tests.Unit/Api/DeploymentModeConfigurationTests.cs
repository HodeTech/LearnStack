using FluentAssertions;
using LearnStack.Api.Tenancy;
using LearnStack.SharedKernel.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LearnStack.Tests.Unit.Api;

/// <summary>
/// <c>Deployment:Mode</c> has no default, per
/// <see href="../../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036
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
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("Development,SaaS")]
    [InlineData(" ")]
    public void A_Numeric_Or_Composite_Mode_Refuses_To_Start(string raw)
    {
        // Enum.TryParse accepts ordinals and comma-separated lists, so
        // Deployment__Mode=0 would have parsed as Development — reintroducing
        // the exact silent default this method exists to remove, through a
        // value that looks like a typo rather than a mode.
        var act = () => Build(raw).RequireDeploymentMode();

        act.Should().Throw<InvalidOperationException>();
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

    [Fact]
    public void Ambient_Forwarded_Headers_Refuse_To_Start()
    {
        // ASPNETCORE_FORWARDEDHEADERS_ENABLED wires the forwarded-headers
        // middleware from host configuration — no code, no assembly reference —
        // ahead of everything, with KnownNetworks and KnownProxies cleared. That
        // makes RemoteIpAddress client-supplied, and it is the anonymous rate
        // limiter's partition key. Measured against the real host: seventy
        // requests rotating X-Forwarded-For produced zero 429s with the key set
        // and eleven without it.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [TenancyCompositionExtensions.ForwardedHeadersKey] = "true",
            })
            .Build();

        var act = configuration.RefuseAmbientForwardedHeaders;

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ForwardedHeaders_Enabled*rate limited*");
    }

    [Fact]
    public void An_Unset_Forwarded_Headers_Key_Starts_Normally()
    {
        var act = new ConfigurationBuilder().Build().RefuseAmbientForwardedHeaders;

        act.Should().NotThrow();
    }
}
