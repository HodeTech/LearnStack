using System.Net;
using System.Reflection;
using System.Threading.RateLimiting;
using FluentAssertions;
using LearnStack.Api.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Tenancy;

public sealed class AnonymousLimiterLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Provider_teardown_disposes_the_global_admission_owner(bool asynchronous)
    {
        var provider = Provider();
        var limiter = Global(provider);
        if (asynchronous) await provider.DisposeAsync();
        else provider.Dispose();

        // Keep request services live: a disposed provider must not be the cause
        // of the expected refusal, masking a global limiter that is still usable.
        await using var requestServices = Provider();
        var context = new DefaultHttpContext { RequestServices = requestServices };
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        var acquire = () => limiter.AttemptAcquire(context);
        acquire.Should().Throw<ObjectDisposedException>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_registered_owner_disposes_each_child_budget_once_on_both_paths(bool asynchronous)
    {
        using var provider = Provider();
        var peer = new TrackedBudget();
        var visitor = new TrackedBudget();
        // Exercise the actual registered owner without exposing an internal API
        // merely for tests or relying on the framework's private timer fields.
        var constructor = Global(provider).GetType().GetConstructor(
            BindingFlags.Instance | BindingFlags.Public, [typeof(PartitionedRateLimiter<HttpContext>[])]);
        constructor.Should().NotBeNull();
        var owner = (PartitionedRateLimiter<HttpContext>)constructor!.Invoke(
            [new PartitionedRateLimiter<HttpContext>[] { peer, visitor }]);
        if (asynchronous) await owner.DisposeAsync();
        else owner.Dispose();
        owner.Dispose();
        await owner.DisposeAsync();
        peer.Disposals.Should().Be(1);
        visitor.Disposals.Should().Be(1);
        peer.AsyncDisposals.Should().Be(asynchronous ? 1 : 0);
        visitor.AsyncDisposals.Should().Be(asynchronous ? 1 : 0);
    }

    private static ServiceProvider Provider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new EffectiveHostAccessor(Options.Create(new TrustedHopOptions())));
        services.AddLearnStackRateLimiting();
        return services.BuildServiceProvider();
    }

    private static PartitionedRateLimiter<HttpContext> Global(ServiceProvider provider) =>
        provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value.GlobalLimiter!;

    private sealed class TrackedBudget : PartitionedRateLimiter<HttpContext>
    {
        public int Disposals { get; private set; }
        public int AsyncDisposals { get; private set; }
        public override RateLimiterStatistics? GetStatistics(HttpContext resource) => null;
        protected override RateLimitLease AttemptAcquireCore(HttpContext resource, int permitCount) =>
            throw new InvalidOperationException("This probe observes ownership, not acquisitions.");
        protected override ValueTask<RateLimitLease> AcquireAsyncCore(HttpContext resource, int permitCount, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This probe observes ownership, not acquisitions.");
        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposals++;
            base.Dispose(disposing);
        }
        protected override async ValueTask DisposeAsyncCore()
        {
            Disposals++;
            AsyncDisposals++;
            await base.DisposeAsyncCore();
        }
    }
}
