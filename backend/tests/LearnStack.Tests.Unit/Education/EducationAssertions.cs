using FluentAssertions;
using LearnStack.SharedKernel.Results;

namespace LearnStack.Tests.Unit.Education;

internal static class EducationAssertions
{
    internal static void AssertFailure(Result<None> result, string code, string field, string reason)
    {
        result.IsFailure.Should().BeTrue();
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be(code);
        result.Error.Details.Should().NotBeNull();
        result.Error.Details!.Keys.Should().Equal(field);
        result.Error.Details[field].Should().ContainSingle().Which.Key.Should().Be(reason);
    }
}
