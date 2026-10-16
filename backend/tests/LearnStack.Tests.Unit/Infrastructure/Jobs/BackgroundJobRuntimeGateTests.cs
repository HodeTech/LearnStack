using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Persistence;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Jobs;

public sealed class BackgroundJobRuntimeGateTests
{
    [Fact]
    public void Host_stop_closes_all_later_start_admissions_and_cannot_reopen()
    {
        var gate = new BackgroundJobRuntimeGate(BackgroundJobInfrastructureRegistry.CreateDefault());
        gate.TryAdmit().Should().BeTrue();
        gate.StopStarts();
        gate.IsOpen.Should().BeFalse();
        gate.TryAdmit().Should().BeFalse();
        gate.StopStarts();
        gate.State.Should().Be(BackgroundJobProcessState.Stopping);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Final_teardown_does_not_become_a_normal_host_stop(bool hostFirst)
    {
        var gate = new BackgroundJobRuntimeGate(BackgroundJobInfrastructureRegistry.CreateDefault());
        if (hostFirst) gate.StopStarts();
        gate.BeginFinalExit();
        gate.StopStarts();
        gate.BeginFinalExit();
        gate.State.Should().Be(BackgroundJobProcessState.FinalExit);
        gate.TryAdmit().Should().BeFalse();
    }

    [Fact]
    public void A_pool_boundary_uses_the_actual_frozen_registration_and_is_stable()
    {
        var topology = BackgroundJobInfrastructureRegistry.CreateDefault();
        var gate = new BackgroundJobRuntimeGate(topology);
        var pool = topology.Pools["transactional"];
        gate.ForPool(pool).Should().BeSameAs(gate.ForPool(pool));
        var impostor = new BackgroundJobWorkerPoolRegistration(pool.Name, pool.Kind, pool.Workers, pool.Queues);
        var resolve = () => gate.ForPool(impostor);
        resolve.Should().Throw<InvalidOperationException>();
        gate.State.Should().Be(BackgroundJobProcessState.Running);
    }

    [Fact]
    public async Task Concurrent_stop_and_final_exit_leave_no_new_start_authority()
    {
        var gate = new BackgroundJobRuntimeGate(BackgroundJobInfrastructureRegistry.CreateDefault());
        await Task.WhenAll(Enumerable.Range(0, 64).Select(index => Task.Run(() =>
        {
            if (index % 2 == 0) gate.StopStarts();
            else gate.BeginFinalExit();
        })));
        gate.State.Should().Be(BackgroundJobProcessState.FinalExit);
        Enumerable.Range(0, 100).Should().OnlyContain(_ => !gate.TryAdmit());
    }

    [Fact]
    public void Normal_host_drain_keeps_only_storage_cleanup_admission_open()
    {
        var gate = new BackgroundJobRuntimeGate(BackgroundJobInfrastructureRegistry.CreateDefault());
        gate.TryAdmitStorage().Should().BeTrue();
        gate.StopStarts();
        gate.TryAdmit().Should().BeFalse();
        gate.TryAdmitStorage().Should().BeTrue();
        gate.BeginFinalExit();
        gate.TryAdmitStorage().Should().BeFalse();
    }

    [Fact]
    public void Final_teardown_refuses_actual_vendor_acquisition_before_resolving_a_source()
    {
        var gate = new BackgroundJobRuntimeGate(BackgroundJobInfrastructureRegistry.CreateDefault());
        using var source = new PrivilegedDataSource(() => throw new InvalidOperationException("Unreachable source factory."));
        var factory = new HangfireConnectionFactory(source, gate);
        gate.BeginFinalExit();
        var acquire = () => factory.GetOrCreateConnection();
        acquire.Should().Throw<InvalidOperationException>().WithMessage("The job storage acquisition gate is closed.");
        source.IsValueCreated.Should().BeFalse();
    }
}
