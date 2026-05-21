using FluentAssertions;
using LearnStack.SharedKernel.Pagination;
using Xunit;

namespace LearnStack.Tests.Unit.SharedKernel.Pagination;

public sealed class PageTests
{
    [Fact]
    public void Empty_HasNoItems_AndNoCursors()
    {
        Page<string>.Empty.Items.Should().BeEmpty();
        Page<string>.Empty.PageInfo.NextCursor.Should().BeNull();
        Page<string>.Empty.PageInfo.PreviousCursor.Should().BeNull();
        Page<string>.Empty.PageInfo.HasNext.Should().BeFalse();
        Page<string>.Empty.PageInfo.HasPrevious.Should().BeFalse();
    }

    [Fact]
    public void Constructed_PageCarriesItemsAndPageInfo()
    {
        var info = new PageInfo("next", "prev", HasNext: true, HasPrevious: true);

        var page = new Page<int>([1, 2, 3], info);

        page.Items.Should().BeEquivalentTo([1, 2, 3]);
        page.PageInfo.Should().Be(info);
    }
}
