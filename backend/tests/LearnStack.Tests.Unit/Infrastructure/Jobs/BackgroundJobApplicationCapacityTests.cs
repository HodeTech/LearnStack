using System.Globalization;
using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Jobs;

public sealed class BackgroundJobApplicationCapacityTests
{
    [Fact]
    public void Default_transactional_pool_requires_ten_job_origin_checkouts()
    {
        var capacity = BackgroundJobApplicationCapacity.Calculate(BackgroundJobInfrastructureRegistry.CreateDefault());
        capacity.InvocationOwners.Should().Be(5);
        capacity.RequiredConnections.Should().Be(10);
    }

    [Fact]
    public void Maximum_transactional_topology_requires_108_job_origin_checkouts()
    {
        var topology = MaximumTransactionalTopology();
        topology.Pools.Should().HaveCount(8);
        topology.Pools.Values.Sum(pool => pool.Workers).Should().Be(32);
        var capacity = BackgroundJobApplicationCapacity.Calculate(topology);
        capacity.InvocationOwners.Should().Be(54);
        capacity.RequiredConnections.Should().Be(108);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pooled_boundary_is_accepted_and_one_below_is_refused(bool maximum)
    {
        var capacity = BackgroundJobApplicationCapacity.Calculate(maximum
            ? MaximumTransactionalTopology() : BackgroundJobInfrastructureRegistry.CreateDefault());
        var boundary = () => capacity.Validate(new(true, capacity.RequiredConnections));
        boundary.Should().NotThrow();
        var below = () => capacity.Validate(new(true, capacity.RequiredConnections - 1));
        below.Should().Throw<ArgumentException>().WithMessage("*job-only physical capacity*");
    }

    [Fact]
    public void Nonpooling_preserves_owner_bounds_without_using_MaxPoolSize_as_a_checkout_cap()
    {
        var capacity = BackgroundJobApplicationCapacity.Calculate(MaximumTransactionalTopology());
        var nonpooling = () => capacity.Validate(new(false, 1));
        nonpooling.Should().NotThrow();
        var invalid = () => capacity.Validate(new(false, 0));
        invalid.Should().Throw<ArgumentException>();
    }

    private static BackgroundJobInfrastructureRegistry MaximumTransactionalTopology()
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
