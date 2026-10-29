using System.Text;
using LearnStack.Api.Common;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Common;

public sealed class AdministrationHttpInputTests
{
    [Theory]
    [InlineData("{\"displayName\":null,\"preferredLocale\":null}", true)]
    [InlineData("{\"preferredLocale\":\"tr\",\"displayName\":\"Example\"}", true)]
    [InlineData("{\"displayName\":null}", false)]
    [InlineData("{\"DisplayName\":null,\"preferredLocale\":null}", false)]
    [InlineData("{\"displayName\":null,\"preferredLocale\":null,\"other\":0}", false)]
    [InlineData("{\"displayName\":null,\"displayName\":\"later\",\"preferredLocale\":null}", false)]
    [InlineData("{\"displayName\":null,\"preferredLocale\":null,}", false)]
    [InlineData("[{\"displayName\":null,\"preferredLocale\":null}]", false)]
    [InlineData("null", false)]
    public async Task Exact_object_shape_preserves_missing_unknown_and_duplicate_evidence(string json, bool valid)
    {
        var http = Http(json);
        using var parsed = await AdministrationHttpInput.ReadObjectAsync(http, "displayName", "preferredLocale");
        Assert.Equal(valid, parsed is not null);
        Assert.Equal(0, http.Request.Body.Position);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Inclusive_UTF8_body_bound_counts_actual_bytes_even_without_content_length(bool declared)
    {
        var http = Http("{}" + new string(' ', AdministrationHttpInput.MaxBodyBytes - 2));
        http.Request.ContentLength = declared ? AdministrationHttpInput.MaxBodyBytes : null;
        using var accepted = await AdministrationHttpInput.ReadObjectAsync(http);
        Assert.NotNull(accepted);
        http = Http("{}" + new string(' ', AdministrationHttpInput.MaxBodyBytes - 2) + "é");
        http.Request.ContentLength = declared ? AdministrationHttpInput.MaxBodyBytes + 2 : null;
        var failure = await Assert.ThrowsAsync<BadHttpRequestException>(() => AdministrationHttpInput.ReadObjectAsync(http));
        Assert.Equal(413, failure.StatusCode);
        Assert.DoesNotContain("é", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, http.Request.Body.Position);
    }

    [Theory]
    [InlineData("application/json; charset=utf-8", true)]
    [InlineData("application/json; charset=\"utf-8\"", true)]
    [InlineData("application/json; charset=utf-16", false)]
    [InlineData("text/plain", false)]
    [InlineData("application/merge-patch+json", false)]
    public async Task Body_media_type_is_exact_UTF8_JSON(string type, bool valid)
    {
        var http = Http("{}");
        http.Request.ContentType = type;
        using var parsed = await AdministrationHttpInput.ReadObjectAsync(http);
        Assert.Equal(valid, parsed is not null);
    }

    [Fact]
    public async Task Invalid_UTF8_and_unpaired_escaped_surrogates_never_enter_text_fields()
    {
        var http = Http("{}");
        http.Request.Body = new MemoryStream([0x7b, 0x22, 0xff, 0x22, 0x3a, 0x30, 0x7d]);
        using var invalid = await AdministrationHttpInput.ReadObjectAsync(http);
        Assert.Null(invalid);
        http = Http("{\"displayName\":\"\\uD800\",\"preferredLocale\":null}");
        using var parsed = await AdministrationHttpInput.ReadObjectAsync(http, "displayName", "preferredLocale");
        Assert.NotNull(parsed);
        Assert.False(AdministrationHttpInput.TryText(parsed.RootElement, "displayName", true, 128, out _));
    }

    [Theory]
    [InlineData("?limit=101", true, 100)]
    [InlineData("?limit=20", true, 20)]
    [InlineData("?limit=020", false, 0)]
    [InlineData("?limit=0", false, 0)]
    [InlineData("?limit=+1", false, 0)]
    [InlineData("?limit=2147483648", false, 0)]
    [InlineData("?limit=1&limit=1", false, 0)]
    [InlineData("?cursor=", false, 0)]
    [InlineData("?cursor=abc&cursor=abc", false, 0)]
    [InlineData("?organizationId=foreign", false, 0)]
    [InlineData("?Limit=20", false, 0)]
    public void Pagination_accepts_only_single_canonical_bounded_fields(string query, bool valid, int expected)
    {
        var request = new DefaultHttpContext().Request;
        request.QueryString = new QueryString(query);
        Assert.Equal(valid, AdministrationHttpInput.TryPage(request, out _, out var limit));
        if (valid) Assert.Equal(expected, limit);
    }

    [Theory]
    [InlineData("018f4d40-0000-7000-8000-000000000011", true)]
    [InlineData("018F4D40-0000-7000-8000-000000000011", false)]
    [InlineData("018f4d40-0000-4000-8000-000000000011", false)]
    [InlineData("018f4d40-0000-7000-f000-000000000011", false)]
    [InlineData("00000000-0000-7000-8000-000000000001", false)]
    [InlineData("00000000-0000-7000-8000-000000000002", false)]
    [InlineData("{018f4d40-0000-7000-8000-000000000011}", false)]
    public void Identifiers_are_canonical_nonreserved_RFC_UUIDv7(string text, bool valid) =>
        Assert.Equal(valid, AdministrationHttpInput.TryId(text, out _));

    [Theory]
    [InlineData("{\"grants\":[]}", true)]
    [InlineData("{\"grants\":[{\"scope\":\"Tenant\",\"key\":\"identity.role.admin\"}]}", true)]
    [InlineData("{\"grants\":[{\"scope\":2,\"key\":\"identity.role.admin\"}]}", false)]
    [InlineData("{\"grants\":[{\"scope\":\"tenant\",\"key\":\"identity.role.admin\"}]}", false)]
    [InlineData("{\"grants\":[{\"scope\":\"Tenant\",\"key\":\"invalid\"}]}", false)]
    [InlineData("{\"grants\":[{\"scope\":\"Tenant\",\"key\":\"identity.role.admin\",\"isActive\":false}]}", false)]
    [InlineData("{\"grants\":[{\"scope\":\"Tenant\",\"scope\":\"Organization\",\"key\":\"identity.role.admin\"}]}", false)]
    [InlineData("{\"grants\":[{\"scope\":\"Tenant\",\"key\":\"identity.role.admin\"},{\"scope\":\"Tenant\",\"key\":\"identity.role.admin\"}]}", false)]
    public async Task Grant_pairs_have_exact_scope_and_unique_closed_object_shape(string json, bool valid)
    {
        using var parsed = await AdministrationHttpInput.ReadObjectAsync(Http(json), "grants");
        Assert.NotNull(parsed);
        Assert.Equal(valid, AdministrationHttpInput.TryGrants(parsed.RootElement, out _));
    }

    [Fact]
    public async Task Role_arrays_and_cursors_have_nonvacuous_upper_bounds()
    {
        var ids = Enumerable.Range(0, 9).Select(_ => "\"" + Guid.CreateVersion7().ToString("D") + "\"").ToArray();
        using var eight = await AdministrationHttpInput.ReadObjectAsync(Http("{\"roleIds\":[" + string.Join(',', ids.Take(8)) + "]}"), "roleIds");
        Assert.True(AdministrationHttpInput.TryRoles(eight!.RootElement, out var roles));
        Assert.Equal(8, roles.Length);
        using var nine = await AdministrationHttpInput.ReadObjectAsync(Http("{\"roleIds\":[" + string.Join(',', ids) + "]}"), "roleIds");
        Assert.False(AdministrationHttpInput.TryRoles(nine!.RootElement, out _));
        using var repeated = await AdministrationHttpInput.ReadObjectAsync(Http("{\"roleIds\":[" + ids[0] + "," + ids[0] + "]}"), "roleIds");
        Assert.False(AdministrationHttpInput.TryRoles(repeated!.RootElement, out _));
        var http = Http(string.Empty);
        http.Request.QueryString = new QueryString("?cursor=" + new string('a', 1024));
        Assert.True(AdministrationHttpInput.TryPage(http.Request, out _, out _));
        http.Request.QueryString = new QueryString("?cursor=" + new string('a', 1025));
        Assert.False(AdministrationHttpInput.TryPage(http.Request, out _, out _));
    }

    private static DefaultHttpContext Http(string body)
    {
        var http = new DefaultHttpContext();
        http.Request.ContentType = "application/json";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return http;
    }
}
