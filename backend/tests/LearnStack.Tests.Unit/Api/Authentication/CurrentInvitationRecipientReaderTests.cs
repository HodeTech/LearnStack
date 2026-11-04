using System.Net;
using System.Reflection;
using System.Text;
using FluentAssertions;
using LearnStack.Api.Authentication;
using LearnStack.Api.Identity;
using LearnStack.Api.Tenancy;
using LearnStack.Modules.Identity.Application.Contracts.Invitations;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.SharedKernel.Hosting;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using LearnStack.SharedKernel.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Authentication;

public sealed class CurrentInvitationRecipientReaderTests
{
    private const string Issuer = "https://recipient.example.invalid/realms/learnstack";
    private const string Subject = "recipient-subject";
    private const string Email = "verified-canary@example.invalid";
    private const string Good = "{\"sub\":\"recipient-subject\",\"email\":\"verified-canary@example.invalid\",\"email_verified\":true}";
    private static readonly TenantId Tenant = TenantId.From(Guid.Parse("01930000-0000-7000-8000-00000000b501"));
    private static readonly UserId Actor = UserId.From(Guid.Parse("01930000-0000-7000-8000-00000000b502"));

    [Fact]
    public async Task Observation_uses_fixed_endpoint_exact_captured_token_and_no_cache()
    {
        using var fixture = await Fixture.CreateAsync((request, _) =>
        {
            request.Method.Should().Be(HttpMethod.Get);
            request.RequestUri.Should().Be(new Uri(Issuer + "/protocol/openid-connect/userinfo"));
            request.Headers.Authorization!.Scheme.Should().Be("Bearer");
            string.Equals(request.Headers.Authorization.Parameter, Token(), StringComparison.Ordinal).Should().BeTrue();
            request.Headers.CacheControl.Should().BeEquivalentTo(new { NoCache = true, NoStore = true });
            return Task.FromResult(Response(Good));
        });

        var result = await fixture.Reader.ReadAsync(fixture.Invocation);

        result.IsSuccess.Should().BeTrue();
        string.Equals(result.Value!.Email, Email, StringComparison.Ordinal).Should().BeTrue();
        result.Value!.IsFor(fixture.Invocation).Should().BeTrue();
        result.Value!.ToString().Should().Be(nameof(CurrentInvitationRecipientEvidence));
        fixture.Handler.Calls.Should().Be(1);
    }

    [Theory]
    [InlineData("wrong-sub")]
    [InlineData("duplicate-sub")]
    [InlineData("duplicate-email")]
    [InlineData("duplicate-verified")]
    [InlineData("string-verified")]
    [InlineData("unverified")]
    [InlineData("numeric-sub")]
    [InlineData("array-email")]
    [InlineData("missing-email")]
    [InlineData("array-root")]
    [InlineData("malformed")]
    public async Task Unusable_claims_are_bounded_forbidden_without_reflecting_provider_data(string mutation)
    {
        var body = mutation switch
        {
            "wrong-sub" => Good.Replace(Subject, "other", StringComparison.Ordinal),
            "duplicate-sub" => Good.Replace("{", "{\"sub\":\"recipient-subject\",", StringComparison.Ordinal),
            "duplicate-email" => Good.Replace("{", "{\"email\":\"second@example.invalid\",", StringComparison.Ordinal),
            "duplicate-verified" => Good.Replace("{", "{\"email_verified\":true,", StringComparison.Ordinal),
            "string-verified" => Good.Replace("true", "\"true\"", StringComparison.Ordinal),
            "unverified" => Good.Replace("true", "false", StringComparison.Ordinal),
            "numeric-sub" => Good.Replace("\"recipient-subject\"", "7", StringComparison.Ordinal),
            "array-email" => Good.Replace("\"verified-canary@example.invalid\"", "[]", StringComparison.Ordinal),
            "missing-email" => "{\"sub\":\"recipient-subject\",\"email_verified\":true}",
            "array-root" => "[]",
            _ => "{",
        };
        using var fixture = await Fixture.CreateAsync((_, _) => Task.FromResult(Response(body)));

        var result = await fixture.Reader.ReadAsync(fixture.Invocation);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("forbidden");
        result.Error!.ToString().Contains(Email, StringComparison.Ordinal).Should().BeFalse();
        result.Error!.ToString().Contains(Subject, StringComparison.Ordinal).Should().BeFalse();
    }

    [Theory]
    [InlineData("header")]
    [InlineData("subject")]
    [InlineData("credential")]
    [InlineData("request")]
    [InlineData("path")]
    [InlineData("method")]
    [InlineData("endpoint")]
    [InlineData("actor")]
    [InlineData("invocation")]
    [InlineData("ancestor")]
    [InlineData("entry")]
    [InlineData("disposed")]
    [InlineData("cancelled")]
    public async Task Evidence_dies_when_its_exact_source_changes(string change)
    {
        using var fixture = await Fixture.CreateAsync((_, _) => Task.FromResult(Response(Good)));
        var result = await fixture.Reader.ReadAsync(fixture.Invocation);
        result.IsSuccess.Should().BeTrue();
        fixture.Change(change);

        result.Value!.IsFor(fixture.Invocation).Should().BeFalse();
    }

    [Fact]
    public async Task Header_replacement_during_provider_io_invalidates_the_observation()
    {
        Fixture? active = null;
        using var fixture = await Fixture.CreateAsync((_, _) =>
        {
            active!.Change("header");
            return Task.FromResult(Response(Good));
        });
        active = fixture;

        var result = await fixture.Reader.ReadAsync(fixture.Invocation);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("dependency_unavailable");
    }

    [Fact]
    public async Task An_active_ancestor_cannot_start_provider_io_after_a_nested_act_becomes_current()
    {
        using var fixture = await Fixture.CreateAsync((_, _) => Task.FromResult(Response(Good)));
        fixture.Change("ancestor");
        fixture.Invocation.IsActive.Should().BeTrue();

        var result = await fixture.Reader.ReadAsync(fixture.Invocation);

        result.Error!.Code.Should().Be("dependency_unavailable");
        fixture.Handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_second_observation_for_the_same_invocation_is_not_cached_or_retried()
    {
        using var fixture = await Fixture.CreateAsync((_, _) => Task.FromResult(Response(Good)));
        (await fixture.Reader.ReadAsync(fixture.Invocation)).IsSuccess.Should().BeTrue();

        var repeated = await fixture.Reader.ReadAsync(fixture.Invocation);

        repeated.IsFailure.Should().BeTrue();
        fixture.Handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Acceptance_makes_a_fresh_observation_after_inspection()
    {
        using var fixture = await Fixture.CreateAsync((_, _) => Task.FromResult(Response(Good)));
        var inspection = await fixture.Reader.ReadAsync(fixture.Invocation);
        using var acceptance = await Fixture.CreateAsync((_, _) => Task.FromResult(Response(Good)), accepting: true);

        var result = await acceptance.Reader.ReadAsync(acceptance.Invocation);

        result.IsSuccess.Should().BeTrue();
        result.Value!.IsFor(acceptance.Invocation).Should().BeTrue();
        inspection.Value!.IsFor(acceptance.Invocation).Should().BeFalse();
        fixture.Handler.Calls.Should().Be(1);
        acceptance.Handler.Calls.Should().Be(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Oversize_declared_or_chunked_response_is_unavailable(bool contentLength)
    {
        using var fixture = await Fixture.CreateAsync((_, _) =>
        {
            var response = Response("");
            response.Content = new StreamContent(new NonSeekableStream(new byte[CurrentInvitationRecipientBackchannel.MaximumResponseBytes + 1]));
            if (contentLength) response.Content.Headers.ContentLength = CurrentInvitationRecipientBackchannel.MaximumResponseBytes + 1;
            else response.Content.Headers.ContentLength = null;
            return Task.FromResult(response);
        });

        var result = await fixture.Reader.ReadAsync(fixture.Invocation);

        result.Error!.Code.Should().Be("dependency_unavailable");
        fixture.Handler.Calls.Should().Be(1);
    }

    [Theory]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Provider_errors_and_redirects_are_fixed_unavailable_without_retry(HttpStatusCode status)
    {
        using var fixture = await Fixture.CreateAsync((_, _) => Task.FromResult(new HttpResponseMessage(status)
        { Content = new StringContent(Email) }));

        var result = await fixture.Reader.ReadAsync(fixture.Invocation);

        result.Error!.Code.Should().Be("dependency_unavailable");
        fixture.Handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Private_five_second_budget_returns_unavailable_without_retry()
    {
        using var fixture = await Fixture.CreateAsync(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response(Good);
        });

        var result = await fixture.Reader.ReadAsync(fixture.Invocation);

        result.Error!.Code.Should().Be("dependency_unavailable");
        fixture.Handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Caller_cancellation_keeps_its_meaning_and_closes_transport()
    {
        using var cancellation = new CancellationTokenSource();
        using var fixture = await Fixture.CreateAsync(async (_, token) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response(Good);
        });

        var act = () => fixture.Reader.ReadAsync(fixture.Invocation, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        fixture.Handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Credential_bearing_transport_failure_is_sanitized()
    {
        using var fixture = await Fixture.CreateAsync((_, _) => throw new HttpRequestException(Email + Token()));

        var result = await fixture.Reader.ReadAsync(fixture.Invocation);

        result.Error!.Code.Should().Be("dependency_unavailable");
        result.Error!.ToString().Contains(Email, StringComparison.Ordinal).Should().BeFalse();
        result.Error!.ToString().Contains(Token(), StringComparison.Ordinal).Should().BeFalse();
    }

    [Fact]
    public async Task Sensitive_response_buffer_is_cleared_after_the_observation()
    {
        using var stream = new ObservedStream(Encoding.UTF8.GetBytes(Good));
        using var fixture = await Fixture.CreateAsync((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(stream) }));

        (await fixture.Reader.ReadAsync(fixture.Invocation)).IsSuccess.Should().BeTrue();

        stream.SawSensitiveBytes.Should().BeTrue();
        stream.Destination.ToArray().Should().OnlyContain(value => value == 0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Token_capture_refuses_header_or_selected_token_changes_during_stock_validation(bool changeHeader)
    {
        using var fixture = await Fixture.CreateAsync((_, _) => Task.FromResult(Response(Good)));
        var subject = fixture.Http.Features.Get<TenantCredentialFeature>()!.VerifiedSubject!;
        ValidatedInvitationCredential.BeginValidation(fixture.Http, changeHeader ? Token() : "other-selected-token");
        if (changeHeader) fixture.Change("header");

        var captured = ValidatedInvitationCredential.Capture(fixture.Http, new JsonWebToken(Token()), subject);

        captured.Should().BeNull();
        fixture.Handler.Calls.Should().Be(0);
    }

    [Fact]
    public void Real_transport_disables_redirects_cookies_and_certificate_replacement()
    {
        using var sockets = new SocketsHttpHandler();
        using var backchannel = new CurrentInvitationRecipientBackchannel(sockets);
        sockets.AllowAutoRedirect.Should().BeFalse();
        sockets.UseCookies.Should().BeFalse();
        sockets.ConnectTimeout.Should().Be(TimeSpan.FromSeconds(5));
        using var unsafeSockets = new SocketsHttpHandler();
        unsafeSockets.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => false;

        var act = () => new CurrentInvitationRecipientBackchannel(unsafeSockets);

        act.Should().Throw<InvalidOperationException>();
    }

    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // The controlled transport suite models an already validated JWT; real stock bearer qualification belongs to HTTP integration.
    private static string Token() => Base64UrlEncoder.Encode("{\"alg\":\"RS256\",\"typ\":\"JWT\"}") + "."
        + Base64UrlEncoder.Encode("{\"sub\":\"recipient-subject\"}") + ".AA";

    private sealed class Fixture : IDisposable
    {
        private readonly TenantAuthorizationCandidateSource _candidates;
        private readonly InvocationSource _invocations = new();
        private readonly CurrentInvitationRecipientBackchannel _backchannel;
        private readonly InvitationEntrySource _entry;
        private readonly InvitationEntryObservation _offer;
        private readonly VerifiedProviderSubject _subject = new(Issuer, Subject, null, null, null);
        internal DefaultHttpContext Http { get; } = new();
        internal ControlledContextAccessor Accessor { get; }
        internal ControlledHandler Handler { get; }
        internal CurrentInvitationRecipientReader Reader { get; }
        internal OperationInvocation Invocation { get; private set; } = null!;

        private Fixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond, bool accepting)
        {
            Http.Request.Method = "POST";
            Http.Request.Path = accepting ? "/api/v1/identity/invitation-entry/acceptances" : "/api/v1/identity/invitation-entry/inspections";
            SetEndpoint(accepting ? nameof(InvitationEntryController.Accept) : nameof(InvitationEntryController.Inspect),
                accepting ? typeof(AcceptInvitationCommand) : typeof(InspectInvitationQuery));
            Http.Request.Headers.Authorization = "Bearer " + Token();
            Http.Features.Set(new TenantCredentialFeature
            {
                VerifiedSubject = _subject,
                Identity = new ValidatedTenantIdentity(Issuer, Subject, Actor, null, null)
            });
            ValidatedInvitationCredential.BeginValidation(Http, Token());
            Http.Features.Set(ValidatedInvitationCredential.Capture(Http, new JsonWebToken(Token()), _subject));
            Accessor = new ControlledContextAccessor { HttpContext = Http };
            _offer = new InvitationEntryObservation(Guid.CreateVersion7(), Tenant, null, 0, Guid.CreateVersion7(), 1,
                Actor, true, false, null, null, null);
            _candidates = new TenantAuthorizationCandidateSource(Accessor);
            var authority = new IdentityAuthorityRegistration(IdentityAuthorityId.From(Guid.CreateVersion7()), Issuer);
            var qualification = new IdentityAuthorityQualification(new Lazy<NpgsqlDataSource>(
                () => throw new InvalidOperationException("Controlled provider tests do not open a database.")), authority);
            // This isolated transport suite supplies the immutable startup observation only; it claims no database qualification.
            typeof(IdentityAuthorityQualification).GetField("_current", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(qualification, authority);
            _entry = new InvitationEntrySource(Accessor, new Lookup(_offer), qualification, () => _invocations);
            Handler = new ControlledHandler(respond);
            _backchannel = new CurrentInvitationRecipientBackchannel(Handler);
            Reader = new CurrentInvitationRecipientReader(Accessor,
                TenantAuthenticationOptions.Create(Issuer, DeploymentMode.SaaS), _backchannel);
        }

        internal static async Task<Fixture> CreateAsync(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
            bool accepting = false)
        {
            var fixture = new Fixture(respond, accepting);
            try { await fixture.PrepareAsync(accepting); return fixture; }
            catch { fixture.Dispose(); throw; }
        }

        private async Task PrepareAsync(bool accepting)
        {
            const string hop = "provider-reader-trusted-hop";
            var native = WebEncoders.Base64UrlEncode(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
            var clock = new Clock();
            Http.Request.Host = new HostString("api.example.invalid");
            Http.Connection.RemoteIpAddress = IPAddress.Loopback;
            Http.Features.Set(HostClassification.ForResolution("tenant.example.invalid", new HostResolution(Tenant, null)));
            Http.Features.Set(new TenantSubjectAdmissionProvenance(_subject));
            Http.Features.Set(new SubjectAdmissionDecision(SubjectAdmissionOutcome.Acquired, null));
            Http.Request.Headers[TrustedHopOptions.HostHeaderName] = "tenant.example.invalid";
            Http.Request.Headers[TrustedHopOptions.SecretHeaderName] = hop;
            Http.Request.Headers[NativeSessionSourcePolicy.SecretHeader] = native;
            if (accepting) Http.Request.Headers.IfMatch = "\"0\"";
            var code = _offer.InvitationId.ToString("D") + "." + WebEncoders.Base64UrlEncode(new byte[32]);
            Http.Request.ContentType = "application/json";
            Http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(
                new { invitationId = _offer.InvitationId.ToString("D"), code })));
            var hosts = new EffectiveHostAccessor(Options.Create(new TrustedHopOptions { Networks = ["127.0.0.0/8"], Secrets = [hop] }));
            var policy = NativeSessionSourcePolicy.Create(native, null, null, [hop], clock.UtcNow);
            await new NativeSessionSourceMiddleware(_ => Task.CompletedTask).InvokeAsync(Http, hosts, policy, clock);
            var prepared = await _entry.PrepareAsync(Http);
            prepared.IsSuccess.Should().BeTrue();
            var context = TenantContextFactory.CreateForInvitationOffer(Tenant, null, _offer, null).Value!;
            var candidate = _candidates.ForValidatedScope(Tenant, null)!;
            candidate.Should().NotBeNull();
            Invocation = _invocations.Open(_entry.Request!, accepting
                ? OperationPolicy.ForInvitationEntry<AcceptInvitationCommand>("identity.invitation.accept")
                : OperationPolicy.ForInvitationEntry<InspectInvitationQuery>("identity.invitation.inspect"), context, candidate);
        }

        internal void Change(string change)
        {
            if (change == "header") Http.Request.Headers.Authorization = "Bearer replacement";
            if (change == "subject") Http.Features.Get<TenantCredentialFeature>()!.VerifiedSubject = _subject with { Subject = "other" };
            if (change == "credential") Http.Features.Set<ValidatedInvitationCredential>(null);
            if (change == "request") Accessor.HttpContext = new DefaultHttpContext();
            if (change == "path") Http.Request.Path = "/other";
            if (change == "method") Http.Request.Method = "GET";
            if (change == "endpoint") Http.SetEndpoint(new Endpoint(null, EndpointMetadataCollection.Empty, "replacement"));
            if (change == "actor") Http.Features.Get<TenantCredentialFeature>()!.Identity = new ValidatedTenantIdentity(Issuer, Subject,
                UserId.From(Guid.CreateVersion7()), null, null);
            if (change == "invocation") _invocations.Close();
            if (change == "ancestor") _invocations.Open(new InspectInvitationQuery(_offer.InvitationId, "other-request"),
                Invocation.Policy, Invocation.Context, Invocation.Candidate!);
            if (change == "entry") Http.Features.Set<InvitationEntrySource>(null);
            if (change == "disposed") Reader.Dispose();
            if (change == "cancelled") Http.RequestAborted = new CancellationToken(true);
        }

        public void Dispose() { Reader.Dispose(); _backchannel.Dispose(); _candidates.Dispose(); _entry.Dispose(); Http.Request.Body.Dispose(); }

        private void SetEndpoint(string action, Type request)
        {
            var controller = typeof(InvitationEntryController);
            Http.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(new ControllerActionDescriptor
            {
                ControllerTypeInfo = controller.GetTypeInfo(),
                MethodInfo = controller.GetMethod(action)!,
            }, new OperationPolicyAttribute(request)), "invitation-recipient-test"));
        }
    }

    private sealed class Lookup(InvitationEntryObservation offer) : IInvitationEntryLookup
    {
        public Task<InvitationEntryObservation?> LookupAsync(Guid invitationId, ReadOnlyMemory<byte> verifierDigest,
            TenantId hostTenant, GlobalIdentitySubject subject, CancellationToken cancellationToken = default) =>
            Task.FromResult<InvitationEntryObservation?>(offer);
        public Task<InvitationEntryObservation?> RecheckAsync(InvitationEntryScopeWitness witness, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Provider observation must not acquire business locks or run acceptance rechecks.");
    }

    private sealed class InvocationSource : OperationInvocation.MintingSource, IOperationInvocationContext
    {
        private OperationInvocation? _current;
        private readonly HashSet<OperationInvocation> _open = [];
        public OperationInvocation? CurrentInvocation => _current;
        public ProtectedOperationDecision? CurrentDecision => null;
        internal OperationInvocation Open(object request, OperationPolicy policy, ITenantContext context, AuthorizationCandidate candidate)
        {
            _current = ForInvocation(request, policy, context, candidate, null, CancellationToken.None);
            _open.Add(_current);
            return _current;
        }
        internal void Close() { _current = null; _open.Clear(); }
        protected override bool IsCurrent(OperationInvocation invocation) => _open.Contains(invocation);
    }

    private sealed class ControlledContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.Parse("2026-11-04T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class ControlledHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return respond(request, cancellationToken); }
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class ObservedStream(byte[] bytes) : MemoryStream(bytes)
    {
        internal Memory<byte> Destination { get; private set; }
        internal bool SawSensitiveBytes { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Destination.IsEmpty) Destination = buffer;
            var read = await base.ReadAsync(buffer, cancellationToken);
            if (read > 0) SawSensitiveBytes |= Encoding.UTF8.GetString(buffer.Span[..read]).Contains(Email, StringComparison.Ordinal);
            return read;
        }
    }
}
