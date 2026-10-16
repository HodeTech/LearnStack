using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.PostgreSql;
using Hangfire.States;
using Hangfire.Storage;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Persistence;
using NSubstitute;
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
            var retainedGlobals = original.Where(filter => filter.Instance is not StatisticsHistoryAttribute).ToArray();
            retainedGlobals.Should().HaveCount(3);
            var originalRetry = retainedGlobals.Select(filter => filter.Instance).OfType<AutomaticRetryAttribute>().Single();
            var originalAttempts = originalRetry.Attempts;
            var originalOrder = originalRetry.Order;
            var filters = HangfireRuntimeFilters.Create();
            GlobalJobFilters.Filters.Should().Equal(retainedGlobals);
            originalRetry.Attempts.Should().Be(originalAttempts);
            originalRetry.Order.Should().Be(originalOrder);

            var jobs = new[] { WrapperJob(), WrapperJob(recurrence: true) };
            var selected = jobs.Select(job => filters.GetFilters(job).ToArray()).ToArray();
            foreach (var effective in selected)
            {
                effective.Where(filter => filter.Scope == JobFilterScope.Global).Should().Equal(
                    retainedGlobals.Where(filter => filter.Instance is not AutomaticRetryAttribute));
                effective.Should().NotContain(filter => filter.Instance is StatisticsHistoryAttribute);
                var effectiveRetry = effective.Should().ContainSingle(filter => filter.Instance is AutomaticRetryAttribute).Which;
                effectiveRetry.Scope.Should().Be(JobFilterScope.Type);
                ((AutomaticRetryAttribute)effectiveRetry.Instance).Attempts.Should().Be(0);
                effectiveRetry.Instance.Should().NotBeSameAs(originalRetry);
            }

            // The worker/client use this snapshot, not a mutable ambient collection.
            GlobalJobFilters.Filters.Add(new StatisticsHistoryAttribute());
            GlobalJobFilters.Filters.Add(new AutomaticRetryAttribute { Attempts = 7, Order = 100 });
            for (var index = 0; index < jobs.Length; index++)
                filters.GetFilters(jobs[index]).Select(filter => filter.Instance)
                    .Should().Equal(selected[index].Select(filter => filter.Instance));
            originalRetry.Attempts.Should().Be(originalAttempts);
            originalRetry.Order.Should().Be(originalOrder);
        }
        finally { Restore(original); }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void Wrapper_failed_state_avoids_vendor_retries_only_with_stock_filter_normalization(bool normalized, bool recurrence)
    {
        var original = GlobalJobFilters.Filters.ToArray();
        try
        {
            IJobFilterProvider filters = normalized
                ? HangfireRuntimeFilters.Create()
                : new RawFilterProvider(original.Where(filter => filter.Instance is not StatisticsHistoryAttribute).ToArray());
            var storage = Substitute.For<JobStorage>();
            storage.GetStateHandlers().Returns(Array.Empty<IStateHandler>());
            var connection = Substitute.For<IStorageConnection>();
            connection.GetJobParameter("17", "RetryCount").Returns("0");
            var transaction = Substitute.For<IWriteOnlyTransaction>();
            var failed = new FailedState(new InvalidOperationException("Fixture wrapper failure"));
            var context = new ApplyStateContext(storage, connection, transaction,
                new BackgroundJob("17", WrapperJob(recurrence), DateTime.UtcNow), failed, ProcessingState.StateName);

            var elected = new StateMachine(filters).ApplyState(context);

            if (normalized)
            {
                elected.Should().BeSameAs(failed);
                connection.DidNotReceiveWithAnyArgs().SetJobParameter(default!, default!, default!);
                transaction.Received(1).SetJobState("17", failed);
                transaction.DidNotReceive().AddToSet("retries", "17");
                transaction.DidNotReceive().AddToSet("schedule", "17", Arg.Any<double>());
            }
            else
            {
                elected.Should().BeOfType<ScheduledState>();
                connection.Received(1).SetJobParameter("17", "RetryCount", "1");
                transaction.Received(1).SetJobState("17", elected);
                transaction.Received().AddToSet("retries", "17");
                transaction.Received(1).AddToSet("schedule", "17", Arg.Any<double>());
            }
        }
        finally { Restore(original); }
    }

    [Fact]
    public void A_reordered_global_retry_refuses_before_history_or_activation_changes()
    {
        var original = GlobalJobFilters.Filters.ToArray();
        try
        {
            var retry = original.Single(filter => filter.Instance is AutomaticRetryAttribute).Instance;
            GlobalJobFilters.Filters.Remove<AutomaticRetryAttribute>();
            GlobalJobFilters.Filters.Add(retry, 100);
            var dirty = GlobalJobFilters.Filters.ToArray();
            var stock = new JobFilterProviderCollection(new RawFilterProvider(dirty));
            foreach (var recurrence in new[] { false, true })
                stock.GetFilters(WrapperJob(recurrence)).Should()
                    .ContainSingle(filter => filter.Instance is AutomaticRetryAttribute).Which.Instance.Should().BeSameAs(retry);

            var create = () => HangfireRuntimeFilters.Create();
            create.Should().Throw<HangfireStorageRefusedException>();
            GlobalJobFilters.Filters.Should().Equal(dirty);
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

    private static Job WrapperJob(bool recurrence = false) => recurrence
        ? Job.FromExpression<HangfireRecurrenceTick>(tick => tick.RunAsync("fixture.tick", 1))
        : Job.FromExpression<HangfireJobWakeup>(wakeup => wakeup.RunAsync(
            LearnStack.SharedKernel.Jobs.BackgroundJobId.From(Guid.Parse("11111111-1111-7111-8111-111111111111")), 1));

    private sealed class RawFilterProvider(JobFilter[] globals) : IJobFilterProvider
    {
        public IEnumerable<JobFilter> GetFilters(Job job) => globals.Concat(new JobFilterAttributeFilterProvider().GetFilters(job));
    }
}
