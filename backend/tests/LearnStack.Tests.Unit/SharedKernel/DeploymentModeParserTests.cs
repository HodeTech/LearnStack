using FluentAssertions;
using LearnStack.SharedKernel.Hosting;
using Xunit;

namespace LearnStack.Tests.Unit.SharedKernel;

/// <summary>The one reading of a deployment mode that the API and the offline tools share.</summary>
public sealed class DeploymentModeParserTests
{
    [Theory]
    [InlineData("Development", DeploymentMode.Development)]
    [InlineData("saas", DeploymentMode.SaaS)]
    [InlineData("  Dedicated ", DeploymentMode.Dedicated)]
    [InlineData("SELFHOSTEDONLINE", DeploymentMode.SelfHostedOnline)]
    [InlineData("selfHostedAirGapped", DeploymentMode.SelfHostedAirGapped)]
    public void A_Declared_Name_Is_Read_Without_Regard_To_Case_Or_Padding(string text, DeploymentMode expected)
    {
        DeploymentModeParser.TryParse(text, out var mode).Should().BeTrue();

        mode.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("-1")]
    [InlineData("Development,SaaS")]
    [InlineData("Development SaaS")]
    [InlineData("Production")]
    [InlineData("Sass")]
    public void Anything_That_Is_Not_A_Declared_Name_Is_Refused_Ordinals_And_Lists_Included(string? text)
    {
        DeploymentModeParser.TryParse(text, out _).Should().BeFalse();
    }

    [Fact]
    public void Every_Declared_Name_Is_Accepted_As_It_Is_Spelled()
    {
        foreach (var name in Enum.GetNames<DeploymentMode>())
        {
            DeploymentModeParser.TryParse(name, out var mode).Should().BeTrue(name);
            mode.ToString().Should().Be(name);
        }
    }
}
