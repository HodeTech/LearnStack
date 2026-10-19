using System.Reflection;
using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Persistence;

public sealed class ApplicationDataSourceLifetimeTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly TimeSpan TestBudget = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Absent_configuration_and_cold_disposal_construct_no_pool()
    {
        await using var lifetime = new ApplicationDataSourceLifetime(null);
        lifetime.Source.IsValueCreated.Should().BeFalse();
        lifetime.Source.Should().BeSameAs(lifetime.Source);
        lifetime.PoolSettings.MaxPoolSize.Should().BeGreaterThan(0);
        lifetime.Dispose();
        lifetime.Source.IsValueCreated.Should().BeFalse();
        var constructAfterDisposal = () => lifetime.Source.Value;
        constructAfterDisposal.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task Pool_settings_come_from_the_same_immutable_configuration_without_building()
    {
        await using var lifetime = new ApplicationDataSourceLifetime(
            "Host=localhost;Username=learnstack_app;Pooling=false;Maximum Pool Size=137");
        lifetime.PoolSettings.Pooling.Should().BeFalse();
        lifetime.PoolSettings.MaxPoolSize.Should().Be(137);
        lifetime.Source.IsValueCreated.Should().BeFalse();
    }

    [Fact]
    public async Task A_container_disposal_request_waits_for_actual_positive_job_completion()
    {
        await using var lifetime = new ApplicationDataSourceLifetime(
            "Host=localhost;Username=learnstack_app");
        var positive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Bind(lifetime, positive.Task);
        var observer = Observer(lifetime);
        await lifetime.DisposeAsync();
        observer.IsCompleted.Should().BeFalse();
        lifetime.Source.IsValueCreated.Should().BeFalse();
        _ = lifetime.Source.Value;
        lifetime.Source.IsValueCreated.Should().BeTrue();
        positive.SetResult();
        await observer.WaitAsync(TestBudget);
        ReadFlag(lifetime, "IsDisposalComplete").Should().BeTrue();
        ReadFlag(lifetime, "HasUnconfirmedCleanup").Should().BeFalse();
        await lifetime.DisposeAsync();
        Observer(lifetime).Should().BeSameAs(observer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_faulted_or_cancelled_join_never_disposes_the_retained_lazy_pool(bool canceled)
    {
        await using var lifetime = new ApplicationDataSourceLifetime(
            "Host=localhost;Username=learnstack_app");
        var incomplete = canceled ? Task.FromCanceled(new CancellationToken(canceled: true))
            : Task.FromException(new InvalidOperationException("unconfirmed actual cleanup"));
        Bind(lifetime, incomplete);
        lifetime.Dispose();
        await Observer(lifetime).WaitAsync(TestBudget);
        ReadFlag(lifetime, "HasUnconfirmedCleanup").Should().BeTrue();
        ReadFlag(lifetime, "IsDisposalComplete").Should().BeFalse();
        lifetime.Source.IsValueCreated.Should().BeFalse();
        // The same still-owned source remains available for already-admitted cleanup.
        _ = lifetime.Source.Value;
        await lifetime.DisposeAsync();
        ReadFlag(lifetime, "IsDisposalComplete").Should().BeFalse();
        await lifetime.Source.Value.DisposeAsync(); // explicit test teardown, not a positive lifetime witness
    }

    private static void Bind(ApplicationDataSourceLifetime lifetime, Task completion) =>
        typeof(ApplicationDataSourceLifetime).GetMethod("RetainBackgroundJobsUntil", PrivateInstance)!
            .Invoke(lifetime, [completion]);

    private static Task Observer(ApplicationDataSourceLifetime lifetime) =>
        (Task)typeof(ApplicationDataSourceLifetime).GetField("_retentionObserver", PrivateInstance)!.GetValue(lifetime)!;

    private static bool ReadFlag(ApplicationDataSourceLifetime lifetime, string name) =>
        (bool)typeof(ApplicationDataSourceLifetime).GetProperty(name, PrivateInstance)!.GetValue(lifetime)!;
}
