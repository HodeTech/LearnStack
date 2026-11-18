using System.Net;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Tools.Seeder;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// ADR-0099 § Sign-in exposure: the anonymous, host-bound, no-store sign-in read. Without a live Active connection
/// every known host reads null; unknown and platform hosts keep the generic public refusals.
/// </summary>
[Collection(PublicReadTestGroup.Name)]
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class PublicSignInHttpTests(PublicReadFixture fixture)
{
    private const string Path = "/api/v1/public/sign-in";

    [Theory]
    [InlineData("GET", "missing.learnstack.local", "not_found")]
    [InlineData("GET", "localhost", "tenant_mismatch")]
    [InlineData("HEAD", "missing.learnstack.local", "not_found")]
    public async Task Unknown_and_platform_hosts_never_reach_the_sign_in_read(string method, string host, string code)
    {
        using var client = fixture.ClientFor(host);
        using var request = new HttpRequestMessage(new HttpMethod(method), Path);
        using var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Headers.CacheControl.Should().NotBeNull("an absent header must not pass the no-store check");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var body = await response.Content.ReadAsStringAsync();
        if (method == "HEAD") body.Should().BeEmpty();
        else JsonDocument.Parse(body).RootElement.GetProperty("code").GetString().Should().Be(code);
    }

    [Fact]
    public async Task A_known_host_without_an_Active_connection_reads_null_anonymously_and_refuses_query_parameters()
    {
        foreach (var tenant in SeedData.All)
        {
            using var client = fixture.ClientFor(tenant.Host);
            using var response = await client.GetAsync(Path);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Headers.CacheControl.Should().NotBeNull("an absent header must not pass the no-store check");
            response.Headers.CacheControl!.NoStore.Should().BeTrue();
            response.Headers.ETag.Should().BeNull();
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            body.EnumerateObject().Select(property => property.Name).Should().Equal("federation");
            body.GetProperty("federation").ValueKind.Should().Be(JsonValueKind.Null);
            using var query = await client.GetAsync(Path + "?alias=lsf-0");
            query.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
    }
}
