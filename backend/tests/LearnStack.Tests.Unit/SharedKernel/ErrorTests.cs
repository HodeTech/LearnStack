using FluentAssertions;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using Xunit;

namespace LearnStack.Tests.Unit.SharedKernel;

public sealed class ErrorTests
{
    [Fact]
    public void Code_StripsLockeyPrefix_FromMessageKey()
    {
        var error = new Error(LocalizedMessage.Of("lockey_forbidden"));

        error.Code.Should().Be("forbidden",
            "Standards 04 § Problem Details Code is the unprefixed stable identifier");
    }

    [Fact]
    public void Code_ForValidationFailed_MatchesStandards09Catalogue()
    {
        var error = new Error(LocalizedMessage.Of("lockey_validation_failed"));

        error.Code.Should().Be("validation_failed");
    }

    [Fact]
    public void Details_AreOptional()
    {
        var error = new Error(LocalizedMessage.Of("lockey_validation_failed"));

        error.Details.Should().BeNull();
    }

    [Fact]
    public void Details_CarryFieldLevelLocalizedMessages()
    {
        // Per the review: field-level errors must also flow as LocalizedMessages
        // so the lockey_ invariant covers every user-facing string the API ships.
        var details = new Dictionary<string, IReadOnlyList<LocalizedMessage>>
        {
            ["email"] =
            [
                LocalizedMessage.Of("lockey_email_required"),
                LocalizedMessage.Of("lockey_email_invalid"),
            ],
        };

        var error = new Error(
            LocalizedMessage.Of("lockey_validation_failed"),
            details);

        error.Details.Should().BeEquivalentTo(details);
        error.Details!["email"].Should().HaveCount(2);
    }
}
