using System.Text;
using LearnStack.Api.Authentication;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Authentication;

public sealed class ConsentSubjectHttpInputTests
{
    private const string Id = "018f4d40-0000-7000-8000-000000000011";
    private const string Scopes = "/api/v1/tenancy/membership-field-consents/own/scopes";
    private const string Withdrawal = "/api/v1/tenancy/membership-field-consents/withdrawals";

    [Theory]
    [InlineData("?limit=0")]
    [InlineData("?limit=21")]
    [InlineData("?limit=01")]
    [InlineData("?limit=1&limit=1")]
    [InlineData("?membershipId=018f4d40-0000-7000-8000-000000000011")]
    [InlineData("?cursor=not-a-membership")]
    public async Task Discovery_rejects_noncanonical_or_scope_selecting_query(string query)
    {
        var http = Http(Scopes, query, "");
        Assert.Null(await ConsentSubjectHttpInput.ReadAsync(http, ConsentSubjectOperation.ForPath(Scopes)!));
    }

    [Fact]
    public async Task Captured_query_loses_liveness_when_changed()
    {
        var http = Http(Scopes, "", "");
        var input = await ConsentSubjectHttpInput.ReadAsync(http, ConsentSubjectOperation.ForPath(Scopes)!);
        Assert.NotNull(input);
        Assert.True(input.IsCurrent(http));
        http.Request.QueryString = new QueryString("?limit=1");
        Assert.False(input.IsCurrent(http));
    }

    [Theory]
    [InlineData("0", "true")]
    [InlineData("9007199254740992", "true")]
    [InlineData("1", "false")]
    [InlineData("1.5", "true")]
    public async Task Withdrawal_refuses_invalid_predecessor_or_confirmation(string sequence, string confirmed)
    {
        var body = $$"""{"evidenceId":"{{Id}}","expectedSequence":{{sequence}},"expectedEvidenceId":"{{Id}}","confirmed":{{confirmed}}}""";
        var http = Http(Withdrawal, $"?membershipId={Id}", body);
        Assert.Null(await ConsentSubjectHttpInput.ReadAsync(http, ConsentSubjectOperation.ForPath(Withdrawal)!));
    }

    [Fact]
    public async Task Withdrawal_rejects_duplicate_body_authority()
    {
        var body = $$"""{"evidenceId":"{{Id}}","expectedSequence":1,"expectedEvidenceId":"{{Id}}","confirmed":true,"confirmed":true}""";
        var http = Http(Withdrawal, $"?membershipId={Id}", body);
        Assert.Null(await ConsentSubjectHttpInput.ReadAsync(http, ConsentSubjectOperation.ForPath(Withdrawal)!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Withdrawal_enforces_four_kib_actual_bytes_with_or_without_length(bool declared)
    {
        var body = $$"""{"evidenceId":"{{Id}}","expectedSequence":1,"expectedEvidenceId":"{{Id}}","confirmed":true}""";
        var exact = body + new string(' ', 4096 - Encoding.UTF8.GetByteCount(body));
        var http = Http(Withdrawal, $"?membershipId={Id}", exact);
        http.Request.ContentLength = declared ? 4096 : null;
        Assert.NotNull(await ConsentSubjectHttpInput.ReadAsync(http, ConsentSubjectOperation.ForPath(Withdrawal)!));
        Assert.Equal(0, http.Request.Body.Position);
        http = Http(Withdrawal, $"?membershipId={Id}", exact + " ");
        http.Request.ContentLength = declared ? 4097 : null;
        var failure = await Assert.ThrowsAsync<BadHttpRequestException>(() => ConsentSubjectHttpInput.ReadAsync(http, ConsentSubjectOperation.ForPath(Withdrawal)!));
        Assert.Equal(413, failure.StatusCode);
    }

    [Fact]
    public async Task Withdrawal_rejects_invalid_utf8_even_if_json_parser_would_replace_it()
    {
        var prefix = Encoding.UTF8.GetBytes($"{{\"evidenceId\":\"");
        var suffix = Encoding.UTF8.GetBytes($"{Id}\",\"expectedSequence\":1,\"expectedEvidenceId\":\"{Id}\",\"confirmed\":true}}");
        var http = Http(Withdrawal, $"?membershipId={Id}", "");
        http.Request.Body = new MemoryStream([.. prefix, 0xff, .. suffix]);
        Assert.Null(await ConsentSubjectHttpInput.ReadAsync(http, ConsentSubjectOperation.ForPath(Withdrawal)!));
    }

    private static DefaultHttpContext Http(string path, string query, string body)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = path;
        http.Request.QueryString = new QueryString(query);
        http.Request.ContentType = "application/json";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return http;
    }
}
