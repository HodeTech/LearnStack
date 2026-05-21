using FluentAssertions;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using Xunit;

namespace LearnStack.Tests.Unit.SharedKernel;

public sealed class ErrorTests
{
    [Fact]
    public void Code_DerivesFromMessageKey()
    {
        var error = new Error(LocalizedMessage.Of("lockey_forbidden"));

        error.Code.Should().Be("lockey_forbidden");
    }

    [Fact]
    public void Details_AreOptional()
    {
        var error = new Error(LocalizedMessage.Of("lockey_validation_failed"));

        error.Details.Should().BeNull();
    }

    [Fact]
    public void Details_RetainFieldErrors()
    {
        var details = new Dictionary<string, string[]>
        {
            ["email"] = ["lockey_email_invalid", "lockey_email_required"],
        };

        var error = new Error(
            LocalizedMessage.Of("lockey_validation_failed"),
            details);

        error.Details.Should().BeEquivalentTo(details);
    }
}
