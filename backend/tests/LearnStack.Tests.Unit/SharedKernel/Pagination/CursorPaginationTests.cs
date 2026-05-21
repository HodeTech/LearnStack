using FluentAssertions;
using LearnStack.SharedKernel.Pagination;
using Xunit;

namespace LearnStack.Tests.Unit.SharedKernel.Pagination;

public sealed class CursorPaginationTests
{
    [Fact]
    public void DefaultLimit_MatchesStandards04()
    {
        new CursorPagination().Limit.Should().Be(CursorPagination.DefaultLimit);
        CursorPagination.DefaultLimit.Should().Be(20);
        CursorPagination.MaxLimit.Should().Be(100);
    }

    [Fact]
    public void Normalised_LimitAboveMax_ClampsToMaxLimit()
    {
        var request = new CursorPagination(Limit: 500);

        request.Normalised().Limit.Should().Be(CursorPagination.MaxLimit);
    }

    [Fact]
    public void Normalised_LimitWithinBounds_ReturnsSameInstance()
    {
        var request = new CursorPagination(Cursor: "abc", Limit: 50);

        request.Normalised().Should().Be(request);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Normalised_LimitNonPositive_Throws(int limit)
    {
        var request = new CursorPagination(Limit: limit);

        var act = () => request.Normalised();

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
