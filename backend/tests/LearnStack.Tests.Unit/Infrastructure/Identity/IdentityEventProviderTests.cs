using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Infrastructure.Identity.ProviderEvents;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Resilience;
using LearnStack.SharedKernel.Secrets;
using Polly;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Identity;

public sealed partial class IdentityEventProviderTests
{
    private const string Issuer = "https://provider.invalid/realms/learnstack";
    private static readonly IdentityAuthorityId Authority = IdentityAuthorityId.From(Guid.CreateVersion7());
    private static readonly DateTimeOffset At = new(2026, 11, 11, 10, 0, 0, TimeSpan.Zero);
    private static readonly string[] Codes = ["LOGIN", "LOGIN_ERROR", "LOGOUT", "LOGOUT_ERROR", "SEND_RESET_PASSWORD", "SEND_RESET_PASSWORD_ERROR",
        "RESET_PASSWORD", "RESET_PASSWORD_ERROR", "UPDATE_CREDENTIAL", "UPDATE_CREDENTIAL_ERROR", "REMOVE_CREDENTIAL", "REMOVE_CREDENTIAL_ERROR",
        "UPDATE_PASSWORD", "UPDATE_PASSWORD_ERROR", "UPDATE_TOTP", "UPDATE_TOTP_ERROR", "REMOVE_TOTP", "REMOVE_TOTP_ERROR",
        "USER_DISABLED_BY_TEMPORARY_LOCKOUT", "USER_DISABLED_BY_PERMANENT_LOCKOUT"];

    [Fact]
    public async Task Bounded_page_preserves_source_positions_and_drops_raw_personal_details()
    {
        const string subject = "provider-subject-privacy-canary";
        var body = JsonSerializer.Serialize(new object[]
        {
            new { id = Guid.NewGuid(), type = "LOGIN_ERROR", realmId = "learnstack", time = At.ToUnixTimeMilliseconds(), userId = subject,
                ipAddress = "192.0.2.5", details = new { email = "event-privacy-canary@example.test", error = "raw-provider-error-canary" } },
            new { id = Guid.NewGuid(), type = "CODE_TO_TOKEN", realmId = "learnstack", time = At.ToUnixTimeMilliseconds(), userId = subject },
            new { id = "invalid-id", type = "LOGIN", realmId = "learnstack", time = At.ToUnixTimeMilliseconds() },
            new { id = Guid.NewGuid(), type = "LOGIN_ERROR", realmId = "learnstack", time = At.ToUnixTimeMilliseconds() },
        });
        var handler = new ScriptHandler((request, _) => Task.FromResult(Json(request.Method == HttpMethod.Post ? Token() : body)));
        var provider = Create(handler);
        await using var lifetime = (IAsyncDisposable)provider;
        var page = await provider.ReadAsync(new(At.AddMinutes(-1), At.AddMinutes(1), 7));
        page.Issue.Should().BeNull();
        page.Items.Select(item => item.Disposition).Should().Equal(IdentityEventItemDisposition.Fact,
            IdentityEventItemDisposition.Unselected, IdentityEventItemDisposition.Malformed, IdentityEventItemDisposition.Fact);
        page.Items[0].Fact!.Kind.Should().Be(IdentityProviderEventKind.LoginFailure);
        page.Items[0].Fact!.SubjectKey.Should().Be(IdentitySubjectKey.Compute(Authority, subject));
        page.Items[3].Fact!.SubjectKey.Should().BeNull();
        var serialized = JsonSerializer.Serialize(page);
        foreach (var canary in new[] { subject, "192.0.2.5", "event-privacy-canary", "raw-provider-error-canary" })
            serialized.Should().NotContain(canary);
        handler.Requests.Should().HaveCount(2);
        handler.Requests[1].Uri.Should().Contain("/admin/realms/learnstack/events?").And.Contain("first=7").And.Contain("direction=asc");
        handler.Requests[1].Uri.Split("&type=", StringSplitOptions.None).Should().HaveCount(21);
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("client")]
    [InlineData("missing-role")]
    [InlineData("extra-role")]
    public async Task A_token_from_the_wrong_source_or_broader_machine_authority_cannot_read_events(string defect)
    {
        var token = Token(defect);
        var handler = new ScriptHandler((_, _) => Task.FromResult(Json(token)));
        var provider = Create(handler);
        await using var lifetime = (IAsyncDisposable)provider;
        var error = await Assert.ThrowsAsync<IdentityEventProviderException>(() => provider.ReadPolicyAsync());
        error.Failure.Should().Be(IdentityEventProviderFailure.Credentials);
        handler.Requests.Should().ContainSingle().Which.Method.Should().Be(HttpMethod.Post);
    }

    [Fact]
    public async Task Each_read_observes_current_private_client_credentials_without_a_bearer_cache()
    {
        var secrets = new Secrets { Value = "first-reader-credential-canary" };
        var handler = new ScriptHandler((request, _) => Task.FromResult(Json(request.Method == HttpMethod.Post ? Token() : Policy())));
        var provider = Create(handler, secrets);
        await using var lifetime = (IAsyncDisposable)provider;
        await provider.ReadPolicyAsync();
        secrets.Value = "rotated-reader-credential-canary";
        await provider.ReadPolicyAsync();
        handler.Requests.Where(request => request.Method == HttpMethod.Post).Select(request => request.Body)
            .Should().HaveCount(2).And.Contain(body => body.Contains("first-reader-credential-canary", StringComparison.Ordinal))
            .And.Contain(body => body.Contains("rotated-reader-credential-canary", StringComparison.Ordinal));
        handler.Requests.Where(request => request.Method == HttpMethod.Get).Should().OnlyContain(request => request.Body.Length == 0);
    }

    private static IIdentityEventProvider Create(HttpMessageHandler handler, Secrets? secrets = null, ResiliencePipeline? pipeline = null)
    {
        var type = typeof(ProviderEventIntakeSource).Assembly.GetType("LearnStack.Infrastructure.Identity.ProviderEvents.KeycloakIdentityEventProvider", throwOnError: true)!;
        return (IIdentityEventProvider)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [new IdentityEventProviderOptions(Authority, Issuer, "learnstack"), secrets ?? new Secrets(), new Resilience(pipeline ?? ResiliencePipeline.Empty), handler], null)!;
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string Policy(int retention = 604800, bool enabled = true, IEnumerable<string>? codes = null) =>
        JsonSerializer.Serialize(new { eventsEnabled = enabled, eventsExpiration = retention, enabledEventTypes = codes ?? Codes });

    private static string Token(string? defect = null)
    {
        var role = defect switch { "missing-role" => Array.Empty<string>(), "extra-role" => new[] { "view-events", "manage-users" }, _ => new[] { "view-events" } };
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = defect == "issuer" ? "https://foreign.invalid/realms/learnstack" : Issuer,
            azp = defect == "client" ? "learnstack-web" : "learnstack-event-reader",
            resource_access = new Dictionary<string, object> { ["realm-management"] = new { roles = role } },
        });
        var jwt = "eyJhbGciOiJSUzI1NiJ9." + Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".test-signature";
        return JsonSerializer.Serialize(new { access_token = jwt, token_type = "Bearer", expires_in = 300 });
    }

    private sealed class Secrets : ISecretProvider
    {
        internal string Value { get; set; } = "event-reader-credential-canary";
        public string? GetSecret(string key) => key == "IdentityEvents:ClientSecret" ? Value : null;
    }
    private sealed class Resilience(ResiliencePipeline pipeline) : IProviderResilience<IIdentityEventProvider>
    {
        public ResiliencePipeline Pipeline => pipeline;
        public string PortName => "identityEvents";
    }
    private sealed record RequestRecord(HttpMethod Method, string Uri, string Body);
    private sealed class ScriptHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal List<RequestRecord> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new(request.Method, request.RequestUri!.AbsoluteUri,
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return await send(request, cancellationToken);
        }
    }
}
