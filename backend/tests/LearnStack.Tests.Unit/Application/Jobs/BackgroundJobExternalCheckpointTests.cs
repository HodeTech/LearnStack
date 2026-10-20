using System.Data.Common;
using System.Diagnostics;
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

public sealed class BackgroundJobExternalCheckpointTests
{
    [Theory]
    [InlineData(false, "ack")]
    [InlineData(true, "ack")]
    [InlineData(false, "rollback")]
    [InlineData(true, "rollback")]
    [InlineData(false, "unknown")]
    [InlineData(true, "unknown")]
    [InlineData(false, "stopped")]
    [InlineData(true, "stopped")]
    [InlineData(false, "expired")]
    [InlineData(true, "expired")]
    public Task Exact_short_checkpoint_uses_the_actual_owner_ack_and_never_converts_stop_or_lost_ack_to_success(
        bool outcome, string disposition) => outcome
        ? ExecuteAsync<RecordBackgroundJobOutcomeCommand>(disposition)
        : ExecuteAsync<RecordBackgroundJobArmCommand>(disposition);

    private static async Task ExecuteAsync<TRequest>(string disposition) where TRequest : IRequest<Result<None>>
    {
        // Deliberately synthetic receipt for isolated pipeline ownership, not a
        // Prepare/Arm/provider execution or PostgreSQL durability proof.
        var tenant = TenantId.From(Guid.CreateVersion7());
        var receipt = JobAdmissionFixtureReceipt.Create("fixture.checkpoint.v1", tenant, null,
            BackgroundJobExecutionStage.Prepare, capacityGroup: "fixture.resource");
        var gate = receipt.Run.OpenBoundary(typeof(TRequest) == typeof(RecordBackgroundJobArmCommand)
            ? BackgroundJobCheckpointKind.Arm : BackgroundJobCheckpointKind.Outcome);
        gate.TryIssue().Should().BeTrue();
        using var binding = receipt.SourceOwner.Enter();
        object checkpoint = typeof(TRequest) == typeof(RecordBackgroundJobArmCommand)
            ? new RecordBackgroundJobArmCommand(receipt, gate, new string('a', 64), Guid.NewGuid())
            : new RecordBackgroundJobOutcomeCommand(receipt, gate, new string('a', 64), Guid.NewGuid(), "{}",
                BackgroundJobExternalState.Succeeded, null, Guid.NewGuid(), BackgroundJobQuiescenceKind.ProviderFenced);
        var request = (TRequest)checkpoint;
        var registry = BackgroundJobDefinitionRegistry.CreateEmpty();
        var admission = new BackgroundJobExecutionAdmission(registry, registry,
            new AuditCatalog([new BackgroundJobAuditCatalogSource()]));
        var context = JobTenantContext.FromValidatedJob(tenant, null, receipt.Envelope.JobId, null, null, null,
            receipt.Envelope.Traceparent, "fixture");
        var unit = Substitute.For<IUnitOfWork>();
        var frame = Substitute.For<IUnitOfWorkScope>();
        var connection = Substitute.For<DbConnection>();
        var transaction = Substitute.For<DbTransaction>();
        transaction.Connection.Returns(connection);
        var active = false;
        TenantContextAnnouncement? announcement = null;
        unit.HasActiveTransaction.Returns(_ => active);
        unit.Mode.Returns(_ => active ? TransactionMode.ReadWrite : null);
        unit.Connection.Returns(connection);
        unit.Transaction.Returns(_ => active ? transaction : null);
        unit.GetTenantContextAnnouncement(Arg.Any<DbTransaction?>()).Returns(_ => announcement);
        frame.IsOwner.Returns(true);
        unit.BeginTransactionAsync(TransactionMode.ReadWrite, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            receipt.SourceOwner.BindMainUnit(unit);
            active = true;
            return Task.FromResult(frame);
        });
        unit.SetTenantContextAsync(Arg.Any<ITenantContext>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            announcement = new(1, tenant, null);
            return Task.CompletedTask;
        });
        frame.CompleteAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            active = false;
            return disposition == "unknown" ? Task.FromException(new InvalidOperationException("checkpoint acknowledgement lost"))
                : Task.CompletedTask;
        });
        frame.FailAsync(Arg.Any<CancellationToken>()).Returns(_ => { active = false; return Task.CompletedTask; });
        var producer = new ProducerExecution(unit);
        var coordinator = new TransactionExecutionCoordinator(unit, context, Substitute.For<IAuditStore>(),
            Substitute.For<IAuditStateCapture>(), producer, new DomainEventCollector(producer, Substitute.For<IPublisher>()),
            NullLogger<TransactionExecutionCoordinator>.Instance);
        var tenantBehavior = new TenantContextBehavior<TRequest, Result<None>>(context);
        var transactionBehavior = new TransactionBehavior<TRequest, Result<None>>(coordinator, registry, new ExplicitReadOnlyRequestCatalog([]));
        using var authority = checkpoint switch
        {
            RecordBackgroundJobArmCommand arm => admission.EnterExternalCheckpoint(arm, unit),
            RecordBackgroundJobOutcomeCommand observation => admission.EnterExternalCheckpoint(observation, unit),
            _ => throw new InvalidOperationException("Unexpected synthetic checkpoint."),
        };
        var run = () => tenantBehavior.Handle(request, () => transactionBehavior.Handle(request, async () =>
        {
            switch (checkpoint)
            {
                case RecordBackgroundJobArmCommand arm: arm.RequireOwner(unit); break;
                case RecordBackgroundJobOutcomeCommand observation: observation.RequireOwner(unit); break;
            }
            BackgroundJobExecutionAdmission.PermitsRequest(typeof(ReadBackgroundJobExternalStateQuery), context)
                .Should().BeFalse("no nested request may borrow this checkpoint's owner");
            var directOwner = () => coordinator.ExecuteAsync(null, TransactionMode.ReadWrite, "borrowed-checkpoint",
                () => Task.FromResult(Result<None>.Ok(None.Value)));
            await directOwner.Should().ThrowAsync<InvalidOperationException>();
            if (disposition == "rollback") return Result<None>.Fail(BackgroundJobExecutionAdmission.Refused);
            switch (checkpoint)
            {
                case RecordBackgroundJobArmCommand arm:
                    arm.RecordCheckpoint(unit,
                    Stopwatch.GetTimestamp() - (disposition == "expired" ? 2 * Stopwatch.Frequency : 0), TimeSpan.FromSeconds(1)); break;
                case RecordBackgroundJobOutcomeCommand observation:
                    observation.RecordCheckpoint(unit,
                    Stopwatch.GetTimestamp() - (disposition == "expired" ? 2 * Stopwatch.Frequency : 0), TimeSpan.FromSeconds(1)); break;
            }
            if (disposition == "stopped") receipt.Run.Close();
            return Result<None>.Ok(None.Value);
        }, CancellationToken.None), CancellationToken.None);
        try
        {
            if (disposition is "unknown" or "stopped") await run.Should().ThrowAsync<InvalidOperationException>();
            else if (disposition == "expired") await run.Should().ThrowAsync<OperationCanceledException>();
            else (await run()).IsSuccess.Should().Be(disposition == "ack");
            gate.CommitAcknowledged.Should().Be(disposition == "ack");
            gate.CommitStarted.Should().Be(disposition is "ack" or "unknown");
            gate.KnownRollback.Should().Be(disposition is "rollback" or "stopped" or "expired");
            await frame.Received(disposition is "ack" or "unknown" ? 1 : 0).CompleteAsync(CancellationToken.None);
            await unit.Received(1).BeginTransactionAsync(TransactionMode.ReadWrite, CancellationToken.None);
        }
        finally { receipt.SourceOwner.ReportMainCleanup(unit, succeeded: true); }
    }

    [Fact]
    public void A_foreign_run_or_transactional_receipt_cannot_construct_an_external_checkpoint()
    {
        var tenant = TenantId.From(Guid.CreateVersion7());
        var external = JobAdmissionFixtureReceipt.Create("fixture.checkpoint.v1", tenant, null, BackgroundJobExecutionStage.Prepare);
        var transactional = JobAdmissionFixtureReceipt.Create("fixture.checkpoint.v1", tenant, null, BackgroundJobExecutionStage.Transactional);
        var foreignGate = transactional.Run.OpenBoundary(BackgroundJobCheckpointKind.Arm);
        Action foreign = () => _ = new RecordBackgroundJobArmCommand(external, foreignGate, new string('a', 64), Guid.NewGuid());
        foreign.Should().Throw<InvalidOperationException>();
        Action wrongProfile = () => _ = new ReadBackgroundJobExternalStateQuery(transactional);
        wrongProfile.Should().Throw<InvalidOperationException>();
    }
}
