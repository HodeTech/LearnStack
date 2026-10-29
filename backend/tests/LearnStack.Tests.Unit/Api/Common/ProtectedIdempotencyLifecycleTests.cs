using System.Text;
using LearnStack.Api.Idempotency;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Idempotency;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Common;

/// <summary>Component ordering witnesses; real owner disposal and MUST audit commit have separate PostgreSQL proofs.</summary>
public sealed class ProtectedIdempotencyLifecycleTests
{
    private static readonly TenantId Tenant = TenantId.From(Guid.Parse("018f4d40-0000-7000-8000-000000000022"));
    private static readonly OrganizationId Organization = OrganizationId.From(Guid.Parse("018f4d40-0000-7000-8000-000000000011"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Generic_outcome_completion_and_release_also_wait_for_actual_cleanup(bool releases)
    {
        var entered = Gate();
        var released = Gate();
        var finished = false;
        var cleanup = new DeferredCleanup(EndAsync);
        async Task<bool> EndAsync()
        {
            entered.TrySetResult();
            await released.Task;
            finished = true;
            return true;
        }
        var store = Substitute.For<IIdempotencyStore>();
        store.TryClaimAsync(Tenant, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new IdempotencyClaimResult(IdempotencyClaim.Acquired, Guid.NewGuid(), null));
        store.CompleteAsync(Tenant, Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<IdempotentResponse?>(), Arg.Any<CancellationToken>())
            .Returns(_ => { Assert.True(finished); return true; });
        store.AbandonAsync(Tenant, Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => { Assert.True(finished); return true; });
        store.ClearReceivedCalls();
        var filter = new IdempotencyFilter(store, Context(), NullLogger<IdempotencyFilter>.Instance, cleanup);
        var context = Context(ProtectedAdministrationOperation.CreateOrganization);
        var pending = filter.OnResourceExecutionAsync(context, async () =>
        {
            context.HttpContext.Response.StatusCode = releases ? 503 : 200;
            await context.HttpContext.Response.Body.WriteAsync("component-outcome"u8.ToArray());
            return new ResourceExecutedContext(context, []) { Result = new EmptyResult() };
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(pending.IsCompleted);
        Assert.Single(store.ReceivedCalls()); // Claim only; no second database owner can begin.
        Assert.Equal(0, context.HttpContext.Response.Body.Length);
        released.TrySetResult();
        await pending;
        Assert.Equal("component-outcome", Encoding.UTF8.GetString(((MemoryStream)context.HttpContext.Response.Body).ToArray()));
        await store.Received(releases ? 0 : 1).CompleteAsync(Tenant, Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<IdempotentResponse?>(), Arg.Any<CancellationToken>());
        await store.Received(releases ? 1 : 0).AbandonAsync(Tenant, Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Disconnect_after_typed_success_still_completes_after_cleanup_and_replay_waits_for_fresh_read()
    {
        var sender = Substitute.For<ISender>();
        sender.Send(Arg.Any<CreateManagedOrganizationCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok(new OrganizationMutationResult(Organization, 8)));
        var cleanupEntered = Gate();
        var cleanupRelease = Gate();
        var cleanupFinished = false;
        var cleanup = new DeferredCleanup(EndAsync);
        async Task<bool> EndAsync()
        {
            cleanupEntered.TrySetResult();
            await cleanupRelease.Task;
            cleanupFinished = true;
            return true;
        }
        IdempotentResponse? recorded = null;
        var store = Substitute.For<IIdempotencyStore>();
        store.TryClaimAsync(Tenant, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => recorded is null ? new IdempotencyClaimResult(IdempotencyClaim.Acquired, Guid.NewGuid(), null)
                : new(IdempotencyClaim.Completed, Guid.Empty, recorded));
        store.CompleteAsync(Tenant, Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<IdempotentResponse?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Assert.True(cleanupFinished);
                Assert.False(call.Arg<CancellationToken>().IsCancellationRequested);
                recorded = call.ArgAt<IdempotentResponse?>(3);
                return true;
            });
        var tenant = Context();
        var filter = Filter(store, tenant, cleanup, sender, ProtectedAdministrationOperation.CreateOrganization);
        var context = Context(ProtectedAdministrationOperation.CreateOrganization);
        using var disconnected = new CancellationTokenSource();
        context.HttpContext.RequestAborted = disconnected.Token;
        var mutations = 0;
        var pending = filter.OnResourceExecutionAsync(context, async () =>
        {
            mutations++;
            var result = await ProtectedAdministrationResult.ExecuteAsync(context.HttpContext, sender, tenant,
                new CreateManagedOrganizationCommand("component", "Private"), CancellationToken.None);
            await result.ExecuteResultAsync(context);
            await disconnected.CancelAsync();
            return new ResourceExecutedContext(context, []) { Result = result };
        });
        await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(pending.IsCompleted);
        Assert.Null(recorded);
        Assert.Equal(0, context.HttpContext.Response.Body.Length);
        cleanupRelease.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.NotNull(recorded);
        Assert.Equal(1, mutations);

        var readEntered = Gate();
        var readRelease = new TaskCompletionSource<Result<None>>(TaskCreationOptions.RunContinuationsAsynchronously);
        sender.Send(Arg.Any<ReadOrganizationReceiptQuery>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            Assert.Equal(Organization, call.Arg<ReadOrganizationReceiptQuery>().OrganizationId);
            readEntered.TrySetResult();
            return readRelease.Task;
        });
        var replay = Context(ProtectedAdministrationOperation.CreateOrganization);
        var replayPending = filter.OnResourceExecutionAsync(replay, () => throw new InvalidOperationException("A completed mutation must not dispatch."));
        await readEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(replayPending.IsCompleted);
        Assert.Equal(0, replay.HttpContext.Response.Body.Length);
        Assert.False(replay.HttpContext.Response.Headers.ContainsKey(IdempotentAttribute.ReplayedHeaderName));
        readRelease.TrySetResult(Result.Ok(None.Value));
        await replayPending;
        Assert.Equal("{\"version\":\"8\"}", Encoding.UTF8.GetString(((MemoryStream)replay.HttpContext.Response.Body).ToArray()));
        Assert.Equal("true", replay.HttpContext.Response.Headers[IdempotentAttribute.ReplayedHeaderName].ToString());
        await store.Received(1).CompleteAsync(Tenant, Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<IdempotentResponse?>(), Arg.Any<CancellationToken>());
        await store.DidNotReceive().AbandonAsync(Tenant, Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        Assert.Equal(1, mutations);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("tenant")]
    [InlineData("organization")]
    [InlineData("method")]
    [InlineData("path")]
    [InlineData("query")]
    [InlineData("body")]
    [InlineData("precondition")]
    [InlineData("operation")]
    public async Task Protected_fingerprint_binds_each_context_input_and_its_versioned_precondition(string changed)
    {
        var tenant = Context();
        var store = Substitute.For<IIdempotencyStore>();
        var cleanup = Substitute.For<IIdempotencyBusinessOwnerCleanup>();
        var sender = Substitute.For<ISender>();
        var filter = Filter(store, tenant, cleanup, sender, ProtectedAdministrationOperation.RenameOrganization);
        var http = Context(ProtectedAdministrationOperation.RenameOrganization).HttpContext;
        var first = await filter.ComputeFingerprintAsync(http, 7, CancellationToken.None);
        var generic = new IdempotencyFilter(store, tenant, NullLogger<IdempotencyFilter>.Instance, cleanup);
        var old = await generic.ComputeFingerprintAsync(http, CancellationToken.None);
        Assert.NotEqual(old, first);
        http.Request.Headers.IfMatch = "\"8\"";
        Assert.Equal(old, await generic.ComputeFingerprintAsync(http, CancellationToken.None));
        switch (changed)
        {
            case "actor": tenant.UserId.Returns(UserId.From(Guid.CreateVersion7())); break;
            case "tenant": tenant.TenantId.Returns(TenantId.From(Guid.CreateVersion7())); break;
            case "organization": tenant.OrganizationId.Returns(Organization); break;
            case "method": http.Request.Method = "POST"; break;
            case "path": http.Request.Path = "/different"; break;
            case "query": http.Request.QueryString = new QueryString("?different=1"); break;
            case "body": http.Request.Body = new MemoryStream("{\"name\":\"Changed\"}"u8.ToArray()); break;
            case "operation": filter = Filter(store, tenant, cleanup, sender, ProtectedAdministrationOperation.ArchiveOrganization); break;
        }
        var second = await filter.ComputeFingerprintAsync(http, changed == "precondition" ? 8 : 7, CancellationToken.None);
        Assert.NotEqual(first, second);
        Assert.Equal(0, http.Request.Body.Position);
    }

    private static ProtectedIdempotencyFilter Filter(IIdempotencyStore store, ITenantContext tenant,
        IIdempotencyBusinessOwnerCleanup cleanup, ISender sender, ProtectedAdministrationOperation operation) =>
        new(store, tenant, cleanup, sender, NullLogger<ProtectedIdempotencyFilter>.Instance, operation);
    private static ITenantContext Context()
    {
        var context = Substitute.For<ITenantContext>();
        context.IsResolved.Returns(true);
        context.TenantId.Returns(Tenant);
        context.UserId.Returns(UserId.From(Guid.Parse("018f4d40-0000-7000-8000-000000000033")));
        return context;
    }
    private static ResourceExecutingContext Context(ProtectedAdministrationOperation operation)
    {
        var http = new DefaultHttpContext();
        http.Request.Headers[IdempotentAttribute.HeaderName] = "component-key";
        http.Request.Method = "PUT";
        http.Request.Path = "/component";
        http.Request.Body = new MemoryStream("{\"name\":\"Private\"}"u8.ToArray());
        http.Request.ContentType = "application/json";
        if (operation != ProtectedAdministrationOperation.CreateOrganization) http.Request.Headers.IfMatch = "\"7\"";
        http.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(new IdempotentAttribute(operation)), "component"));
        http.Response.Body = new MemoryStream();
        return new ResourceExecutingContext(new ActionContext(http, new RouteData(), new ActionDescriptor()), [], []);
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class DeferredCleanup(Func<Task<bool>> end) : IIdempotencyBusinessOwnerCleanup
    {
        public ValueTask<bool> EndBusinessOwnerAsync() => new(end());
    }
}
