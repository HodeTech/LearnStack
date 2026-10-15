using FluentAssertions;
using LearnStack.Application.Messaging;
using LearnStack.Application.Persistence;
using LearnStack.Infrastructure.Messaging;
using LearnStack.SharedKernel.Messaging;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Application.Jobs;

public sealed class BackgroundJobDirectEntryTests
{
    [Fact]
    public async Task Alternate_event_entries_refuse_before_scope_creation_serialization_or_gate_consumption()
    {
        var fixture = new BackgroundJobExecutionAdmissionTests.Fixture();
        var scopes = Substitute.For<IServiceScopeFactory>();
        var accessor = new StaticTenantContextAccessor(fixture.Context);
        var partitions = Substitute.For<IPartitionSerializer>();
        // Metadata is deliberately absent: admission must refuse before any entry
        // can inspect a claim/envelope or resolve transport/module services.
        var executor = new DeliveryExecutor(scopes, accessor, partitions, null!, null!);
        var bus = new InProcessEventBus(scopes, accessor, partitions, null!);
        var gate = new DeliveryCommitGate();
        var control = new DeliveryExecutionControl();
        using var admission = fixture.Enter();

        var execute = async () => await executor.ExecuteAsync(null!, gate);
        var controlled = async () => await executor.ExecuteControlledAsync(null!, control);
        var publish = async () => await bus.PublishAsync(null!);
        await execute.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Ordinary background-job*");
        await controlled.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Ordinary background-job*");
        await publish.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Ordinary background-job*");
        gate.CommitStarted.Should().BeFalse();
        accessor.Current.Should().BeSameAs(fixture.Context);
        scopes.DidNotReceive().CreateScope();
    }

    [Fact]
    public async Task Prepared_delivery_entry_refuses_before_opening_a_second_owner()
    {
        var fixture = new BackgroundJobExecutionAdmissionTests.Fixture();
        var unit = Substitute.For<IUnitOfWork>();
        using var admission = fixture.Enter();
        var prepare = async () => await TransactionExecutionCoordinator.PrepareDeliveryOwnerAsync(unit, fixture.Context);
        await prepare.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Ordinary background-job*");
        await unit.DidNotReceiveWithAnyArgs().BeginTransactionAsync(default(TransactionMode), default);
    }

    [Fact]
    public async Task Direct_provisioning_coordinator_refuses_before_begin_or_deferred_construction()
    {
        var fixture = new BackgroundJobExecutionAdmissionTests.Fixture();
        var unit = Substitute.For<IUnitOfWork>();
        var producer = new ProducerExecution(unit);
        var coordinator = new TransactionExecutionCoordinator(unit, fixture.Context,
            Substitute.For<IAuditStore>(), Substitute.For<IAuditStateCapture>(), producer,
            new DomainEventCollector(producer, Substitute.For<IPublisher>()),
            NullLogger<TransactionExecutionCoordinator>.Instance);
        var constructed = false;
        using var admission = fixture.Enter();
        var execute = async () => await coordinator.ExecuteAsync(new BackgroundJobExecutionAdmissionTests.Provisioning(),
            TransactionMode.ReadWrite, "provisioning-probe", () =>
            {
                constructed = true;
                return Task.FromResult(Result.Ok("constructed"));
            });
        await execute.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Ordinary background-job*");
        await unit.DidNotReceiveWithAnyArgs().BeginTransactionAsync(default(TransactionMode), default);
        constructed.Should().BeFalse();
    }
}
