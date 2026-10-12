using FluentAssertions;
using LearnStack.Api.Messaging;
using LearnStack.SharedKernel.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Messaging;

public sealed class OutboxPublishAttemptTests
{
    [Fact]
    public async Task Synchronous_construction_and_publish_prefix_run_inside_the_owned_task()
    {
        using var constructRelease = new ManualResetEventSlim();
        using var publishRelease = new ManualResetEventSlim();
        var constructed = Signal();
        var published = Signal();
        var disposed = Signal();
        var services = new ServiceCollection();
        services.AddScoped<IEventBus>(_ =>
        {
            constructed.TrySetResult();
            constructRelease.Wait(); // Intentionally hostile synchronous construction.
            return new CallbackBus((_, _) =>
            {
                published.TrySetResult();
                publishRelease.Wait(CancellationToken.None); // Intentionally hostile synchronous PublishAsync prefix.
                return Task.CompletedTask;
            }, () => { disposed.TrySetResult(); return ValueTask.CompletedTask; });
        });
        await using var provider = services.BuildServiceProvider();
        var attempt = new OutboxPublishAttempt(provider.GetRequiredService<IServiceScopeFactory>(), Envelope());
        try
        {
            await constructed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            attempt.Completion.IsCompleted.Should().BeFalse();
            constructRelease.Set();
            await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
            attempt.Completion.IsCompleted.Should().BeFalse();
            disposed.Task.IsCompleted.Should().BeFalse();
            attempt.RequestCancellation();
        }
        finally
        {
            constructRelease.Set();
            publishRelease.Set();
            await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }

        (await attempt.Completion).Should().BeFalse("cancelled late success cannot become a successful attempt");
        disposed.Task.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task Ignored_cancellation_and_async_cleanup_retain_scope_and_token_until_actual_completion()
    {
        var entered = Signal();
        var releaseCallback = Signal();
        var cleanupEntered = Signal();
        var releaseCleanup = Signal();
        CancellationToken captured = default;
        var disposeCount = 0;
        var services = new ServiceCollection();
        services.AddScoped<IEventBus>(_ => new CallbackBus(async (_, token) =>
        {
            captured = token;
            entered.TrySetResult();
            await releaseCallback.Task;
        }, async () =>
        {
            Interlocked.Increment(ref disposeCount);
            cleanupEntered.TrySetResult();
            await releaseCleanup.Task;
        }));
        await using var provider = services.BuildServiceProvider();
        var attempt = new OutboxPublishAttempt(provider.GetRequiredService<IServiceScopeFactory>(), Envelope());
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            attempt.RequestCancellation();
            captured.IsCancellationRequested.Should().BeTrue();
            captured.WaitHandle.Should().NotBeNull();
            disposeCount.Should().Be(0);
            attempt.Completion.IsCompleted.Should().BeFalse();
            releaseCallback.TrySetResult();
            await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            captured.WaitHandle.Should().NotBeNull("asynchronous cleanup still owns the cancellation source");
            attempt.Completion.IsCompleted.Should().BeFalse();
        }
        finally
        {
            releaseCallback.TrySetResult();
            releaseCleanup.TrySetResult();
            await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }

        disposeCount.Should().Be(1);
        var accessDisposedToken = () => captured.WaitHandle;
        accessDisposedToken.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task Blocking_cancellation_registration_is_retained_and_observed_before_scope_disposal()
    {
        var entered = Signal();
        var releaseCallback = Signal();
        var cancellationEntered = Signal();
        using var releaseCancellation = new ManualResetEventSlim();
        var disposed = Signal();
        var services = new ServiceCollection();
        services.AddScoped<IEventBus>(_ => new CallbackBus(async (_, token) =>
        {
            token.Register(() =>
            {
                cancellationEntered.TrySetResult();
                releaseCancellation.Wait(); // Exercise a cancellation callback that blocks and faults.
                throw new InvalidOperationException("private-provider-cancellation-error");
            });
            entered.TrySetResult();
            await releaseCallback.Task;
        }, () => { disposed.TrySetResult(); return ValueTask.CompletedTask; }));
        await using var provider = services.BuildServiceProvider();
        var attempt = new OutboxPublishAttempt(provider.GetRequiredService<IServiceScopeFactory>(), Envelope());
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            attempt.RequestCancellation();
            await cancellationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            releaseCallback.TrySetResult();
            attempt.Completion.IsCompleted.Should().BeFalse();
            disposed.Task.IsCompleted.Should().BeFalse();
        }
        finally
        {
            releaseCallback.TrySetResult();
            releaseCancellation.Set();
            await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }

        (await attempt.Completion).Should().BeFalse();
        disposed.Task.IsCompleted.Should().BeTrue();
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static IntegrationEventEnvelope Envelope() => new(new TestEvent
    {
        EventId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        OccurredAt = DateTimeOffset.UtcNow,
    }, "00-0123456789abcdef0123456789abcdef-0123456789abcdef-01");

    private sealed record TestEvent : IntegrationEventBase
    {
        public override string Topic => "learnstack.test.attempt";
        public override string PartitionKey => TenantId.ToString("D");
    }

    private sealed class CallbackBus(
        Func<IntegrationEventEnvelope, CancellationToken, Task> publish,
        Func<ValueTask> dispose) : IEventBus, IAsyncDisposable
    {
        public Task PublishAsync(IntegrationEventEnvelope envelope, CancellationToken cancellationToken = default) =>
            publish(envelope, cancellationToken);
        public ValueTask DisposeAsync() => dispose();
    }
}
