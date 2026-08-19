using FluentAssertions;
using LearnStack.SharedKernel.Pagination;
using Xunit;

namespace LearnStack.Tests.Unit.SharedKernel;

/// <summary>
/// The <c>sort</c> grammar
/// <see href="../../../docs/standards/04-api-design.md">Standards 04
/// § Filtering and Sorting</see> specifies.
/// </summary>
public sealed class SortSpecificationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_Absent_Sort_Is_Empty_Not_An_Error(string? raw)
    {
        SortSpecification.TryParse(raw, out var specification, out var offending)
            .Should().BeTrue();

        specification.IsEmpty.Should().BeTrue();
        offending.Should().BeNull();
    }

    [Fact]
    public void A_Bare_Field_Sorts_Ascending()
    {
        SortSpecification.TryParse("title", out var specification, out _).Should().BeTrue();

        specification.Terms.Should().ContainSingle()
            .Which.Should().Be(new SortTerm("title", SortDirection.Ascending));
    }

    [Fact]
    public void A_Leading_Minus_Sorts_Descending()
    {
        SortSpecification.TryParse("-publishedAt", out var specification, out _).Should().BeTrue();

        specification.Terms.Should().ContainSingle()
            .Which.Should().Be(new SortTerm("publishedAt", SortDirection.Descending));
    }

    [Fact]
    public void Several_Keys_Keep_The_Order_The_Client_Gave()
    {
        // Standards 04's own example. Order is priority, so preserving it is
        // the whole contract — a set would answer the same question wrongly.
        SortSpecification.TryParse("-publishedAt,title", out var specification, out _)
            .Should().BeTrue();

        specification.Terms.Should().Equal(
            new SortTerm("publishedAt", SortDirection.Descending),
            new SortTerm("title", SortDirection.Ascending));
    }

    [Theory]
    [InlineData("title,", "trailing comma")]
    [InlineData("a,,b", "empty segment")]
    [InlineData("-", "a minus with no field")]
    [InlineData("1title", "field starting with a digit")]
    [InlineData("drop table", "whitespace inside a field")]
    [InlineData("title;drop", "punctuation")]
    [InlineData("title.", "trailing dot")]
    [InlineData(".title", "leading dot")]
    [InlineData("a..b", "empty path segment")]
    public void A_Malformed_Sort_Is_Refused_And_Names_The_Segment(string raw, string why)
    {
        SortSpecification.TryParse(raw, out _, out var offending)
            .Should().BeFalse(why);

        offending.Should().NotBeNull();
    }

    [Fact]
    public void A_Nested_Path_Is_Well_Formed()
    {
        SortSpecification.TryParse("author.name", out var specification, out _).Should().BeTrue();

        specification.Terms.Should().ContainSingle()
            .Which.Field.Should().Be("author.name");
    }

    [Fact]
    public void The_Same_Field_Twice_Is_Refused()
    {
        // Sorting by one field ascending and then descending is not an
        // ordering, it is a typo — and silently keeping the first would order
        // the page by something the client did not ask for.
        SortSpecification.TryParse("title,-title", out _, out var offending)
            .Should().BeFalse();

        offending.Should().NotBeNull();
    }

    [Fact]
    public void More_Keys_Than_The_Maximum_Are_Refused()
    {
        var raw = string.Join(",", Enumerable.Range(0, SortSpecification.MaxTerms + 1)
            .Select(index => "f" + index));

        SortSpecification.TryParse(raw, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Restrict_Passes_A_Permitted_Field_Through()
    {
        SortSpecification.TryParse("-publishedAt", out var specification, out _)
            .Should().BeTrue();

        var result = specification.Restrict(["publishedAt", "title"]);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Terms.Should().ContainSingle();
    }

    [Fact]
    public void Restrict_Fails_With_The_Field_Named_Under_sort()
    {
        SortSpecification.TryParse("secretColumn", out var specification, out _)
            .Should().BeTrue("the grammar is fine; only the allow-list rejects it");

        var result = specification.Restrict(["publishedAt", "title"]);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("validation_failed");
        result.Error.Details.Should().ContainKey("sort");
        result.Error.Details!["sort"].Should().ContainSingle()
            .Which.Params.Should().ContainKey("field")
            .WhoseValue.Should().Be("secretColumn");
    }

    [Fact]
    public void Restrict_Returns_The_Allow_Lists_Spelling_Not_The_Clients()
    {
        // The match is case-insensitive, so ?sort=PublishedAt is accepted.
        // Handing the handler "PublishedAt" would then give it a string the
        // endpoint never declared — and a handler that switches on the field
        // name, or builds an EF OrderBy from it, breaks on a spelling nobody
        // thought to test.
        SortSpecification.TryParse("PUBLISHEDat", out var specification, out _)
            .Should().BeTrue();

        var result = specification.Restrict(["publishedAt"]);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Terms.Should().ContainSingle()
            .Which.Field.Should().Be("publishedAt");
    }

    [Fact]
    public void Two_Specifications_Parsed_From_The_Same_String_Are_Equal()
    {
        // A record's generated Equals compares the Terms list by reference.
        SortSpecification.TryParse("-a,b", out var first, out _).Should().BeTrue();
        SortSpecification.TryParse("-a,b", out var second, out _).Should().BeTrue();

        first.Should().Be(second);
        first.GetHashCode().Should().Be(second.GetHashCode());
    }

    [Theory]
    [InlineData(SortSpecification.MaxTerms, true)]
    [InlineData(SortSpecification.MaxTerms + 1, false)]
    public void The_Term_Count_Boundary_Is_Exact(int count, bool accepted)
    {
        var raw = string.Join(",", Enumerable.Range(0, count).Select(i => "f" + i));

        SortSpecification.TryParse(raw, out _, out _).Should().Be(accepted);
    }

    [Theory]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public void The_Field_Length_Boundary_Is_Exact(int length, bool accepted)
    {
        var field = "a" + new string('b', length - 1);

        SortSpecification.TryParse(field, out _, out _).Should().Be(accepted);
    }

    [Fact]
    public void A_Duplicate_Differing_Only_In_Case_Is_Still_A_Duplicate()
    {
        // The only thing stopping two contradictory orderings of one column.
        SortSpecification.TryParse("title,-TITLE", out _, out var offending)
            .Should().BeFalse();

        offending.Should().NotBeNull();
    }

    [Fact]
    public void A_Malformed_Sort_Error_Names_The_Segment_And_Bounds_It()
    {
        var error = SortSpecification.MalformedSortError(new string('x', 200));

        error.Code.Should().Be("validation_failed");
        error.Details.Should().NotBeNull().And.ContainKey(SortSpecification.ErrorsKey);
        error.Details![SortSpecification.ErrorsKey].Should().ContainSingle()
            .Which.Params!["segment"].Length.Should().Be(64,
                "the raw value is attacker-controlled and reaches a log and an audit row");
    }

    [Fact]
    public void Restrict_Is_Case_Insensitive_About_The_Allow_List()
    {
        // Asserting the parse is load-bearing, not ceremony: TryParse sets
        // specification = Empty before it can fail, and Restrict on an empty
        // spec returns Ok — so without this line the test would pass while
        // having parsed nothing at all.
        SortSpecification.TryParse("PublishedAt", out var specification, out _)
            .Should().BeTrue();

        specification.Restrict(["publishedAt"]).IsSuccess.Should().BeTrue();
    }
}
