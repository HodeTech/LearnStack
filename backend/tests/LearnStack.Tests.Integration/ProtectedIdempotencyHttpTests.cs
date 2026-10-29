using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LearnStack.Api.Common;
using LearnStack.Api.Idempotency;
using LearnStack.Infrastructure.Idempotency;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Application.Contracts.Roles;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Idempotency;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace LearnStack.Tests.Integration;

/// <summary>Actual MVC/filter/serialization with explicit component storage and typed dispatch doubles.
/// Real authorization, audit commit and PostgreSQL cleanup are proved by the separate database populations.</summary>
public sealed class ProtectedIdempotencyHttpTests(ProtectedIdempotencyFixture fixture)
    : IClassFixture<ProtectedIdempotencyFixture>
{
    private readonly HttpClient _client = fixture.CreateClient();
    private ProtectedIdempotencyProbeState State => fixture.State;

    [Fact]
    public async Task Created_receipt_replays_only_after_the_original_target_read_and_preserves_outcome_headers()
    {
        State.Reset();
        var key = NewKey();
        using var first = await SendAsync("create", key, null);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal("{\"version\":\"8\"}", await first.Content.ReadAsStringAsync());
        Assert.Equal("\"8\"", first.Headers.ETag!.Tag);
        Assert.EndsWith(State.Target.ToString("D"), first.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Equal(new IdempotencyReplayTarget(IdempotencyReplayTargetKind.Organization, State.Target), State.Stored!.ReplayTarget);

        using var replay = await SendAsync("create", key, null);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        Assert.Equal(first.Headers.Location, replay.Headers.Location);
        Assert.Equal(first.Headers.ETag, replay.Headers.ETag);
        Assert.Equal("retained", Assert.Single(replay.Headers.GetValues("X-Outcome")));
        Assert.Equal("true", Assert.Single(replay.Headers.GetValues(IdempotentAttribute.ReplayedHeaderName)));
        Assert.NotEqual(Assert.Single(first.Headers.GetValues(CorrelationHeaderMiddleware.HeaderName)),
            Assert.Single(replay.Headers.GetValues(CorrelationHeaderMiddleware.HeaderName)));
        Assert.False(replay.Headers.Contains("Set-Cookie"));
        Assert.False(State.Stored.Headers.ContainsKey("Set-Cookie"));
        Assert.False(State.Stored.Headers.ContainsKey("Date"));
        Assert.False(State.Stored.Headers.ContainsKey(CorrelationHeaderMiddleware.HeaderName));
        Assert.Equal(1, State.Mutations);
        Assert.Equal(State.Target, Assert.IsType<ReadOrganizationReceiptQuery>(Assert.Single(State.Reads)).OrganizationId.Value);
        Assert.Equal(1, State.Completions);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("W/\"7\"")]
    [InlineData("\"07\"")]
    [InlineData("\"7\", \"8\"")]
    [InlineData("\"9223372036854775808\"")]
    public async Task Invalid_preconditions_fail_before_claim_and_dispatch(string? tag)
    {
        State.Reset();
        using var response = await SendAsync("rename/" + State.Target.ToString("D"), NewKey(), tag);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", await CodeAsync(response));
        Assert.Equal(0, State.Claims);
        Assert.Equal(0, State.Mutations);
    }

    [Fact]
    public async Task Repeated_preconditions_and_a_command_that_does_not_match_its_header_cannot_dispatch()
    {
        State.Reset();
        using var repeated = await SendAsync("rename/" + State.Target.ToString("D"), NewKey(), "\"7\"", repeated: true);
        Assert.Equal(HttpStatusCode.BadRequest, repeated.StatusCode);
        Assert.Equal(0, State.Claims);
        using var mismatched = await SendAsync("rename/" + State.Target.ToString("D"), NewKey(), "\"7\"",
            new ProtectedIdempotencyProbeInput("Probe", ExpectedOverride: 6));
        Assert.Equal(HttpStatusCode.BadRequest, mismatched.StatusCode);
        Assert.Equal(0, State.Mutations);
        Assert.Equal(1, State.Releases);
    }

    [Fact]
    public async Task Valid_stale_versions_reach_the_handler_and_release_the_key()
    {
        State.Reset();
        var key = NewKey();
        using var first = await SendAsync("rename/" + State.Target.ToString("D"), key, "\"6\"");
        using var retry = await SendAsync("rename/" + State.Target.ToString("D"), key, "\"6\"");
        Assert.Equal(HttpStatusCode.Conflict, first.StatusCode);
        Assert.Equal("concurrency_conflict", await CodeAsync(first));
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
        Assert.Equal(2, State.Mutations);
        Assert.Equal(2, State.Releases);
        Assert.Equal(0, State.Completions);
    }

    [Fact]
    public async Task Canonical_precondition_and_body_are_both_bound_to_the_protected_fingerprint()
    {
        State.Reset();
        var path = "rename/" + State.Target.ToString("D");
        var key = NewKey();
        using var first = await SendAsync(path, key, "\"7\"");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var precondition = await SendAsync(path, key, "\"8\"");
        using var body = await SendAsync(path, key, "\"7\"", new ProtectedIdempotencyProbeInput("Changed"));
        Assert.Equal("idempotency_key_reuse", await CodeAsync(precondition));
        Assert.Equal("idempotency_key_reuse", await CodeAsync(body));
        Assert.Equal(1, State.Mutations);
        Assert.Empty(State.Reads);
    }

    [Theory]
    [InlineData("forbidden", HttpStatusCode.Forbidden)]
    [InlineData("audit_unavailable", HttpStatusCode.ServiceUnavailable)]
    public async Task Failed_fresh_disclosure_keeps_completed_state_and_publishes_no_receipt(string failure, HttpStatusCode status)
    {
        State.Reset();
        var key = NewKey();
        using var first = await SendAsync("create", key, null);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var stored = State.Stored;
        State.ReadFailure = failure;
        using var denied = await SendAsync("create", key, null);
        Assert.Equal(status, denied.StatusCode);
        Assert.Equal(failure, await CodeAsync(denied));
        Assert.False(denied.Headers.Contains(IdempotentAttribute.ReplayedHeaderName));
        Assert.Null(denied.Headers.Location);
        Assert.DoesNotContain("\"version\"", await denied.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Same(stored, State.Stored);
        Assert.Equal(1, State.Completions);
        Assert.Equal(0, State.Releases);
        State.ReadFailure = null;
        using var recovered = await SendAsync("create", key, null);
        Assert.Equal(HttpStatusCode.Created, recovered.StatusCode);
        Assert.Equal(1, State.Mutations);
    }

    [Fact]
    public async Task Own_profile_and_default_selection_replay_the_original_membership_and_tenant_targets()
    {
        State.Reset();
        var ownKey = NewKey();
        using var firstOwn = await SendAsync("own", ownKey, "\"7\"");
        Assert.Equal(HttpStatusCode.OK, firstOwn.StatusCode);
        var original = State.Target;
        State.Target = Guid.CreateVersion7(); // A later current/successor identity is not the receipt target.
        using var replayOwn = await SendAsync("own", ownKey, "\"7\"");
        Assert.Equal(HttpStatusCode.OK, replayOwn.StatusCode);
        Assert.Equal(original, Assert.IsType<ReadOwnMembershipProfileReceiptQuery>(Assert.Single(State.Reads)).MembershipId);

        State.Reset();
        var defaultKey = NewKey();
        using var firstDefault = await SendAsync("default", defaultKey, "\"7\"");
        Assert.Equal(HttpStatusCode.OK, firstDefault.StatusCode);
        Assert.Equal(new IdempotencyReplayTarget(IdempotencyReplayTargetKind.Tenant, ProtectedIdempotencyFixture.Tenant), State.Stored!.ReplayTarget);
        State.Target = Guid.CreateVersion7();
        using var replayDefault = await SendAsync("default", defaultKey, "\"7\"");
        Assert.Equal(HttpStatusCode.OK, replayDefault.StatusCode);
        Assert.Equal(ProtectedIdempotencyFixture.Tenant,
            Assert.IsType<ReadDefaultOrganizationReceiptQuery>(Assert.Single(State.Reads)).TenantId.Value);
    }

    [Theory]
    [InlineData("missing-carrier")]
    [InlineData("wrong-target")]
    [InlineData("wrong-etag")]
    [InlineData("extra-body")]
    [InlineData("large-header")]
    [InlineData("large-body")]
    [InlineData("wrong-location")]
    public async Task Invalid_success_is_tombstoned_without_exposing_its_bytes_or_rerunning(string corruption)
    {
        State.Reset();
        var create = corruption == "wrong-location";
        var path = create ? "create" : "rename/" + State.Target.ToString("D");
        State.WrongTarget = corruption == "wrong-target";
        var key = NewKey();
        using var first = await SendAsync(path, key, create ? null : "\"7\"", corruption: corruption);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.Equal("dependency_unavailable", await CodeAsync(first));
        Assert.Null(first.Headers.ETag);
        Assert.Null(first.Headers.Location);
        Assert.Null(State.Stored);
        Assert.Equal(1, State.Completions);
        Assert.Equal(0, State.Releases);
        using var retry = await SendAsync(path, key, create ? null : "\"7\"", corruption: corruption);
        Assert.Equal("idempotency_outcome_unavailable", await CodeAsync(retry));
        Assert.Equal(corruption == "missing-carrier" ? 0 : 1, State.Mutations);
    }

    [Fact]
    public async Task Held_cleanup_blocks_completion_and_publication_until_the_seam_finishes()
    {
        State.Reset();
        State.HoldCleanup = true;
        var pending = SendAsync("create", NewKey(), null);
        await State.CleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(pending.IsCompleted);
        Assert.Equal(0, State.Completions);
        Assert.Equal(0, State.Releases);
        State.CleanupRelease.TrySetResult();
        using var response = await pending;
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, State.Completions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unconfirmed_or_faulted_cleanup_prevents_every_outcome_store_call(bool throws)
    {
        State.Reset();
        State.CleanupSucceeds = false;
        State.CleanupThrows = throws;
        using var response = await SendAsync("create", NewKey(), null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("dependency_unavailable", await CodeAsync(response));
        Assert.Equal(0, State.Completions);
        Assert.Equal(0, State.Releases);
        Assert.Equal(1, State.Mutations);
    }

    [Fact]
    public async Task Failed_completion_withholds_success_and_never_retries_the_mutation()
    {
        State.Reset();
        State.FailCompletion = true;
        var key = NewKey();
        using var failed = await SendAsync("create", key, null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Null(failed.Headers.Location);
        Assert.Equal(1, State.Mutations);
        Assert.Equal(1, State.Completions);
        using var retry = await SendAsync("create", key, null);
        Assert.Equal("request_in_progress", await CodeAsync(retry));
        Assert.Equal(1, State.Mutations);
        Assert.Equal(0, State.Releases);
    }

    [Fact]
    public async Task Unconfirmed_cleanup_also_blocks_release_after_a_failed_mutation()
    {
        State.Reset();
        State.MutationFailure = "forbidden";
        State.CleanupSucceeds = false;
        using var response = await SendAsync("rename/" + State.Target.ToString("D"), NewKey(), "\"7\"");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("dependency_unavailable", await CodeAsync(response));
        Assert.Equal(0, State.Releases);
        Assert.Equal(0, State.Completions);
        Assert.Equal(1, State.Mutations);
    }

    [Fact]
    public async Task Corrupt_stored_target_is_refused_before_receipt_dispatch_and_does_not_change_the_completed_row()
    {
        State.Reset();
        var key = NewKey();
        using var first = await SendAsync("create", key, null);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var original = State.Stored;
        State.CorruptStoredTarget = true;
        using var replay = await SendAsync("create", key, null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, replay.StatusCode);
        Assert.Equal("dependency_unavailable", await CodeAsync(replay));
        Assert.Null(replay.Headers.Location);
        Assert.False(replay.Headers.Contains(IdempotentAttribute.ReplayedHeaderName));
        Assert.Empty(State.Reads);
        Assert.Same(original, State.Stored);
        Assert.Equal(1, State.Completions);
        Assert.Equal(0, State.Releases);
        Assert.Equal(1, State.Mutations);
    }

    [Theory]
    [InlineData("validation_failed", HttpStatusCode.BadRequest)]
    [InlineData("forbidden", HttpStatusCode.Forbidden)]
    [InlineData("not_found", HttpStatusCode.NotFound)]
    public async Task Every_protected_non_success_releases_after_cleanup_instead_of_becoming_a_receipt(string code, HttpStatusCode status)
    {
        State.Reset();
        State.MutationFailure = code;
        var key = NewKey();
        var path = "rename/" + State.Target.ToString("D");
        using var first = await SendAsync(path, key, "\"7\"");
        using var second = await SendAsync(path, key, "\"7\"");
        Assert.Equal(status, first.StatusCode);
        Assert.Equal(code, await CodeAsync(first));
        Assert.Equal(status, second.StatusCode);
        Assert.Equal(2, State.Mutations);
        Assert.Equal(2, State.Releases);
        Assert.Equal(0, State.Completions);
        Assert.Null(State.Stored);
    }

    private async Task<HttpResponseMessage> SendAsync(string path, string key, string? tag,
        ProtectedIdempotencyProbeInput? input = null, bool repeated = false, string? corruption = null)
    {
        using var request = new HttpRequestMessage(path == "create" ? HttpMethod.Post : HttpMethod.Put,
            new Uri("/api/v1/protectedidempotencyprobe/" + path, UriKind.Relative));
        request.Headers.TryAddWithoutValidation(IdempotentAttribute.HeaderName, key);
        if (tag is not null) request.Headers.TryAddWithoutValidation("If-Match", repeated ? [tag, tag] : [tag]);
        if (corruption is not null) request.Headers.TryAddWithoutValidation("X-Test-Corruption", corruption);
        request.Content = JsonContent.Create(input ?? new ProtectedIdempotencyProbeInput("Probe"));
        return await _client.SendAsync(request);
    }

    private static string NewKey() => "protected-" + Guid.NewGuid().ToString("N");
    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();
}

public sealed record ProtectedIdempotencyProbeInput(string Name, long? ExpectedOverride = null);

public sealed class ProtectedIdempotencyFixture : ComponentApiFactory
{
    internal static readonly Guid Tenant = Guid.Parse("018f4d40-0000-7000-8000-00000000000a");
    internal static readonly Guid Actor = Guid.Parse("018f4d40-0000-7000-8000-0000000000a1");
    internal ProtectedIdempotencyProbeState State { get; } = new();

    protected override void ConfigureTestHost(IWebHostBuilder builder) => builder.ConfigureServices(services =>
    {
        services.AddControllers(options =>
        {
            options.Conventions.Insert(0, new TestControllerFilter(typeof(ProtectedIdempotencyProbeController)));
            options.Filters.Add<ProtectedReceiptCorruptionFilter>();
        }).AddApplicationPart(typeof(ProtectedIdempotencyProbeController).Assembly);
        services.RemoveAll<ITenantContext>();
        services.AddScoped<ITenantContext, ProbeTenantContext>();
        services.AddSingleton(State);
        services.RemoveAll<ISender>();
        services.AddScoped<ISender, ProtectedReceiptProbeSender>();
        services.RemoveAll<IIdempotencyStore>();
        services.AddSingleton<IIdempotencyStore, ProtectedReceiptProbeStore>();
        services.RemoveAll<IIdempotencyBusinessOwnerCleanup>();
        services.AddScoped<IIdempotencyBusinessOwnerCleanup, ProtectedReceiptProbeCleanup>();
    });

    private sealed class ProbeTenantContext : ITenantContext
    {
        public bool IsResolved => true;
        public TenantId TenantId => TenantId.From(Tenant);
        public OrganizationId? OrganizationId => null;
        public UserId? UserId => SharedKernel.Identifiers.UserId.From(Actor);
        public string? CorrelationId => null;
        public string? ModuleName => "protected-idempotency-component";
    }
}

public sealed class ProtectedIdempotencyProbeController(ISender sender, ITenantContext tenant, ProtectedIdempotencyProbeState state)
    : ApiControllerBase, ITestOnlyController
{
    [HttpPost("create")]
    [Idempotent(ProtectedAdministrationOperation.CreateOrganization)]
    public Task<IActionResult> Create(ProtectedIdempotencyProbeInput input, CancellationToken token) =>
        Dispatch(new CreateManagedOrganizationCommand("component-organization", input.Name), token);

    [HttpPut("rename/{id:guid}")]
    [Idempotent(ProtectedAdministrationOperation.RenameOrganization)]
    public Task<IActionResult> Rename(Guid id, ProtectedIdempotencyProbeInput input, CancellationToken token)
    {
        if (Request.Headers["X-Test-Corruption"] == "missing-carrier")
            return Task.FromResult<IActionResult>(Ok(new { version = "8" }));
        _ = ProtectedEntityTag.TryRead(Request, out var version);
        return Dispatch(new RenameOrganizationCommand(OrganizationId.From(id), input.ExpectedOverride ?? version, input.Name), token);
    }

    [HttpPut("own")]
    [Idempotent(ProtectedAdministrationOperation.SetOwnMembershipProfile)]
    public Task<IActionResult> Own(ProtectedIdempotencyProbeInput input, CancellationToken token)
    {
        _ = ProtectedEntityTag.TryRead(Request, out var version);
        return Dispatch(new SetOwnMembershipProfileCommand(version, input.Name, null), token);
    }

    [HttpPut("default")]
    [Idempotent(ProtectedAdministrationOperation.SelectDefaultOrganization)]
    public Task<IActionResult> Default(ProtectedIdempotencyProbeInput input, CancellationToken token)
    {
        _ = ProtectedEntityTag.TryRead(Request, out var version);
        return Dispatch(new SelectDefaultOrganizationCommand(OrganizationId.From(state.Target), version), token);
    }

    private Task<IActionResult> Dispatch(object command, CancellationToken token)
    {
        Response.Headers["X-Outcome"] = "retained";
        Response.Headers["Set-Cookie"] = "component=first; HttpOnly";
        Response.Headers["Date"] = "Thu, 29 Oct 2026 12:00:00 GMT";
        return ProtectedAdministrationResult.ExecuteAsync(HttpContext, sender, tenant, command, token);
    }
}

public sealed class ProtectedIdempotencyProbeState
{
    internal Guid Target = Guid.CreateVersion7();
    internal int Claims, Mutations, Completions, Releases;
    internal bool WrongTarget, HoldCleanup, CleanupThrows, CleanupFinished, FailCompletion, CorruptStoredTarget;
    internal bool CleanupSucceeds = true;
    internal string? ReadFailure;
    internal string? MutationFailure;
    internal IdempotentResponse? Stored;
    internal ConcurrentQueue<object> Reads { get; } = new();
    internal TaskCompletionSource CleanupEntered = NewGate();
    internal TaskCompletionSource CleanupRelease = NewGate();
    internal void Reset()
    {
        Target = Guid.CreateVersion7(); Claims = Mutations = Completions = Releases = 0;
        WrongTarget = HoldCleanup = CleanupThrows = CleanupFinished = FailCompletion = CorruptStoredTarget = false;
        CleanupSucceeds = true; ReadFailure = MutationFailure = null; Stored = null; Reads.Clear();
        CleanupEntered = NewGate(); CleanupRelease = NewGate();
    }
    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class ProtectedReceiptProbeCleanup(ProtectedIdempotencyProbeState state) : IIdempotencyBusinessOwnerCleanup
{
    public async ValueTask<bool> EndBusinessOwnerAsync()
    {
        state.CleanupEntered.TrySetResult();
        if (state.HoldCleanup) await state.CleanupRelease.Task;
        if (state.CleanupThrows) throw new InvalidOperationException("Component cleanup fault.");
        state.CleanupFinished = state.CleanupSucceeds;
        return state.CleanupFinished;
    }
}

internal sealed class ProtectedReceiptProbeStore(ProtectedIdempotencyProbeState state) : IIdempotencyStore
{
    private readonly InMemoryIdempotencyStore _inner = new(new SystemClock());
    public async Task<IdempotencyClaimResult> TryClaimAsync(TenantId tenantId, string key, string fingerprint, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref state.Claims);
        var claim = await _inner.TryClaimAsync(tenantId, key, fingerprint, cancellationToken);
        return state.CorruptStoredTarget && claim.Stored is { } stored
            ? claim with { Stored = stored with { ReplayTarget = null } } : claim;
    }
    public Task<bool> CompleteAsync(TenantId tenantId, string key, Guid token, IdempotentResponse? response, CancellationToken cancellationToken)
    {
        Assert.True(state.CleanupFinished);
        Interlocked.Increment(ref state.Completions);
        if (state.FailCompletion) throw new IdempotencyStoreException();
        state.Stored = response;
        return _inner.CompleteAsync(tenantId, key, token, response, cancellationToken);
    }
    public Task<bool> AbandonAsync(TenantId tenantId, string key, Guid token, CancellationToken cancellationToken)
    {
        Assert.True(state.CleanupFinished);
        Interlocked.Increment(ref state.Releases);
        return _inner.AbandonAsync(tenantId, key, token, cancellationToken);
    }
}

internal sealed class ProtectedReceiptProbeSender(ProtectedIdempotencyProbeState state) : ISender
{
    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        object response;
        if (request is ReadOrganizationReceiptQuery or ReadDefaultOrganizationReceiptQuery or ReadOwnMembershipProfileReceiptQuery
            or ReadMemberProfileReceiptQuery or ReadMembershipReceiptQuery or ReadRoleReceiptQuery)
        {
            state.Reads.Enqueue(request);
            response = state.ReadFailure is { } code ? Result<None>.Fail(new Error(LocalizedMessage.Of("lockey_" + code))) : Result.Ok(None.Value);
        }
        else
        {
            Interlocked.Increment(ref state.Mutations);
            var expected = ProtectedAdministrationDescriptor.ExpectedVersion(request);
            var target = state.WrongTarget ? Guid.CreateVersion7() : state.Target;
            var error = new Error(LocalizedMessage.Of("lockey_" + (state.MutationFailure ?? "concurrency_conflict")));
            var succeeds = expected == 7 && state.MutationFailure is null;
            response = request switch
            {
                CreateManagedOrganizationCommand => Result.Ok(new OrganizationMutationResult(OrganizationId.From(target), 8)),
                RenameOrganizationCommand or ArchiveOrganizationCommand => succeeds
                    ? Result.Ok(new OrganizationMutationResult(OrganizationId.From(target), 8)) : Result<OrganizationMutationResult>.Fail(error),
                SetOwnMembershipProfileCommand or SetMembershipProfileCommand or ChangeMembershipStatusCommand or ReplaceMembershipRoleAssignmentsCommand =>
                    succeeds ? Result.Ok(new MembershipMutationResult(target, 8)) : Result<MembershipMutationResult>.Fail(error),
                SelectDefaultOrganizationCommand or ChangeRoleGrantsCommand or AdoptRoleGrantManifestCommand =>
                    succeeds ? Result.Ok(8L) : Result<long>.Fail(error),
                _ => throw new InvalidOperationException("Unexpected component request."),
            };
        }
        return Task.FromResult((TResponse)response);
    }
    public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
    public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal sealed class ProtectedReceiptCorruptionFilter : IAsyncResultFilter
{
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        await next();
        var response = context.HttpContext.Response;
        switch (context.HttpContext.Request.Headers["X-Test-Corruption"].ToString())
        {
            case "wrong-etag": response.Headers.ETag = "\"9\""; break;
            case "wrong-location": response.Headers.Location = ProtectedAdministrationResult.LocationFor(Guid.CreateVersion7()); break;
            case "large-header": response.Headers["X-Pad"] = new string('x', 2048); break;
            case "extra-body": await response.Body.WriteAsync("x"u8.ToArray()); break;
            case "large-body": await response.Body.WriteAsync(new byte[2048]); break;
        }
    }
}
