using FluentAssertions;
using LearnStack.Api.Composition;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Composition;

/// <summary>What the platform credential may and may not turn on (ADR-0101 § 8).</summary>
public sealed class PlatformCredentialConnectionStringTests
{
    private const string Valid = "Host=localhost;Database=learnstack;Username=learnstack_platform;Password=synthetic-secret-canary"; // leakwatch:ignore

    [Fact]
    public void The_Ordinary_Credential_Is_Accepted()
    {
        var act = () => PersistenceCompositionExtensions.ValidatePlatformConnectionString(Valid);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("No Reset On Close=true")]
    [InlineData("Multiplexing=true")]
    public void A_Setting_That_Lets_Session_State_Outlive_A_Scope_Is_Refused(string setting)
    {
        // Session state set in one platform scope would reach the next on a connection that bypasses row
        // level security. The sibling privileged sources pin these off; this one refuses a string that
        // turns them on.
        var act = () => PersistenceCompositionExtensions.ValidatePlatformConnectionString(Valid + ";" + setting);

        act.Should().Throw<InvalidOperationException>().WithMessage("*No Reset On Close*");
    }
}
