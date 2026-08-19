using FluentAssertions;
using LearnStack.Api.Common;
using Xunit;

namespace LearnStack.Tests.Unit.Api;

/// <summary>
/// <see cref="HttpStatusMap.CanonicalCodeFor(int)"/> defines the wire
/// <c>code</c> for every client error no handler produced, so it is part of the
/// published contract the generated SDK routes on. It shipped with no test.
/// </summary>
public sealed class HttpStatusMapCanonicalCodeTests
{
    [Theory]
    [InlineData(400, "validation_failed")]
    [InlineData(401, "unauthorized")]
    [InlineData(403, "forbidden")]
    [InlineData(404, "not_found")]
    [InlineData(405, "method_not_allowed")]
    [InlineData(409, "concurrency_conflict")]
    [InlineData(413, "payload_too_large")]
    [InlineData(415, "unsupported_media_type")]
    [InlineData(422, "validation_failed")]
    [InlineData(429, "rate_limited")]
    [InlineData(503, "dependency_unavailable")]
    public void CanonicalCodeFor_Returns_The_Published_Code(int status, string expected) =>
        HttpStatusMap.CanonicalCodeFor(status).Should().Be(expected);

    [Theory]
    [InlineData(402)]
    [InlineData(410)]
    [InlineData(418)]
    [InlineData(451)]
    public void An_Unlisted_Client_Error_Is_Never_Reported_As_A_Server_Error(int status)
    {
        // The fallback used to be `internal_error`, which put a code and a
        // status that contradict each other in the same body — 418 arriving as
        // "internal_error" tells an SDK the server broke when the client did.
        HttpStatusMap.CanonicalCodeFor(status).Should().Be("request_rejected");
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    public void A_Server_Error_Falls_Back_To_internal_error(int status) =>
        HttpStatusMap.CanonicalCodeFor(status).Should().Be("internal_error");

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(405)]
    [InlineData(409)]
    [InlineData(413)]
    [InlineData(415)]
    [InlineData(429)]
    [InlineData(503)]
    public void Every_Canonical_Code_Round_Trips_To_Its_Own_Status(int status)
    {
        // The two directions are not inverses — several codes share a status,
        // so code→status is many-to-one. What must hold is that a code minted
        // from a status maps back to that status, or the body would carry a
        // `code` the forward map disagrees with. `api_version_sunset` for 410
        // failed exactly this: nothing else defined it and For(string) sent it
        // to 500.
        var code = HttpStatusMap.CanonicalCodeFor(status);

        HttpStatusMap.For(code).Should().Be(status,
            "code '{0}' was minted from status {1}", code, status);
    }

    [Theory]
    [InlineData(400, "validation_failed")]
    [InlineData(413, "payload_too_large")]
    [InlineData(418, "request_rejected")]
    public void A_Framework_Rejection_Never_Reports_Itself_As_A_Server_Error(
        int status, string expected)
    {
        // BadHttpRequestException is what Kestrel throws for an oversized body
        // or a malformed chunk, and it carries the status it decided on.
        // Honouring that status without also minting the code from it produced
        // `status: 413` beside `code: "internal_error"` — the two halves of one
        // body contradicting each other, which is the exact failure
        // CanonicalCodeFor exists to prevent.
        var problem = ProblemDetailsFactory.For(
            new Microsoft.AspNetCore.Http.BadHttpRequestException("too big", status));

        problem.Status.Should().Be(status);
        problem.Extensions["code"].Should().Be(expected);
        problem.Extensions["messageKey"].Should().Be("lockey_" + expected);
    }

    [Fact]
    public void An_Ordinary_Unhandled_Exception_Is_Still_internal_error()
    {
        var problem = ProblemDetailsFactory.For(new InvalidOperationException("boom"));

        problem.Status.Should().Be(500);
        problem.Extensions["code"].Should().Be("internal_error");
    }

    [Fact]
    public void Status_422_Is_The_One_Deliberate_Asymmetry()
    {
        // 422 mints `validation_failed`, which maps forward to 400. Standards
        // 04 § Status Codes calls 422 "rarely needed; prefer 400", so the two
        // share one code on purpose — and the round-trip test above excludes
        // it rather than pretending it holds.
        HttpStatusMap.CanonicalCodeFor(422).Should().Be("validation_failed");
        HttpStatusMap.For("validation_failed").Should().Be(400);
    }
}
