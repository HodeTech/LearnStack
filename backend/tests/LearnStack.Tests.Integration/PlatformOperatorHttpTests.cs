using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Api.Authentication;
using LearnStack.Infrastructure.PlatformOperator;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Entitlements;
using LearnStack.SharedKernel.Platform;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LearnStack.Tests.Integration;

/// <summary>
/// The operator surface through the production Program and the stock bearer handler, with a
/// controlled issuer signing real RS256 tokens (ADR-0101).
/// </summary>
/// <remarks>
/// <para>
/// Protocol refusals are isolated with controlled signed tokens, as the tenant suite does; the
/// real-realm proof is a separate case against pinned Keycloak. The killswitch writer is a
/// recording double here, so these cases decide admission and mapping only and never reach a
/// database; the writer's own transaction semantics are asserted against real PostgreSQL.
/// </para>
/// <para>
/// What a refusal must show is the same in every case: the double was never called, so no
/// privileged connection was acquired, and the answer carries no detail a probing caller could use.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "xUnit disposes the fields through IAsyncLifetime.DisposeAsync.")]
public sealed class PlatformOperatorHttpTests : IAsyncLifetime
{
    private const string Path = "/api/platform/v1/killswitches/killswitch.classroom.recording";
    private const string Subject = "operator-sub-1";

    private TenantAuthenticationIssuer _tenant = null!;
    private TenantAuthenticationIssuer _operator = null!;
    private PlatformOperatorApiFactory _factory = null!;
    private RecordingToggle _toggle = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _tenant = await TenantAuthenticationIssuer.StartAsync();
        _operator = await TenantAuthenticationIssuer.StartAsync();
        _toggle = new RecordingToggle();
        _factory = new PlatformOperatorApiFactory(_tenant, _operator, _toggle, grantSubject: Subject);
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _operator.DisposeAsync();
        await _tenant.DisposeAsync();
    }

    [Fact]
    public async Task A_Valid_Operator_With_The_Grant_Toggles_And_The_Admission_Carries_Them()
    {
        var response = await Put(Valid(), """{"enabled":false,"ticket":"INC-42"}""");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("key").GetString().Should().Be("killswitch.classroom.recording");
        body.GetProperty("after").GetBoolean().Should().BeFalse();
        _toggle.Calls.Should().ContainSingle();
        var admission = _toggle.Calls[0];
        admission.Operator.SubjectDigest.Should().Be(PlatformOperatorSubject.Digest(_operator.Issuer, Subject));
        admission.Operator.Issuer.Should().Be(_operator.Issuer);
        admission.Operator.Acr.Should().Be("mfa");
        admission.Ticket.Should().Be("INC-42");
        admission.Operation.Should().BeSameAs(PlatformOperations.KillswitchToggle);
        admission.Operator.CorrelationId.Should().NotBeNullOrWhiteSpace(
            "a result row is never recorded without a handle the operator can quote");
        body.GetProperty("correlationId").GetString().Should().Be(admission.Operator.CorrelationId);
        admission.IssuedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task The_Bearer_Prefix_And_The_Route_Prefix_Are_Matched_Without_Regard_To_Case()
    {
        // Routing is case-insensitive, so the scheme's own prefix checks must be too: a request the
        // router sends to the operator endpoint is a request the operator scheme authenticates.
        using var request = new HttpRequestMessage(HttpMethod.Put, Path.ToUpperInvariant().Replace("KILLSWITCH.CLASSROOM.RECORDING", "killswitch.classroom.recording"))
        {
            Content = new StringContent("""{"enabled":false}""", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", "bearer " + Valid());

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _toggle.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task No_Credential_Is_Refused_Without_Detail()
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, Path)
        {
            Content = JsonContent.Create(new { enabled = false }),
        };

        var response = await _client.SendAsync(request);

        await AssertUnauthorized(response);
    }

    [Theory]
    [InlineData("audience", "token_audience")]
    [InlineData("issuer", "token_issuer")]
    [InlineData("typ", "token_type")]
    [InlineData("alg", "token_signature")] // the stock handler reports a disallowed algorithm as a signature failure
    [InlineData("signature", "token_signature")]
    [InlineData("azp", "profile_azp")]
    [InlineData("acr", "profile_acr")]
    [InlineData("acr-missing", "profile_acr")]
    [InlineData("auth-time-old", "profile_auth_age")]
    [InlineData("auth-time-missing", "profile_auth_time")]
    [InlineData("auth-time-future", "profile_auth_age")]
    [InlineData("lifetime", "profile_lifetime")]
    [InlineData("expired", "token_expired")]
    [InlineData("subject-missing", "profile_subject")]
    public async Task A_Wrongly_Profiled_Token_Is_Refused_Counted_Under_Its_Cause_And_Nothing_Is_Called(
        string defect, string expectedCause)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var token = defect switch
        {
            "audience" => Sign(("aud", "learnstack-api")),
            "issuer" => Sign(("iss", _tenant.Issuer)),
            "typ" => _operator.Token(Payload([]), header: Header("RS256", "at+jwt")),
            "alg" => SignWithSha384(),
            "signature" => Sign()[..^4] + "AAAA",
            "azp" => Sign(("azp", "learnstack-web")),
            "acr" => Sign(("acr", "0")),
            "acr-missing" => SignWithout("acr"),
            "auth-time-old" => Sign(("auth_time", now - 3600)),
            "auth-time-missing" => SignWithout("auth_time"),
            "auth-time-future" => Sign(("auth_time", now + 600)),
            "lifetime" => Sign(("iat", now - 5), ("exp", now + 3600)),
            "expired" => Sign(("iat", now - 7200), ("exp", now - 3600)),
            _ => SignWithout("sub"),
        };
        using var counted = Counted();

        var response = await Put(token, """{"enabled":false}""");

        await AssertUnauthorized(response);
        _toggle.Calls.Should().BeEmpty("a refused credential reaches no operator service");
        counted.Causes.Should().Contain(expectedCause, "the cause is a closed label an operator can alert on");
    }

    [Fact]
    public async Task A_Missing_Credential_Is_Counted_As_Missing()
    {
        using var counted = Counted();
        using var request = new HttpRequestMessage(HttpMethod.Put, Path)
        {
            Content = new StringContent("""{"enabled":false}""", System.Text.Encoding.UTF8, "application/json"),
        };

        await AssertUnauthorized(await _client.SendAsync(request));

        counted.Causes.Should().Contain("token_missing");
    }

    [Theory]
    [InlineData("redirect")]
    [InlineData("oversized-keys")]
    [InlineData("foreign-jwks-uri")]
    public async Task Unsafe_Issuer_Metadata_Refuses_Every_Operator_And_Is_Counted(string fault)
    {
        // The scheme's own transport follows no redirect, reads at most 256 KB and fetches nothing
        // outside the issuer's origin, so a hostile or broken metadata endpoint cannot choose the
        // keys the operator scheme trusts. An unreachable issuer refuses everyone (no last known
        // good), which is the safe side of an outage.
        var misbehaving = await TenantAuthenticationIssuer.StartAsync();
        var foreign = await TenantAuthenticationIssuer.StartAsync();
        try
        {
            TenantAuthenticationSigningKey? signer = null;
            switch (fault)
            {
                case "redirect":
                    misbehaving.DiscoveryStatusCode = StatusCodes.Status302Found;
                    misbehaving.DiscoveryRedirect = misbehaving.CanaryAddress;
                    break;
                case "oversized-keys":
                    misbehaving.JwksResponse = new string(' ', 300 * 1024);
                    break;
                default:
                    // A second, live origin that would happily serve a key set and so make a token
                    // signed by its key valid, if the discovery document's address were followed.
                    misbehaving.DiscoveryResponse = misbehaving.DiscoveryDocument.Replace(
                        misbehaving.Issuer + "/keys", foreign.Issuer + "/keys", StringComparison.Ordinal);
                    signer = foreign.CurrentKey;
                    break;
            }

            var toggle = new RecordingToggle();
            await using var factory = new PlatformOperatorApiFactory(_tenant, misbehaving, toggle, grantSubject: Subject);
            using var client = factory.CreateClient();
            using var counted = Counted();
            using var request = new HttpRequestMessage(HttpMethod.Put, Path)
            {
                Headers = { { "Authorization", "Bearer " + misbehaving.Token(misbehaving.Payload(
                    ("aud", "learnstack-platform"), ("azp", "learnstack-operator"), ("acr", "mfa"),
                    ("auth_time", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 30), ("sub", Subject)), key: signer) } },
                Content = new StringContent("""{"enabled":false}""", System.Text.Encoding.UTF8, "application/json"),
            };

            var response = await client.SendAsync(request);

            await AssertUnauthorized(response);
            toggle.Calls.Should().BeEmpty();
            counted.MetadataFailures.Should().BeGreaterThan(0,
                "the transport refusal is counted where it happens; the request itself finds no key");
            misbehaving.CanaryRequests.Should().Be(0, "no redirect target is ever dereferenced");
            foreign.JwksRequests.Should().Be(0, "a key set at another origin is never fetched");
        }
        finally
        {
            await foreign.DisposeAsync();
            await misbehaving.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_Tenant_Realm_Token_Is_Refused_On_The_Operator_Surface()
    {
        // Signed by the tenant issuer with the tenant audience and client: the operator scheme
        // validates its own pinned issuer and audience, so it cannot be admitted here.
        var tenantToken = _tenant.Token(_tenant.Payload());

        var response = await Put(tenantToken, """{"enabled":false}""");

        await AssertUnauthorized(response);
        _toggle.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task An_Authenticated_Operator_Without_The_Grant_Is_Forbidden_And_Nothing_Is_Called()
    {
        var response = await Put(Sign(("sub", "someone-else")), """{"enabled":false}""");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await AssertProblem(response, "forbidden");
        _toggle.Calls.Should().BeEmpty("admission is refused before any operator service, so before any bypass connection");
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    [InlineData("this-ticket-is-far-too-long-to-be-a-ticket-reference-it-exceeds-sixty-four")]
    public async Task A_Malformed_Ticket_Is_A_Validation_Failure_Not_A_Permission_One(string ticket)
    {
        var response = await Put(Valid(), JsonSerializer.Serialize(new { enabled = false, ticket }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertProblem(response, "validation_failed");
        _toggle.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"ticket":"INC-1"}""")]
    [InlineData("""{"enabled":null}""")]
    public async Task A_Body_Without_The_State_Is_A_Validation_Failure(string body)
    {
        var response = await Put(Valid(), body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _toggle.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task An_Unknown_Killswitch_Key_Is_Not_Found()
    {
        _toggle.Outcome = KillswitchToggleOutcome.UnknownKey;

        var response = await Put(Valid(), """{"enabled":false}""");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_Failed_Audit_Write_Is_A_Retryable_503_Not_A_Success()
    {
        _toggle.Failure = new AuditWriteFailedException("forced");

        var response = await Put(Valid(), """{"enabled":false}""");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        await AssertProblem(response, "audit_unavailable");
        response.Headers.RetryAfter.Should().NotBeNull("every 503 the API answers carries Retry-After");
    }

    [Fact]
    public async Task The_Operator_Contract_Is_Its_Own_Document_And_The_Tenant_Document_Does_Not_Carry_It()
    {
        var operatorDocument = await _client.GetStringAsync("/openapi/platform-v1.json");
        var tenantDocument = await _client.GetStringAsync("/openapi/v1.json");

        operatorDocument.Should().Contain("/api/platform/v1/killswitches/{key}");
        tenantDocument.Should().NotContain("/api/platform", "the tenant contract is pinned and untouched (ADR-0101 § 4)");
    }

    [Fact]
    public async Task Without_An_Issuer_The_Surface_And_Its_Document_Do_Not_Exist()
    {
        await using var absent = new PlatformOperatorApiFactory(_tenant, _operator, new RecordingToggle(), configured: false);
        using var client = absent.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Put, Path)
        {
            Headers = { { "Authorization", "Bearer " + Valid() } },
            Content = JsonContent.Create(new { enabled = false }),
        };
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "no issuer means no operator route");
        (await client.GetAsync("/openapi/platform-v1.json")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_Operator_Scheme_Is_Not_The_Default_And_The_Tenant_Scheme_Stays_The_Only_One_Without_An_Issuer()
    {
        var withIssuer = await _factory.Services.GetRequiredService<Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider>()
            .GetAllSchemesAsync();
        withIssuer.Select(scheme => scheme.Name).Should().BeEquivalentTo([TenantAuthenticationRegistration.Scheme, "PlatformOperator"]);
        (await _factory.Services.GetRequiredService<Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider>()
            .GetDefaultAuthenticateSchemeAsync())!.Name.Should().Be(TenantAuthenticationRegistration.Scheme,
                "the tenant scheme stays the default and the only scheme on /api/v1 (ADR-0064 Amendment 9)");

        await using var absent = new PlatformOperatorApiFactory(_tenant, _operator, new RecordingToggle(), configured: false);
        (await absent.Services.GetRequiredService<Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider>()
            .GetAllSchemesAsync()).Select(scheme => scheme.Name).Should().Equal(TenantAuthenticationRegistration.Scheme);
    }

    [Fact]
    public void The_Operator_Surface_Is_Exactly_One_Authorized_Put_Endpoint_Under_Its_Prefix()
    {
        // The tenant surface census excludes this surface by exact type (it has its own scheme), so
        // its population is closed here instead: every routed endpoint under /api/platform (whatever
        // the case of its pattern) or carrying the operator metadata anywhere, its verb, and the
        // policy that guards it. A second endpoint, another verb or a missing policy fails.
        var endpoints = _factory.Services.GetServices<Microsoft.AspNetCore.Routing.EndpointDataSource>()
            .SelectMany(source => source.Endpoints).OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText!.TrimStart('/')
                    .StartsWith("api/platform", StringComparison.OrdinalIgnoreCase)
                || endpoint.Metadata.GetMetadata<InfrastructureOperationMetadata>()
                    ?.Kind == InfrastructureOperationKind.PlatformOperator)
            .ToList();

        var only = endpoints.Should().ContainSingle().Subject;
        only.RoutePattern.RawText.Should().Be("/api/platform/v1/killswitches/{key}");
        only.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IHttpMethodMetadata>()!.HttpMethods.Should().Equal("PUT");
        only.Metadata.GetOrderedMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>()
            .Select(data => data.Policy).Should().Contain(PlatformOperatorAuthentication.PolicyName,
                "an operator endpoint without the authenticated-operator policy would admit an anonymous caller");
        only.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>().Should().BeNull();
    }

    private string Header(string alg, string typ) => JsonSerializer.Serialize(new { alg, typ, kid = _operator.CurrentKey.Kid });

    // Correctly signed, with an algorithm the scheme does not allow: only the algorithm is wrong.
    private string SignWithSha384()
    {
        var input = TenantAuthenticationIssuer.Base64Url(System.Text.Encoding.UTF8.GetBytes(Header("RS384", "JWT")))
            + "." + TenantAuthenticationIssuer.Base64Url(System.Text.Encoding.UTF8.GetBytes(Payload([])));
        var signature = _operator.CurrentKey.Rsa.SignData(
            System.Text.Encoding.ASCII.GetBytes(input),
            System.Security.Cryptography.HashAlgorithmName.SHA384,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        return input + "." + TenantAuthenticationIssuer.Base64Url(signature);
    }

    private string Valid() => Sign();

    private static CauseListener Counted() => new();

    /// <summary>Captures the <c>cause</c> label of every refusal counted while it is alive.</summary>
    private sealed class CauseListener : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentBag<string> _causes = [];
        private long _metadataFailures;

        public CauseListener()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == PlatformOperatorMetrics.MeterName
                    && instrument.Name is PlatformOperatorMetrics.RefusalCounterName or PlatformOperatorMetrics.MetadataFailureCounterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                if (instrument.Name == PlatformOperatorMetrics.MetadataFailureCounterName)
                {
                    Interlocked.Add(ref _metadataFailures, value);
                }

                foreach (var tag in tags)
                {
                    if (tag is { Key: "cause", Value: string cause })
                    {
                        _causes.Add(cause);
                    }
                }
            });
            _listener.Start();
        }

        public IReadOnlyCollection<string> Causes => _causes;

        public long MetadataFailures => Interlocked.Read(ref _metadataFailures);

        public void Dispose() => _listener.Dispose();
    }

    private string Sign(params (string Name, object? Value)[] replacements) => _operator.Token(Payload(replacements));

    private string SignWithout(string name)
    {
        using var baseline = JsonDocument.Parse(Payload([]));
        return _operator.Token("{" + string.Join(",", baseline.RootElement.EnumerateObject()
            .Where(property => property.Name != name)
            .Select(property => JsonSerializer.Serialize(property.Name) + ":" + property.Value.GetRawText())) + "}");
    }

    private string Payload((string Name, object? Value)[] replacements)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var claims = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["iss"] = _operator.Issuer,
            ["sub"] = Subject,
            ["aud"] = "learnstack-platform",
            ["iat"] = now - 5,
            ["exp"] = now + 600,
            ["azp"] = "learnstack-operator",
            ["acr"] = "mfa",
            ["auth_time"] = now - 30,
        };
        foreach (var (name, value) in replacements)
        {
            claims[name] = value;
        }

        return JsonSerializer.Serialize(claims);
    }

    private async Task<HttpResponseMessage> Put(string token, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, Path)
        {
            Headers = { { "Authorization", "Bearer " + token } },
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        return await _client.SendAsync(request);
    }

    private static async Task AssertUnauthorized(HttpResponseMessage response)
    {
        var diagnostics = response.Headers + await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, diagnostics);
        response.Headers.WwwAuthenticate.Should().ContainSingle(diagnostics).Which.Scheme.Should().Be("Bearer");
        await AssertProblem(response, "unauthorized");
    }

    private static async Task AssertProblem(HttpResponseMessage response, string code)
    {
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain($"\"code\":\"{code}\"");
    }

    private sealed class RecordingToggle : IKillswitchToggle
    {
        public List<PlatformAdmission> Calls { get; } = [];
        public KillswitchToggleOutcome Outcome { get; set; } = KillswitchToggleOutcome.Applied;
        public Exception? Failure { get; set; }

        public Task<KillswitchToggleResult> ToggleAsync(
            PlatformAdmission admission, KillswitchKey key, bool enabled, CancellationToken cancellationToken = default)
        {
            Calls.Add(admission);
            return Failure is not null
                ? Task.FromException<KillswitchToggleResult>(Failure)
                : Task.FromResult(new KillswitchToggleResult(Outcome, key, true, enabled));
        }
    }

    private sealed class PlatformOperatorApiFactory(
        TenantAuthenticationIssuer tenant,
        TenantAuthenticationIssuer @operator,
        IKillswitchToggle toggle,
        string grantSubject = Subject,
        bool configured = true) : ConstructionOnlyApiFactory
    {
        protected override void ConfigureTestHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Development);
            builder.UseSetting(TenantAuthenticationOptions.IssuerConfigurationKey, tenant.Issuer);
            if (configured)
            {
                builder.UseSetting("PlatformOperator:Issuer", @operator.Issuer);
                builder.UseSetting("PlatformOperator:AcceptedAcr:0", "mfa");
                builder.UseSetting("PlatformOperator:AuthorizedParties:0", "learnstack-operator");
                builder.UseSetting("PlatformOperator:Grants:0:Subject", grantSubject);
                builder.UseSetting("PlatformOperator:Grants:0:Permissions:0", PlatformPermissions.KillswitchWrite.Key);
            }

            builder.ConfigureTestServices(services => services.AddScoped(_ => toggle));
        }
    }
}
