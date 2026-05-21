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

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Ctor_LimitNonPositive_Throws(int limit)
    {
        // The kernel-level guard belongs at construction so the invariant
        // cannot be skipped by callers that forget to call Normalised().
        var act = () => new CursorPagination(Limit: limit);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Normalised_LimitAboveMax_ClampsToMaxLimit()
    {
        var request = new CursorPagination(Limit: 500);

        request.Normalised().Limit.Should().Be(CursorPagination.MaxLimit);
    }

    [Fact]
    public void Normalised_LimitWithinBounds_ReturnsEquivalentInstance()
    {
        var request = new CursorPagination(Cursor: "abc", Limit: 50);

        var normalised = request.Normalised();

        normalised.Cursor.Should().Be(request.Cursor);
        normalised.Limit.Should().Be(request.Limit);
    }

    [Fact]
    public void Normalised_NeverThrows_BecauseCtorValidatesInput()
    {
        // After ctor validation, the only thing left to normalise is the
        // upper-bound clamp. Calling Normalised() on any constructed
        // instance is safe.
        var request = new CursorPagination(Limit: CursorPagination.MaxLimit + 1);

        var act = () => request.Normalised();

        act.Should().NotThrow();
    }
}
