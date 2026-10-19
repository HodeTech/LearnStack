using System.Data;
using System.Data.Common;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Application.Messaging;
using LearnStack.Application.Persistence;
using LearnStack.Application.Pipeline;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Application.Jobs;

public sealed class BackgroundJobCapacityCleanupTests
{
    [Theory]
    [InlineData(BackgroundJobCapacityCleanupEvidence.TransactionalRollback, "ack")]
    [InlineData(BackgroundJobCapacityCleanupEvidence.NeverStarted, "ack")]
    [InlineData(BackgroundJobCapacityCleanupEvidence.TransactionalRollback, "rollback")]
    [InlineData(BackgroundJobCapacityCleanupEvidence.NeverStarted, "unknown")]
    public async Task Exact_checkpoint_reports_only_its_actual_typed_owner_commit_boundary(
        BackgroundJobCapacityCleanupEvidence evidence, string outcome)
    {
        var recovery = RecoveryFixture.Create();
        var owner = JobApplicationSourceOwner.Create();
        using var binding = owner.Enter();
        var command = new RecordBackgroundJobCapacityCleanupCommand(recovery, evidence, CheckpointGate());
        var catalog = BackgroundJobDefinitionRegistry.CreateEmpty();
        var admission = new BackgroundJobExecutionAdmission(catalog, catalog, new AuditCatalog([new BackgroundJobAuditCatalogSource()]));
        var context = Context(recovery);
        var unit = Substitute.For<IUnitOfWork>();
        var frame = Substitute.For<IUnitOfWorkScope>();
        var transaction = Substitute.For<DbTransaction>();
        var active = false;
        TenantContextAnnouncement? announcement = null;
        unit.HasActiveTransaction.Returns(_ => active);
        unit.Mode.Returns(_ => active ? TransactionMode.ReadWrite : null);
        unit.Transaction.Returns(_ => active ? transaction : null);
        unit.GetTenantContextAnnouncement(Arg.Any<DbTransaction?>()).Returns(_ => announcement);
        frame.IsOwner.Returns(true);
        unit.BeginTransactionAsync(TransactionMode.ReadWrite, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            owner.BindMainUnit(unit);
            active = true;
            return Task.FromResult(frame);
        });
        unit.SetTenantContextAsync(Arg.Any<ITenantContext>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            announcement = new(1, recovery.TenantId, recovery.OrganizationId);
            return Task.CompletedTask;
        });
        frame.CompleteAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            active = false;
            return outcome == "unknown" ? Task.FromException(new InvalidOperationException("acknowledgement lost")) : Task.CompletedTask;
        });
        frame.FailAsync(Arg.Any<CancellationToken>()).Returns(_ => { active = false; return Task.CompletedTask; });
        var producer = new ProducerExecution(unit);
        var coordinator = new TransactionExecutionCoordinator(unit, context, Substitute.For<IAuditStore>(),
            Substitute.For<IAuditStateCapture>(), producer, new DomainEventCollector(producer, Substitute.For<IPublisher>()),
            NullLogger<TransactionExecutionCoordinator>.Instance);
        var tenant = new TenantContextBehavior<RecordBackgroundJobCapacityCleanupCommand, Result<None>>(context);
        var transactionBehavior = new TransactionBehavior<RecordBackgroundJobCapacityCleanupCommand, Result<None>>(coordinator, catalog, []);
        using var authority = admission.EnterCapacityCleanup(command, unit);

        var run = () => tenant.Handle(command, () => transactionBehavior.Handle(command, () =>
        {
            command.RequireOwner(unit);
            var borrowed = () => command.RequireOwner(Substitute.For<IUnitOfWork>());
            borrowed.Should().Throw<InvalidOperationException>();
            BackgroundJobExecutionAdmission.PermitsRequest(typeof(RecordBackgroundJobCapacityCleanupCommand), context)
                .Should().BeFalse("a checkpoint cannot nest another checkpoint");
            if (outcome == "rollback") return Task.FromResult(Result<None>.Fail(BackgroundJobExecutionAdmission.Refused));
            command.RecordCleanup(unit);
            return Task.FromResult(Result<None>.Ok(None.Value));
        }, CancellationToken.None), CancellationToken.None);
        try
        {
            if (outcome == "unknown") await run.Should().ThrowAsync<InvalidOperationException>().WithMessage("acknowledgement lost");
            else (await run()).IsSuccess.Should().Be(outcome == "ack");

            command.CommitStarted.Should().Be(outcome != "rollback");
            command.CommitAcknowledged.Should().Be(outcome == "ack");
            command.KnownRollback.Should().Be(outcome == "rollback");
            command.SourceOwner.Should().BeSameAs(owner);
            await unit.Received(1).BeginTransactionAsync(TransactionMode.ReadWrite, CancellationToken.None);
            await unit.Received(1).SetTenantContextAsync(context, CancellationToken.None);
        }
        finally { owner.ReportMainCleanup(unit, succeeded: true); }
    }

    [Fact]
    public async Task A_checkpoint_cannot_lend_its_authority_to_a_direct_owner_or_another_unit()
    {
        var recovery = RecoveryFixture.Create();
        var owner = JobApplicationSourceOwner.Create();
        using var binding = owner.Enter();
        var command = new RecordBackgroundJobCapacityCleanupCommand(recovery, BackgroundJobCapacityCleanupEvidence.NeverStarted,
            CheckpointGate());
        var catalog = BackgroundJobDefinitionRegistry.CreateEmpty();
        var admission = new BackgroundJobExecutionAdmission(catalog, catalog, new AuditCatalog([new BackgroundJobAuditCatalogSource()]));
        var context = Context(recovery);
        var unit = Substitute.For<IUnitOfWork>();
        using var authority = admission.EnterCapacityCleanup(command, unit);
        BackgroundJobExecutionAdmission.TryEnterRequest(typeof(RecordBackgroundJobCapacityCleanupCommand), context, out var request)
            .Should().BeTrue();
        using (request)
        {
            var producer = new ProducerExecution(unit);
            var coordinator = new TransactionExecutionCoordinator(unit, context, Substitute.For<IAuditStore>(),
                Substitute.For<IAuditStateCapture>(), producer, new DomainEventCollector(producer, Substitute.For<IPublisher>()),
                NullLogger<TransactionExecutionCoordinator>.Instance);
            var run = () => coordinator.ExecuteAsync(null, TransactionMode.ReadWrite, "direct-checkpoint", () =>
                Task.FromResult(Result<None>.Ok(None.Value)));
            await run.Should().ThrowAsync<InvalidOperationException>();
            await unit.DidNotReceiveWithAnyArgs().BeginTransactionAsync(default(TransactionMode), default);
            var foreignContext = Context(RecoveryFixture.Create());
            var retarget = () => BackgroundJobExecutionAdmission.RequireContext(foreignContext);
            retarget.Should().Throw<InvalidOperationException>();
        }
    }

    [Fact]
    public async Task An_unframed_checkpoint_is_refused_before_the_handler_and_cannot_be_created_without_a_source_owner()
    {
        var recovery = RecoveryFixture.Create();
        var create = () => new RecordBackgroundJobCapacityCleanupCommand(recovery, BackgroundJobCapacityCleanupEvidence.NeverStarted,
            CheckpointGate());
        create.Should().Throw<InvalidOperationException>();
        var owner = JobApplicationSourceOwner.Create();
        using var binding = owner.Enter();
        var command = create();
        var constructed = false;
        var behavior = new TenantContextBehavior<RecordBackgroundJobCapacityCleanupCommand, Result<None>>(Context(recovery));
        var result = await behavior.Handle(command, () =>
        {
            constructed = true;
            return Task.FromResult(Result<None>.Ok(None.Value));
        }, CancellationToken.None);
        result.IsFailure.Should().BeTrue();
        constructed.Should().BeFalse();
    }

    private static JobTenantContext Context(BackgroundJobCapacityRecoveryReceipt recovery) => JobTenantContext.FromValidatedJob(
        recovery.TenantId, recovery.OrganizationId, recovery.JobId, null, null, null,
        "00-11111111111111111111111111111111-1111111111111111-01", "tenancy");

    private static BackgroundJobCommitGate CheckpointGate()
    {
        var gate = new BackgroundJobRunGate(new BackgroundJobRunGate.RuntimeStop())
            .OpenBoundary(BackgroundJobCheckpointKind.TransactionalCleanup);
        gate.TryIssue().Should().BeTrue("the explicit synthetic checkpoint has been issued");
        return gate;
    }

    private sealed class RecoveryFixture : BackgroundJobCapacityRecoveryReceipt.MintingSource
    {
        internal static BackgroundJobCapacityRecoveryReceipt Create()
        {
            using var table = new DataTable();
            foreach (var type in new[] { typeof(Guid), typeof(Guid), typeof(Guid), typeof(string), typeof(string), typeof(string),
                         typeof(string), typeof(DateTimeOffset), typeof(Guid), typeof(int), typeof(DateTimeOffset), typeof(DateTimeOffset),
                         typeof(Guid), typeof(long), typeof(Guid), typeof(DateTimeOffset), typeof(DateTimeOffset) })
                table.Columns.Add(new DataColumn { DataType = type });
            var now = new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
            table.Rows.Add(Guid.CreateVersion7(), Guid.CreateVersion7(), DBNull.Value, "tenancy.probe.v1", "transactional", "probe",
                "probe", now, Guid.CreateVersion7(), 1, now, now.AddMinutes(2), Guid.NewGuid(), 1L, Guid.NewGuid(), now.AddMinutes(1), now);
            using var reader = table.CreateDataReader();
            if (!reader.Read()) throw new InvalidOperationException("The synthetic recovery row is missing.");
            return ConfirmCandidate(CaptureCandidate(reader));
        }
    }
}
