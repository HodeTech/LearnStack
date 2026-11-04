using LearnStack.Api.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Common;

public sealed class ProtectedEntityTagTests
{
    [Theory]
    [InlineData("\"0\"", 0L)]
    [InlineData("\"7\"", 7L)]
    [InlineData("\"9223372036854775807\"", long.MaxValue)]
    public void One_canonical_strong_decimal_tag_is_lossless(string tag, long expected)
    {
        var request = new DefaultHttpContext().Request;
        request.Headers.IfMatch = tag;
        Assert.True(ProtectedEntityTag.TryRead(request, out var version));
        Assert.Equal(expected, version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("W/\"7\"")]
    [InlineData("\"7\",\"8\"")]
    [InlineData(" \"7\"")]
    [InlineData("\"7\" ")]
    [InlineData("7")]
    [InlineData("\"07\"")]
    [InlineData("\"+7\"")]
    [InlineData("\"-1\"")]
    [InlineData("\"1.0\"")]
    [InlineData("\"1e1\"")]
    [InlineData("\"٧\"")]
    [InlineData("\"9223372036854775808\"")]
    public void Absent_weak_list_wildcard_or_noncanonical_tags_are_invalid(string? tag)
    {
        var request = new DefaultHttpContext().Request;
        if (tag is not null) request.Headers.IfMatch = tag;
        Assert.False(ProtectedEntityTag.TryRead(request, out _));
    }

    [Fact]
    public void Repeated_headers_are_invalid_even_when_their_values_agree()
    {
        var request = new DefaultHttpContext().Request;
        request.Headers.IfMatch = new StringValues(["\"7\"", "\"7\""]);
        Assert.False(ProtectedEntityTag.TryRead(request, out _));
        Assert.Equal(EntityTag.Precondition.AnyExisting, EvaluateGeneric("*"));
        Assert.Equal(EntityTag.Precondition.Matched, EvaluateGeneric("\"6\",\"7\""));
    }

    private static EntityTag.Precondition EvaluateGeneric(string tag)
    {
        var request = new DefaultHttpContext().Request;
        request.Headers.IfMatch = tag;
        return EntityTag.Evaluate(request, 7);
    }
}
