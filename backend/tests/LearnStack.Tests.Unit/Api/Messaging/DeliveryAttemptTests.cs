using FluentAssertions;
using LearnStack.Infrastructure.Messaging;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Messaging;

public sealed class DeliveryAttemptTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_closes_gate_before_blocking_callbacks_and_retains_token_until_actual_cleanup(bool callbackFault)
    {
        var control = new DeliveryExecutionControl();
        var token = control.Token;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedClosedGate = false;
        var observedLiveToken = false;
        using var registration = token.Register(() =>
        {
            observedClosedGate = control.Gate.IsClosed;
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            observedLiveToken = token.WaitHandle.WaitOne(0);
            if (callbackFault) throw new InvalidOperationException("private-cancellation-canary");
        });
        Task<bool>? finishing = null;
        try
        {
            control.RequestCancellation();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            observedClosedGate.Should().BeTrue("the gate decision precedes every cancellation callback");
            control.Gate.IsClosed.Should().BeTrue();
            token.IsCancellationRequested.Should().BeTrue();
            finishing = control.FinishCallbacksAsync();
            finishing.IsCompleted.Should().BeFalse("the live callback still owns cancellation resources");
            control.RequestCancellation();
        }
        finally
        {
            release.TrySetResult();
            finishing ??= control.FinishCallbacksAsync();
            (await finishing.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(!callbackFault);
            control.DisposeCancellation();
        }
        observedLiveToken.Should().BeTrue();
        FluentActions.Invoking(() => _ = token.WaitHandle).Should().Throw<ObjectDisposedException>();
        control.RequestCancellation();
    }
}
