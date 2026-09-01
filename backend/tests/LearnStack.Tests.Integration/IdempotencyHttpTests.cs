using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Api.Common;
using LearnStack.Api.Idempotency;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LearnStack.Tests.Integration;

/// <summary>
/// <c>Idempotency-Key</c>, per
/// <see href="../../../docs/standards/04-api-design.md">Standards 04
/// § Idempotency</see> and
/// <see href="../../../docs/decisions/0037-idempotency-key-contract.md">ADR-0037</see>:
/// the server stores <c>(key, response)</c> and a repeat returns the stored one
/// instead of doing the work twice.
/// </summary>
public sealed class IdempotencyHttpTests(IdempotencyFixture fixture)
    : IClassFixture<IdempotencyFixture>
{
    private readonly HttpClient _client = fixture.CreateClientForTenant(IdempotencyFixture.TenantA);

    [Fact]
    public async Task A_Repeat_Replays_The_First_Response_Without_Doing_The_Work_Again()
    {
        var key = NewKey();
        SideEffectProbeController.Reset();

        var first = await PostAsync("/api/v1/sideeffectprobe", key);
        var second = await PostAsync("/api/v1/sideeffectprobe", key);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        // The whole point: the body is identical because it IS the first one.
        (await second.Content.ReadAsStringAsync())
            .Should().Be(await first.Content.ReadAsStringAsync());

        SideEffectProbeController.Invocations.Should().Be(1,
            "the operation must run once, however many times the client asks");
    }

    [Fact]
    public async Task A_Replay_Says_So()
    {
        // A client retrying after a timeout cannot otherwise tell whether its
        // second call did the work or collected the first one's answer.
        var key = NewKey();
        SideEffectProbeController.Reset();

        var first = await PostAsync("/api/v1/sideeffectprobe", key);
        var second = await PostAsync("/api/v1/sideeffectprobe", key);

        first.Headers.Contains(IdempotentAttribute.ReplayedHeaderName).Should().BeFalse();
        second.Headers.GetValues(IdempotentAttribute.ReplayedHeaderName)
            .Single().Should().Be("true");
    }

    [Fact]
    public async Task A_Different_Key_Does_The_Work_Again()
    {
        SideEffectProbeController.Reset();

        await PostAsync("/api/v1/sideeffectprobe", NewKey());
        await PostAsync("/api/v1/sideeffectprobe", NewKey());

        SideEffectProbeController.Invocations.Should().Be(2);
    }

    [Theory]
    [InlineData(null, "absent")]
    [InlineData("short", "under the minimum length")]
    [InlineData("has a space in it", "a space is not printable-ASCII-no-space")]
    // Escaped rather than embedded: the raw byte an earlier version carried is
    // invisible in a diff, in a review, and in every editor that renders it.
    [InlineData("control\u0001char-key", "a control character")]
    public async Task A_Missing_Or_Malformed_Key_Is_A_400(string? key, string why)
    {
        var response = await PostAsync("/api/v1/sideeffectprobe", key);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, why);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("code").GetString().Should().Be("validation_failed");
        problem.GetProperty("errors")
            .TryGetProperty(IdempotentAttribute.ErrorsKey, out _).Should().BeTrue(
                "the camelCase form survives the errors-map projection unchanged; "
                + "camelCasing the literal header name yields 'idempotency-Key'");
    }

    [Fact]
    public async Task A_Key_Over_The_Maximum_Length_Is_A_400()
    {
        // The upper bound is the one that keeps a client-chosen value out of a
        // database column it does not fit; only the lower one was covered.
        var response = await PostAsync(
            "/api/v1/sideeffectprobe", new string('k', IdempotentAttribute.MaxKeyLength + 1));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_Key_At_The_Maximum_Length_Is_Accepted()
    {
        var response = await PostAsync(
            "/api/v1/sideeffectprobe", new string('k', IdempotentAttribute.MaxKeyLength));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_Repeated_Key_Header_Is_Refused_Not_Resolved_By_First_Or_Last()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri("/api/v1/sideeffectprobe", UriKind.Relative));
        request.Headers.Add(IdempotentAttribute.HeaderName, NewKey());
        request.Headers.Add(IdempotentAttribute.HeaderName, NewKey());

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- what is recorded, and what is released ----------------------------

    [Fact]
    public async Task A_Thrown_Attempt_Does_Not_Pin_The_Key()
    {
        // Storing a failure would make every retry replay it for the retention
        // window, turning one transient fault into a day of them.
        var key = NewKey();
        SideEffectProbeController.Reset();

        var failed = await PostAsync("/api/v1/sideeffectprobe?fail=true", key);
        failed.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        var retried = await PostAsync("/api/v1/sideeffectprobe?fail=true", key);

        retried.StatusCode.Should().Be(HttpStatusCode.InternalServerError,
            "the key was released, so the retry is allowed to run");
        SideEffectProbeController.Invocations.Should().Be(2);
    }

    [Fact]
    public async Task A_Returned_5xx_Does_Not_Pin_The_Key_Either()
    {
        // The covering test exercised the THROW path. A handler that returns a
        // 503 without throwing is a different branch, and it was untested.
        var key = NewKey();
        SideEffectProbeController.Reset();

        await PostAsync("/api/v1/sideeffectprobe?status=503", key);
        var retried = await PostAsync("/api/v1/sideeffectprobe?status=503", key);

        retried.Headers.Contains(IdempotentAttribute.ReplayedHeaderName).Should().BeFalse();
        SideEffectProbeController.Invocations.Should().Be(2);
    }

    [Fact]
    public async Task A_429_Does_Not_Pin_The_Key()
    {
        // Same reasoning as a 5xx: it describes a condition, not an outcome.
        // Pinning it would answer "too many requests" for the whole window.
        var key = NewKey();
        SideEffectProbeController.Reset();

        await PostAsync("/api/v1/sideeffectprobe?status=429", key);
        await PostAsync("/api/v1/sideeffectprobe?status=429", key);

        SideEffectProbeController.Invocations.Should().Be(2);
    }

    [Fact]
    public async Task A_Deterministic_4xx_Is_Recorded_And_Replayed()
    {
        // A 400 is the operation's answer, and it is the same answer every
        // time. Re-running it would defeat the key for no gain.
        var key = NewKey();
        SideEffectProbeController.Reset();

        await PostAsync("/api/v1/sideeffectprobe?status=400", key);
        var retried = await PostAsync("/api/v1/sideeffectprobe?status=400", key);

        retried.Headers.GetValues(IdempotentAttribute.ReplayedHeaderName).Single()
            .Should().Be("true");
        SideEffectProbeController.Invocations.Should().Be(1);
    }

    [Theory]
    [InlineData(408)]
    [InlineData(425)]
    public async Task A_Retryable_Status_Does_Not_Pin_The_Key(int status)
    {
        // 408 and 425 say "the exchange did not work", not "here is the
        // outcome". Recording either would answer a transport condition for the
        // whole retention window.
        var key = NewKey();
        SideEffectProbeController.Reset();

        await PostAsync($"/api/v1/sideeffectprobe?status={status}", key);
        await PostAsync($"/api/v1/sideeffectprobe?status={status}", key);

        SideEffectProbeController.Invocations.Should().Be(2);
    }

    [Fact]
    public async Task A_Concurrency_Conflict_Is_Not_Pinned_To_The_Key()
    {
        // A 409 concurrency_conflict tells the client to re-read and re-submit.
        // Recording it makes that impossible: the key would answer "conflict"
        // for the whole window and the client could never succeed with it.
        // Status alone cannot separate this from an outcome — both are 409 —
        // which is why the classification reads the error code.
        var key = NewKey();
        SideEffectProbeController.Reset();

        var first = await PostAsync("/api/v1/sideeffectprobe?conflict=true", key);
        first.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadCodeAsync(first)).Should().Be("concurrency_conflict");

        await PostAsync("/api/v1/sideeffectprobe?conflict=true", key);

        SideEffectProbeController.Invocations.Should().Be(2,
            "the key was released, so the client's retry is allowed to run");
    }

    [Fact]
    public async Task A_Response_Too_Large_To_Store_Refuses_The_Retry_Rather_Than_Rerunning_It()
    {
        // The cap keeps the entry ceiling a memory ceiling too. But releasing
        // the key when it is hit would let the operation run twice — silently,
        // with a 2xx both times, on the surface Standards 04 reserves for
        // payments. The outcome is tombstoned instead: it happened, the answer
        // is gone, and the retry is told so.
        var key = NewKey();
        SideEffectProbeController.Reset();

        var first = await PostAsync("/api/v1/sideeffectprobe?big=true", key);
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var retried = await PostAsync("/api/v1/sideeffectprobe?big=true", key);

        retried.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadCodeAsync(retried)).Should().Be("idempotency_outcome_unavailable");
        SideEffectProbeController.Invocations.Should().Be(1,
            "the operation must not run a second time just because its answer was too big");
    }

    [Fact]
    public async Task A_Result_That_Throws_After_Writing_Part_Of_The_Body_Still_Answers_A_Problem_Details_500()
    {
        // The filter buffers the response body. MVC returns normally from
        // next() when the result throws and rethrows only after the filter
        // unwinds, so at that point the buffer can already hold a half-written
        // body. Copying it out would start the response — handing the client a
        // truncated 2xx and taking the exception away from UseExceptionHandler,
        // which can no longer write its 500 once the response has started.
        var key = NewKey();
        SideEffectProbeController.Reset();

        var response = await PostAsync("/api/v1/sideeffectprobe?partial=true", key);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await ReadCodeAsync(response)).Should().Be("internal_error",
            "the client gets the error shape, not the bytes the formatter managed to write");

        var retried = await PostAsync("/api/v1/sideeffectprobe?partial=true", key);
        retried.StatusCode.Should().Be(HttpStatusCode.InternalServerError,
            "a thrown attempt releases its key");
        SideEffectProbeController.Invocations.Should().Be(2);
    }

    // ---- replay fidelity ---------------------------------------------------

    [Fact]
    public async Task A_Replayed_201_Keeps_Its_Location_And_Every_Other_Outcome_Header()
    {
        // A 201 without its Location is not the same response — the location IS
        // the answer. A client that retried through a timeout would otherwise
        // learn the resource exists and never learn where.
        var key = NewKey();
        SideEffectProbeController.Reset();

        var first = await PostAsync("/api/v1/sideeffectprobe?created=true", key);
        var second = await PostAsync("/api/v1/sideeffectprobe?created=true", key);

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.Created);
        second.Headers.Location.Should().Be(first.Headers.Location);
        second.Headers.ETag.Should().Be(first.Headers.ETag);
        second.Headers.GetValues(SideEffectProbeController.OutcomeHeader).Single()
            .Should().Be(first.Headers.GetValues(SideEffectProbeController.OutcomeHeader).Single());
        second.Content.Headers.ContentType!.ToString()
            .Should().Be(first.Content.Headers.ContentType!.ToString());
    }

    [Fact]
    public async Task A_Replay_Does_Not_Reuse_The_First_Attempts_Correlation_Id()
    {
        // The correlation id belongs to the exchange that is happening now.
        // Replaying the first one would point a support engineer at a trace
        // that has nothing to do with the request they are holding.
        var key = NewKey();
        SideEffectProbeController.Reset();

        var first = await PostAsync("/api/v1/sideeffectprobe", key);
        var second = await PostAsync("/api/v1/sideeffectprobe", key);

        var firstId = first.Headers.GetValues(CorrelationHeaderMiddleware.HeaderName).Single();
        var secondId = second.Headers.GetValues(CorrelationHeaderMiddleware.HeaderName).Single();

        secondId.Should().NotBe(firstId);
    }

    // ---- the key does not identify a request on its own --------------------

    [Fact]
    public async Task The_Same_Key_On_A_Different_Endpoint_Is_Refused_Not_Replayed()
    {
        var key = NewKey();
        SideEffectProbeController.Reset();

        await PostAsync("/api/v1/sideeffectprobe", key);
        var reused = await PostAsync("/api/v1/otherprobe", key);

        reused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadCodeAsync(reused)).Should().Be("idempotency_key_reuse");
        SideEffectProbeController.Invocations.Should().Be(1,
            "the second endpoint must not run, and must not be told the first one's answer");
    }

    [Fact]
    public async Task The_Same_Key_With_A_Different_Body_Is_Refused()
    {
        // The classic client bug: a key reused after the payload was edited.
        // Replaying would report success about the amount that was NOT sent.
        var key = NewKey();
        SideEffectProbeController.Reset();

        await PostAsync("/api/v1/sideeffectprobe", key, body: new { amount = 100 });
        var reused = await PostAsync("/api/v1/sideeffectprobe", key, body: new { amount = 500 });

        reused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadCodeAsync(reused)).Should().Be("idempotency_key_reuse");
    }

    [Fact]
    public async Task The_Same_Key_From_A_Different_User_In_One_Tenant_Is_Refused()
    {
        // A tenant is not a principal. Two users under one tenant can pick the
        // same client-chosen key, and the second must not be handed the first
        // one's response body.
        var key = NewKey();
        SideEffectProbeController.Reset();

        using var alice = fixture.CreateClientForTenant(
            IdempotencyFixture.TenantA, IdempotencyFixture.Alice);
        using var bob = fixture.CreateClientForTenant(
            IdempotencyFixture.TenantA, IdempotencyFixture.Bob);

        await PostAsync("/api/v1/sideeffectprobe", key, client: alice);
        var asBob = await PostAsync("/api/v1/sideeffectprobe", key, client: bob);

        asBob.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadCodeAsync(asBob)).Should().Be("idempotency_key_reuse");
    }

    [Fact]
    public async Task Two_Anonymous_Callers_Sending_The_Same_Request_Share_The_Answer()
    {
        // The principal component degenerates to "anonymous" when there is no
        // authenticated subject, so two anonymous callers in one tenant CAN
        // collide on a key. That is a decision, not an oversight: with the
        // organization, method, path, query and body all equal, the two requests
        // are indistinguishable to the server, and replaying is the same answer
        // to the same question. ADR-0037 § Scope records it; this pins it.
        var key = NewKey();
        SideEffectProbeController.Reset();

        using var first = fixture.CreateClientForTenant(IdempotencyFixture.TenantA);
        using var second = fixture.CreateClientForTenant(IdempotencyFixture.TenantA);

        await PostAsync("/api/v1/sideeffectprobe", key, client: first);
        var replayed = await PostAsync("/api/v1/sideeffectprobe", key, client: second);

        replayed.Headers.GetValues(IdempotentAttribute.ReplayedHeaderName).Single()
            .Should().Be("true");
        SideEffectProbeController.Invocations.Should().Be(1);
    }

    [Fact]
    public async Task An_Anonymous_Key_Still_Does_Not_Cross_To_An_Authenticated_Caller()
    {
        // The degeneracy stops at "no subject". Once a caller has one, it is in
        // the digest, so an authenticated request cannot collect an anonymous
        // one's response — or the reverse.
        var key = NewKey();
        SideEffectProbeController.Reset();

        await PostAsync("/api/v1/sideeffectprobe", key);
        using var alice = fixture.CreateClientForTenant(
            IdempotencyFixture.TenantA, IdempotencyFixture.Alice);

        var asAlice = await PostAsync("/api/v1/sideeffectprobe", key, client: alice);

        asAlice.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadCodeAsync(asAlice)).Should().Be("idempotency_key_reuse");
    }

    [Fact]
    public async Task Two_Tenants_Using_The_Same_Key_Do_Not_Share_A_Response()
    {
        // The key is client-chosen, so two tenants WILL eventually pick the
        // same one. A flat key space would hand the second one the first one's
        // response body.
        var key = NewKey();
        SideEffectProbeController.Reset();

        var first = await PostAsync("/api/v1/sideeffectprobe", key);
        using var otherTenant = fixture.CreateClientForTenant(IdempotencyFixture.TenantB);
        var second = await PostAsync("/api/v1/sideeffectprobe", key, client: otherTenant);

        second.Headers.Contains(IdempotentAttribute.ReplayedHeaderName).Should().BeFalse(
            "the second tenant's key is its own");
        (await second.Content.ReadAsStringAsync())
            .Should().NotBe(await first.Content.ReadAsStringAsync());
        SideEffectProbeController.Invocations.Should().Be(2);
    }

    [Fact]
    public async Task A_Request_With_No_Resolved_Tenant_Is_Refused()
    {
        // There is no unscoped key space to serve it from, and inventing one
        // is how a key becomes global.
        using var unresolved = fixture.CreateClientForTenant(tenantId: null);

        var response = await PostAsync("/api/v1/sideeffectprobe", NewKey(), client: unresolved);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- concurrency -------------------------------------------------------

    [Fact]
    public async Task A_Concurrent_Duplicate_Is_Refused_While_The_First_Is_Still_Running()
    {
        // The only guard against a genuine double-submit, and it had no test.
        var key = NewKey();
        SideEffectProbeController.Reset();
        SideEffectProbeController.CloseGate();

        var first = PostAsync("/api/v1/sideeffectprobe?hold=true", key);
        await SideEffectProbeController.Entered;

        var concurrent = await PostAsync("/api/v1/sideeffectprobe?hold=true", key);

        concurrent.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadCodeAsync(concurrent)).Should().Be("request_in_progress",
            "the client should retry with this same key — the opposite of what a "
            + "concurrency_conflict asks for, which is why it is a different code");

        SideEffectProbeController.Release();
        (await first).StatusCode.Should().Be(HttpStatusCode.OK);
        SideEffectProbeController.Invocations.Should().Be(1);
    }

    private static string NewKey() => "01HX" + Guid.NewGuid().ToString("N");

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        return problem.GetProperty("code").GetString();
    }

    private async Task<HttpResponseMessage> PostAsync(
        string path, string? key, HttpClient? client = null, object? body = null)
    {
        // Awaited inside, so the request is not disposed while it is being sent.
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative));
        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation(IdempotentAttribute.HeaderName, key);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await (client ?? _client).SendAsync(request);
    }
}

/// <summary>A host whose tenant and user are chosen per request, not per host.</summary>
/// <remarks>
/// The tenant arrives on a header rather than through a mutable singleton. An
/// earlier version switched a host-wide context object and never restored it,
/// which made every test in the class depend on the order the others ran in —
/// Standards 06 § Forbidden bars exactly that.
/// </remarks>
public sealed class IdempotencyFixture : WebApplicationFactory<Program>
{
    public static readonly Guid TenantA = Guid.Parse("018f4d40-0000-7000-8000-00000000000a");
    public static readonly Guid TenantB = Guid.Parse("018f4d40-0000-7000-8000-00000000000b");
    public static readonly Guid Alice = Guid.Parse("018f4d40-0000-7000-8000-0000000000a1");
    public static readonly Guid Bob = Guid.Parse("018f4d40-0000-7000-8000-0000000000b1");

    internal const string TenantHeader = "X-Test-Tenant";
    internal const string UserHeader = "X-Test-User";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment(Environments.Development);
        builder.ConfigureTestServices(services =>
        {
            services.AddControllers(options =>
                    options.Conventions.Insert(0, new TestControllerFilter(
                        typeof(SideEffectProbeController), typeof(OtherProbeController))))
                .AddApplicationPart(typeof(SideEffectProbeController).Assembly);

            services.AddHttpContextAccessor();
            services.RemoveAll<ITenantContext>();
            services.AddScoped<ITenantContext, HeaderTenantContext>();
        });
    }

    /// <summary>A client whose requests resolve to the given tenant, or to none.</summary>
    public HttpClient CreateClientForTenant(Guid? tenantId, Guid? userId = null)
    {
        var client = CreateClient();
        if (tenantId is { } tenant)
        {
            client.DefaultRequestHeaders.Add(TenantHeader, tenant.ToString());
        }

        if (userId is { } user)
        {
            client.DefaultRequestHeaders.Add(UserHeader, user.ToString());
        }

        return client;
    }

    internal sealed class HeaderTenantContext(IHttpContextAccessor accessor) : ITenantContext
    {
        public bool IsResolved => Read(TenantHeader) is not null;

        public TenantId TenantId => Read(TenantHeader) is { } tenant
            ? SharedKernel.Identifiers.TenantId.From(tenant)
            : throw new InvalidOperationException("No tenant on this request.");

        public OrganizationId? OrganizationId => null;

        public UserId? UserId => Read(UserHeader) is { } id
            ? SharedKernel.Identifiers.UserId.From(id)
            : null;

        public string? CorrelationId => null;

        public string? ModuleName => "integration-test";

        private Guid? Read(string header) =>
            accessor.HttpContext?.Request.Headers[header] is { Count: 1 } raw
            && Guid.TryParse(raw[0], out var value)
                ? value
                : null;
    }
}

public sealed class SideEffectProbeController : ApiControllerBase, ITestOnlyController
{
    /// <summary>A header that is part of the outcome and must survive a replay.</summary>
    public const string OutcomeHeader = "X-Probe-Outcome";

    private static int _invocations;
    private static TaskCompletionSource _entered = NewGate();
    private static TaskCompletionSource _release = NewGate();

    public static int Invocations => _invocations;

    /// <summary>Completes once a held request has reached the action body.</summary>
    public static Task Entered => _entered.Task;

    public static void Reset() => Interlocked.Exchange(ref _invocations, 0);

    public static void CloseGate()
    {
        _entered = NewGate();
        _release = NewGate();
    }

    public static void Release() => _release.TrySetResult();

    [HttpPost]
    [Idempotent]
    public async Task<IActionResult> Post(
        [FromQuery] bool fail = false,
        [FromQuery] bool created = false,
        [FromQuery] bool big = false,
        [FromQuery] bool hold = false,
        [FromQuery] bool conflict = false,
        [FromQuery] bool partial = false,
        [FromQuery] int? status = null)
    {
        Interlocked.Increment(ref _invocations);

        if (hold)
        {
            _entered.TrySetResult();
            await _release.Task;
        }

        if (fail)
        {
            throw new InvalidOperationException("probe: a failed attempt must release its key");
        }

        if (conflict)
        {
            return new ProblemDetailsActionResult(EntityTag.ConflictError());
        }

        if (partial)
        {
            return new PartialThenThrowResult();
        }

        if (status is { } explicitStatus)
        {
            return StatusCode(explicitStatus, new { ran = _invocations });
        }

        if (big)
        {
            return Ok(new { blob = new string('x', IdempotentAttribute.MaxStoredResponseBytes + 1) });
        }

        if (created)
        {
            Response.Headers[OutcomeHeader] = "created";
            EntityTag.SetEntityTag(Response, _invocations);
            return Created(
                new Uri($"/api/v1/sideeffectprobe/{_invocations}", UriKind.Relative),
                new { ran = _invocations });
        }

        return Ok(new { ran = _invocations, at = Guid.NewGuid() });
    }

    private static TaskCompletionSource NewGate() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Writes part of a body and then throws, the way an output formatter does
    /// when a converter fails partway through serialising a payload.
    /// </summary>
    private sealed class PartialThenThrowResult : IActionResult
    {
        public async Task ExecuteResultAsync(ActionContext context)
        {
            context.HttpContext.Response.ContentType = "application/json";
            await context.HttpContext.Response.WriteAsync("{\"partial\":\"written-before-the-throw\"");
            throw new InvalidOperationException("probe: the formatter failed mid-body");
        }
    }
}

/// <summary>A second idempotent endpoint, so "same key, different endpoint" is reachable.</summary>
public sealed class OtherProbeController : ApiControllerBase, ITestOnlyController
{
    [HttpPost]
    [Idempotent]
    public IActionResult Post() => Ok(new { probe = "other" });
}
