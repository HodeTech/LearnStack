using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using LearnStack.Infrastructure.Identity.AccountRemoval;
using LearnStack.Infrastructure.Resilience;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Resilience;
using LearnStack.SharedKernel.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.Retry;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Identity;

/// <summary>
/// ADR-0107 § 7: the confined account-removal adapter against a scripted provider. Every outcome of the port, the token
/// narrowing, the refusals that precede any network effect, and that no subject, secret or provider text leaves the adapter.
/// </summary>
public sealed class KeycloakAccountRemovalTests
{
    private const string Issuer = "https://provider.invalid/realms/learnstack";
    private const string Secret = "account-removal-secret-canary";
    private const string Subject = "0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d";

    [Fact]
    public async Task A_deleted_account_is_removed_by_one_token_and_one_delete_on_the_exact_user_route()
    {
        var provider = new FakeKeycloak();
        using var adapter = Create(provider);

        (await adapter.RemoveAsync(Subject)).Should().Be(AccountRemovalOutcome.Removed);

        provider.Requests.Select(request => request.Method + " " + request.Path).Should().Equal(
            "POST /realms/learnstack/protocol/openid-connect/token",
            "DELETE /admin/realms/learnstack/users/" + Subject);
        var delete = provider.Requests[1];
        delete.Authorization.Should().StartWith("Bearer ");
        delete.Query.Should().BeEmpty();
        delete.Body.Should().BeEmpty("a deletion carries no body");
        delete.NoStore.Should().BeTrue();
        provider.Requests[0].Body.Should().Contain("client_id=learnstack-account-removal").And.Contain("grant_type=client_credentials")
            .And.NotContain(Subject, "the subject is sent on the deletion path only");
    }

    [Fact]
    public async Task A_missing_account_is_absent_which_is_success()
    {
        using var adapter = Create(new FakeKeycloak { DeleteStatus = HttpStatusCode.NotFound });
        (await adapter.RemoveAsync(Subject)).Should().Be(AccountRemovalOutcome.Absent);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    public async Task A_definitive_refusal_or_a_redirect_is_refused_and_nothing_follows_the_redirect(HttpStatusCode status)
    {
        var provider = new FakeKeycloak { DeleteStatus = status, Location = "https://attacker.invalid/steal" };
        using var adapter = Create(provider);

        (await adapter.RemoveAsync(Subject)).Should().Be(AccountRemovalOutcome.Refused);
        provider.Requests.Should().HaveCount(2, "a redirect is never followed and a refusal is never repeated");
        provider.Requests.Should().NotContain(request => request.Path.Contains("steal", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task A_server_fault_or_throttle_on_the_deletion_is_unavailable(HttpStatusCode status)
    {
        using var adapter = Create(new FakeKeycloak { DeleteStatus = status });
        (await adapter.RemoveAsync(Subject)).Should().Be(AccountRemovalOutcome.Unavailable);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Accepted)]
    [InlineData(HttpStatusCode.ResetContent)]
    public async Task A_success_that_is_not_the_deletions_own_answer_is_uncertain(HttpStatusCode status)
    {
        using var adapter = Create(new FakeKeycloak { DeleteStatus = status });
        (await adapter.RemoveAsync(Subject)).Should().Be(AccountRemovalOutcome.Uncertain);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task A_server_fault_or_throttle_on_the_token_is_unavailable_and_no_deletion_is_sent(HttpStatusCode status)
    {
        var provider = new FakeKeycloak { TokenStatus = status };
        using var adapter = Create(provider);
        (await adapter.RemoveAsync(Subject)).Should().Be(AccountRemovalOutcome.Unavailable);
        provider.Requests.Should().NotContain(request => request.Method == "DELETE");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Found)]
    public async Task A_token_endpoint_that_refuses_the_client_is_refused_and_no_deletion_is_sent(HttpStatusCode status)
    {
        var provider = new FakeKeycloak { TokenStatus = status };
        using var adapter = Create(provider);
        (await adapter.RemoveAsync(Subject)).Should().Be(AccountRemovalOutcome.Refused);
        provider.Requests.Should().NotContain(request => request.Method == "DELETE");
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("client")]
    [InlineData("extra-role")]
    [InlineData("wrong-role")]
    [InlineData("no-role")]
    [InlineData("realm-role")]
    [InlineData("two-clients")]
    [InlineData("no-resource-access")]
    [InlineData("not-a-jwt")]
    [InlineData("not-json-payload")]
    [InlineData("type-mac")]
    [InlineData("no-access-token")]
    [InlineData("body-not-json")]
    [InlineData("body-array")]
    [InlineData("oversize-token")]
    public async Task A_token_broader_or_other_than_the_confined_client_is_refused_before_any_deletion(string defect)
    {
        var provider = new FakeKeycloak { TokenDefect = defect };
        using var adapter = Create(provider);
        (await adapter.RemoveAsync(Subject)).Should().Be(AccountRemovalOutcome.Refused);
        provider.Requests.Should().NotContain(request => request.Method == "DELETE");
    }

    [Fact]
    public async Task An_oversized_token_body_is_refused_before_any_deletion()
    {
        var provider = new FakeKeycloak { TokenDefect = "oversize-body" };
        using var adapter = Create(provider);
        (await adapter.RemoveAsync(Subject)).Should().Be(AccountRemovalOutcome.Refused);
        provider.Requests.Should().NotContain(request => request.Method == "DELETE");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("**********")]
    [InlineData("${vault.account-removal}")]
    public async Task An_unusable_secret_is_refused_before_any_network_call(string? secret)
    {
        var provider = new FakeKeycloak();
        using var adapter = Create(provider, secret);
        (await adapter.RemoveAsync(Subject)).Should().Be(AccountRemovalOutcome.Refused);
        provider.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task An_oversized_secret_is_refused_before_any_network_call()
    {
        var provider = new FakeKeycloak();
        using var adapter = Create(provider, new string('s', 1025));
        (await adapter.RemoveAsync(Subject)).Should().Be(AccountRemovalOutcome.Refused);
        provider.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Only_its_own_secret_key_is_read_so_the_federation_secret_is_never_used()
    {
        var provider = new FakeKeycloak();
        var secrets = new Secrets(new Dictionary<string, string?> { ["Federation:ClientSecret"] = Secret, ["IdentityEvents:ClientSecret"] = Secret });
        using var adapter = new KeycloakAccountRemoval(new KeycloakAccountRemovalOptions(Issuer), secrets, new Resilience(), provider);
        (await adapter.RemoveAsync(Subject)).Should().Be(AccountRemovalOutcome.Refused);
        provider.Requests.Should().BeEmpty();
        secrets.Asked.Should().Equal("AccountRemoval:ClientSecret");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("not-a-uuid")]
    [InlineData("0A1B2C3D-4E5F-4A6B-8C7D-9E0F1A2B3C4D")]
    [InlineData("{0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d}")]
    [InlineData("0a1b2c3d4e5f4a6b8c7d9e0f1a2b3c4d")]
    [InlineData("0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d ")]
    [InlineData(" 0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d")]
    [InlineData("0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d/../clients")]
    [InlineData("0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d?briefRepresentation=true")]
    [InlineData("0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d#x")]
    [InlineData("..%2f..%2fclients")]
    [InlineData("../clients/0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4é")]
    public async Task A_subject_that_is_not_a_canonical_provider_user_id_is_refused_before_any_network_call(string subject)
    {
        var provider = new FakeKeycloak();
        using var adapter = Create(provider);
        (await adapter.RemoveAsync(subject)).Should().Be(AccountRemovalOutcome.Refused);
        provider.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_null_subject_is_a_programmer_error()
    {
        using var adapter = Create(new FakeKeycloak());
        await FluentActions.Awaiting(() => adapter.RemoveAsync(null!)).Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task A_refused_connection_to_the_token_endpoint_is_unavailable_and_nothing_is_deleted()
    {
        var provider = new FakeKeycloak { TokenFault = new HttpRequestException(HttpRequestError.ConnectionError, "connect") };
        using var adapter = Create(provider);
        (await adapter.RemoveAsync(Subject)).Should().Be(AccountRemovalOutcome.Unavailable);
        provider.Requests.Should().NotContain(request => request.Method == "DELETE");
    }

    [Theory]
    [InlineData(HttpRequestError.ConnectionError)]
    [InlineData(HttpRequestError.NameResolutionError)]
    [InlineData(HttpRequestError.SecureConnectionError)]
    [InlineData(HttpRequestError.ProxyTunnelError)]
    public async Task A_fault_before_the_deletion_was_written_is_unavailable(HttpRequestError error)
    {
        using var adapter = Create(new FakeKeycloak { DeleteFault = () => new HttpRequestException(error, "before-write") });
        (await adapter.RemoveAsync(Subject)).Should().Be(AccountRemovalOutcome.Unavailable);
    }

    [Theory]
    [InlineData(HttpRequestError.ResponseEnded)]
    [InlineData(HttpRequestError.InvalidResponse)]
    [InlineData(HttpRequestError.Unknown)]
    public async Task A_connection_lost_after_the_deletion_was_sent_is_uncertain(HttpRequestError error)
    {
        using var adapter = Create(new FakeKeycloak { DeleteFault = () => new HttpRequestException(error, "after-write") });
        (await adapter.RemoveAsync(Subject)).Should().Be(AccountRemovalOutcome.Uncertain);
    }

    [Fact]
    public async Task A_timeout_before_the_deletion_is_unavailable_and_after_it_is_uncertain()
    {
        var beforeSend = new FakeKeycloak { TokenHangs = true };
        using (var adapter = Create(beforeSend, resilience: new Resilience(Timeout(200))))
            (await adapter.RemoveAsync(Subject)).Should().Be(AccountRemovalOutcome.Unavailable, "the token call never reaches the deletion");
        beforeSend.Requests.Should().NotContain(request => request.Method == "DELETE");

        var afterSend = new FakeKeycloak { DeleteHangs = true };
        using (var adapter = Create(afterSend, resilience: new Resilience(Timeout(200))))
            (await adapter.RemoveAsync(Subject)).Should().Be(AccountRemovalOutcome.Uncertain, "the deletion was handed to the transport");
        afterSend.Requests.Should().Contain(request => request.Method == "DELETE");
    }

    [Fact]
    public async Task A_transport_cancellation_that_is_not_the_callers_follows_the_same_phase_rule()
    {
        using (var adapter = Create(new FakeKeycloak { TokenFault = new TaskCanceledException() }))
            (await adapter.RemoveAsync(Subject)).Should().Be(AccountRemovalOutcome.Unavailable);
        using (var adapter = Create(new FakeKeycloak { DeleteFault = () => new TaskCanceledException() }))
            (await adapter.RemoveAsync(Subject)).Should().Be(AccountRemovalOutcome.Uncertain);
    }

    [Fact]
    public async Task The_callers_cancellation_propagates_before_and_after_the_deletion_is_sent()
    {
        using var before = new CancellationTokenSource();
        var token = new FakeKeycloak { TokenHangs = true };
        using (var adapter = Create(token))
        {
            var call = adapter.RemoveAsync(Subject, before.Token);
            await token.TokenReached.Task;
            await before.CancelAsync();
            await FluentActions.Awaiting(() => call).Should().ThrowAsync<OperationCanceledException>();
        }

        using var after = new CancellationTokenSource();
        var delete = new FakeKeycloak { DeleteHangs = true };
        using (var adapter = Create(delete))
        {
            var call = adapter.RemoveAsync(Subject, after.Token);
            await delete.DeleteReached.Task;
            await after.CancelAsync();
            await FluentActions.Awaiting(() => call).Should().ThrowAsync<OperationCanceledException>();
        }
    }

    [Fact]
    public async Task The_subject_secret_and_provider_text_never_reach_an_exception_the_pipeline_sees()
    {
        // A pipeline whose retry hook captures every exception the adapter throws into it: the closed fault carries none of them.
        var captured = new List<Exception>();
        var spy = new ResiliencePipelineBuilder().AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = 1,
            Delay = TimeSpan.Zero,
            ShouldHandle = new PredicateBuilder().Handle<Exception>(),
            OnRetry = arguments => { captured.Add(arguments.Outcome.Exception!); return ValueTask.CompletedTask; },
        }).Build();
        foreach (var provider in new[]
        {
            new FakeKeycloak { DeleteStatus = HttpStatusCode.InternalServerError, DeleteBody = "provider-body-canary" },
            new FakeKeycloak { TokenStatus = HttpStatusCode.ServiceUnavailable },
            new FakeKeycloak { DeleteFault = () => new HttpRequestException(HttpRequestError.ConnectionError, "transport-message-canary " + Subject) },
            new FakeKeycloak { DeleteFault = () => new HttpRequestException(HttpRequestError.ResponseEnded, "transport-message-canary " + Subject) },
            new FakeKeycloak { DeleteStatus = HttpStatusCode.OK },
        })
        {
            using var adapter = Create(provider, resilience: new Resilience(spy));
            await adapter.RemoveAsync(Subject);
        }
        captured.Should().HaveCount(5).And.AllBeOfType<AccountRemovalProviderException>();
        foreach (var error in captured)
        {
            var text = error + " " + error.Message + " " + error.InnerException;
            text.Should().NotContain(Subject).And.NotContain(Secret).And.NotContain("canary").And.NotContain("Bearer");
        }
        // The adapter and its options render as their type name: no issuer, secret key or subject can come from a log of either.
        using var rendered = Create(new FakeKeycloak());
        rendered.ToString().Should().Be("KeycloakAccountRemoval");
        new KeycloakAccountRemovalOptions(Issuer).ToString().Should().Be("KeycloakAccountRemovalOptions");
    }

    [Fact]
    public async Task A_real_breaker_opens_on_repeated_faults_and_an_open_breaker_never_sends()
    {
        var provider = new FakeKeycloak { DeleteStatus = HttpStatusCode.InternalServerError };
        using var adapter = Create(provider, resilience: Real(("CircuitBreaker:MinimumThroughput", "2"), ("CircuitBreaker:FailureRatio", "1"),
            ("CircuitBreaker:BreakDurationSeconds", "30")));
        (await adapter.RemoveAsync(Subject)).Should().Be(AccountRemovalOutcome.Unavailable);
        (await adapter.RemoveAsync(Subject)).Should().Be(AccountRemovalOutcome.Unavailable);
        var sent = provider.Requests.Count;
        (await adapter.RemoveAsync(Subject)).Should().Be(AccountRemovalOutcome.Unavailable);
        provider.Requests.Should().HaveCount(sent, "an open breaker answers without a request");
    }

    [Fact]
    public async Task A_second_concurrent_call_meets_the_bulkhead_and_is_unavailable_without_a_request()
    {
        var provider = new FakeKeycloak { DeleteHangs = true };
        using var adapter = Create(provider, resilience: Real(("Bulkhead:MaxConcurrency", "1"), ("Bulkhead:QueueLength", "0"),
            ("Timeout:TotalSeconds", "30")));
        var first = adapter.RemoveAsync(Subject);
        await provider.DeleteReached.Task;
        var sent = provider.Requests.Count;

        (await adapter.RemoveAsync("1b2c3d4e-5f6a-4b7c-8d9e-0f1a2b3c4d5e")).Should().Be(AccountRemovalOutcome.Unavailable);
        provider.Requests.Should().HaveCount(sent);

        provider.ReleaseDelete.SetResult();
        (await first).Should().Be(AccountRemovalOutcome.Removed);
    }

    [Theory]
    [InlineData("http://provider.invalid/realms/learnstack")]
    [InlineData("http://127.0.0.1/realms/learnstack")]
    [InlineData("https://user:pass@provider.invalid/realms/learnstack")]
    [InlineData("https://provider.invalid/realms/learnstack?x=1")]
    [InlineData("https://provider.invalid/realms/learnstack#x")]
    [InlineData("https://provider.invalid/realms/other")]
    [InlineData("https://provider.invalid/")]
    [InlineData("provider.invalid/realms/learnstack")]
    [InlineData("")]
    public void The_issuer_must_be_an_https_learnstack_realm(string issuer)
    {
        FluentActions.Invoking(() => new KeycloakAccountRemovalOptions(issuer)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_loopback_http_issuer_needs_the_development_flag_and_derives_the_fixed_routes()
    {
        FluentActions.Invoking(() => new KeycloakAccountRemovalOptions("http://localhost:8080/realms/learnstack")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new KeycloakAccountRemovalOptions("http://provider.invalid/realms/learnstack", true)).Should().Throw<ArgumentException>();
        var options = new KeycloakAccountRemovalOptions("http://localhost:8080/realms/learnstack/", true);
        options.Issuer.Should().Be("http://localhost:8080/realms/learnstack");
        KeycloakAccountRemovalRoutes.Token(options).AbsoluteUri.Should().Be("http://localhost:8080/realms/learnstack/protocol/openid-connect/token");
        KeycloakAccountRemovalRoutes.User(options, Subject).AbsoluteUri.Should().Be("http://localhost:8080/admin/realms/learnstack/users/" + Subject);
    }

    [Fact]
    public void The_user_route_refuses_what_is_not_a_canonical_user_id()
    {
        var options = new KeycloakAccountRemovalOptions(Issuer);
        foreach (var subject in new[] { "", "x", Subject.ToUpperInvariant(), Subject + "/x", "../" + Subject })
            FluentActions.Invoking(() => KeycloakAccountRemovalRoutes.User(options, subject)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_factory_returns_the_port_and_names_the_secret_key()
    {
        using var adapter = (IDisposable)AccountRemovalAdapterFactory.Create(new KeycloakAccountRemovalOptions(Issuer),
            new Secrets(new Dictionary<string, string?>()), new Resilience());
        adapter.Should().BeAssignableTo<IIdentityProviderAccountRemoval>();
        AccountRemovalAdapterFactory.SecretKey.Should().Be("AccountRemoval:ClientSecret");
    }

    [Fact]
    public async Task The_default_refuses_every_call_without_reading_the_subject()
    {
        var port = new RefusingIdentityProviderAccountRemoval();
        (await port.RemoveAsync(Subject)).Should().Be(AccountRemovalOutcome.Refused);
        (await port.RemoveAsync("")).Should().Be(AccountRemovalOutcome.Refused);
        (await port.RemoveAsync(null!)).Should().Be(AccountRemovalOutcome.Refused);
        port.ToString().Should().Be("RefusingIdentityProviderAccountRemoval");
    }

    private static KeycloakAccountRemoval Create(FakeKeycloak provider, string? secret = Secret, IProviderResilience<IIdentityProviderAccountRemoval>? resilience = null) =>
        new(new KeycloakAccountRemovalOptions(Issuer), new Secrets(new Dictionary<string, string?> { ["AccountRemoval:ClientSecret"] = secret }),
            resilience ?? new Resilience(), provider);

    private static ResiliencePipeline Timeout(int milliseconds) =>
        new ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromMilliseconds(milliseconds)).Build();

    /// <summary>The real pipeline the composition root builds, with a few values overridden for one test.</summary>
    private static IProviderResilience<IIdentityProviderAccountRemoval> Real(params (string Key, string Value)[] values)
    {
        var settings = new Dictionary<string, string?> { ["Retry:Enabled"] = "false", ["Retry:MaxRetryAttempts"] = "0" };
        foreach (var (key, value) in values) settings[key] = value;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            settings.ToDictionary(pair => "Resilience:accountRemoval:" + pair.Key, pair => pair.Value)).Build();
        var services = new ServiceCollection();
        services.AddProviderResilience<IIdentityProviderAccountRemoval>(configuration, "accountRemoval");
        return services.BuildServiceProvider().GetRequiredService<IProviderResilience<IIdentityProviderAccountRemoval>>();
    }

    private sealed class Secrets(Dictionary<string, string?> values) : ISecretProvider
    {
        internal List<string> Asked { get; } = [];

        public string? GetSecret(string key)
        {
            Asked.Add(key);
            return values.GetValueOrDefault(key);
        }
    }

    private sealed class Resilience(ResiliencePipeline? pipeline = null) : IProviderResilience<IIdentityProviderAccountRemoval>
    {
        public ResiliencePipeline Pipeline => pipeline ?? ResiliencePipeline.Empty;
        public string PortName => "accountRemoval";
    }

    internal sealed record Recorded(string Method, string Path, string Query, string Body, string? Authorization, bool NoStore);

    /// <summary>A scripted Keycloak: a token endpoint with injectable defects and a user deletion with injectable answers.</summary>
    private sealed class FakeKeycloak : HttpMessageHandler
    {
        internal List<Recorded> Requests { get; } = [];
        internal string? TokenDefect { get; init; }
        internal HttpStatusCode? TokenStatus { get; init; }
        internal Exception? TokenFault { get; init; }
        internal bool TokenHangs { get; init; }
        internal HttpStatusCode DeleteStatus { get; init; } = HttpStatusCode.NoContent;
        internal string? DeleteBody { get; init; }
        internal Func<Exception>? DeleteFault { get; init; }
        internal bool DeleteHangs { get; init; }
        internal string? Location { get; init; }
        internal TaskCompletionSource TokenReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DeleteReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseDelete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var path = request.RequestUri!.AbsolutePath;
            lock (Requests)
                Requests.Add(new(request.Method.Method, path, request.RequestUri.Query, body, request.Headers.Authorization?.ToString(),
                    request.Headers.CacheControl?.NoStore == true));
            if (path.EndsWith("/protocol/openid-connect/token", StringComparison.Ordinal))
            {
                TokenReached.TrySetResult();
                if (TokenHangs) await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
                if (TokenFault is not null) throw TokenFault;
                if (TokenStatus is { } status) return Answer(status);
                return TokenAnswer(TokenDefect);
            }
            if (request.Method != HttpMethod.Delete || request.Headers.Authorization?.Scheme != "Bearer") return new(HttpStatusCode.Unauthorized);
            DeleteReached.TrySetResult();
            if (DeleteHangs)
            {
                var released = ReleaseDelete.Task;
                var done = await Task.WhenAny(released, Task.Delay(System.Threading.Timeout.Infinite, cancellationToken));
                await done;
                return new(HttpStatusCode.NoContent);
            }
            if (DeleteFault is { } fault) throw fault();
            return Answer(DeleteStatus, DeleteBody);
        }

        private HttpResponseMessage Answer(HttpStatusCode status, string? body = null)
        {
            var response = new HttpResponseMessage(status);
            if (body is not null) response.Content = new StringContent(body);
            if (Location is not null && (int)status is >= 300 and < 400) response.Headers.Location = new Uri(Location);
            return response;
        }

        private static HttpResponseMessage Json(string text) =>
            new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

        private static HttpResponseMessage TokenAnswer(string? defect)
        {
            switch (defect)
            {
                case "body-not-json": return Json("not json");
                case "body-array": return Json("[]");
                case "no-access-token": return Json("{\"token_type\":\"Bearer\"}");
                case "oversize-body": return Json("{\"access_token\":\"" + new string('a', 20 * 1024) + "\",\"token_type\":\"Bearer\"}");
            }
            var access = defect switch
            {
                "not-a-jwt" => "not-a-jwt",
                "not-json-payload" => Part("{\"alg\":\"none\"}") + "." + Part("not json") + ".c2ln",
                "oversize-token" => new string('a', 16 * 1024 + 1),
                _ => Token(defect),
            };
            return Json(new JsonObject { ["access_token"] = access, ["token_type"] = defect == "type-mac" ? "MAC" : "Bearer", ["expires_in"] = 60 }.ToJsonString());
        }

        private static string Part(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static string Token(string? defect)
        {
            var roles = defect switch
            {
                "extra-role" => new JsonArray("manage-users", "view-users"),
                "wrong-role" => new JsonArray("manage-identity-providers"),
                "no-role" => new JsonArray(),
                _ => new JsonArray("manage-users"),
            };
            var management = new JsonObject { ["roles"] = roles };
            var resources = defect == "two-clients"
                ? new JsonObject { ["realm-management"] = management, ["account"] = new JsonObject { ["roles"] = new JsonArray("manage-account") } }
                : new JsonObject { ["realm-management"] = management };
            var payload = new JsonObject
            {
                ["iss"] = defect == "issuer" ? "https://other.invalid/realms/learnstack" : Issuer,
                ["azp"] = defect == "client" ? "learnstack-federation-admin" : "learnstack-account-removal",
            };
            if (defect != "no-resource-access") payload["resource_access"] = resources;
            if (defect == "realm-role") payload["realm_access"] = new JsonObject { ["roles"] = new JsonArray("default-roles-learnstack") };
            return Part("{\"alg\":\"none\"}") + "." + Part(payload.ToJsonString()) + ".c2ln";
        }
    }
}
