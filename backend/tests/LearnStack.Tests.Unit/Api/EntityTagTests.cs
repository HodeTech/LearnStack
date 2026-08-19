using FluentAssertions;
using LearnStack.Api.Common;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LearnStack.Tests.Unit.Api;

/// <summary>
/// <c>ETag</c> / <c>If-Match</c> optimistic concurrency, per
/// <see href="../../../../docs/standards/04-api-design.md">Standards 04
/// § Optimistic Concurrency</see>.
/// </summary>
public sealed class EntityTagTests
{
    [Fact]
    public void A_Version_Formats_As_A_Quoted_Strong_Tag()
    {
        // The quoting is not decoration: an unquoted value is not a valid
        // entity tag, and a client echoing it back produces a header the
        // server cannot parse.
        EntityTag.For(7).Should().Be("\"7\"");
    }

    [Fact]
    public void No_If_Match_Is_Absent_Not_Failed()
    {
        Evaluate(ifMatch: null, currentVersion: 7)
            .Should().Be(EntityTag.Precondition.Absent);
    }

    [Fact]
    public void A_Matching_Tag_Matches()
    {
        Evaluate("\"7\"", 7).Should().Be(EntityTag.Precondition.Matched);
    }

    [Fact]
    public void A_Different_Version_Fails()
    {
        Evaluate("\"6\"", 7).Should().Be(EntityTag.Precondition.Failed);
    }

    [Fact]
    public void One_Match_In_A_List_Is_Enough()
    {
        Evaluate("\"5\", \"6\", \"7\"", 7).Should().Be(EntityTag.Precondition.Matched);
    }

    [Fact]
    public void A_Star_Means_Any_Existing_Version()
    {
        Evaluate("*", 7).Should().Be(EntityTag.Precondition.AnyExisting);
    }

    [Fact]
    public void A_Weak_Tag_Never_Matches()
    {
        // RFC 9110 § 13.1.1 requires strong comparison for If-Match, and it is
        // the only comparison that means anything here: a weak tag says
        // "semantically equivalent", and two versions of a row that are
        // semantically equivalent are still two versions — one of which the
        // client did not see.
        Evaluate("W/\"7\"", 7).Should().Be(EntityTag.Precondition.Failed);
    }

    [Theory]
    [InlineData("7", "unquoted")]
    [InlineData("\"7", "unterminated")]
    [InlineData("garbage", "not a tag at all")]
    public void A_Malformed_Header_Fails_Rather_Than_Counting_As_Absent(string raw, string why)
    {
        // Treating "I could not parse your precondition" as "you did not send
        // one" turns a conditional write into an unconditional one — the exact
        // overwrite the client was trying to prevent.
        Evaluate(raw, 7).Should().Be(EntityTag.Precondition.Failed, why);
    }

    [Theory]
    [InlineData("\"7\", ")]
    [InlineData(", \"7\"")]
    public void A_Trailing_Or_Leading_Comma_Is_Legal_And_Still_Matches(string raw)
    {
        // Not a malformed header, which a first version of this test assumed.
        // RFC 9110 § 5.6.1 requires a recipient to accept and ignore empty
        // list elements, so `"7", ` is the one-element list `["7"]`.
        Evaluate(raw, 7).Should().Be(EntityTag.Precondition.Matched);
    }

    [Fact]
    public void The_Conflict_Is_The_Code_Standards_04_Names()
    {
        var error = EntityTag.ConflictError();

        error.Code.Should().Be("concurrency_conflict");
        HttpStatusMap.For(error).Should().Be(409,
            "Standards 04 § Optimistic Concurrency: a version mismatch is 409");
    }

    [Fact]
    public void SetEntityTag_Writes_The_Quoted_Form()
    {
        var context = new DefaultHttpContext();

        EntityTag.SetEntityTag(context.Response, 42);

        context.Response.Headers.ETag.ToString().Should().Be("\"42\"");
    }

    private static EntityTag.Precondition Evaluate(string? ifMatch, long currentVersion)
    {
        var context = new DefaultHttpContext();
        if (ifMatch is not null)
        {
            context.Request.Headers.IfMatch = ifMatch;
        }

        return EntityTag.Evaluate(context.Request, currentVersion);
    }
}
