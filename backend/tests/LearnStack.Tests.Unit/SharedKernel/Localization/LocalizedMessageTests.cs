using FluentAssertions;
using LearnStack.SharedKernel.Localization;
using Xunit;

namespace LearnStack.Tests.Unit.SharedKernel.Localization;

public sealed class LocalizedMessageTests
{
    [Fact]
    public void Ctor_WithLockeyPrefix_Constructs()
    {
        var message = new LocalizedMessage("lockey_validation_failed");

        message.Key.Should().Be("lockey_validation_failed");
        message.Params.Should().BeNull();
    }

    [Fact]
    public void Ctor_WithParams_RetainsThem()
    {
        var @params = new Dictionary<string, string> { ["field"] = "email" };

        var message = new LocalizedMessage("lockey_validation_required", @params);

        message.Params.Should().BeEquivalentTo(@params);
    }

    [Theory]
    [InlineData("validation_failed")]
    [InlineData("lockEY_wrong_case")]
    [InlineData("LOCKEY_uppercase")]
    [InlineData("error.something")]
    [InlineData(" lockey_leading_space")]
    public void Ctor_WithoutLockeyPrefix_Throws(string badKey)
    {
        var act = () => new LocalizedMessage(badKey);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*lockey_*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Ctor_WithEmptyOrWhitespaceKey_Throws(string key)
    {
        var act = () => new LocalizedMessage(key);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Of_IsEquivalentToCtor()
    {
        var @params = new Dictionary<string, string> { ["count"] = "5" };

        var direct = new LocalizedMessage("lockey_too_many", @params);
        var viaOf = LocalizedMessage.Of("lockey_too_many", @params);

        viaOf.Should().Be(direct);
    }
}
