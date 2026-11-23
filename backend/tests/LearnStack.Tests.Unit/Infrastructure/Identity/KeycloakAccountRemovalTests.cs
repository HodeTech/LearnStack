using System.Net;
using System.Net.Sockets;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

    [Theory]
    [InlineData("application/json")]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("APPLICATION/JSON")]
    public async Task A_missing_account_the_provider_reports_in_its_own_json_is_absent_which_is_success(string mediaType)
    {
        var log = new CapturingLogger();
        using var adapter = Create(new FakeKeycloak { DeleteStatus = HttpStatusCode.NotFound, DeleteBody = KeycloakNotFound, DeleteMediaType = mediaType }, log: log);
        (await adapter.RemoveAsync(Subject)).Should().Be(AccountRemovalOutcome.Absent);
        log.Entries.Should().BeEmpty("success logs nothing");
    }

    [Theory]
    [InlineData("text/html", "<html><body>404 Not Found</body></html>")]
    [InlineData("text/html; charset=utf-8", "<h1>Not Found</h1>")]
    [InlineData("text/plain", "not found")]
    [InlineData("application/xml", "<error>not found</error>")]
    [InlineData("application/problem+json", "{\"title\":\"Not Found\"}")]
    [InlineData(null, "")]
    [InlineData(null, "<html>no content type</html>")]
    public async Task A_404_that_is_not_the_providers_own_json_is_refused_because_a_hop_in_front_may_have_answered(
        string? mediaType, string body)
    {
        // A proxy or gateway that hides the admin path answers 404 in HTML, plain text or nothing at all. Treated as absence it
        // would let the closure finish while the account still exists, and nothing in the audit would differ from a true absence.
        var provider = new FakeKeycloak { DeleteStatus = HttpStatusCode.NotFound, DeleteBody = body, DeleteMediaType = mediaType };
        var log = new CapturingLogger();
        using var adapter = Create(provider, log: log);

        (await adapter.RemoveAsync(Subject)).Should().Be(AccountRemovalOutcome.Refused);
        provider.Requests.Should().HaveCount(2, "a refusal is never repeated by the adapter");
        log.Reasons.Should().Equal("delete_not_found_foreign");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "delete_unauthorized")]
    [InlineData(HttpStatusCode.Forbidden, "delete_unauthorized")]
    [InlineData(HttpStatusCode.BadRequest, "delete_rejected")]
    [InlineData(HttpStatusCode.Conflict, "delete_rejected")]
    [InlineData(HttpStatusCode.MethodNotAllowed, "delete_rejected")]
    [InlineData(HttpStatusCode.Found, "delete_redirect")]
    [InlineData(HttpStatusCode.MovedPermanently, "delete_redirect")]
    [InlineData(HttpStatusCode.TemporaryRedirect, "delete_redirect")]
    public async Task A_definitive_refusal_or_a_redirect_is_refused_and_nothing_follows_the_redirect(HttpStatusCode status, string reason)
    {
        var provider = new FakeKeycloak { DeleteStatus = status, Location = "https://attacker.invalid/steal" };
        var log = new CapturingLogger();
        using var adapter = Create(provider, log: log);

        (await adapter.RemoveAsync(Subject)).Should().Be(AccountRemovalOutcome.Refused);
        provider.Requests.Should().HaveCount(2, "a redirect is never followed and a refusal is never repeated");
        provider.Requests.Should().NotContain(request => request.Path.Contains("steal", StringComparison.Ordinal));
        log.Reasons.Should().Equal(reason);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData((HttpStatusCode)425)]
    public async Task A_server_fault_throttle_or_transient_answer_on_the_deletion_is_unavailable(HttpStatusCode status)
    {
        // 408 and 425 are defined as "not processed" (RFC 9110, RFC 8470): repeating is right, and the breaker counts them.
        var log = new CapturingLogger();
        using var adapter = Create(new FakeKeycloak { DeleteStatus = status }, log: log);
        (await adapter.RemoveAsync(Subject)).Should().Be(AccountRemovalOutcome.Unavailable);
        log.Entries.Should().BeEmpty("only a refusal is logged");
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
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData((HttpStatusCode)425)]
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
        var log = new CapturingLogger();
        using var adapter = Create(provider, log: log);
        (await adapter.RemoveAsync(Subject)).Should().Be(AccountRemovalOutcome.Refused);
        provider.Requests.Should().NotContain(request => request.Method == "DELETE");
        log.Reasons.Should().Equal("token_refused");
    }

    [Theory]
    [InlineData("issuer", "token_not_confined")]
    [InlineData("client", "token_not_confined")]
    [InlineData("extra-role", "token_not_confined")]
    [InlineData("wrong-role", "token_not_confined")]
    [InlineData("no-role", "token_not_confined")]
    [InlineData("realm-role", "token_not_confined")]
    [InlineData("two-clients", "token_not_confined")]
    [InlineData("no-resource-access", "token_not_confined")]
    [InlineData("not-a-jwt", "token_malformed")]
    [InlineData("not-json-payload", "token_malformed")]
    [InlineData("type-mac", "token_malformed")]
    [InlineData("no-access-token", "token_malformed")]
    [InlineData("body-not-json", "token_malformed")]
    [InlineData("body-array", "token_malformed")]
    [InlineData("oversize-token", "token_malformed")]
    [InlineData("oversize-body", "token_malformed")]
    public async Task A_token_broader_or_other_than_the_confined_client_is_refused_before_any_deletion(string defect, string reason)
    {
        var provider = new FakeKeycloak { TokenDefect = defect };
        var log = new CapturingLogger();
        using var adapter = Create(provider, log: log);
        (await adapter.RemoveAsync(Subject)).Should().Be(AccountRemovalOutcome.Refused);
        provider.Requests.Should().NotContain(request => request.Method == "DELETE");
        log.Reasons.Should().Equal(reason);
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
        var log = new CapturingLogger();
        using var adapter = Create(provider, secret, log: log);
        (await adapter.RemoveAsync(Subject)).Should().Be(AccountRemovalOutcome.Refused);
        provider.Requests.Should().BeEmpty();
        log.Reasons.Should().Equal("secret_unusable");
    }

    [Fact]
    public async Task An_oversized_secret_is_refused_before_any_network_call()
    {
        var provider = new FakeKeycloak();
        var log = new CapturingLogger();
        using var adapter = Create(provider, new string('s', 1025), log: log);
        (await adapter.RemoveAsync(Subject)).Should().Be(AccountRemovalOutcome.Refused);
        provider.Requests.Should().BeEmpty();
        log.Reasons.Should().Equal("secret_unusable");
    }

    [Fact]
    public async Task Only_its_own_secret_key_is_read_so_the_federation_secret_is_never_used()
    {
        var provider = new FakeKeycloak();
        var secrets = new Secrets(new Dictionary<string, string?> { ["Federation:ClientSecret"] = Secret, ["IdentityEvents:ClientSecret"] = Secret });
        using var adapter = new KeycloakAccountRemoval(new KeycloakAccountRemovalOptions(Issuer), secrets, new Resilience(), NullLogger.Instance, provider);
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
        var log = new CapturingLogger();
        using var adapter = Create(provider, log: log);
        (await adapter.RemoveAsync(subject)).Should().Be(AccountRemovalOutcome.Refused);
        provider.Requests.Should().BeEmpty();
        log.Reasons.Should().Equal("subject_shape");
        log.Entries.Should().NotContain(entry => entry.Message.Contains(subject.Trim(), StringComparison.Ordinal) && subject.Trim().Length > 0);
    }

    [Fact]
    public async Task A_bad_subject_and_a_bad_secret_together_report_the_subject_which_no_repair_of_the_secret_changes()
    {
        var log = new CapturingLogger();
        using var adapter = Create(new FakeKeycloak(), secret: null, log: log);
        (await adapter.RemoveAsync("not-a-uuid")).Should().Be(AccountRemovalOutcome.Refused);
        log.Reasons.Should().Equal("subject_shape");
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
    [InlineData(HttpRequestError.NameResolutionError)]
    [InlineData(HttpRequestError.SecureConnectionError)]
    [InlineData(HttpRequestError.ProxyTunnelError)]
    public async Task A_fault_that_surely_precedes_the_deletions_write_is_unavailable(HttpRequestError error)
    {
        using var adapter = Create(new FakeKeycloak { DeleteFault = () => new HttpRequestException(error, "before-write") });
        (await adapter.RemoveAsync(Subject)).Should().Be(AccountRemovalOutcome.Unavailable);
    }

    [Theory]
    [InlineData(HttpRequestError.ConnectionError)]
    [InlineData(HttpRequestError.ResponseEnded)]
    [InlineData(HttpRequestError.InvalidResponse)]
    [InlineData(HttpRequestError.Unknown)]
    public async Task A_connection_lost_or_failed_on_the_deletion_that_may_have_been_written_is_uncertain(HttpRequestError error)
    {
        using var adapter = Create(new FakeKeycloak { DeleteFault = () => new HttpRequestException(error, "after-write") });
        (await adapter.RemoveAsync(Subject)).Should().Be(AccountRemovalOutcome.Uncertain);
    }

    [Fact]
    public async Task A_reused_connection_that_dies_after_the_deletion_was_read_and_cannot_be_reopened_is_uncertain()
    {
        // The real transport, not an injected fault. The token call leaves a pooled keep-alive connection; the provider reads the
        // deletion's request head on it, then goes away: it closes that connection and its listener. The transport retries the
        // request on a new connection, which is refused, and reports a connection error although the deletion was read. A
        // connection error is therefore not proof that nothing was written.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var issuer = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/realms/learnstack";
        var server = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync();
            var stream = connection.GetStream();
            await ReadHeadAsync(stream, withBody: true);
            var payload = new JsonObject
            {
                ["iss"] = issuer,
                ["azp"] = "learnstack-account-removal",
                ["resource_access"] = new JsonObject { ["realm-management"] = new JsonObject { ["roles"] = new JsonArray("manage-users") } },
            };
            var body = new JsonObject
            {
                ["access_token"] = FakeKeycloak.Jwt(payload.ToJsonString()),
                ["token_type"] = "Bearer",
            }.ToJsonString();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: "
                + body.Length + "\r\n\r\n" + body));
            await ReadHeadAsync(stream, withBody: false);
            listener.Stop();
            connection.Close();
        });
        var observed = new FaultRecorder(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
        var log = new CapturingLogger();
        using var adapter = new KeycloakAccountRemoval(new KeycloakAccountRemovalOptions(issuer, allowLocalDevelopmentHttp: true),
            new Secrets(new Dictionary<string, string?> { ["AccountRemoval:ClientSecret"] = Secret }), new Resilience(), log, observed);

        (await adapter.RemoveAsync(Subject)).Should().Be(AccountRemovalOutcome.Uncertain);
        await server;
        observed.Errors.Should().Contain(HttpRequestError.ConnectionError,
            "the premise: the transport reported a connection error for a request whose head the provider had read");
        log.Entries.Should().BeEmpty("an uncertain answer is not a refusal");
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
    public async Task A_hanging_provider_opens_the_breaker_because_the_pipeline_timeout_is_what_the_adapter_surfaces()
    {
        // The composed pair: the real pipeline timeout (the fixed five seconds the composition root enforces), the real breaker
        // and the adapter's own cancellation. Were the two five-second limits equal, the adapter's, started first, would win; the
        // timeout strategy would rethrow a plain cancellation, the breaker would not count it, and a hanging provider would
        // answer uncertain, five seconds at a time, for ever. Two hangs must open the breaker; the third call never sends.
        var provider = new FakeKeycloak { DeleteHangs = true };
        using var adapter = Create(provider, resilience: Real(("Timeout:TotalSeconds", "5"), ("CircuitBreaker:MinimumThroughput", "2"),
            ("CircuitBreaker:FailureRatio", "1"), ("CircuitBreaker:BreakDurationSeconds", "30")));

        var calls = await Task.WhenAll(adapter.RemoveAsync(Subject), adapter.RemoveAsync(Subject));
        calls.Should().Equal([AccountRemovalOutcome.Uncertain, AccountRemovalOutcome.Uncertain],
            "the deletion was handed to the transport and its answer never came");
        var sent = provider.Requests.Count;

        (await adapter.RemoveAsync(Subject)).Should().Be(AccountRemovalOutcome.Unavailable, "the breaker recorded both timeouts");
        provider.Requests.Should().HaveCount(sent, "an open breaker answers without a request");
    }

    [Fact]
    public void The_adapters_own_backstop_is_later_than_the_pipeline_timeout_the_composition_root_fixes()
    {
        KeycloakAccountRemoval.Backstop.Should().BeGreaterThan(KeycloakAccountRemoval.CallCeiling);
        KeycloakAccountRemoval.CallCeiling.TotalSeconds.Should().Be(AccountRemovalAdapterFactory.CallCeilingSeconds);
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
    [InlineData("https://provider.invalid/realms/learnstack-hub")]
    [InlineData("https://provider.invalid/realms/learnstack/extra")]
    [InlineData("https://provider.invalid/realms/learnstack-hub/")]
    [InlineData("https://provider.invalid/x-realms/learnstack")]
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
            new Secrets(new Dictionary<string, string?>()), new Resilience(), NullLoggerFactory.Instance);
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

    private const string KeycloakNotFound = "{\"error\":\"User not found\"}";

    private static KeycloakAccountRemoval Create(FakeKeycloak provider, string? secret = Secret,
        IProviderResilience<IIdentityProviderAccountRemoval>? resilience = null, CapturingLogger? log = null) =>
        new(new KeycloakAccountRemovalOptions(Issuer), new Secrets(new Dictionary<string, string?> { ["AccountRemoval:ClientSecret"] = secret }),
            resilience ?? new Resilience(), log ?? new CapturingLogger(), provider);

    /// <summary>Reads one request head (and, with a body, whatever follows it) off a raw loopback connection.</summary>
    private static async Task ReadHeadAsync(NetworkStream stream, bool withBody)
    {
        var buffer = new byte[8192];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total));
            if (read == 0) return;
            total += read;
            var text = Encoding.ASCII.GetString(buffer, 0, total);
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0 && (!withBody || text.Length > end + 4)) return;
        }
    }

    /// <summary>Records every transport error the adapter's client saw, then lets it propagate.</summary>
    private sealed class FaultRecorder(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        internal List<HttpRequestError> Errors { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try { return await base.SendAsync(request, cancellationToken); }
            catch (HttpRequestException error)
            {
                Errors.Add(error.HttpRequestError);
                throw;
            }
        }
    }

    /// <summary>Keeps every line the adapter logs, with its level, event id and formatted text.</summary>
    private sealed class CapturingLogger : ILogger
    {
        internal List<(LogLevel Level, int Event, string Message)> Entries { get; } = [];

        /// <summary>The closed reason code of each refusal line, taken from the text after the last colon.</summary>
        internal IEnumerable<string> Reasons => Entries.Where(entry => entry.Level == LogLevel.Warning && entry.Event == 7125)
            .Select(entry => entry.Message[(entry.Message.LastIndexOf(": ", StringComparison.Ordinal) + 2)..].TrimEnd('.'));

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            lock (Entries) Entries.Add((logLevel, eventId.Id, message));
            message.Should().NotContain(Subject).And.NotContain(Secret).And.NotContain("provider.invalid").And.NotContain("Bearer")
                .And.NotContain("canary");
        }
    }

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

        /// <summary>The deletion answer's media type; null leaves the answer without a Content-Type header.</summary>
        internal string? DeleteMediaType { get; init; }
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
            return Answer(DeleteStatus, DeleteBody, DeleteMediaType);
        }

        private HttpResponseMessage Answer(HttpStatusCode status, string? body = null, string? mediaType = null)
        {
            var response = new HttpResponseMessage(status);
            if (body is not null)
            {
                var bytes = Encoding.UTF8.GetBytes(body);
                response.Content = new ByteArrayContent(bytes);
                if (mediaType is not null) response.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(mediaType);
            }
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

        /// <summary>An unsigned token with the given payload: the adapter reads the claims and never verifies the signature.</summary>
        internal static string Jwt(string payloadJson) => Part("{\"alg\":\"none\"}") + "." + Part(payloadJson) + ".c2ln";

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
