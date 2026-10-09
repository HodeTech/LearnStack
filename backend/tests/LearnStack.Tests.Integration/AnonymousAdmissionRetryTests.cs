using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using FluentAssertions;
using LearnStack.Api.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace LearnStack.Tests.Integration;

public sealed class AnonymousAdmissionRetryTests
{
    private const string Secret = "test-only-endpoint-retry-secret-at-least-32";

    [Fact]
    public async Task Endpoint_refusal_replays_successful_global_admission_without_spending_either_budget_twice()
    {
        await using var app = await CreateHost();
        using var client = app.GetTestClient();
        var global = app.Services.GetRequiredService<ObservedGlobalLimiter>();

        using var first = await Send(client, "/endpoint-limited", "203.0.113.1");
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        using var endpointRefusal = await Send(client, "/endpoint-limited", "203.0.113.1");
        endpointRefusal.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        endpointRefusal.Headers.RetryAfter?.Delta.Should().BeGreaterThan(TimeSpan.Zero);
        global.Retries.Should().ContainSingle().Which.Acquired.Should().BeTrue(
            "ASP.NET retries global acquisition after its endpoint policy refuses");

        for (var index = 2; index < RateLimitingExtensions.AnonymousPermitPerWindow; index++)
        {
            using var admitted = await Send(client, "/admitted", "203.0.113.1");
            admitted.StatusCode.Should().Be(HttpStatusCode.OK,
                "the endpoint refusal consumed one global admission, including its framework retry");
        }
        using var visitorRefusal = await Send(client, "/admitted", "203.0.113.1");
        visitorRefusal.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        global.Retries.Should().HaveCount(2);
        global.Retries[1].Acquired.Should().BeFalse();

        var remaining = RateLimitingExtensions.PeerPermitPerWindow - RateLimitingExtensions.AnonymousPermitPerWindow;
        for (var index = 0; index < remaining; index++)
        {
            var visitor = "203.0.113." + (index / RateLimitingExtensions.AnonymousPermitPerWindow + 2)
                .ToString(CultureInfo.InvariantCulture);
            using var admitted = await Send(client, "/admitted", visitor);
            admitted.StatusCode.Should().Be(HttpStatusCode.OK,
                "neither the endpoint retry nor the exhausted visitor spent an extra peer permit");
        }
        using var peerRefusal = await Send(client, "/admitted", "203.0.113.99");
        peerRefusal.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        global.Retries.Should().HaveCount(3);
        global.Retries.Should().OnlyContain(retry => retry.CompletedSynchronously,
            "no-queue global admission completes immediately on both success and refusal");
        foreach (var retry in global.Retries)
        {
            retry.Acquired.Should().Be(retry.InitiallyAcquired);
            retry.Metadata.Should().BeEquivalentTo(retry.InitialMetadata,
                "each framework retry uses the same HttpContext and saved outcome metadata");
        }
    }

    private static async Task<WebApplication> CreateHost()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IOptions<TrustedHopOptions>>(Options.Create(new TrustedHopOptions
        {
            Networks = ["127.0.0.1/32"],
            Secrets = [Secret],
        }));
        builder.Services.AddSingleton<EffectiveHostAccessor>();
        builder.Services.AddLearnStackRateLimiting();
        // Exercise real fixed-window/no-queue budgets, with replenishment frozen
        // so the HTTP accounting proof does not depend on finishing within a minute.
        builder.Services.RemoveAll<NoQueueAdmissionLimiter>();
        builder.Services.AddSingleton(_ => new NoQueueAdmissionLimiter(
            FrozenAnonymousPeerBudget.Create(),
            () => NoQueueAdmissionLimiter.CreateVisitorBudget(autoReplenishment: false),
            RateLimitingExtensions.Window, TimeProvider.System));
        builder.Services.AddSingleton<ObservedGlobalLimiter>();
        builder.Services.AddOptions<RateLimiterOptions>().Configure<ObservedGlobalLimiter>(
            (options, limiter) => options.GlobalLimiter = limiter);
        // The endpoint partition must also hide the replenishing interface;
        // AddFixedWindowLimiter's partition heartbeat would otherwise reset it.
        builder.Services.AddRateLimiter(options => options.AddPolicy("endpoint", _ =>
            RateLimitPartition.Get("endpoint", _ => FrozenAnonymousPeerBudget.CreateLimiter(1))));

        var app = builder.Build();
        app.Use((context, next) =>
        {
            // TestServer has no physical socket; supply its peer feature before
            // the production identity resolver authenticates the test hop headers.
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            return next(context);
        });
        app.UseRouting();
        app.UseRateLimiter();
        app.MapGet("/admitted", () => Results.Ok());
        app.MapGet("/endpoint-limited", () => Results.Ok()).RequireRateLimiting("endpoint");
        await app.StartAsync();
        return app;
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, string path, string visitor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(TrustedHopOptions.SecretHeaderName, Secret);
        request.Headers.Add(AnonymousRequestIdentity.VisitorHeaderName, visitor);
        return await client.SendAsync(request);
    }

    private sealed class ObservedGlobalLimiter(NoQueueAdmissionLimiter inner) : PartitionedRateLimiter<HttpContext>
    {
        public List<RetryObservation> Retries { get; } = [];

        public override RateLimiterStatistics? GetStatistics(HttpContext resource) => inner.GetStatistics(resource);

        protected override RateLimitLease AttemptAcquireCore(HttpContext resource, int permitCount)
        {
            var lease = inner.AttemptAcquire(resource, permitCount);
            resource.Items[this] = (lease.IsAcquired, lease.GetAllMetadata().ToDictionary(StringComparer.Ordinal));
            return lease;
        }

        protected override async ValueTask<RateLimitLease> AcquireAsyncCore(
            HttpContext resource, int permitCount, CancellationToken cancellationToken)
        {
            var initial = ((bool Acquired, Dictionary<string, object?> Metadata))resource.Items[this]!;
            var pending = inner.AcquireAsync(resource, permitCount, cancellationToken);
            var completedSynchronously = pending.IsCompletedSuccessfully;
            var lease = await pending;
            Retries.Add(new RetryObservation(initial.Acquired, lease.IsAcquired, completedSynchronously,
                initial.Metadata, lease.GetAllMetadata().ToDictionary(StringComparer.Ordinal)));
            return lease;
        }
    }

    private sealed record RetryObservation(bool InitiallyAcquired, bool Acquired, bool CompletedSynchronously,
        IReadOnlyDictionary<string, object?> InitialMetadata, IReadOnlyDictionary<string, object?> Metadata);
}

internal static class FrozenAnonymousPeerBudget
{
    internal static PartitionedRateLimiter<HttpContext> Create() =>
        PartitionedRateLimiter.Create<HttpContext, string>(context => RateLimitPartition.Get(
            context.RequestServices.GetRequiredService<AnonymousRequestIdentity>().For(context).PeerKey,
            _ => CreateLimiter(RateLimitingExtensions.PeerPermitPerWindow)));

    internal static RateLimiter CreateLimiter(int permits) =>
        new FrozenLimiter(new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = RateLimitingExtensions.Window,
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = false,
        }));

    // DefaultPartitionedRateLimiter replenishes ReplenishingRateLimiter children
    // from its own heartbeat, even when their AutoReplenishment is false. This
    // test-only RateLimiter wrapper keeps the actual fixed-window implementation
    // while hiding that replenishment interface from the partition owner.
    private sealed class FrozenLimiter(FixedWindowRateLimiter inner) : RateLimiter
    {
        public override TimeSpan? IdleDuration => inner.IdleDuration;
        public override RateLimiterStatistics? GetStatistics() => inner.GetStatistics();
        protected override RateLimitLease AttemptAcquireCore(int permitCount) => inner.AttemptAcquire(permitCount);
        protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken) =>
            inner.AcquireAsync(permitCount, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        protected override async ValueTask DisposeAsyncCore()
        {
            await inner.DisposeAsync();
            await base.DisposeAsyncCore();
        }
    }
}
