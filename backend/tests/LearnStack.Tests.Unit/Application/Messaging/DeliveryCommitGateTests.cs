using System.Diagnostics;
using System.Reflection;
using System.Data;
using System.Data.Common;
using FluentAssertions;
using LearnStack.Application.Messaging;
using LearnStack.Application.Persistence;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.Messaging;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Messaging;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Application.Messaging;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DeliveryCommitGateIsolation
{
    public const string Name = "Delivery commit gate without telemetry";
}

[Collection(DeliveryCommitGateIsolation.Name)]
public sealed class DeliveryCommitGateTests
{
    [Fact]
    public void The_actual_delivery_activity_falls_back_to_W3C_with_no_listener_and_restores_its_caller()
    {
        const string trace = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
        var parent = Activity.Current;
        Activity.Current = null;
        Activity? activity = null;
        try
        {
            using var controlSource = new ActivitySource("learnstack.delivery.no-listener-proof");
            using var control = controlSource.StartActivity("control");
            control.Should().BeNull();
            var actualSource = (ActivitySource)typeof(DeliveryExecutor)
                .GetField("Activities", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            actualSource.HasListeners().Should().BeFalse("this collection runs without any host or telemetry provider");
            activity = (Activity)typeof(DeliveryExecutor)
                .GetMethod("BeginConsumerActivity", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [new IntegrationEventEnvelope(Fact(), trace)])!;

            activity.Should().NotBeNull();
            Activity.Current.Should().BeSameAs(activity);
            activity.IdFormat.Should().Be(ActivityIdFormat.W3C);
            activity.ParentId.Should().Be(trace);
            activity.TraceId.ToHexString().Should().Be("4bf92f3577b34da6a3ce929d0e0e4736");
            activity.Id.Should().MatchRegex("^00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$");
            activity.Dispose();
            Activity.Current.Should().BeNull();
        }
        finally
        {
            activity?.Dispose();
            Activity.Current = parent;
        }
    }

    [Fact]
    public async Task Concurrent_execution_attachment_has_exactly_one_winner_and_closed_gate_never_resets()
    {
        var gate = new DeliveryCommitGate();
        gate.Close();
        var starts = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(gate.TryStartExecution)));
        starts.Count(started => started).Should().Be(1);
        gate.TryStartExecution().Should().BeFalse();
        gate.CommitStarted.Should().BeFalse();
        gate.CommitAcknowledged.Should().BeFalse();
    }

    [Fact]
    public async Task Closing_after_saved_work_refuses_the_actual_owner_commit_and_rolls_back()
    {
        var unit = new RecordingUnit();
        var gate = new DeliveryCommitGate();
        var capture = new AuditStateCapture();
        var producer = new ProducerExecution(unit);
        var coordinator = Coordinator(unit, capture, producer);
        await using var owner = await TransactionExecutionCoordinator.PrepareDeliveryOwnerAsync(unit, Context);
        var participant = new Participant(unit, gate, () => unit.Calls.Add("fence"));
        var run = () => coordinator.ExecutePreparedDeliveryAsync(owner, participant, Guid.CreateVersion7(), () =>
        {
            unit.Calls.Add("saved-business");
            producer.RecordProducedWork();
            gate.Close();
            return Task.FromResult(Result.Ok(None.Value));
        });

        await run.Should().ThrowAsync<OperationCanceledException>();
        unit.Calls.Should().ContainInOrder("saved-business", "audit", "fence", "rollback");
        unit.Calls.Should().NotContain("commit");
        unit.IsRollbackOnly.Should().BeTrue();
        capture.State.Should().Be(AuditIntentState.RolledBack);
        gate.CommitStarted.Should().BeFalse();
        gate.CommitAcknowledged.Should().BeFalse();
        producer.HasInvocation.Should().BeFalse();
    }

    [Fact]
    public async Task Closing_after_admission_cannot_revoke_a_commit_or_its_acknowledgement()
    {
        var unit = new RecordingUnit();
        var gate = new DeliveryCommitGate();
        var capture = new AuditStateCapture();
        unit.OnCommit = () =>
        {
            gate.CommitStarted.Should().BeTrue();
            gate.CommitAcknowledged.Should().BeFalse();
            gate.Close();
        };
        await using var owner = await TransactionExecutionCoordinator.PrepareDeliveryOwnerAsync(unit, Context);
        var result = await Coordinator(unit, capture).ExecutePreparedDeliveryAsync(owner,
            new Participant(unit, gate, () => unit.Calls.Add("fence")), Guid.CreateVersion7(),
            () => Task.FromResult(Result.Ok(None.Value)));

        result.IsSuccess.Should().BeTrue();
        unit.Calls.Should().ContainInOrder("audit", "fence", "commit", "dispose");
        gate.Close();
        gate.CommitAcknowledged.Should().BeTrue();
        capture.State.Should().Be(AuditIntentState.Committed);
    }

    [Fact]
    public async Task A_post_acknowledgement_disposal_fault_retains_definitive_commit_evidence()
    {
        var fault = new IOException("owned frame cleanup failed");
        var unit = new RecordingUnit { DisposeFailure = fault };
        var gate = new DeliveryCommitGate();
        var capture = new AuditStateCapture();
        await using var owner = await TransactionExecutionCoordinator.PrepareDeliveryOwnerAsync(unit, Context);
        var run = () => Coordinator(unit, capture).ExecutePreparedDeliveryAsync(owner,
            new Participant(unit, gate), Guid.CreateVersion7(), () => Task.FromResult(Result.Ok(None.Value)));

        (await run.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(fault);
        gate.CommitAcknowledged.Should().BeTrue();
        capture.State.Should().Be(AuditIntentState.Committed);
        unit.Calls.Should().NotContain("rollback");
    }

    [Fact]
    public async Task An_unacknowledged_commit_fault_is_indeterminate_and_cleanup_cannot_replace_it()
    {
        var fault = new IOException("COMMIT response lost");
        var unit = new RecordingUnit
        {
            CommitFailure = fault,
            DisposeFailure = new IOException("secondary cleanup failure"),
        };
        var gate = new DeliveryCommitGate();
        var capture = new AuditStateCapture();
        await using var owner = await TransactionExecutionCoordinator.PrepareDeliveryOwnerAsync(unit, Context);
        var run = () => Coordinator(unit, capture).ExecutePreparedDeliveryAsync(owner,
            new Participant(unit, gate), Guid.CreateVersion7(), () => Task.FromResult(Result.Ok(None.Value)));

        (await run.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(fault);
        gate.CommitStarted.Should().BeTrue();
        gate.CommitAcknowledged.Should().BeFalse();
        capture.State.Should().Be(AuditIntentState.Indeterminate);
        unit.Calls.Should().NotContain("rollback");
    }

    [Fact]
    public async Task A_completion_participant_cannot_cross_units_even_when_tenant_and_generation_match()
    {
        var unit = new RecordingUnit();
        var other = new RecordingUnit();
        var gate = new DeliveryCommitGate();
        var constructed = false;
        await using (var owner = await TransactionExecutionCoordinator.PrepareDeliveryOwnerAsync(unit, Context))
        {
            var run = () => Coordinator(unit, new AuditStateCapture()).ExecutePreparedDeliveryAsync(owner,
                new Participant(other, gate), Guid.CreateVersion7(), () =>
                {
                    constructed = true;
                    return Task.FromResult(Result.Ok(None.Value));
                });
            await run.Should().ThrowAsync<InvalidOperationException>().WithMessage("*share*unit*");
            unit.HasActiveTransaction.Should().BeTrue("the rejected participant never consumes the owner");
        }
        constructed.Should().BeFalse();
        unit.Calls.Should().Contain("rollback");
        gate.CommitStarted.Should().BeFalse();
    }

    [Fact]
    public async Task Delivery_causation_is_inherited_by_nested_production_and_domain_identity_takes_precedence()
    {
        var unit = new RecordingUnit();
        var producer = new ProducerExecution(unit);
        var coordinator = Coordinator(unit, new AuditStateCapture(), producer);
        var incoming = Guid.CreateVersion7();
        var domain = Guid.CreateVersion7();
        await using var owner = await TransactionExecutionCoordinator.PrepareDeliveryOwnerAsync(unit, Context);
        (await coordinator.ExecutePreparedDeliveryAsync(owner, new Participant(unit, new DeliveryCommitGate()),
            incoming, async () =>
            {
                producer.CreateEnvelope(Fact()).CausationId.Should().Be(incoming);
                await coordinator.ExecuteAsync(null, TransactionMode.ReadWrite, "nested", () =>
                {
                    producer.CreateEnvelope(Fact()).CausationId.Should().Be(incoming);
                    return Task.FromResult(Result.Ok(None.Value));
                });
                using (producer.EnterDomainDispatch(producer.CaptureOrigin(), domain))
                    producer.CreateEnvelope(Fact()).CausationId.Should().Be(domain);
                return Result.Ok(None.Value);
            })).IsSuccess.Should().BeTrue();
        producer.HasInvocation.Should().BeFalse();
    }

    private static readonly DeliveryContext Context = new();

    private static TransactionExecutionCoordinator Coordinator(RecordingUnit unit, AuditStateCapture capture,
        ProducerExecution? producer = null)
    {
        producer ??= new ProducerExecution(unit);
        var store = Substitute.For<IAuditStore>();
        store.WritePendingAsync(unit, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            unit.Calls.Add("audit");
            return Task.CompletedTask;
        });
        return new TransactionExecutionCoordinator(unit, Context, store, capture, producer,
            new DomainEventCollector(producer, Substitute.For<IPublisher>()),
            NullLogger<TransactionExecutionCoordinator>.Instance);
    }

    private static FactEvent Fact() => new()
    {
        TenantId = Context.TenantId.Value,
        EventId = Guid.CreateVersion7(),
        OccurredAt = DateTimeOffset.UtcNow,
    };

    private sealed record FactEvent : IntegrationEventBase
    {
        public override string Topic => "learnstack.proof.delivery";
        public override string PartitionKey => TenantId.ToString();
    }

    private sealed class DeliveryContext : ITenantContext
    {
        public bool IsResolved => true;
        public TenantId TenantId { get; } = TenantId.From(Guid.CreateVersion7());
        public OrganizationId? OrganizationId => null;
        public UserId? UserId => LearnStack.SharedKernel.Identifiers.UserId.SystemActor;
        public string? CorrelationId => null;
        public string? ModuleName => "proof";
    }

    private sealed class Participant(IUnitOfWork unit, DeliveryCommitGate gate, Action? fence = null)
        : DeliveryCompletionParticipant(unit, gate)
    {
        protected override Task FenceAsync(CancellationToken cancellationToken)
        {
            fence?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingUnit : IUnitOfWork
    {
        private int _depth;
        private TenantContextAnnouncement? _announcement;
        public List<string> Calls { get; } = [];
        public Action? OnCommit { get; set; }
        public Exception? CommitFailure { get; init; }
        public Exception? DisposeFailure { get; init; }
        public DbConnection Connection => throw new NotSupportedException();
        public DbTransaction? Transaction { get; private set; }
        public bool HasActiveTransaction => _depth > 0;
        public TransactionMode? Mode { get; private set; }
        public bool IsRollbackOnly { get; private set; }
        public Task<IUnitOfWorkScope> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            BeginTransactionAsync(TransactionMode.ReadWrite, cancellationToken);
        public Task<IUnitOfWorkScope> BeginTransactionAsync(TransactionMode mode,
            CancellationToken cancellationToken = default)
        {
            if (_depth == 0) { Transaction = new TestTransaction(); Mode = mode; }
            return Task.FromResult<IUnitOfWorkScope>(new Frame(this, ++_depth));
        }
        public Task SetTenantContextAsync(ITenantContext context, CancellationToken cancellationToken = default)
        {
            if (_depth == 1) _announcement = new TenantContextAnnouncement(1, context.TenantId, context.OrganizationId);
            return Task.CompletedTask;
        }
        public TenantContextAnnouncement? GetTenantContextAnnouncement(DbTransaction? transaction) =>
            transaction is not null && ReferenceEquals(Transaction, transaction) ? _announcement : null;
        public bool IsTenantContextIssuedOn(DbTransaction? transaction) => GetTenantContextAnnouncement(transaction) is not null;
        public Task SetProvisioningTenantContextAsync(TenantId tenantId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            if (--_depth > 0) return Task.CompletedTask;
            Calls.Add("commit");
            OnCommit?.Invoke();
            Transaction = null;
            Mode = null;
            _announcement = null;
            return CommitFailure is { } fault ? Task.FromException(fault) : Task.CompletedTask;
        }
        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("rollback");
            _depth = 0;
            Transaction = null;
            Mode = null;
            _announcement = null;
            return Task.CompletedTask;
        }
        public void MarkRollbackOnly() => IsRollbackOnly = true;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private sealed class Frame(RecordingUnit unit, int depth) : IUnitOfWorkScope
        {
            private bool _resolved;
            public bool IsOwner => depth == 1;
            public Task CompleteAsync(CancellationToken cancellationToken = default)
            {
                _resolved = true;
                return unit.CommitAsync(cancellationToken);
            }
            public Task FailAsync(CancellationToken cancellationToken = default)
            {
                _resolved = true;
                return unit.RollbackAsync(cancellationToken);
            }
            public async ValueTask DisposeAsync()
            {
                unit.Calls.Add("dispose");
                if (!_resolved) await unit.RollbackAsync();
                if (unit.DisposeFailure is { } fault) throw fault;
            }
        }
    }

    private sealed class TestTransaction : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection? DbConnection => null;
        public override void Commit() => throw new NotSupportedException();
        public override void Rollback() => throw new NotSupportedException();
    }
}
