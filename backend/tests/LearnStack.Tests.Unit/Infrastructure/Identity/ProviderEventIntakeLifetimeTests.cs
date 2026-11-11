using System.Collections;
using System.Reflection;
using FluentAssertions;
using LearnStack.Infrastructure.Identity.ProviderEvents;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Identity;

public sealed class ProviderEventIntakeLifetimeTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Disposal_joins_every_retained_completion_even_when_another_cleanup_faults(int failedIndex)
    {
        // Controlled private completion states exercise the real public disposal
        // join. This is not a claim that a real driver's cleanup was injected.
        var connection = new NpgsqlConnectionStringBuilder
        { Host = "192.0.2.1", Database = "owned_unopened_source", Username = "learnstack_identity_ingress", Password = Guid.NewGuid().ToString("N") };
        var source = new ProviderEventIntakeSource(connection.ConnectionString);
        var ownerType = typeof(ProviderEventIntakeSource).GetNestedType("Owner", BindingFlags.NonPublic)!;
        var owners = typeof(ProviderEventIntakeSource).GetField("_owners", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(source)!;
        var add = owners.GetType().GetMethod("Add")!;
        foreach (var _ in new[] { 0, 1 }) add.Invoke(owners, [Activator.CreateInstance(ownerType, nonPublic: true)]);
        var records = ((IEnumerable)owners).Cast<object>().ToArray();
        TaskCompletionSource Completion(int index) => (TaskCompletionSource)ownerType
            .GetProperty("Completion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(records[index])!;
        var failure = new InvalidOperationException("Owned cleanup failure.");
        Completion(failedIndex).SetException(failure);
        var pool = (NpgsqlDataSource)typeof(ProviderEventIntakeSource).GetField("_source", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(source)!;
        var disposal = source.DisposeAsync().AsTask();
        try
        {
            await Task.Yield();
            disposal.IsCompleted.Should().BeFalse("the other actual completion has not joined");
            Completion(1 - failedIndex).SetResult();
            (await Assert.ThrowsAsync<InvalidOperationException>(() => disposal.WaitAsync(TimeSpan.FromSeconds(5))))
                .Should().BeSameAs(failure);
            source.DisposeAsync().AsTask().Should().BeSameAs(disposal, "the failed disposal is one retained result");
        }
        finally
        {
            Completion(1 - failedIndex).TrySetResult();
            try { await disposal; } catch (InvalidOperationException) { }
            // No connector was opened by this fixture; retire its otherwise
            // intentionally retained pool only after checking the join result.
            await pool.DisposeAsync();
        }
    }
}
