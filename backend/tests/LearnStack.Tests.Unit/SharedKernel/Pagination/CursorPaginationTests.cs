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
        // Kernel-level programmer-error guard. The API-layer validation
        // turns malformed user input into validation_failed BEFORE the
        // kernel sees the request.
        var act = () => new CursorPagination(Limit: limit);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Ctor_LimitAboveMax_ClampsToMaxLimit()
    {
        // Construction is the single chokepoint for the invariant - any
        // CursorPagination instance is guaranteed to have Limit in
        // [1, MaxLimit] without callers having to remember a separate
        // normalisation step (the previous Normalised() method).
        var request = new CursorPagination(Limit: 500);

        request.Limit.Should().Be(CursorPagination.MaxLimit);
    }

    [Fact]
    public void Ctor_LimitEqualMax_IsAccepted()
    {
        // Off-by-one boundary: MaxLimit itself is legal, not above.
        var request = new CursorPagination(Limit: CursorPagination.MaxLimit);

        request.Limit.Should().Be(CursorPagination.MaxLimit);
    }

    [Fact]
    public void Ctor_LimitOne_IsAccepted()
    {
        var request = new CursorPagination(Limit: 1);

        request.Limit.Should().Be(1);
    }

    [Fact]
    public void Ctor_CarriesCursor()
    {
        var request = new CursorPagination(Cursor: "abc", Limit: 50);

        request.Cursor.Should().Be("abc");
        request.Limit.Should().Be(50);
    }

    [Fact]
    public void ObjectInitializer_AndWithExpression_PreserveValuesAndInvariants()
    {
        // The Limit invariant lives in the init accessor, not the
        // constructor, so it covers object-initializer syntax AND the
        // record's `with` expression - neither can bypass the guard the
        // ctor would otherwise be the only enforcer of.
        var fromInit = new CursorPagination { Cursor = "abc", Limit = 50 };
        var fromWith = fromInit with { Limit = 75 };

        fromInit.Cursor.Should().Be("abc");
        fromInit.Limit.Should().Be(50);

        fromWith.Cursor.Should().Be("abc");
        fromWith.Limit.Should().Be(75);
    }

    [Fact]
    public void ObjectInitializer_ZeroLimit_Throws()
    {
        var act = () => new CursorPagination { Limit = 0 };

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void WithExpression_ZeroLimit_Throws()
    {
        var request = new CursorPagination(Limit: 50);

        var act = () => request with { Limit = 0 };

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ObjectInitializer_AboveMaxLimit_Clamps()
    {
        var request = new CursorPagination { Limit = 500 };

        request.Limit.Should().Be(CursorPagination.MaxLimit);
    }
}
