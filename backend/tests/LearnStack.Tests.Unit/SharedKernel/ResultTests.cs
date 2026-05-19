using FluentAssertions;
using LearnStack.SharedKernel.Results;
using Xunit;

namespace LearnStack.Tests.Unit.SharedKernel;

public sealed class ResultTests
{
    [Fact]
    public void Ok_WhenGivenValue_ReturnsSuccessResult()
    {
        var result = Result<int>.Ok(42);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
        result.Error.Should().BeNull();
    }

    [Fact]
    public void Fail_WhenGivenError_ReturnsFailureResult()
    {
        var error = new Error("test_error", "boom");

        var result = Result<int>.Fail(error);

        result.IsSuccess.Should().BeFalse();
        result.Value.Should().Be(default);
        result.Error.Should().Be(error);
    }
}
