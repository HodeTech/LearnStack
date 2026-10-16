using System.Globalization;
using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Jobs;

public sealed class BackgroundJobPhysicalCapacityTests
{
    [Fact]
    public void Default_topology_counts_actual_invocation_routing_owners_and_requires_45_connections()
    {
        var capacity = BackgroundJobPhysicalCapacity.Calculate(BackgroundJobInfrastructureRegistry.CreateDefault());
        capacity.Pools.Should().Be(3);
        capacity.Workers.Should().Be(4);
        capacity.ControlPools.Should().Be(1);
        capacity.InvocationOwners.Should().Be(16);
        capacity.RequiredConnections.Should().Be(45);
    }

    [Fact]
    public void Maximum_valid_control_multiplicity_requires_151_connections()
    {
        var capacity = BackgroundJobPhysicalCapacity.Calculate(Topology(6));
        capacity.Pools.Should().Be(8);
        capacity.Workers.Should().Be(32);
        capacity.ControlPools.Should().Be(6);
        capacity.InvocationOwners.Should().Be(64);
        capacity.RequiredConnections.Should().Be(151);
    }

    [Fact]
    public void Every_control_pool_adds_both_real_sequential_schedulers()
    {
        var one = BackgroundJobPhysicalCapacity.Calculate(Topology(1));
        var six = BackgroundJobPhysicalCapacity.Calculate(Topology(6));
        one.Pools.Should().Be(six.Pools);
        one.Workers.Should().Be(six.Workers);
        six.RequiredConnections.Should().Be(one.RequiredConnections + 10);
    }

    [Fact]
    public void Pooled_configuration_accepts_the_supported_boundary_and_refuses_one_below_it()
    {
        var capacity = BackgroundJobPhysicalCapacity.Calculate(Topology(6));
        var below = () => capacity.Validate(new(true, capacity.RequiredConnections - 1));
        below.Should().Throw<ArgumentException>().WithMessage("The background job source does not meet the supported physical capacity.*");
        var boundary = () => capacity.Validate(new(true, capacity.RequiredConnections));
        boundary.Should().NotThrow();
    }

    [Fact]
    public void Nonpooling_uses_owner_bounds_without_treating_MaxPoolSize_as_its_checkout_cap()
    {
        var capacity = BackgroundJobPhysicalCapacity.Calculate(Topology(6));
        var nonpooling = () => capacity.Validate(new(false, 1));
        nonpooling.Should().NotThrow();
        var invalid = () => capacity.Validate(new(false, 0));
        invalid.Should().Throw<ArgumentException>();
    }

    private static BackgroundJobInfrastructureRegistry Topology(int controls)
    {
        var pools = Enumerable.Range(0, 8).Select(index => new BackgroundJobWorkerPoolRegistration(
            "pool_" + index.ToString(CultureInfo.InvariantCulture), index == 0 ? BackgroundJobWorkerPoolKind.Transactional
                : index == 1 ? BackgroundJobWorkerPoolKind.External
                : index < 2 + controls ? BackgroundJobWorkerPoolKind.Control : BackgroundJobWorkerPoolKind.Transactional,
            4, ["queue_" + index.ToString(CultureInfo.InvariantCulture)]));
        return BackgroundJobInfrastructureRegistry.Create(pools, [], [], []);
    }
}
