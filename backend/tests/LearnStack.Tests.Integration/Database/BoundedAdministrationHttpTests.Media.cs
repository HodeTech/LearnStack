using System.Net;
using System.Net.Http.Headers;
using System.Text;
using LearnStack.Tools.Seeder;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityAuthorizationHttpProof;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BoundedAdministrationHttpTests
{
    [Theory]
    [InlineData("text")]
    [InlineData("absent")]
    [InlineData("charset")]
    public async Task Completed_replay_preserves_the_same_JSON_media_boundary_as_a_new_mutation(string media)
    {
        await using var proof = await CreatePreparedAsync();
        using var client = proof.Client();
        var token = proof.Token(EnglishAdmin, Tenant);
        var path = "/api/v1/identity/memberships/" + SeedData.IdentityMemberships[1].MembershipId.ToString("D");
        var version = await ResourceVersionAsync(client, token, path);
        var key = Key();
        const string Body = "{\"displayName\":\"Media boundary canary\",\"preferredLocale\":\"en\"}";
        _ = await MutateAsync(client, token, "PUT", path + "/profile", Body, version, key);
        var stored = await StoredAsync(proof, key);
        Assert.NotNull(stored);

        using (var fresh = await SendMediaAsync(Key()))
            await ProblemAsync(fresh, HttpStatusCode.BadRequest, "validation_failed");
        using (var replay = await SendMediaAsync(key))
        {
            await ProblemAsync(replay, HttpStatusCode.BadRequest, "validation_failed");
            Assert.False(replay.Headers.Contains("Idempotency-Replayed"));
        }
        Assert.Equal(stored, await StoredAsync(proof, key));

        async Task<HttpResponseMessage> SendMediaAsync(string requestKey)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, path + "/profile");
            request.Headers.Host = Tenant.Host;
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("If-Match", "\"" + version + "\"");
            request.Headers.TryAddWithoutValidation("Idempotency-Key", requestKey);
            request.Content = new StringContent(Body, Encoding.UTF8, "application/json");
            request.Content.Headers.ContentType = media switch
            {
                "text" => new MediaTypeHeaderValue("text/plain"),
                "absent" => null,
                "charset" => new MediaTypeHeaderValue("application/json") { CharSet = "utf-16" },
                _ => throw new ArgumentOutOfRangeException(nameof(media)),
            };
            return await client.SendAsync(request);
        }
    }
}
