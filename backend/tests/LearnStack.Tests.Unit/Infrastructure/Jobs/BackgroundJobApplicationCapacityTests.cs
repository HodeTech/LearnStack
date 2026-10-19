using System.Globalization;
using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Jobs;

public sealed class BackgroundJobApplicationCapacityTests
{
    [Fact]
    public void Default_business_pools_terminal_and_recipient_cleanup_require_thirty_two_job_origin_checkouts()
    {
        var capacity = BackgroundJobApplicationCapacity.Calculate(BackgroundJobInfrastructureRegistry.CreateDefault());
        capacity.InvocationOwners.Should().Be(11);
        capacity.RecipientOwners.Should().Be(4);
        capacity.RequiredConnections.Should().Be(32);
    }

    [Fact]
    public void Maximum_business_topology_terminal_and_recipient_cleanup_require_128_job_origin_checkouts()
    {
        var topology = MaximumBusinessTopology();
        topology.Pools.Should().HaveCount(8);
        topology.Pools.Values.Sum(pool => pool.Workers).Should().Be(32);
        var capacity = BackgroundJobApplicationCapacity.Calculate(topology);
        capacity.InvocationOwners.Should().Be(59);
        capacity.RequiredConnections.Should().Be(128);
    }

    [Fact]
    public void Both_business_kinds_contribute_equally_and_control_pools_add_no_extra_terminal_owner()
    {
        var pools = BackgroundJobInfrastructureRegistry.CreateDefault().Pools.Values.ToArray();
        var original = BackgroundJobApplicationCapacity.Calculate(BackgroundJobInfrastructureRegistry.CreateDefault());
        var changed = pools.Select(pool => new BackgroundJobWorkerPoolRegistration(pool.Name,
            pool.Kind == BackgroundJobWorkerPoolKind.Transactional ? BackgroundJobWorkerPoolKind.External
                : pool.Kind == BackgroundJobWorkerPoolKind.External ? BackgroundJobWorkerPoolKind.Transactional : pool.Kind,
            pool.Workers + (pool.Kind == BackgroundJobWorkerPoolKind.Control ? 1 : 0), pool.Queues));
        var capacity = BackgroundJobApplicationCapacity.Calculate(
            BackgroundJobInfrastructureRegistry.Create(changed, [], [], []));
        capacity.Should().Be(original);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pooled_boundary_is_accepted_and_one_below_is_refused(bool maximum)
    {
        var capacity = BackgroundJobApplicationCapacity.Calculate(maximum
            ? MaximumBusinessTopology() : BackgroundJobInfrastructureRegistry.CreateDefault());
        var boundary = () => capacity.Validate(new(true, capacity.RequiredConnections));
        boundary.Should().NotThrow();
        var below = () => capacity.Validate(new(true, capacity.RequiredConnections - 1));
        below.Should().Throw<ArgumentException>().WithMessage("*job-only physical capacity*");
    }

    [Fact]
    public void Nonpooling_preserves_owner_bounds_without_using_MaxPoolSize_as_a_checkout_cap()
    {
        var capacity = BackgroundJobApplicationCapacity.Calculate(MaximumBusinessTopology());
        var nonpooling = () => capacity.Validate(new(false, 1));
        nonpooling.Should().NotThrow();
        var invalid = () => capacity.Validate(new(false, 0));
        invalid.Should().Throw<ArgumentException>();
    }

    private static BackgroundJobInfrastructureRegistry MaximumBusinessTopology()
    {
        var workers = new[] { 16, 10, 1, 1, 1, 1 };
        var pools = workers.Select((count, index) => new BackgroundJobWorkerPoolRegistration(
            "transactional_" + index.ToString(CultureInfo.InvariantCulture), BackgroundJobWorkerPoolKind.Transactional,
            count, ["transactional_" + index.ToString(CultureInfo.InvariantCulture)])).Concat(
            [new("external", BackgroundJobWorkerPoolKind.External, 1, ["external"]),
                new("control", BackgroundJobWorkerPoolKind.Control, 1, ["control"])]);
        return BackgroundJobInfrastructureRegistry.Create(pools, [], [], []);
    }
}
