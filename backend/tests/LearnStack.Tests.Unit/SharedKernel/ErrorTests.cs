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
    public void Ctor_NullMessage_Throws()
    {
        var act = () => new Error(message: null!);

        act.Should().Throw<ArgumentNullException>();
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

    [Fact]
    public void Details_AreDefensivelyCopied()
    {
        // Caller mutation after construction must not leak into the Error;
        // Problem Details bodies, audit rows, and logs all read Details
        // after the handler returns, and divergent state across those
        // sinks would be a real bug.
        var emailErrors = new List<LocalizedMessage>
        {
            LocalizedMessage.Of("lockey_email_required"),
        };
        var details = new Dictionary<string, IReadOnlyList<LocalizedMessage>>
        {
            ["email"] = emailErrors,
        };

        var error = new Error(LocalizedMessage.Of("lockey_validation_failed"), details);

        // Mutate the caller's collections after the Error was constructed.
        emailErrors.Add(LocalizedMessage.Of("lockey_email_invalid"));
        details["password"] = [LocalizedMessage.Of("lockey_password_required")];

        error.Details!["email"].Should().HaveCount(1, "the snapshot was taken at ctor time");
        error.Details.ContainsKey("password").Should().BeFalse();
    }

    [Fact]
    public void Equality_IsStructural_AcrossDetails()
    {
        // Record equality on IReadOnlyDictionary defaults to reference
        // equality; the override compares Message + Details key-by-key.
        var detailsA = new Dictionary<string, IReadOnlyList<LocalizedMessage>>
        {
            ["email"] = [LocalizedMessage.Of("lockey_email_invalid")],
        };
        var detailsB = new Dictionary<string, IReadOnlyList<LocalizedMessage>>
        {
            ["email"] = [LocalizedMessage.Of("lockey_email_invalid")],
        };

        var a = new Error(LocalizedMessage.Of("lockey_validation_failed"), detailsA);
        var b = new Error(LocalizedMessage.Of("lockey_validation_failed"), detailsB);

        a.Equals(b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void Equality_DifferentDetails_AreNotEqual()
    {
        var a = new Error(
            LocalizedMessage.Of("lockey_validation_failed"),
            new Dictionary<string, IReadOnlyList<LocalizedMessage>>
            {
                ["email"] = [LocalizedMessage.Of("lockey_email_invalid")],
            });

        var b = new Error(
            LocalizedMessage.Of("lockey_validation_failed"),
            new Dictionary<string, IReadOnlyList<LocalizedMessage>>
            {
                ["email"] = [LocalizedMessage.Of("lockey_email_required")],
            });

        a.Equals(b).Should().BeFalse();
    }
}
