using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using FluentAssertions;
using LearnStack.Api.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Tenancy;

public sealed class AnonymousAdmissionTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(330)]
    [InlineData(600)]
    public void AttemptAcquire_ExhaustedKnownVisitor_DoesNotSpendTheFreshVisitorsPeerBudget(int refusals)
    {
        using var fixture = new AdmissionFixture();
        for (var index = 0; index < 60; index++)
        {
            using var admitted = fixture.Owner.AttemptAcquire(fixture.Request("exhausted"));
            admitted.IsAcquired.Should().BeTrue();
        }

        for (var index = 0; index < refusals; index++)
        {
            using var refused = fixture.Owner.AttemptAcquire(fixture.Request("exhausted"));
            refused.IsAcquired.Should().BeFalse();
        }
        using var fresh = fixture.Owner.AttemptAcquire(fixture.Request("fresh"));

        fresh.IsAcquired.Should().BeTrue();
        fixture.Peer.For("peer").PositiveAttempts.Should().Be(61);
        fixture.Peer.For("peer").Remaining.Should().Be(539);
        fixture.Visitors.Should().HaveCount(2);
        fixture.Visitors[0].ZeroProbes.Should().Be(59 + refusals);
        fixture.Visitors[0].IdleReads.Should().Be(0, "refusal work must not sweep the visitor registry");
        fixture.Timer.Fires.Should().Be(0);
    }

    [Fact]
    public void AttemptAcquire_ExhaustedPeer_DoesNotAllocateUnknownVisitors()
    {
        using var fixture = new AdmissionFixture();
        for (var index = 0; index < 660; index++)
        {
            using var lease = fixture.Owner.AttemptAcquire(fixture.Request($"visitor-{index}"));
            lease.IsAcquired.Should().Be(index < 600);
        }

        fixture.Visitors.Should().HaveCount(600);
        fixture.Visitors.Sum(visitor => visitor.PositiveAttempts).Should().Be(600);
        fixture.Visitors.Should().OnlyContain(visitor => visitor.IdleReads == 0);
        fixture.Peer.For("peer").Remaining.Should().Be(0);
    }

    [Fact]
    public void AttemptAcquire_BothBudgetsExhausted_SelectsVisitorRetryAfterWithoutProbingPeer()
    {
        using var fixture = new AdmissionFixture(peerPermits: 1, visitorPermits: 1);
        fixture.Peer.For("peer").RetryAfter = TimeSpan.FromSeconds(13);
        using var admitted = fixture.Owner.AttemptAcquire(fixture.Request("visitor"));
        fixture.Visitors[0].RetryAfter = TimeSpan.FromSeconds(37);

        using var refused = fixture.Owner.AttemptAcquire(fixture.Request("visitor"));

        refused.IsAcquired.Should().BeFalse();
        RetryAfter(refused).Should().Be(TimeSpan.FromSeconds(37));
        fixture.Peer.For("peer").PositiveAttempts.Should().Be(1);
        fixture.Peer.For("peer").ZeroProbes.Should().Be(0);
        fixture.Visitors[0].PositiveAttempts.Should().Be(1);
        fixture.Visitors[0].ZeroProbes.Should().Be(1);
    }

    [Fact]
    public void AttemptAcquire_VisitorAvailableButPeerExhausted_PreservesVisitorQuotaAndPeerRetryAfter()
    {
        using var fixture = new AdmissionFixture(peerPermits: 1, visitorPermits: 2);
        fixture.Peer.For("peer").RetryAfter = TimeSpan.FromSeconds(13);
        using var admitted = fixture.Owner.AttemptAcquire(fixture.Request("visitor"));
        fixture.Visitors[0].RetryAfter = TimeSpan.FromSeconds(37);

        using var refused = fixture.Owner.AttemptAcquire(fixture.Request("visitor"));

        refused.IsAcquired.Should().BeFalse();
        RetryAfter(refused).Should().Be(TimeSpan.FromSeconds(13));
        fixture.Visitors[0].Remaining.Should().Be(1);
        fixture.Visitors[0].PositiveAttempts.Should().Be(1);
        fixture.Visitors[0].ZeroProbes.Should().Be(1);
    }

    [Fact]
    public async Task AcquireAsync_SuccessfulRequestReplay_ReturnsIndependentLeasesWithoutAnotherCharge()
    {
        using var fixture = new AdmissionFixture();
        var request = fixture.Request("visitor");
        var first = fixture.Owner.AttemptAcquire(request);
        first.IsAcquired.Should().BeTrue();
        first.Dispose();

        using var second = await fixture.Owner.AcquireAsync(request);
        using var third = fixture.Owner.AttemptAcquire(request);

        second.IsAcquired.Should().BeTrue();
        third.IsAcquired.Should().BeTrue();
        second.Should().NotBeSameAs(first).And.NotBeSameAs(third);
        fixture.Peer.For("peer").PositiveAttempts.Should().Be(1);
        fixture.Visitors.Single().PositiveAttempts.Should().Be(1);
        fixture.Visitors.Single().ZeroProbes.Should().Be(0);
        fixture.Peer.For("peer").Leases.Should().OnlyContain(lease => lease.Disposals == 1);
        fixture.Visitors.Single().Leases.Should().OnlyContain(lease => lease.Disposals == 1);
    }

    [Fact]
    public async Task AcquireAsync_RefusedRequestReplay_RetainsTheOutcomeAfterReplenishment()
    {
        using var fixture = new AdmissionFixture(visitorPermits: 1);
        using var admitted = fixture.Owner.AttemptAcquire(fixture.Request("visitor"));
        var visitor = fixture.Visitors.Single();
        visitor.RetryAfter = TimeSpan.FromSeconds(37);
        var request = fixture.Request("visitor");
        var first = fixture.Owner.AttemptAcquire(request);
        first.IsAcquired.Should().BeFalse();
        first.Dispose();
        visitor.Replenish();
        visitor.RetryAfter = TimeSpan.FromSeconds(5);

        using var second = await fixture.Owner.AcquireAsync(request);
        using var third = fixture.Owner.AttemptAcquire(request);

        second.IsAcquired.Should().BeFalse();
        third.IsAcquired.Should().BeFalse();
        RetryAfter(second).Should().Be(TimeSpan.FromSeconds(37));
        RetryAfter(third).Should().Be(TimeSpan.FromSeconds(37));
        second.Should().NotBeSameAs(first).And.NotBeSameAs(third);
        visitor.PositiveAttempts.Should().Be(1);
        visitor.ZeroProbes.Should().Be(1);
        fixture.Peer.For("peer").PositiveAttempts.Should().Be(1);
        visitor.Leases.Should().OnlyContain(lease => lease.Disposals == 1);
    }

    [Fact]
    public async Task AcquireAsync_UnknownVisitorPeerRefusalReplay_DoesNotAllocateAfterPeerReplenishment()
    {
        using var fixture = new AdmissionFixture(peerPermits: 0);
        var request = fixture.Request("visitor");
        using var first = fixture.Owner.AttemptAcquire(request);
        fixture.Peer.For("peer").Replenish(1);

        using var second = await fixture.Owner.AcquireAsync(request);

        first.IsAcquired.Should().BeFalse();
        second.IsAcquired.Should().BeFalse();
        fixture.Visitors.Should().BeEmpty();
        fixture.Peer.For("peer").PositiveAttempts.Should().Be(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Acquisition_UnsupportedPermitCount_RejectsBeforeAnyBudgetWork(int permits)
    {
        using var fixture = new AdmissionFixture();
        var request = fixture.Request("visitor");
        var attempt = () => fixture.Owner.AttemptAcquire(request, permits);
        var acquire = async () => await fixture.Owner.AcquireAsync(request, permits);

        attempt.Should().Throw<ArgumentOutOfRangeException>();
        await acquire.Should().ThrowAsync<ArgumentOutOfRangeException>();
        fixture.Visitors.Should().BeEmpty();
        fixture.Peer.TotalPositiveAttempts.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcquireAsync_PreCanceledRequest_DoesNotChargeOrCreateAVisitor(bool alreadyAdmitted)
    {
        using var fixture = new AdmissionFixture();
        var request = fixture.Request("visitor");
        if (alreadyAdmitted)
        {
            using var admitted = fixture.Owner.AttemptAcquire(request);
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var acquire = async () => await fixture.Owner.AcquireAsync(request, cancellationToken: cancellation.Token);

        await acquire.Should().ThrowAsync<OperationCanceledException>();
        fixture.Peer.TotalPositiveAttempts.Should().Be(alreadyAdmitted ? 1 : 0);
        fixture.Visitors.Should().HaveCount(alreadyAdmitted ? 1 : 0);
        using var retried = await fixture.Owner.AcquireAsync(request);
        retried.IsAcquired.Should().BeTrue();
        fixture.Peer.TotalPositiveAttempts.Should().Be(1);
    }

    [Fact]
    public async Task AttemptAcquire_ParallelLastPermitAcrossPhysicalPeers_AdmitsExactlyOneRequest()
    {
        using var fixture = new AdmissionFixture(visitorPermits: 1);
        var admitted = await ConcurrentAdmissions(fixture, index => fixture.Request("visitor", $"peer-{index % 2}"));

        admitted.Should().Be(1);
        fixture.Visitors.Should().ContainSingle();
        fixture.Visitors.Single().PositiveAttempts.Should().Be(1);
        fixture.Peer.TotalPositiveAttempts.Should().Be(1);
    }

    [Fact]
    public async Task AttemptAcquire_ParallelLastPeerPermitForUnknownVisitors_AdmitsAndAllocatesExactlyOne()
    {
        using var fixture = new AdmissionFixture(peerPermits: 1);
        var admitted = await ConcurrentAdmissions(fixture, index => fixture.Request($"visitor-{index}"));

        admitted.Should().Be(1);
        fixture.Visitors.Should().ContainSingle();
        fixture.Visitors.Single().PositiveAttempts.Should().Be(1);
        fixture.Peer.For("peer").Remaining.Should().Be(0);
        fixture.Peer.TotalPositiveAttempts.Should().Be(32);
    }

    [Fact]
    public async Task AttemptAcquire_ParallelCreationForOneVisitor_OwnsOnlyOneLimiter()
    {
        using var fixture = new AdmissionFixture();
        var admitted = await ConcurrentAdmissions(fixture, _ => fixture.Request("visitor"));

        admitted.Should().Be(32);
        fixture.Visitors.Should().ContainSingle();
        fixture.Visitors.Single().PositiveAttempts.Should().Be(32);
        fixture.Peer.TotalPositiveAttempts.Should().Be(32);
    }

    [Fact]
    public async Task AttemptAcquire_RealFixedWindowAutomaticallyReplenishes_AdmitsWithoutReconstructingVisitor()
    {
        var allocations = 0;
        using var fixture = new AdmissionFixture(visitorFactory: () =>
        {
            allocations++;
            return new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
            {
                PermitLimit = 1,
                Window = TimeSpan.FromMilliseconds(500),
                AutoReplenishment = true,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            });
        });
        using var first = fixture.Owner.AttemptAcquire(fixture.Request("visitor"));
        using var refused = fixture.Owner.AttemptAcquire(fixture.Request("visitor"));

        first.IsAcquired.Should().BeTrue();
        refused.IsAcquired.Should().BeFalse();
        RetryAfter(refused).Should().BeGreaterThan(TimeSpan.Zero);
        fixture.Peer.TotalPositiveAttempts.Should().Be(1, "the exhausted visitor's zero probe refuses before the peer debit");

        // .NET owns quota replenishment through its real timer. The injected
        // manual clock controls only the admission owner's periodic sweep.
        using var deadline = new CancellationTokenSource(Timeout);
        while (true)
        {
            using var retry = fixture.Owner.AttemptAcquire(fixture.Request("visitor"));
            if (retry.IsAcquired) break;
            await Task.Delay(TimeSpan.FromMilliseconds(10), deadline.Token);
        }

        allocations.Should().Be(1);
        fixture.Peer.TotalPositiveAttempts.Should().Be(2);
        fixture.Clock.GetTimestamp().Should().Be(0);
        fixture.Timer.Fires.Should().Be(0);
    }

    [Fact]
    public void Sweep_ElapsedWallTimeWithSpentQuota_DoesNotReconstructTheVisitorOrRestoreQuota()
    {
        using var fixture = new AdmissionFixture(visitorPermits: 1);
        using var admitted = fixture.Owner.AttemptAcquire(fixture.Request("visitor"));
        fixture.Clock.Advance(Window + Window);

        fixture.Timer.Fire();
        using var refused = fixture.Owner.AttemptAcquire(fixture.Request("visitor"));

        refused.IsAcquired.Should().BeFalse();
        fixture.Visitors.Should().ContainSingle();
        fixture.Visitors.Single().Disposals.Should().Be(0);
        fixture.Peer.TotalPositiveAttempts.Should().Be(1);
    }

    [Fact]
    public void Sweep_ReplenishedVisitor_RetiresOnlyAfterAWholeFullQuotaIdleWindow()
    {
        using var fixture = new AdmissionFixture(visitorPermits: 1);
        using var admitted = fixture.Owner.AttemptAcquire(fixture.Request("visitor"));
        var original = fixture.Visitors.Single();
        fixture.Clock.Advance(Window);
        original.Replenish();
        fixture.Clock.Advance(Window - TimeSpan.FromTicks(1));

        fixture.Timer.Fire();
        original.Disposals.Should().Be(0);
        fixture.Clock.Advance(TimeSpan.FromTicks(1));
        fixture.Timer.Fire();
        original.Disposals.Should().Be(1);
        using var replacement = fixture.Owner.AttemptAcquire(fixture.Request("visitor"));

        replacement.IsAcquired.Should().BeTrue();
        fixture.Visitors.Should().HaveCount(2);
        fixture.Visitors[1].Should().NotBeSameAs(original);
    }

    [Fact]
    public async Task Sweep_RegistryLargerThanTwoBatches_VisitsTheWholeCensusInOneTick()
    {
        using var fixture = new AdmissionFixture(visitorPermits: 1);
        for (var index = 0; index < 300; index++)
        {
            using var lease = fixture.Owner.AttemptAcquire(fixture.Request($"visitor-{index}"));
        }
        var original = fixture.Visitors.ToArray();
        foreach (var visitor in original) visitor.Replenish();
        fixture.Clock.Advance(Window);
        using var release = new ManualResetEventSlim();
        var entered = Completion();
        original[0].OnDispose = () =>
        {
            entered.TrySetResult();
            release.Wait(Timeout).Should().BeTrue();
        };
        var sweep = Task.Run(fixture.Timer.Fire);
        try
        {
            await entered.Task.WaitAsync(Timeout);
            original.Sum(visitor => visitor.IdleReads).Should().Be(128,
                "the first bounded lock batch must end before retired children are disposed");
            var acquire = Task.Run(() =>
            {
                using var lease = fixture.Owner.AttemptAcquire(fixture.Request("between-batches"));
                return lease.IsAcquired;
            });
            (await acquire.WaitAsync(Timeout)).Should().BeTrue(
                "new admission must progress while the first batch is disposed outside the lock");
            original.Sum(visitor => visitor.IdleReads).Should().Be(128);
        }
        finally
        {
            release.Set();
            await sweep.WaitAsync(Timeout);
        }

        original.Should().HaveCount(300);
        original.Should().OnlyContain(visitor => visitor.IdleReads == 1 && visitor.Disposals == 1);
        fixture.Timer.Fire();
        original.Should().OnlyContain(visitor => visitor.Disposals == 1);
    }

    [Fact]
    public async Task Sweep_ConcurrentTimerCallbacks_DoNotOverlap()
    {
        using var fixture = new AdmissionFixture(visitorPermits: 1);
        using var admitted = fixture.Owner.AttemptAcquire(fixture.Request("visitor"));
        var visitor = fixture.Visitors.Single();
        using var release = new ManualResetEventSlim();
        var entered = Completion();
        visitor.OnIdleRead = () =>
        {
            entered.TrySetResult();
            release.Wait(Timeout).Should().BeTrue();
        };
        var first = Task.Run(fixture.Timer.Fire);
        try
        {
            await entered.Task.WaitAsync(Timeout);
            var second = Task.Run(fixture.Timer.Fire);
            await second.WaitAsync(Timeout);
            visitor.IdleReads.Should().Be(1);
        }
        finally
        {
            release.Set();
            await first.WaitAsync(Timeout);
        }
    }

    [Fact]
    public async Task Sweep_RetiredLimiterDisposal_IsOutsideAdmissionLockAndAfterRemoval()
    {
        using var fixture = new AdmissionFixture(visitorPermits: 1);
        using var admitted = fixture.Owner.AttemptAcquire(fixture.Request("visitor"));
        var original = fixture.Visitors.Single();
        original.Replenish();
        fixture.Clock.Advance(Window);
        using var release = new ManualResetEventSlim();
        var entered = Completion();
        original.OnDispose = () =>
        {
            entered.TrySetResult();
            release.Wait(Timeout).Should().BeTrue();
        };
        var sweep = Task.Run(fixture.Timer.Fire);
        try
        {
            await entered.Task.WaitAsync(Timeout);
            var acquire = Task.Run(() =>
            {
                using var lease = fixture.Owner.AttemptAcquire(fixture.Request("visitor"));
                return lease.IsAcquired;
            });
            (await acquire.WaitAsync(Timeout)).Should().BeTrue();
            fixture.Visitors.Should().HaveCount(2);
            original.PositiveAttempts.Should().Be(1);
            fixture.Visitors[1].PositiveAttempts.Should().Be(1);
        }
        finally
        {
            release.Set();
            await sweep.WaitAsync(Timeout);
        }
        original.Disposals.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disposal_ActiveSweep_ClosesAdmissionJoinsCallbacksAndDisposesEveryLimiterOnce(bool asynchronous)
    {
        using var fixture = new AdmissionFixture(visitorPermits: 1);
        var savedRequest = fixture.Request("retiring");
        using var admitted = fixture.Owner.AttemptAcquire(savedRequest);
        using var retained = fixture.Owner.AttemptAcquire(fixture.Request("retained"));
        var retiring = fixture.Visitors[0];
        retiring.Replenish();
        fixture.Clock.Advance(Window);
        using var release = new ManualResetEventSlim();
        var entered = Completion();
        retiring.OnDispose = () =>
        {
            entered.TrySetResult();
            release.Wait(Timeout).Should().BeTrue();
        };
        var sweep = Task.Run(fixture.Timer.Fire);
        Task? shutdown = null;
        Task? concurrentShutdown = null;
        try
        {
            await entered.Task.WaitAsync(Timeout);
            shutdown = asynchronous
                ? fixture.Owner.DisposeAsync().AsTask()
                : Task.Run(fixture.Owner.Dispose);
            await fixture.Timer.DisposalRequested.Task.WaitAsync(Timeout);
            concurrentShutdown = asynchronous
                ? Task.Run(fixture.Owner.Dispose)
                : fixture.Owner.DisposeAsync().AsTask();

            shutdown.IsCompleted.Should().BeFalse();
            concurrentShutdown.IsCompleted.Should().BeFalse();
            var replay = () => fixture.Owner.AttemptAcquire(savedRequest);
            replay.Should().Throw<ObjectDisposedException>("closed admission must override cached success");
            var fresh = () => fixture.Owner.AttemptAcquire(fixture.Request("fresh"));
            fresh.Should().Throw<ObjectDisposedException>();
        }
        finally
        {
            release.Set();
            await sweep.WaitAsync(Timeout);
            if (shutdown is not null) await shutdown.WaitAsync(Timeout);
            if (concurrentShutdown is not null) await concurrentShutdown.WaitAsync(Timeout);
        }

        fixture.Timer.Fire().Should().BeFalse();
        fixture.Timer.AsyncDisposals.Should().Be(1);
        fixture.Peer.Disposals.Should().Be(1);
        fixture.Visitors.Should().OnlyContain(visitor => visitor.Disposals == 1);
    }

    private static TimeSpan RetryAfter(RateLimitLease lease)
    {
        lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter).Should().BeTrue();
        return retryAfter;
    }

    private static TaskCompletionSource Completion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<int> ConcurrentAdmissions(AdmissionFixture fixture, Func<int, HttpContext> request)
    {
        using var start = new ManualResetEventSlim();
        var attempts = Enumerable.Range(0, 32).Select(index => Task.Run(() =>
        {
            start.Wait(Timeout).Should().BeTrue();
            using var lease = fixture.Owner.AttemptAcquire(request(index));
            return lease.IsAcquired;
        })).ToArray();
        start.Set();
        var results = await Task.WhenAll(attempts).WaitAsync(Timeout);
        return results.Count(acquired => acquired);
    }

    private sealed class AdmissionFixture : IDisposable
    {
        private readonly ServiceProvider _services;

        public AdmissionFixture(int peerPermits = 600, int visitorPermits = 60, Func<RateLimiter>? visitorFactory = null)
        {
            Clock = new ManualClock();
            Peer = new PeerBudget(Clock, peerPermits);
            _services = new ServiceCollection()
                .AddSingleton(new AnonymousRequestIdentity(new EffectiveHostAccessor(Options.Create(new TrustedHopOptions()))))
                .BuildServiceProvider();
            Owner = new NoQueueAdmissionLimiter(Peer, () =>
            {
                if (visitorFactory is not null) return visitorFactory();
                var visitor = new Budget(Clock, visitorPermits);
                Visitors.Add(visitor);
                return visitor;
            }, Window, Clock);
        }

        public ManualClock Clock { get; }
        public ManualTimer Timer => Clock.Timer!;
        public PeerBudget Peer { get; }
        public NoQueueAdmissionLimiter Owner { get; }
        public List<Budget> Visitors { get; } = [];

        public DefaultHttpContext Request(string visitor, string peer = "peer")
        {
            var context = new DefaultHttpContext { RequestServices = _services };
            context.Features.Set(new AnonymousVisitor(peer, visitor, false));
            return context;
        }

        public void Dispose()
        {
            Owner.Dispose();
            _services.Dispose();
        }
    }

    private sealed class PeerBudget(ManualClock clock, int permits) : PartitionedRateLimiter<HttpContext>
    {
        private readonly ConcurrentDictionary<string, Budget> _budgets = new(StringComparer.Ordinal);
        public int Disposals { get; private set; }
        public int TotalPositiveAttempts => _budgets.Values.Sum(budget => budget.PositiveAttempts);
        public Budget For(string peer) => _budgets.GetOrAdd(peer, _ => new Budget(clock, permits));
        public override RateLimiterStatistics? GetStatistics(HttpContext resource) => null;
        protected override RateLimitLease AttemptAcquireCore(HttpContext resource, int permitCount) =>
            For(resource.Features.Get<AnonymousVisitor>()!.PeerKey).AttemptAcquire(permitCount);
        protected override ValueTask<RateLimitLease> AcquireAsyncCore(HttpContext resource, int permitCount, CancellationToken cancellationToken) =>
            ValueTask.FromResult(AttemptAcquireCore(resource, permitCount));
        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposals++;
            base.Dispose(disposing);
        }
        protected override ValueTask DisposeAsyncCore()
        {
            Disposals++;
            return base.DisposeAsyncCore();
        }
    }

    private sealed class Budget(ManualClock clock, int permits) : RateLimiter
    {
        private readonly object _gate = new();
        private readonly int _permitLimit = permits;
        private long? _idleSince = clock.GetTimestamp();
        private int _remaining = permits;
        private int _positiveAttempts;
        private int _zeroProbes;
        private int _idleReads;
        private int _disposals;

        public int Remaining => Volatile.Read(ref _remaining);
        public int PositiveAttempts => Volatile.Read(ref _positiveAttempts);
        public int ZeroProbes => Volatile.Read(ref _zeroProbes);
        public int IdleReads => Volatile.Read(ref _idleReads);
        public int Disposals => Volatile.Read(ref _disposals);
        public TimeSpan RetryAfter { get; set; } = Window;
        public Action? OnIdleRead { get; set; }
        public Action? OnDispose { get; set; }
        public List<BudgetLease> Leases { get; } = [];
        public override TimeSpan? IdleDuration
        {
            get
            {
                Interlocked.Increment(ref _idleReads);
                OnIdleRead?.Invoke();
                lock (_gate) return _idleSince is { } since ? clock.GetElapsedTime(since) : null;
            }
        }

        public void Replenish(int? remaining = null)
        {
            lock (_gate)
            {
                _remaining = remaining ?? _permitLimit;
                _idleSince = clock.GetTimestamp();
            }
        }

        public override RateLimiterStatistics? GetStatistics() => null;
        protected override RateLimitLease AttemptAcquireCore(int permitCount)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(Disposals != 0, this);
                if (permitCount == 0) Interlocked.Increment(ref _zeroProbes);
                else Interlocked.Increment(ref _positiveAttempts);
                var acquired = _remaining > 0 && _remaining >= permitCount;
                if (acquired && permitCount > 0)
                {
                    _remaining -= permitCount;
                    _idleSince = null;
                }
                var lease = new BudgetLease(acquired, RetryAfter);
                Leases.Add(lease);
                return lease;
            }
        }
        protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken) =>
            ValueTask.FromResult(AttemptAcquireCore(permitCount));
        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeBudget();
            base.Dispose(disposing);
        }
        protected override ValueTask DisposeAsyncCore()
        {
            DisposeBudget();
            return base.DisposeAsyncCore();
        }
        private void DisposeBudget()
        {
            Interlocked.Increment(ref _disposals);
            OnDispose?.Invoke();
        }
    }

    private sealed class BudgetLease(bool acquired, TimeSpan retryAfter) : RateLimitLease
    {
        public int Disposals { get; private set; }
        public override bool IsAcquired
        {
            get
            {
                ObjectDisposedException.ThrowIf(Disposals != 0, this);
                return acquired;
            }
        }
        public override IEnumerable<string> MetadataNames => acquired ? [] : [MetadataName.RetryAfter.Name];
        public override bool TryGetMetadata(string metadataName, out object? value)
        {
            ObjectDisposedException.ThrowIf(Disposals != 0, this);
            value = !acquired && metadataName == MetadataName.RetryAfter.Name ? retryAfter : null;
            return value is not null;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposals++;
            base.Dispose(disposing);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp;
        public ManualTimer? Timer { get; private set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _timestamp, duration.Ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Timer.Should().BeNull("the admission owner creates one periodic sweeper");
            dueTime.Should().BeGreaterThan(TimeSpan.Zero);
            period.Should().BeGreaterThan(TimeSpan.Zero);
            Timer = new ManualTimer(callback, state);
            return Timer;
        }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource _joined = Completion();
        private bool _disposed;
        private int _callbacks;
        private int _fires;
        private int _asyncDisposals;
        public int Fires => Volatile.Read(ref _fires);
        public int AsyncDisposals => Volatile.Read(ref _asyncDisposals);
        public TaskCompletionSource DisposalRequested { get; } = Completion();

        public bool Fire()
        {
            lock (_gate)
            {
                if (_disposed) return false;
                _callbacks++;
                Interlocked.Increment(ref _fires);
            }
            try
            {
                callback(state);
                return true;
            }
            finally
            {
                lock (_gate)
                {
                    _callbacks--;
                    if (_disposed && _callbacks == 0) _joined.TrySetResult();
                }
            }
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate) return !_disposed;
        }
        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                if (_callbacks == 0) _joined.TrySetResult();
            }
            DisposalRequested.TrySetResult();
        }
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _asyncDisposals);
            Dispose();
            return new ValueTask(_joined.Task);
        }
    }
}
