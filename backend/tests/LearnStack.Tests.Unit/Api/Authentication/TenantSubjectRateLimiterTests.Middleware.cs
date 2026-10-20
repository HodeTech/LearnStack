using FluentAssertions;
using LearnStack.Api.Authentication;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Authentication;

public sealed partial class TenantSubjectRateLimiterTests
{
    [Fact]
    public async Task Middleware_caches_all_four_decisions_and_emits_only_one_closed_rejection_per_request()
    {
        using var clock = new ManualClock();
        using var metrics = new SubjectMetricCapture();
        List<TrackedBudget> budgets = [];
        var owner = new TenantSubjectRateLimiter(() =>
        {
            var budget = new TrackedBudget(1) { RetryAfter = TimeSpan.FromMilliseconds(1201) };
            budgets.Add(budget);
            return budget;
        }, Window, clock, maximumPartitions: 1);
        var dispatches = 0;
        var middleware = new TenantSubjectAdmissionMiddleware(_ =>
        {
            dispatches++;
            return Task.CompletedTask;
        }, owner);
        var admitted = Request();
        var quota = Request();
        var capacity = Request(Identity("capacity-canary"));
        var fault = Request(Identity("fault-canary"));
        var failure = new InvalidOperationException("synthetic middleware cleanup failure");
        try
        {
            await middleware.InvokeAsync(admitted);
            await middleware.InvokeAsync(admitted);
            budgets.Single().Attempts.Should().Be(1);
            metrics.Rejections.Should().BeEmpty();
            admitted.Response.Headers.Should().NotContainKey("Retry-After");

            await middleware.InvokeAsync(quota);
            await middleware.InvokeAsync(quota);
            quota.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
            quota.Response.Headers.RetryAfter.ToString().Should().Be("2", "stock retry metadata is rounded upwards");

            await middleware.InvokeAsync(capacity);
            await middleware.InvokeAsync(capacity);
            capacity.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
            capacity.Response.Headers.RetryAfter.ToString().Should().Be("60");
            budgets.Should().ContainSingle();

            budgets[0].Replenish(Window);
            budgets[0].OnDispose = () => throw failure;
            await middleware.InvokeAsync(fault);
            await middleware.InvokeAsync(fault);
            fault.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
            fault.Response.Headers.RetryAfter.ToString().Should().Be("30");
            dispatches.Should().Be(2, "only the cached Acquired decision can dispatch on re-execution");
            budgets[0].Attempts.Should().Be(2, "one acquired request and one quota refusal, with no re-execution debit");
            budgets.Should().ContainSingle("the fault-owning request allocated no new child");
            metrics.Rejections.Should().HaveCount(3);
            metrics.Rejections.Should().OnlyContain(value => value.Value == 1 && value.Tags.Count == 1);
            metrics.Rejections.Select(value => value.Tags).Should().BeEquivalentTo(
            new[]
            {
                new Dictionary<string, object?> { ["reason"] = "quota" },
                new Dictionary<string, object?> { ["reason"] = "registry_capacity" },
                new Dictionary<string, object?> { ["reason"] = "registry_fault" },
            });
            metrics.CleanupFailures.Should().ContainSingle();
            metrics.CleanupFailures[0].Value.Should().Be(1);
            metrics.CleanupFailures[0].Tags.Should().BeEmpty();
            foreach (var context in new[] { quota, capacity, fault })
                context.Response.Body.Length.Should().Be(0, "the outer standard Problem Details writer owns the body");
        }
        finally
        {
            var shutdownFailure = await Record.ExceptionAsync(() => owner.DisposeAsync().AsTask().WaitAsync(Timeout));
            shutdownFailure.Should().BeOfType<AggregateException>().Which.Flatten().InnerExceptions.Should().Contain(failure);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(999)]
    public async Task Middleware_quota_retry_hint_has_a_minimum_of_one_second(int retryMilliseconds)
    {
        using var clock = new ManualClock();
        var budget = new TrackedBudget(1) { RetryAfter = TimeSpan.FromMilliseconds(retryMilliseconds) };
        using var owner = new TenantSubjectRateLimiter(() => budget, Window, clock);
        owner.AttemptAcquire(Identity());
        var dispatched = false;
        var middleware = new TenantSubjectAdmissionMiddleware(_ =>
        {
            dispatched = true;
            return Task.CompletedTask;
        }, owner);
        var context = Request();

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        context.Response.Headers.RetryAfter.ToString().Should().Be("1");
        dispatched.Should().BeFalse();
    }
}
