using System.Reflection;
using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Jobs;

public sealed class BackgroundJobBridgeGateTests
{
    [Theory]
    [InlineData("open")]
    [InlineData("host")]
    [InlineData("final")]
    [InlineData("bridge")]
    public async Task A_queued_claim_rechecks_closure_at_actual_source_acquisition(string closure)
    {
        var definitions = BackgroundJobDefinitionRegistry.CreateEmpty();
        var gate = new BackgroundJobRuntimeGate(definitions.Infrastructure);
        var calls = 0;
        await using var source = new PrivilegedDataSource(() =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("The positive source boundary was reached.");
        });
        var store = new PostgresBackgroundJobBridgeStore(source, definitions, new(), new SystemGuidFactory(), gate);
        var lane = (SemaphoreSlim)(typeof(PostgresBackgroundJobBridgeStore)
            .GetField("_sqlLane", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(store)
            ?? throw new InvalidOperationException("The confined SQL lane is missing."));
        await lane.WaitAsync();
        var pending = store.TryClaimAsync([]);
        try
        {
            pending.IsCompleted.Should().BeFalse();
            if (closure == "host") gate.StopStarts();
            else if (closure == "final") gate.BeginFinalExit();
            else if (closure == "bridge") store.StopAcquisitions();
        }
        finally { lane.Release(); }
        var acquire = () => pending;
        await acquire.Should().ThrowAsync<InvalidOperationException>().WithMessage(closure == "open"
            ? "The positive source boundary was reached." : "The bridge acquisition gate is closed.");
        calls.Should().Be(closure == "open" ? 1 : 0);
        source.IsValueCreated.Should().BeFalse();
    }
}
