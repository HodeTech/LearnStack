using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.PostgreSql;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Persistence;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Jobs;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HangfireGlobalConfigurationIsolation
{
    public const string Name = "Hangfire global configuration";
}

[Collection(HangfireGlobalConfigurationIsolation.Name)]
public sealed class HangfireRuntimeInventoryTests
{
    [Fact]
    public void The_exact_stock_inventory_freezes_two_selected_instances_without_opening_a_source()
    {
        using var source = new PrivilegedDataSource(() => throw new InvalidOperationException("Must not acquire"));
        var storage = new PostgreSqlStorage(new HangfireConnectionFactory(source), HangfireStorageBootstrap.Options());
        var inventory = new HangfireComponentInventory(storage);
#pragma warning disable CS0618 // Qualify the pinned public component seam.
        inventory.Selected.Should().HaveCount(2);
        inventory.Selected[0].Should().BeSameAs(inventory.Expiration);
        inventory.Selected[1].Should().BeSameAs(inventory.FetchedJobHeartbeat);
        inventory.Selected.Select(component => component.GetType().FullName).Should().Equal(
            "Hangfire.PostgreSql.ExpirationManager", "Hangfire.PostgreSql.PostgreSqlHeartbeatProcess");
        inventory.FetchedJobHeartbeat.Should().BeSameAs(storage.GetComponents().Single(component =>
            component.GetType().FullName == "Hangfire.PostgreSql.PostgreSqlHeartbeatProcess"));
#pragma warning restore CS0618
        source.IsValueCreated.Should().BeFalse();
    }

    [Fact]
    public void Missing_stock_heartbeat_refuses_instead_of_qualifying_a_smaller_inventory()
    {
        using var source = new PrivilegedDataSource(() => throw new InvalidOperationException("Must not acquire"));
        var options = HangfireStorageBootstrap.Options();
        options.UseSlidingInvisibilityTimeout = false;
        var storage = new PostgreSqlStorage(new HangfireConnectionFactory(source), options);
        var create = () => new HangfireComponentInventory(storage);
        create.Should().Throw<HangfireStorageRefusedException>();
        source.IsValueCreated.Should().BeFalse();
    }

    [Fact]
    public void History_suppression_preserves_stock_filter_identities_and_zero_retry_wrapper_attribute()
    {
        var original = GlobalJobFilters.Filters.ToArray();
        try
        {
            var filters = HangfireRuntimeFilters.Create();
            var job = Job.FromExpression<HangfireJobWakeup>(wakeup => wakeup.RunAsync(
                LearnStack.SharedKernel.Jobs.BackgroundJobId.From(
                    Guid.Parse("11111111-1111-7111-8111-111111111111")), 1));
            var selected = filters.GetFilters(job).ToArray();
            selected.Where(filter => filter.Scope == JobFilterScope.Global).Select(filter => filter.Instance)
                .Should().BeEquivalentTo(original.Where(filter => filter.Instance.GetType() != typeof(StatisticsHistoryAttribute))
                    .Select(filter => filter.Instance), options => options.WithStrictOrdering());
            selected.Should().NotContain(filter => filter.Instance is StatisticsHistoryAttribute);
            selected.Where(filter => filter.Scope == JobFilterScope.Type)
                .Select(filter => filter.Instance).Should().ContainSingle().Which.Should()
                .BeOfType<AutomaticRetryAttribute>().Which.Attempts.Should().Be(0);

            // The worker/client use this snapshot, not a mutable ambient collection.
            var lateHistory = new StatisticsHistoryAttribute();
            GlobalJobFilters.Filters.Add(lateHistory);
            filters.GetFilters(job).Should().NotContain(filter => filter.Instance is StatisticsHistoryAttribute);
        }
        finally { Restore(original); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Duplicate_or_missing_required_global_filter_refuses_before_activation(bool duplicate)
    {
        var original = GlobalJobFilters.Filters.ToArray();
        try
        {
            if (duplicate) GlobalJobFilters.Filters.Add(new AutomaticRetryAttribute());
            else GlobalJobFilters.Filters.Remove<ContinuationsSupportAttribute>();
            var create = () => HangfireRuntimeFilters.Create();
            create.Should().Throw<HangfireStorageRefusedException>();
        }
        finally { Restore(original); }
    }

    private static void Restore(IEnumerable<JobFilter> original)
    {
        GlobalJobFilters.Filters.Clear();
        foreach (var filter in original) GlobalJobFilters.Filters.Add(filter.Instance, filter.Order);
    }
}
