using System.Threading.RateLimiting;
using FluentAssertions;
using LearnStack.Api.Authentication;
using LearnStack.Api.Tenancy;
using LearnStack.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Authentication;

public sealed partial class TenantSubjectRateLimiterTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Window = TenantSubjectRateLimiter.Window;

    [Fact]
    public void Production_budget_allows_600_then_refuses_without_actor_or_scope_reset()
    {
        using var services = new ServiceCollection().AddLearnStackTenantSubjectAdmission().BuildServiceProvider();
        var owner = services.GetRequiredService<TenantSubjectRateLimiter>();
        var identity = Identity();
        for (var index = 0; index < 600; index++)
        {
            // The external identity remains stable while application attribution
            // and selected tenant/organization change between requests.
            using var acquired = owner.AttemptAcquire(identity with
            {
                ActorUserId = UserId.From(Guid.NewGuid()),
                TenantId = TenantId.From(Guid.NewGuid()),
                OrganizationId = index % 2 == 0 ? null : OrganizationId.From(Guid.NewGuid()),
            });
            acquired.IsAcquired.Should().BeTrue();
        }

        using var refused = owner.AttemptAcquire(identity);
        refused.IsAcquired.Should().BeFalse();
        refused.TryGetMetadata(MetadataName.RetryAfter, out var retry).Should().BeTrue();
        retry.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThanOrEqualTo(Window);
        // Both tuple positions and their ordinal casing distinguish identities.
        using var otherIssuer = owner.AttemptAcquire(identity with { Issuer = "https://other.example/realm" });
        using var otherSubject = owner.AttemptAcquire(identity with { Subject = "another-subject" });
        using var caseSensitive = owner.AttemptAcquire(identity with { Subject = "SUBJECT" });
        otherIssuer.IsAcquired.Should().BeTrue();
        otherSubject.IsAcquired.Should().BeTrue();
        caseSensitive.IsAcquired.Should().BeTrue();
    }

    [Fact]
    public async Task Middleware_reuses_request_outcomes_and_only_debits_validated_classified_requests()
    {
        using var clock = new ManualClock();
        var budget = new TrackedBudget(2);
        using var owner = new TenantSubjectRateLimiter(() => budget, Window, clock);
        var nextCalls = 0;
        var middleware = new TenantSubjectAdmissionMiddleware(_ =>
        {
            nextCalls++;
            return Task.CompletedTask;
        }, owner);
        var unclassified = Request(classified: false);
        var anonymous = Request(identity: null);
        await middleware.InvokeAsync(unclassified);
        await middleware.InvokeAsync(anonymous);
        budget.Attempts.Should().Be(0);

        var admitted = Request();
        await middleware.InvokeAsync(admitted);
        await middleware.InvokeAsync(admitted);
        var second = Request();
        await middleware.InvokeAsync(second);
        var refused = Request();
        await middleware.InvokeAsync(refused);
        await middleware.InvokeAsync(refused);

        budget.Attempts.Should().Be(3);
        nextCalls.Should().Be(5);
        refused.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        refused.Response.Headers.RetryAfter.ToString().Should().Be("60");
        refused.Response.Body.Length.Should().Be(0, "outer status-code pages own the single Problem Details body");
        budget.Leases.Should().OnlyContain(lease => lease.Disposals == 1);
    }

    [Fact]
    public async Task Concurrent_last_permit_owns_one_partition_and_admits_exactly_one_request()
    {
        using var clock = new ManualClock();
        List<TrackedBudget> budgets = [];
        using var owner = new TenantSubjectRateLimiter(() =>
        {
            var budget = new TrackedBudget(1);
            budgets.Add(budget);
            return budget;
        }, Window, clock);
        var start = Completion();
        var tasks = Enumerable.Range(0, 64).Select(async _ =>
        {
            await start.Task;
            return await Task.Run(() =>
            {
                using var lease = owner.AttemptAcquire(Identity());
                return lease.IsAcquired;
            });
        }).ToArray();
        start.SetResult();
        var outcomes = await Task.WhenAll(tasks).WaitAsync(Timeout);

        outcomes.Count(acquired => acquired).Should().Be(1);
        budgets.Should().ContainSingle();
        budgets[0].Attempts.Should().Be(64);
        budgets[0].Leases.Should().OnlyContain(lease => lease.Disposals == 1);
    }

    [Fact]
    public void Sweep_does_not_reset_real_unexpired_exhausted_quota_when_retirement_clock_advances()
    {
        using var clock = new ManualClock();
        var allocations = 0;
        using var owner = new TenantSubjectRateLimiter(() =>
        {
            allocations++;
            return TenantSubjectRateLimiter.CreateBudget(1, autoReplenishment: false);
        }, Window, clock);
        using var admitted = owner.AttemptAcquire(Identity());
        clock.Advance(TimeSpan.FromDays(1));
        for (var index = 0; index < 4; index++) clock.Timer.Fire();
        using var refused = owner.AttemptAcquire(Identity());

        admitted.IsAcquired.Should().BeTrue();
        refused.IsAcquired.Should().BeFalse();
        allocations.Should().Be(1, "elapsed sweep time cannot replenish a stock exhausted partition");
    }

    [Fact]
    public async Task Sweep_retires_real_stock_budget_after_replenishment_and_a_full_idle_window()
    {
        using var clock = new ManualClock();
        var window = TimeSpan.FromMilliseconds(50);
        List<FixedWindowRateLimiter> budgets = [];
        using var owner = new TenantSubjectRateLimiter(() =>
        {
            var budget = TenantSubjectRateLimiter.CreateBudget(1, window, autoReplenishment: false);
            budgets.Add(budget);
            return budget;
        }, window, clock);
        using var admitted = owner.AttemptAcquire(Identity());
        using var refused = owner.AttemptAcquire(Identity());
        var original = budgets[0];
        original.IdleDuration.Should().BeNull();
        clock.Timer.Fire();
        budgets.Should().ContainSingle();
        using var deadline = new CancellationTokenSource(Timeout);
        // TryReplenish reports support for external replenishment, rather than
        // whether quota actually changed. Keep driving it until the stock idle
        // state proves that all permits are available.
        while (original.IdleDuration is null)
        {
            original.TryReplenish().Should().BeTrue();
            await Task.Delay(TimeSpan.FromMilliseconds(5), deadline.Token);
        }
        while (original.IdleDuration is not { } idle || idle < window)
            await Task.Delay(TimeSpan.FromMilliseconds(5), deadline.Token);

        clock.Timer.Fire();
        var retiredAcquire = () => original.AttemptAcquire();
        retiredAcquire.Should().Throw<ObjectDisposedException>();
        using var replacement = owner.AttemptAcquire(Identity());
        budgets.Should().HaveCount(2);
        replacement.IsAcquired.Should().BeTrue();
        // The old child leases were already disposed; retained snapshots remain usable.
        admitted.IsAcquired.Should().BeTrue();
        refused.IsAcquired.Should().BeFalse();
        refused.TryGetMetadata(MetadataName.RetryAfter, out var retry).Should().BeTrue();
        retry.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThanOrEqualTo(window);
    }

    [Fact]
    public void Sweep_inspects_at_most_128_per_callback_and_rotates_survivors()
    {
        using var clock = new ManualClock();
        List<TrackedBudget> budgets = [];
        using var owner = new TenantSubjectRateLimiter(() =>
        {
            var budget = new TrackedBudget(1);
            budgets.Add(budget);
            return budget;
        }, Window, clock);
        for (var index = 0; index < 300; index++)
        {
            using var lease = owner.AttemptAcquire(Identity(subject: $"subject-{index}"));
        }

        clock.Timer.Fire();
        budgets.Sum(budget => budget.IdleReads).Should().Be(128);
        budgets.Should().OnlyContain(budget => budget.Disposals == 0);
        foreach (var budget in budgets) budget.Replenish(Window);
        for (var index = 0; index < 3; index++)
        {
            var before = budgets.Sum(budget => budget.IdleReads);
            clock.Timer.Fire();
            (budgets.Sum(budget => budget.IdleReads) - before).Should().BeInRange(1, 128);
        }
        budgets.Should().OnlyContain(budget => budget.Disposals == 1 && (budget.IdleReads == 1 || budget.IdleReads == 2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shutdown_joins_active_retirement_closes_admission_and_disposes_each_owned_budget_once(bool asynchronous)
    {
        using var clock = new ManualClock();
        List<TrackedBudget> budgets = [];
        var owner = new TenantSubjectRateLimiter(() =>
        {
            var budget = new TrackedBudget(1);
            budgets.Add(budget);
            return budget;
        }, Window, clock);
        using var admitted = owner.AttemptAcquire(Identity("retiring"));
        using var retained = owner.AttemptAcquire(Identity("retained"));
        using var refused = owner.AttemptAcquire(Identity("retained"));
        var retiring = budgets[0];
        retiring.Replenish(Window);
        using var release = new ManualResetEventSlim();
        var entered = Completion();
        retiring.OnDispose = () =>
        {
            entered.TrySetResult();
            release.Wait(Timeout).Should().BeTrue();
        };
        var sweep = Task.Run(clock.Timer.Fire);
        Task shutdown = Task.CompletedTask;
        Task concurrentShutdown = Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(Timeout);
            // Retired limiter disposal must leave the owner lock available.
            using var newAdmission = await Task.Run(() => owner.AttemptAcquire(Identity("during-retirement")))
                .WaitAsync(Timeout);
            newAdmission.IsAcquired.Should().BeTrue();
            await Task.Run(clock.Timer.Fire).WaitAsync(Timeout);
            budgets.Sum(budget => budget.IdleReads).Should().Be(2, "overlapping sweeps must not start another batch");

            shutdown = Task.Run(async () =>
            {
                if (asynchronous) await owner.DisposeAsync();
                else owner.Dispose();
            });
            await clock.Timer.DisposalRequested.Task.WaitAsync(Timeout);
            concurrentShutdown = owner.DisposeAsync().AsTask();
            shutdown.IsCompleted.Should().BeFalse();
            concurrentShutdown.IsCompleted.Should().BeFalse();
            var acquireAfterClose = () => owner.AttemptAcquire(Identity());
            acquireAfterClose.Should().Throw<ObjectDisposedException>();
        }
        finally
        {
            release.Set();
            await Task.WhenAll(sweep, shutdown, concurrentShutdown).WaitAsync(Timeout);
            await owner.DisposeAsync();
        }

        owner.Dispose();
        clock.Timer.AsyncDisposals.Should().Be(1);
        clock.Timer.Fire().Should().BeFalse();
        budgets.Should().HaveCount(3).And.OnlyContain(budget => budget.Disposals == 1);
        budgets.Skip(1).Should().OnlyContain(budget => budget.AsyncDisposals == (asynchronous ? 1 : 0));
        admitted.IsAcquired.Should().BeTrue();
        retained.IsAcquired.Should().BeTrue();
        refused.IsAcquired.Should().BeFalse();
        refused.TryGetMetadata(MetadataName.RetryAfter, out var retry).Should().BeTrue();
        retry.Should().Be(Window);
        budgets.SelectMany(budget => budget.Leases).Should().OnlyContain(lease => lease.Disposals == 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Registered_singleton_is_closed_on_both_service_provider_shutdown_paths(bool asynchronous)
    {
        var provider = new ServiceCollection().AddLearnStackTenantSubjectAdmission().BuildServiceProvider();
        var owner = provider.GetRequiredService<TenantSubjectRateLimiter>();
        using var lease = owner.AttemptAcquire(Identity());
        if (asynchronous) await provider.DisposeAsync();
        else provider.Dispose();
        var acquire = () => owner.AttemptAcquire(Identity());
        acquire.Should().Throw<ObjectDisposedException>();
        lease.IsAcquired.Should().BeTrue();
    }

    private static ValidatedTenantIdentity Identity(string subject = "subject") =>
        new("https://issuer.example/realm", subject,
            UserId.From(Guid.Parse("bdcd67b9-2822-4479-84ce-e5fa05421544")), null, null);

    private static DefaultHttpContext Request(bool classified = true) => Request(Identity(), classified);

    private static DefaultHttpContext Request(ValidatedTenantIdentity? identity, bool classified = true)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        if (classified) context.Features.Set(HostClassification.Platform("platform.example"));
        context.Features.Set(new TenantCredentialFeature { IsPresent = identity is not null, Identity = identity });
        return context;
    }

    private static TaskCompletionSource Completion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
