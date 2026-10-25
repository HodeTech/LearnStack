using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using LearnStack.Application.Audit;
using LearnStack.Infrastructure.Audit;
using FluentAssertions;
using LearnStack.Application.Messaging;
using LearnStack.Application.Persistence;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Customization.Domain;
using LearnStack.Modules.Customization.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Messaging;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class DeliveryExecutionTests(SchemaFixture schema)
{
    private const string Subscription = "proof.execute.v1";
    private const string PendingSubscription = "proof.future.v1";
    private const string SensitiveCanary = "private_delivery_canary_84e315";
    private static readonly Guid PayloadCanary = new("b90387b1-c760-4c58-a276-8a7fa9a2f98a");
    private static readonly Guid CausalActor = new("019bf000-7d6c-7000-8000-000000000057");
    private const string Operation = "customization.content_type.delivery_probe";
    private const string Trace = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";

    /// <summary>
    /// <see href="../../../../docs/decisions/0046-durable-event-delivery-and-recovery.md">ADR-0046</see>
    /// restores resolved tenant/organization context and causal identity before handler
    /// construction, then commits business, inbox, outbox, audit and ledger together.
    /// </summary>
    [Fact]
    public async Task Integration_Event_Handler_Restores_Tenant_Context()
    {
        await using var fixture = await Fixture.CreateAsync(schema);
        CausalActor.Should().NotBe(UserId.SystemActor.Value);
        var fact = Fact();
        var claim = await fixture.AcceptAndClaimAsync(fact);
        using var cancellation = new CancellationTokenSource();
        fixture.Observation.ExpectedToken = cancellation.Token;
        fixture.Observation.ObserveBareUnit = true;
        var previous = new ProbeContext(SchemaFixture.TenantB, SchemaFixture.OrgA2);
        fixture.Accessor.Current = previous;
        var ambientActivity = Activity.Current;
        Activity.Current = null;
        try
        {
            var gate = new DeliveryCommitGate();
            var clearsBefore = fixture.Observation.AuditClearCount;
            var result = await fixture.Executor.ExecuteAsync(claim, gate, cancellation.Token);

            result.Should().Be(new DeliveryExecutionResult(DeliveryExecutionOutcome.Completed, true, false));
            (fixture.Observation.AuditClearCount - clearsBefore).Should().Be(1);
            gate.CommitAcknowledged.Should().BeTrue();
            fixture.Observation.Constructions.Should().Be(1);
            fixture.Observation.Executions.Should().Be(1);
            fixture.Observation.ObservedSavedWork.Should().BeTrue();
            fixture.Observation.InboxObservedAtConstruction.Should().BeTrue();
            fixture.Observation.BareUnitObserved.Should().BeTrue();
            fixture.Accessor.Current.Should().BeSameAs(previous);
            Activity.Current.Should().BeNull();
            var state = await fixture.StateAsync(fact);
            state.Business.Should().Be(1);
            state.Inbox.Should().Be(1);
            state.Outbox.Should().Be(1);
            state.SuccessAudit.Should().Be(1);
            state.Processed.Should().BeTrue();
            state.OutboxCausation.Should().Be(fact.EventId);
            state.OutboxActor.Should().Be(CausalActor);
            fixture.Observation.OutgoingAnnouncement.Should().NotBeNull();
            var announcement = fixture.Observation.OutgoingAnnouncement!;
            announcement.TenantId.Value.Should().Be(fact.TenantId);
            announcement.OrganizationId!.Value.Value.Should().Be(SchemaFixture.OrgA1);
            state.OutboxTenant.Should().Be(announcement.TenantId.Value);
            state.OutboxOrganization.Should().Be(announcement.OrganizationId?.Value);
            state.OutboxPartition.Should().NotBeNullOrEmpty().And.Be(fact.PartitionKey);
            state.OutboxCorrelation.Should().Be(fixture.Observation.OutgoingActivity);
            ActivityContext.TryParse(state.OutboxCorrelation, null, out var persisted).Should().BeTrue();
            ActivityContext.TryParse(fixture.Observation.OutgoingActivity, null, out var enqueuing).Should().BeTrue();
            persisted.TraceId.Should().Be(enqueuing.TraceId);
            state.AuditActor.Should().Be(UserId.SystemActor.Value);
            state.AuditCorrelation.Should().Be(Trace);
            state.AuditOrganization.Should().Be(SchemaFixture.OrgA1);
            state.AuditCausation.Should().Be(fact.EventId);
            state.AuditCausalActor.Should().Be(CausalActor);
            state.LockedBy.Should().Be(claim.Token, "application completion cannot rewrite scheduler provenance");
        }
        finally
        {
            fixture.Accessor.Current = null;
            Activity.Current = ambientActivity;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Durable_acceptance_does_not_wait_for_same_partition_handler_or_acknowledged_cleanup(bool holdCleanup)
    {
        await using var fixture = await Fixture.CreateAsync(schema);
        var first = Fact();
        var claim = await fixture.AcceptAndClaimAsync(first);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (holdCleanup) fixture.Observation.CleanupMode = "hang";
        else fixture.Observation.AfterSave = async () =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        var gate = new DeliveryCommitGate();
        var execution = fixture.Executor.ExecuteAsync(claim, gate);
        Task<DeliveryClaim>? accepting = null;
        try
        {
            await (holdCleanup ? fixture.Observation.CleanupEntered.Task : entered.Task)
                .WaitAsync(TimeSpan.FromSeconds(10));
            gate.CommitAcknowledged.Should().Be(holdCleanup);
            var next = first with { EventId = Guid.CreateVersion7() };
            next.PartitionKey.Should().Be(first.PartitionKey);
            accepting = fixture.AcceptAndClaimAsync(next);
            var accepted = await accepting.WaitAsync(TimeSpan.FromSeconds(5));

            accepted.EventId.Should().Be(next.EventId, "the real bus durably accepted another event while execution was held");
            execution.IsCompleted.Should().BeFalse();
            fixture.Observation.Constructions.Should().Be(1, "acceptance constructs no second handler");
            gate.CommitAcknowledged.Should().Be(holdCleanup);
        }
        finally
        {
            release.TrySetResult();
            fixture.Observation.CleanupRelease.TrySetResult();
            await execution.WaitAsync(TimeSpan.FromSeconds(10));
            if (accepting is not null) await accepting.WaitAsync(TimeSpan.FromSeconds(10));
        }
        (await fixture.StateAsync(first)).Processed.Should().BeTrue();
    }

    [Theory]
    [InlineData("handler")]
    [InlineData("constructor")]
    [InlineData("null-task")]
    [InlineData("foreign-cancel")]
    public async Task Faults_roll_back_actual_attempted_work_and_a_fresh_delivery_scope_remains_usable(string fault)
    {
        await using var fixture = await Fixture.CreateAsync(schema);
        var fact = Fact();
        var claim = await fixture.AcceptAndClaimAsync(fact);
        fixture.Observation.Fault = fault;
        var previous = new ProbeContext(SchemaFixture.TenantB, null);
        fixture.Accessor.Current = previous;
        var clearsBefore = fixture.Observation.AuditClearCount;
        var result = await fixture.Executor.ExecuteAsync(claim, new DeliveryCommitGate());

        result.CommitAcknowledged.Should().BeFalse();
        result.Outcome.Should().Be(DeliveryExecutionOutcome.Failed);
        (fixture.Observation.AuditClearCount - clearsBefore).Should().Be(1);
        fixture.Accessor.Current.Should().BeSameAs(previous);
        fixture.Logs.Text.Should().NotContain(SensitiveCanary).And.NotContain(PayloadCanary.ToString()).And.NotContain(typeof(Handler).FullName!);
        (await fixture.StateAsync(fact)).Should().Match<DurableState>(state =>
            state.Business == 0 && state.Inbox == 0 && state.Outbox == 0
            && state.SuccessAudit == 0 && !state.Processed);
        if (fault is "handler" or "foreign-cancel") fixture.Observation.ObservedSavedWork.Should().BeTrue();
        var failedOrigin = await fixture.FailedAuditOriginAsync();
        failedOrigin.Actor.Should().Be(UserId.SystemActor.Value);
        failedOrigin.Causation.Should().Be(fact.EventId);
        failedOrigin.CausalActor.Should().Be(CausalActor);

        fixture.Observation.Fault = null;
        var retry = await fixture.Executor.ExecuteAsync(claim, new DeliveryCommitGate());
        retry.Outcome.Should().Be(DeliveryExecutionOutcome.Completed);
        retry.CommitAcknowledged.Should().BeTrue();
        (await fixture.StateAsync(fact)).Business.Should().Be(1);
        fixture.Accessor.Current.Should().BeSameAs(previous);
        fixture.Accessor.Current = null;
    }

    [Fact]
    public async Task A_durable_inbox_duplicate_completes_the_claim_without_constructing_or_classifying_the_handler()
    {
        await using var fixture = await Fixture.CreateAsync(schema);
        var fact = Fact();
        var claim = await fixture.AcceptAndClaimAsync(fact);
        await fixture.InsertInboxAsync(fact);
        fixture.Observation.Fault = "constructor";
        var result = await fixture.Executor.ExecuteAsync(claim, new DeliveryCommitGate());

        result.Should().Be(new DeliveryExecutionResult(DeliveryExecutionOutcome.Duplicate, true, false));
        fixture.Observation.Constructions.Should().Be(0);
        var state = await fixture.StateAsync(fact);
        state.Inbox.Should().Be(1);
        state.Processed.Should().BeTrue();
        state.Business.Should().Be(0);
        state.Outbox.Should().Be(0);
        state.SuccessAudit.Should().Be(0);
    }

    [Theory]
    [InlineData("token")]
    [InlineData("expired")]
    [InlineData("gate")]
    public async Task Final_ownership_loss_rolls_back_saved_business_inbox_outbox_and_success_audit(string loss)
    {
        await using var fixture = await Fixture.CreateAsync(schema);
        var fact = Fact();
        var claim = await fixture.AcceptAndClaimAsync(fact);
        var gate = new DeliveryCommitGate();
        fixture.Observation.AfterSave = async () =>
        {
            if (loss == "gate") gate.Close();
            else await fixture.ChangeLeaseAsync(fact.EventId, loss);
        };
        var result = await fixture.Executor.ExecuteAsync(claim, gate);

        fixture.Observation.ObservedSavedWork.Should().BeTrue("ownership must be lost after an actual saved write");
        result.CommitAcknowledged.Should().BeFalse();
        gate.CommitStarted.Should().BeFalse();
        var state = await fixture.StateAsync(fact);
        state.Business.Should().Be(0);
        state.Inbox.Should().Be(0);
        state.Outbox.Should().Be(0);
        state.SuccessAudit.Should().Be(0);
        state.Processed.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closing_the_gate_while_a_callback_ignores_cancellation_prevents_a_late_commit(bool cancelToken)
    {
        await using var fixture = await Fixture.CreateAsync(schema);
        var fact = Fact();
        var claim = await fixture.AcceptAndClaimAsync(fact);
        var gate = new DeliveryCommitGate();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Observation.AfterSave = async () =>
        {
            entered.SetResult();
            await release.Task;
        };
        using var callbackCancellation = new CancellationTokenSource();
        fixture.Observation.ExpectedToken = callbackCancellation.Token;
        var execution = fixture.Executor.ExecuteAsync(claim, gate, callbackCancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        gate.Close();
        if (cancelToken) callbackCancellation.Cancel();
        callbackCancellation.IsCancellationRequested.Should().Be(cancelToken,
            "the live-token control proves gate closure independently carries the timeout decision");
        execution.IsCompleted.Should().BeFalse("the executor still owns the live callback and transaction");
        (await fixture.StateAsync(fact)).Business.Should().Be(0, "the observed saved write is still uncommitted");
        release.SetResult();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(10));

        result.Outcome.Should().Be(DeliveryExecutionOutcome.Canceled);
        result.CommitAcknowledged.Should().BeFalse();
        (await fixture.StateAsync(fact)).Should().Match<DurableState>(state =>
            state.Business == 0 && state.Inbox == 0 && state.Outbox == 0
            && state.SuccessAudit == 0 && !state.Processed);
    }

    [Fact]
    public async Task Retained_active_subscription_executes_despite_a_pending_sibling_and_pending_target_is_refused()
    {
        await using var fixture = await Fixture.CreateAsync(schema, pendingSibling: true);
        var fact = Fact();
        var claim = await fixture.InsertAndClaimAsync(fact);
        fixture.Registry.Resolve(claim.Type).Disposition.Should().Be(OutboxEventDisposition.Pending);
        var result = await fixture.Executor.ExecuteAsync(claim, new DeliveryCommitGate());
        result.Outcome.Should().Be(DeliveryExecutionOutcome.Completed);
        (await fixture.StateAsync(fact)).Business.Should().Be(1);

        var pending = Fact();
        var pendingClaim = await fixture.InsertAndClaimAsync(pending, PendingSubscription);
        var refused = await fixture.Executor.ExecuteAsync(pendingClaim, new DeliveryCommitGate());
        refused.Outcome.Should().Be(DeliveryExecutionOutcome.InvalidSubscription);
        fixture.Observation.Constructions.Should().Be(1);
        (await fixture.StateAsync(pending, PendingSubscription)).Inbox.Should().Be(0);
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("type")]
    [InlineData("topic")]
    [InlineData("partition")]
    [InlineData("claim-time")]
    [InlineData("unclassified")]
    [InlineData("privileged")]
    public async Task Invalid_metadata_or_unclassified_handler_refuses_before_business_construction(string invalid)
    {
        await using var fixture = await Fixture.CreateAsync(schema,
            classified: invalid != "unclassified", privileged: invalid == "privileged");
        var fact = Fact();
        var claim = await fixture.InsertAndClaimAsync(fact, corruption: invalid);
        if (invalid == "claim-time") claim = claim with { OccurredAt = claim.OccurredAt.AddSeconds(1) };
        var result = await fixture.Executor.ExecuteAsync(claim, new DeliveryCommitGate());

        result.Outcome.Should().Be(invalid switch
        {
            "privileged" => DeliveryExecutionOutcome.InvalidSubscription,
            "unclassified" => DeliveryExecutionOutcome.Refused,
            _ => DeliveryExecutionOutcome.InvalidEnvelope,
        });
        result.CommitAcknowledged.Should().BeFalse();
        fixture.Observation.Constructions.Should().Be(0);
        fixture.Logs.Text.Should().NotContain(SensitiveCanary).And.NotContain(PayloadCanary.ToString()).And.NotContain(typeof(Handler).FullName!);
        if (invalid == "unclassified") fixture.Logs.Entries.Should().Contain(entry => entry.Level == LogLevel.Error);
        var state = await fixture.StateAsync(fact);
        state.Inbox.Should().Be(0);
        state.Processed.Should().BeFalse();
        state.Business.Should().Be(0);
    }

    [Fact]
    public async Task A_lease_expiring_behind_an_unchanged_row_lock_is_rechecked_after_lock_acquisition()
    {
        await using var fixture = await Fixture.CreateAsync(schema);
        var fact = Fact();
        var claim = await fixture.AcceptAndClaimAsync(fact);
        var entered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Observation.AfterSave = async () =>
        {
            await using var command = fixture.Observation.Unit!.Connection.CreateCommand();
            command.Transaction = fixture.Observation.Unit.Transaction;
            command.CommandText = "SELECT pg_backend_pid()";
            entered.SetResult((int)(await command.ExecuteScalarAsync())!);
            await release.Task;
        };
        var execution = fixture.Executor.ExecuteAsync(claim, new DeliveryCommitGate());
        var pid = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.ChangeLeaseAsync(fact.EventId, "short");
        await using var blocker = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Database.OutboxConnectionString);
        await using var blockerTransaction = await blocker.BeginTransactionAsync();
        try
        {
            await using (var rowLock = new NpgsqlCommand("""
                SELECT event_id FROM event_deliveries WHERE event_id = @event FOR UPDATE
                """, blocker, blockerTransaction))
            {
                rowLock.Parameters.AddWithValue("event", fact.EventId);
                (await rowLock.ExecuteScalarAsync()).Should().Be(fact.EventId);
            }
            release.SetResult();
            await WaitUntilBlockedAsync(fixture.Database.AppConnectionString, pid);
            execution.IsCompleted.Should().BeFalse();
            // This transaction changes no row. The final fence has already reached
            // the lock, and database time is the only change before its release.
            await using (var wait = new NpgsqlCommand("""
                SELECT pg_sleep(GREATEST(0, EXTRACT(EPOCH FROM (locked_until - clock_timestamp()))) + 0.05)
                FROM event_deliveries WHERE event_id = @event
                """, blocker, blockerTransaction))
            {
                wait.Parameters.AddWithValue("event", fact.EventId);
                await wait.ExecuteNonQueryAsync();
            }
            await blockerTransaction.RollbackAsync();
        }
        finally { release.TrySetResult(); }
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(10));
        result.CommitAcknowledged.Should().BeFalse();
        fixture.Observation.ObservedSavedWork.Should().BeTrue();
        var state = await fixture.StateAsync(fact);
        state.Business.Should().Be(0);
        state.Inbox.Should().Be(0);
        state.Outbox.Should().Be(0);
        state.Processed.Should().BeFalse();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    public async Task Lost_COMMIT_acknowledgement_retains_atomic_completion_and_never_repeats_business(int attempts)
    {
        await using var fixture = await Fixture.CreateAsync(schema);
        var fact = Fact();
        var claim = await fixture.AcceptAndClaimAsync(fact, attempts);
        await using var proxy = new CommitResponseDroppingProxy(fixture.Database.AppConnectionString);
        await using var uncertain = fixture.ForConnection(proxy.ConnectionString);
        var gate = new DeliveryCommitGate();
        var clearsBefore = uncertain.Observation.AuditClearCount;
        var result = await uncertain.Executor.ExecuteAsync(claim, gate);

        result.Outcome.Should().Be(DeliveryExecutionOutcome.Indeterminate);
        result.CommitAcknowledged.Should().BeFalse();
        gate.CommitStarted.Should().BeTrue();
        proxy.DroppedCommitResponses.Should().Be(1);
        uncertain.Observation.ObservedSavedWork.Should().BeTrue();
        (uncertain.Observation.AuditClearCount - clearsBefore).Should().Be(1,
            "the outer delivery audit owner clears the real capture exactly once after reconciliation");
        var state = await fixture.StateAsync(fact);
        state.Business.Should().Be(1);
        state.Inbox.Should().Be(1);
        state.Outbox.Should().Be(1);
        state.SuccessAudit.Should().Be(1);
        state.IndeterminateAudit.Should().Be(1);
        state.Processed.Should().BeTrue();

        var retryClearsBefore = fixture.Observation.AuditClearCount;
        var retry = await fixture.Executor.ExecuteAsync(claim, new DeliveryCommitGate());
        retry.Outcome.Should().Be(DeliveryExecutionOutcome.LostClaim);
        (fixture.Observation.AuditClearCount - retryClearsBefore).Should().Be(0,
            "durable completion refuses the retry before another audit frame can clear");
        fixture.Observation.Constructions.Should().Be(0, "durable completion excludes every subsequent execution");
        (await fixture.StateAsync(fact)).Should().BeEquivalentTo(state);
        uncertain.Logs.Text.Should().NotContain(SensitiveCanary).And.NotContain(PayloadCanary.ToString());
    }

    [Fact]
    public async Task Post_acknowledgement_scope_disposal_fault_retains_completion_and_blocks_gate_reuse()
    {
        await using var fixture = await Fixture.CreateAsync(schema);
        var fact = Fact();
        var claim = await fixture.AcceptAndClaimAsync(fact);
        fixture.Observation.CleanupMode = "fault";
        var gate = new DeliveryCommitGate();
        var result = await fixture.Executor.ExecuteAsync(claim, gate);

        result.Should().Be(new DeliveryExecutionResult(DeliveryExecutionOutcome.Completed, true, true));
        gate.CommitAcknowledged.Should().BeTrue();
        var state = await fixture.StateAsync(fact);
        state.Business.Should().Be(1);
        state.Inbox.Should().Be(1);
        state.Processed.Should().BeTrue();
        await FluentActions.Awaiting(() => fixture.Executor.ExecuteAsync(claim, gate))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*gate*one execution*");
        fixture.Observation.CleanupMode = null;
        (await fixture.Executor.ExecuteAsync(claim, new DeliveryCommitGate())).Outcome
            .Should().Be(DeliveryExecutionOutcome.LostClaim);
        fixture.Observation.Constructions.Should().Be(1);
        (await fixture.StateAsync(fact)).Should().BeEquivalentTo(state);
        fixture.Logs.Text.Should().NotContain(SensitiveCanary).And.NotContain(PayloadCanary.ToString());
    }

    [Fact]
    public async Task Post_acknowledgement_hanging_scope_cleanup_retains_receipt_and_live_resource_until_observed_completion()
    {
        await using var fixture = await Fixture.CreateAsync(schema);
        var fact = Fact();
        var claim = await fixture.AcceptAndClaimAsync(fact);
        fixture.Observation.CleanupMode = "hang";
        var gate = new DeliveryCommitGate();
        var execution = fixture.Executor.ExecuteAsync(claim, gate);
        await fixture.Observation.CleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            execution.IsCompleted.Should().BeFalse();
            fixture.Observation.CleanupAlive.Should().BeTrue();
            gate.CommitAcknowledged.Should().BeTrue("the receipt is published before asynchronous resource cleanup");
            gate.Close();
            var state = await fixture.StateAsync(fact);
            state.Business.Should().Be(1);
            state.Inbox.Should().Be(1);
            state.Outbox.Should().Be(1);
            state.SuccessAudit.Should().Be(1);
            state.Processed.Should().BeTrue();
        }
        finally { fixture.Observation.CleanupRelease.TrySetResult(); }
        (await execution.WaitAsync(TimeSpan.FromSeconds(10))).Should()
            .Be(new DeliveryExecutionResult(DeliveryExecutionOutcome.Completed, true, false));
        fixture.Observation.CleanupAlive.Should().BeFalse();
        (await fixture.Executor.ExecuteAsync(claim, new DeliveryCommitGate())).Outcome
            .Should().Be(DeliveryExecutionOutcome.LostClaim);
        fixture.Observation.Constructions.Should().Be(1);
    }

    [Fact]
    public async Task Retained_envelope_read_failure_rolls_back_before_construction_and_fresh_scope_can_retry()
    {
        await using var fixture = await Fixture.CreateAsync(schema);
        var fact = Fact();
        var claim = await fixture.AcceptAndClaimAsync(fact);
        await fixture.AlterSchemaAsync("REVOKE SELECT ON event_deliveries FROM learnstack_app");
        DeliveryExecutionResult result;
        try { result = await fixture.Executor.ExecuteAsync(claim, new DeliveryCommitGate()); }
        finally { await fixture.AlterSchemaAsync("GRANT SELECT ON event_deliveries TO learnstack_app"); }

        result.Outcome.Should().Be(DeliveryExecutionOutcome.Failed);
        result.CommitAcknowledged.Should().BeFalse();
        fixture.Observation.Constructions.Should().Be(0);
        (await fixture.StateAsync(fact)).Should().Match<DurableState>(state =>
            state.Business == 0 && state.Inbox == 0 && state.Outbox == 0 && !state.Processed);
        (await fixture.Executor.ExecuteAsync(claim, new DeliveryCommitGate())).Outcome
            .Should().Be(DeliveryExecutionOutcome.Completed);
        (await fixture.StateAsync(fact)).Business.Should().Be(1);
    }

    [Fact]
    public async Task A_real_MUST_audit_write_fault_rolls_back_every_effect_and_logs_only_bounded_diagnostics()
    {
        await using var fixture = await Fixture.CreateAsync(schema);
        var fact = Fact();
        var claim = await fixture.AcceptAndClaimAsync(fact);
        await fixture.AlterSchemaAsync("ALTER TABLE audit_log ADD CONSTRAINT private_delivery_canary_84e315 CHECK (false)");
        var result = await fixture.Executor.ExecuteAsync(claim, new DeliveryCommitGate());

        fixture.Observation.ObservedSavedWork.Should().BeTrue();
        result.Outcome.Should().Be(DeliveryExecutionOutcome.Failed);
        result.CommitAcknowledged.Should().BeFalse();
        var state = await fixture.StateAsync(fact);
        state.Business.Should().Be(0);
        state.Inbox.Should().Be(0);
        state.Outbox.Should().Be(0);
        state.SuccessAudit.Should().Be(0);
        state.Processed.Should().BeFalse();
        fixture.Logs.Entries.Should().Contain(entry => entry.Level == LogLevel.Critical,
            "the actual failed standalone MUST reconciliation logs through this capture");
        fixture.Logs.Text.Should().NotContain(SensitiveCanary).And.NotContain(PayloadCanary.ToString()).And.NotContain(typeof(Handler).FullName!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_delivery_owner_drains_saved_domain_facts_and_rolls_back_translation_failure(bool failTranslation)
    {
        await using var fixture = await Fixture.CreateAsync(schema);
        var fact = Fact();
        var claim = await fixture.AcceptAndClaimAsync(fact);
        fixture.Observation.EmitDomainEvent = true;
        fixture.Observation.FailTranslation = failTranslation;
        var gate = new DeliveryCommitGate();
        var result = await fixture.Executor.ExecuteAsync(claim, gate);

        fixture.Observation.ObservedSavedWork.Should().BeTrue();
        fixture.Observation.TranslationObserved.Should().BeTrue();
        fixture.Observation.Source!.DomainEvents.Should().BeEmpty("owner completion clears saved facts on every exit");
        var state = await fixture.StateAsync(fact);
        if (failTranslation)
        {
            result.Outcome.Should().Be(DeliveryExecutionOutcome.Failed);
            gate.CommitStarted.Should().BeFalse();
            state.Business.Should().Be(0);
            state.Inbox.Should().Be(0);
            state.Outbox.Should().Be(0);
            state.SuccessAudit.Should().Be(0);
            state.Processed.Should().BeFalse();
        }
        else
        {
            result.Outcome.Should().Be(DeliveryExecutionOutcome.Completed);
            gate.CommitAcknowledged.Should().BeTrue();
            state.Business.Should().Be(1);
            state.Inbox.Should().Be(1);
            state.Outbox.Should().Be(2);
            state.SuccessAudit.Should().Be(1);
            state.Processed.Should().BeTrue();
            (await fixture.OutboxOriginsAsync(fact)).Should().BeEquivalentTo(new[]
            {
                (fact.EventId, CausalActor),
                (fixture.Observation.DomainEventId, CausalActor),
            });
        }
        fixture.Logs.Text.Should().NotContain(SensitiveCanary).And.NotContain(PayloadCanary.ToString());
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("compact")]
    [InlineData("uppercase")]
    [InlineData("zero")]
    [InlineData("sentinel")]
    public async Task Invalid_claim_token_or_platform_tenant_refuses_before_creating_any_application_scope(string invalid)
    {
        await using var fixture = await Fixture.CreateAsync(schema);
        var valid = await fixture.AcceptAndClaimAsync(Fact());
        var claim = invalid switch
        {
            "malformed" => valid with { Token = SensitiveCanary },
            "compact" => valid with { Token = "aaaaaaaabbbb4ccc8dddeeeeeeeeeeee" },
            "uppercase" => valid with { Token = "AAAAAAAA-BBBB-4CCC-8DDD-EEEEEEEEEEEE" },
            "zero" => valid with { Token = Guid.Empty.ToString("D") },
            _ => valid with { TenantId = TenantId.PlatformSentinel.Value },
        };
        var factory = new RefusingScopeFactory();
        var handlers = IntegrationEventHandlerRegistry.FromServiceDescriptors(
            [ServiceDescriptor.Scoped<IIntegrationEventHandler<ProofEvent>, Handler>()]);
        var executor = new DeliveryExecutor(factory, fixture.Accessor, new PartitionSerializer(), fixture.Registry, handlers);
        var previous = new ProbeContext(SchemaFixture.TenantB, null);
        fixture.Accessor.Current = previous;
        try
        {
            (await executor.ExecuteAsync(claim, new DeliveryCommitGate())).Should()
                .Be(new DeliveryExecutionResult(DeliveryExecutionOutcome.InvalidEnvelope, false, false));
            factory.CreatedScopes.Should().Be(0);
            fixture.Accessor.Current.Should().BeSameAs(previous);

            // A valid claim reaches the deliberately failing factory, proving the
            // negative result above was early validation, not an inert probe.
            (await executor.ExecuteAsync(valid, new DeliveryCommitGate())).Outcome
                .Should().Be(DeliveryExecutionOutcome.Failed);
            factory.CreatedScopes.Should().Be(1);
            fixture.Accessor.Current.Should().BeSameAs(previous);
        }
        finally { fixture.Accessor.Current = null; }
    }

    private sealed class RefusingScopeFactory : IServiceScopeFactory
    {
        public int CreatedScopes { get; private set; }
        public IServiceScope CreateScope()
        {
            CreatedScopes++;
            throw new InvalidOperationException(SensitiveCanary);
        }
    }

    private static async Task WaitUntilBlockedAsync(string connectionString, int pid)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);
        await SchemaQueries.AssertApplicationRoleAsync(connection);
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            await using var read = new NpgsqlCommand("SELECT cardinality(pg_blocking_pids(@pid)) > 0", connection);
            read.Parameters.AddWithValue("pid", pid);
            if (await read.ExecuteScalarAsync(deadline.Token) is true) return;
            await Task.Yield();
        }
    }

    private static ProofEvent Fact() => new()
    {
        EventId = Guid.CreateVersion7(),
        TenantId = SchemaFixture.TenantA,
        OccurredAt = new DateTimeOffset(2026, 10, 13, 9, 0, 0, TimeSpan.Zero),
        SubjectId = Guid.CreateVersion7(),
    };

    [IntegrationEventPartition(nameof(SubjectId))]
    public sealed record ProofEvent : IntegrationEventBase
    {
        public required Guid SubjectId { get; init; }
        public Guid PrivateReferenceId { get; init; } = PayloadCanary;
        public override string Topic => "learnstack.proof.delivery";
        public override string PartitionKey => SubjectId.ToString("D");
    }

    public sealed class Handler
        : IIntegrationEventHandler<ProofEvent>
    {
        private readonly IServiceProvider _services;
        private readonly Observation _observation;
        public Handler(IServiceProvider services, Observation observation)
        {
            _services = services;
            _observation = observation;
            observation.Constructions++;
            _ = services.GetRequiredService<CleanupProbe>();
            AssertContext(services);
            var unit = services.GetRequiredService<IUnitOfWork>();
            unit.HasActiveTransaction.Should().BeTrue();
            using (var inbox = unit.Connection.CreateCommand())
            {
                inbox.Transaction = unit.Transaction;
                inbox.CommandText = "SELECT count(*) FROM inbox_messages WHERE subscription_id = @subscription";
                var subscription = inbox.CreateParameter();
                subscription.ParameterName = "subscription";
                subscription.Value = Subscription;
                inbox.Parameters.Add(subscription);
                inbox.ExecuteScalar().Should().Be(1L, "the transport claimed inbox before entering the business constructor");
                observation.InboxObservedAtConstruction = true;
            }
            services.GetRequiredService<IAuditStateCapture>().HasOpenFrame.Should().BeTrue();
            services.GetRequiredService<ProducerExecution>().RequireAnnouncement().TenantId.Value.Should().Be(SchemaFixture.TenantA);
            if (observation.Fault == "constructor") throw new InvalidOperationException(SensitiveCanary);
        }
        public Task HandleAsync(ProofEvent @event, CancellationToken cancellationToken = default)
        {
            _observation.Executions++;
            cancellationToken.Should().Be(_observation.ExpectedToken);
            return _observation.Fault == "null-task" ? null! : WorkAsync(@event, cancellationToken);
        }
        private async Task WorkAsync(ProofEvent fact, CancellationToken cancellationToken)
        {
            await Task.Yield();
            AssertContext(_services);
            var unit = _services.GetRequiredService<IUnitOfWork>();
            _observation.Unit = unit;
            await SchemaQueries.AssertApplicationRoleAsync(unit.Connection);
            var db = _services.GetRequiredService<CustomizationDbContext>();
            var id = TenantContentTypeId.From(fact.SubjectId);
            var source = TenantContentType.Create(id, TenantId.From(fact.TenantId), $"delivery-{fact.SubjectId:N}"[..24], 1,
                LocalizedText.From(("en", "Delivery execution proof")), """{"type":"object"}""", "default-card",
                _services.GetRequiredService<IClock>(), UserId.SystemActor);
            _observation.Source = source;
            if (_observation.EmitDomainEvent)
            {
                typeof(Entity<TenantContentTypeId>).GetMethod("RaiseDomainEvent", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(source, [new DeliveryDomainEvent
                    {
                        EventId = _observation.DomainEventId, OccurredAt = fact.OccurredAt, SubjectId = fact.SubjectId,
                    }]);
            }
            db.Add(source);
            await db.SaveChangesAsync(cancellationToken);
            _observation.OutgoingActivity = Activity.Current?.Id;
            _observation.OutgoingAnnouncement = unit.GetTenantContextAnnouncement(unit.Transaction);
            await _services.GetRequiredService<IOutbox>().EnqueueAsync(fact with { EventId = Guid.CreateVersion7() }, cancellationToken);
            await using var read = unit.Connection.CreateCommand();
            read.Transaction = unit.Transaction;
            read.CommandText = "SELECT count(*) FROM tenant_content_types WHERE id = @id";
            var parameter = read.CreateParameter();
            parameter.ParameterName = "id";
            parameter.Value = fact.SubjectId;
            read.Parameters.Add(parameter);
            (await read.ExecuteScalarAsync(cancellationToken)).Should().Be(1L);
            _observation.ObservedSavedWork = true;
            if (_observation.AfterSave is { } callback) await callback();
            if (_observation.Fault == "handler") throw new InvalidOperationException(SensitiveCanary);
            if (_observation.Fault == "foreign-cancel")
            {
                using var foreign = new CancellationTokenSource();
                foreign.Cancel();
                throw new OperationCanceledException(SensitiveCanary, foreign.Token);
            }
        }
        private static void AssertContext(IServiceProvider services)
        {
            var context = services.GetRequiredService<ITenantContext>();
            context.IsResolved.Should().BeTrue("delivery restores resolved context before handler construction and execution");
            context.TenantId.Value.Should().Be(SchemaFixture.TenantA);
            context.OrganizationId!.Value.Value.Should().Be(SchemaFixture.OrgA1);
            context.UserId.Should().Be(UserId.SystemActor);
            context.CausalActorUserId.Should().Be(UserId.From(CausalActor));
            context.CorrelationId.Should().Be(Trace);
            context.ModuleName.Should().Be("unknown");
            Activity.Current.Should().NotBeNull();
            Activity.Current!.IdFormat.Should().Be(ActivityIdFormat.W3C);
            Activity.Current.TraceId.ToHexString().Should().Be("4bf92f3577b34da6a3ce929d0e0e4736");
        }
    }

    public sealed class Observation
    {
        private int _auditClearCount;

        public int Constructions { get; set; }
        public int AuditClearCount => Volatile.Read(ref _auditClearCount);
        public void RecordAuditClear() => Interlocked.Increment(ref _auditClearCount);
        public bool InboxObservedAtConstruction { get; set; }
        public bool ObserveBareUnit { get; set; }
        public bool BareUnitObserved { get; set; }
        public bool EmitDomainEvent { get; set; }
        public bool FailTranslation { get; set; }
        public bool TranslationObserved { get; set; }
        public Guid DomainEventId { get; } = Guid.CreateVersion7();
        public TenantContentType? Source { get; set; }
        public int Executions { get; set; }
        public bool ObservedSavedWork { get; set; }
        public string? OutgoingActivity { get; set; }
        public TenantContextAnnouncement? OutgoingAnnouncement { get; set; }
        public string? Fault { get; set; }
        public Func<Task>? AfterSave { get; set; }
        public CancellationToken ExpectedToken { get; set; }
        public IUnitOfWork? Unit { get; set; }
        public string? CleanupMode { get; set; }
        public bool CleanupAlive { get; set; }
        public TaskCompletionSource CleanupEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CleanupRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed record DeliveryDomainEvent : DomainEvent
    {
        public required Guid SubjectId { get; init; }
    }

    public sealed class DomainTranslator(IOutbox outbox, IUnitOfWork unit, Observation observation)
        : INotificationHandler<DeliveryDomainEvent>
    {
        public async Task Handle(DeliveryDomainEvent notification, CancellationToken cancellationToken)
        {
            observation.Source!.DomainEvents.Should().ContainSingle();
            await outbox.EnqueueAsync(new ProofEvent
            {
                EventId = Guid.CreateVersion7(),
                TenantId = SchemaFixture.TenantA,
                OccurredAt = notification.OccurredAt,
                SubjectId = notification.SubjectId,
            }, cancellationToken);
            await using var read = unit.Connection.CreateCommand();
            read.Transaction = unit.Transaction;
            read.CommandText = "SELECT count(*) FROM outbox_messages WHERE payload ->> 'SubjectId' = @subject";
            var parameter = read.CreateParameter();
            parameter.ParameterName = "subject";
            parameter.Value = notification.SubjectId.ToString();
            read.Parameters.Add(parameter);
            (await read.ExecuteScalarAsync(cancellationToken)).Should().Be(2L,
                "both direct and translated production existed on the actual owner transaction");
            observation.TranslationObserved = true;
            if (observation.FailTranslation) throw new InvalidOperationException(SensitiveCanary);
        }
    }

    public sealed class CleanupProbe : IAsyncDisposable
    {
        private readonly Observation _observation;
        private readonly ITenantContextAccessor _accessor;
        public CleanupProbe(Observation observation, ITenantContextAccessor accessor)
        {
            _observation = observation;
            _accessor = accessor;
            observation.CleanupAlive = true;
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                _accessor.Current.Should().BeOfType<EventTenantContext>();
                _accessor.Current!.TenantId.Value.Should().Be(SchemaFixture.TenantA);
                _accessor.Current.OrganizationId!.Value.Value.Should().Be(SchemaFixture.OrgA1);
                _accessor.Current.UserId.Should().Be(UserId.SystemActor);
                Activity.Current.Should().NotBeNull("delivery metadata outlives owned scope disposal");
                if (_observation.CleanupMode == "hang")
                {
                    _observation.CleanupEntered.TrySetResult();
                    await _observation.CleanupRelease.Task;
                }
                if (_observation.CleanupMode == "fault") throw new IOException(SensitiveCanary);
            }
            finally { _observation.CleanupAlive = false; }
        }
    }

    private sealed record LogEntry(LogLevel Level, string Text);

    private sealed class DeliveryLogs : ILoggerProvider
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();
        public string Text => string.Join("\n", Entries.Select(entry => entry.Text));
        public ILogger CreateLogger(string categoryName) => new CapturedLogger<object>(this);
        public void Dispose() { }
    }

    private sealed class CapturedLogger<T>(DeliveryLogs logs) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            logs.Entries.Enqueue(new LogEntry(logLevel, formatter(state, exception) + " " + exception));
    }

    private sealed class CatalogSource : IAuditCatalogSource
    {
        public string ModuleName => "customization";
        public void Describe(IAuditCatalogBuilder builder) =>
            builder.MustAudit<Handler>(Operation, OperationType.Create, typeof(TenantContentType));
    }

    private sealed class ObservedCapture(IAuditStateCapture actual, Observation observation) : IAuditStateCapture
    {
        public IReadOnlyList<CapturedEntityChange> Changes => actual.Changes;
        public IReadOnlyList<AuditIntent> Intents => actual.Intents;
        public AuditIntentState State => actual.State;
        public bool HasOpenFrame => actual.HasOpenFrame;
        public void Add(CapturedEntityChange change) => actual.Add(change);
        public IReadOnlyList<CapturedEntityChange> ChangesOf(AuditIntent intent) => actual.ChangesOf(intent);
        public void OpenFrame() => actual.OpenFrame();
        public void CloseFrame(AuditIntentResult result) => actual.CloseFrame(result);
        public void DeclareIntent(AuditIntent intent) => actual.DeclareIntent(intent);
        public void MarkWrittenInTransaction() => actual.MarkWrittenInTransaction();
        public void MarkCommitted() => actual.MarkCommitted();
        public void MarkRolledBack() => actual.MarkRolledBack();
        public void MarkIndeterminate(Exception cause) => actual.MarkIndeterminate(cause);
        public void Clear()
        {
            actual.Clear();
            observation.RecordAuditClear();
        }
    }

    private sealed class ProbeContext(Guid tenant, Guid? organization) : ITenantContext
    {
        public bool IsResolved => true;
        public TenantId TenantId => TenantId.From(tenant);
        public OrganizationId? OrganizationId => organization is { } id ? LearnStack.SharedKernel.Identifiers.OrganizationId.From(id) : null;
        public UserId? UserId => LearnStack.SharedKernel.Identifiers.UserId.From(SchemaFixture.Actor);
        public string? CorrelationId => Trace;
        public string? ModuleName => "customization";
    }

    private sealed record DurableState(long Business, long Inbox, long Outbox, long SuccessAudit,
        bool Processed, Guid? OutboxCausation, Guid? OutboxActor, Guid? AuditActor,
        string? AuditCorrelation, Guid? AuditOrganization, string? LockedBy,
        Guid? AuditCausation, Guid? AuditCausalActor, string? OutboxCorrelation,
        Guid? OutboxTenant, Guid? OutboxOrganization, string? OutboxPartition, long IndeterminateAudit);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly WebApplicationFactory<Program> _host;
        private readonly bool _ownsDatabase;
        private Fixture(DisposableSchemaDatabase database, bool pendingSibling, bool classified, bool privileged,
            string? connectionString = null, bool ownsDatabase = true)
        {
            Database = database;
            _ownsDatabase = ownsDatabase;
            Observation = new Observation();
            Logs = new DeliveryLogs();
            var handlers = IntegrationEventHandlerRegistry.FromServiceDescriptors(
                [ServiceDescriptor.Scoped<IIntegrationEventHandler<ProofEvent>, Handler>()]);
            var selected = OutboxSubscriptionManifest.Active(Subscription, typeof(Handler), privileged
                ? OutboxSubscriptionExecutionScope.PrivilegedPlatform : OutboxSubscriptionExecutionScope.OrdinaryTenant);
            Registry = OutboxEventRegistry.Create([
                OutboxEventRegistration.Required(typeof(ProofEvent), pendingSibling
                    ? [selected, OutboxSubscriptionManifest.Pending(PendingSubscription)] : [selected]),
            ], [], handlers, [typeof(ProofEvent).Assembly]);
            _host = new ConstructionOnlyApiFactory().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Default", connectionString ?? database.AppConnectionString);
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<OutboxEventRegistry>();
                    services.AddSingleton(Registry);
                    services.RemoveAll<IntegrationEventHandlerRegistry>();
                    services.AddSingleton(handlers);
                    services.AddSingleton(Observation);
                    services.RemoveAll<IAuditStateCapture>();
                    services.AddScoped<IAuditStateCapture>(provider =>
                        new ObservedCapture(provider.GetRequiredService<AuditStateCapture>(), Observation));
                    services.RemoveAll<IUnitOfWork>();
                    services.AddScoped<IUnitOfWork>(provider =>
                    {
                        if (Observation.ObserveBareUnit)
                        {
                            provider.GetRequiredService<ITenantContextAccessor>().Current.Should().BeNull(
                                "routing SQL must resolve its bare unit without another caller's ambient identity");
                            Observation.BareUnitObserved = true;
                        }
                        return ActivatorUtilities.CreateInstance<NpgsqlUnitOfWork>(provider,
                            provider.GetRequiredService<Lazy<NpgsqlDataSource>>().Value);
                    });
                    services.AddScoped<Handler>();
                    services.AddScoped<CleanupProbe>();
                    services.AddScoped<INotificationHandler<DeliveryDomainEvent>, DomainTranslator>();
                    services.AddSingleton<ILoggerProvider>(Logs);
                    services.AddSingleton<ILogger<AuditExecutionCoordinator>>(new CapturedLogger<AuditExecutionCoordinator>(Logs));
                    services.AddSingleton<ILogger<AuditConfigService>>(new CapturedLogger<AuditConfigService>(Logs));
                    services.AddSingleton<ILogger<PostgresAuditStore>>(new CapturedLogger<PostgresAuditStore>(Logs));
                    services.AddSingleton<ILogger<TransactionExecutionCoordinator>>(new CapturedLogger<TransactionExecutionCoordinator>(Logs));
                    if (classified) services.AddSingleton<IAuditCatalogSource, CatalogSource>();
                    services.AddSingleton<DeliveryExecutor>();
                });
            });
        }
        public DisposableSchemaDatabase Database { get; }
        public Observation Observation { get; }
        public DeliveryLogs Logs { get; }
        public Fixture ForConnection(string connectionString) =>
            new(Database, false, true, false, connectionString, ownsDatabase: false);
        public OutboxEventRegistry Registry { get; }
        public ITenantContextAccessor Accessor => _host.Services.GetRequiredService<ITenantContextAccessor>();
        public DeliveryExecutor Executor => _host.Services.GetRequiredService<DeliveryExecutor>();
        public static async Task<Fixture> CreateAsync(SchemaFixture schema, bool pendingSibling = false,
            bool classified = true, bool privileged = false)
        {
            var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
            var fixture = new Fixture(database, pendingSibling, classified, privileged);
            try { await fixture.SeedAsync(); return fixture; }
            catch { await fixture.DisposeAsync(); throw; }
        }
        private async Task SeedAsync()
        {
            Accessor.Current = new ProbeContext(SchemaFixture.TenantA, null);
            try
            {
                await using var scope = _host.Services.CreateAsyncScope();
                var services = scope.ServiceProvider;
                var unit = services.GetRequiredService<IUnitOfWork>();
                await using var frame = await unit.BeginTransactionAsync();
                await unit.SetTenantContextAsync(services.GetRequiredService<ITenantContext>());
                await SchemaQueries.AssertApplicationRoleAsync(unit.Connection);
                var db = services.GetRequiredService<TenancyDbContext>();
                db.Add(Tenant.Create(TenantId.From(SchemaFixture.TenantA), "delivery-proof", "Delivery proof",
                    services.GetRequiredService<IClock>(), UserId.From(SchemaFixture.Actor)));
                await db.SaveChangesAsync();
                db.Add(Organization.Create(OrganizationId.From(SchemaFixture.OrgA1), TenantId.From(SchemaFixture.TenantA),
                    "proof-branch", "Proof branch", services.GetRequiredService<IClock>(), UserId.From(SchemaFixture.Actor)));
                await db.SaveChangesAsync();
                await frame.CompleteAsync();
            }
            finally { Accessor.Current = null; }
        }
        public async Task<DeliveryClaim> AcceptAndClaimAsync(ProofEvent fact, int attempts = 1)
        {
            await _host.Services.GetRequiredService<IEventBus>().PublishAsync(Envelope(fact));
            return await ClaimAsync(fact, Subscription, attempts);
        }
        public async Task<DeliveryClaim> InsertAndClaimAsync(ProofEvent fact, string subscription = Subscription,
            string? corruption = null)
        {
            var envelope = Envelope(fact);
            await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(Database.AppConnectionString);
            await using var transaction = await connection.BeginTransactionAsync();
            await SchemaQueries.AssertApplicationRoleAsync(connection);
            await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);
            await SchemaQueries.SetSettingAsync(connection, transaction, "app.organization_id", SchemaFixture.OrgA1.ToString());
            await using var insert = new NpgsqlCommand("""
                INSERT INTO event_deliveries
                    (tenant_id, subscription_id, event_id, organization_id, occurred_at,
                     correlation_id, causation_id, actor_user_id, type, topic, partition_key, payload)
                VALUES (@tenant, @subscription, @event, @organization, @occurred,
                    @correlation, @causation, @actor, @type, @topic, @partition, @payload)
                """, connection, transaction);
            insert.Parameters.AddWithValue("tenant", fact.TenantId);
            insert.Parameters.AddWithValue("subscription", subscription);
            insert.Parameters.AddWithValue("event", fact.EventId);
            insert.Parameters.AddWithValue("organization", SchemaFixture.OrgA1);
            insert.Parameters.AddWithValue("occurred", fact.OccurredAt);
            insert.Parameters.AddWithValue("correlation", Trace);
            insert.Parameters.AddWithValue("causation", Guid.CreateVersion7());
            insert.Parameters.AddWithValue("actor", CausalActor);
            insert.Parameters.AddWithValue("type", corruption == "type" ? SensitiveCanary : typeof(ProofEvent).AssemblyQualifiedName!);
            insert.Parameters.AddWithValue("topic", corruption == "topic" ? "learnstack.proof.wrong" : fact.Topic);
            insert.Parameters.AddWithValue("partition", corruption == "partition" ? "wrong-partition" : fact.PartitionKey);
            insert.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, corruption == "payload" ? "{}" : fact.ToPayloadJson());
            await insert.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
            return await ClaimAsync(fact, subscription);
        }
        private static IntegrationEventEnvelope Envelope(ProofEvent fact) =>
            new(fact, Trace, SchemaFixture.OrgA1, Guid.CreateVersion7(), UserId.From(CausalActor));
        private async Task<DeliveryClaim> ClaimAsync(ProofEvent fact, string subscription, int attempts = 1)
        {
            await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(Database.OutboxConnectionString);
            await using var update = new NpgsqlCommand("""
                UPDATE event_deliveries SET locked_by = @token,
                    locked_until = clock_timestamp() + interval '30 seconds', attempts = @attempts
                WHERE event_id = @event AND subscription_id = @subscription
                RETURNING locked_until, type, topic, partition_key
                """, connection);
            var token = Guid.CreateVersion7().ToString("D");
            update.Parameters.AddWithValue("token", token);
            update.Parameters.AddWithValue("attempts", attempts);
            update.Parameters.AddWithValue("event", fact.EventId);
            update.Parameters.AddWithValue("subscription", subscription);
            await using var read = await update.ExecuteReaderAsync();
            (await read.ReadAsync()).Should().BeTrue();
            return new DeliveryClaim(fact.TenantId, subscription, fact.EventId, SchemaFixture.OrgA1,
                token, attempts, read.GetFieldValue<DateTimeOffset>(0), DeliveryClaimKind.Normal,
                read.GetString(1), read.GetString(2), read.GetString(3), fact.OccurredAt);
        }
        public async Task ChangeLeaseAsync(Guid eventId, string loss)
        {
            await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(Database.OutboxConnectionString);
            var sql = loss switch
            {
                "token" => "UPDATE event_deliveries SET locked_by = @replacement WHERE event_id = @event",
                "short" => "UPDATE event_deliveries SET locked_until = clock_timestamp() + interval '1 second' WHERE event_id = @event",
                _ => "UPDATE event_deliveries SET locked_until = clock_timestamp() - interval '1 second' WHERE event_id = @event",
            };
            await using var update = new NpgsqlCommand(sql, connection);
            update.Parameters.AddWithValue("event", eventId);
            if (loss == "token") update.Parameters.AddWithValue("replacement", Guid.CreateVersion7().ToString("D"));
            (await update.ExecuteNonQueryAsync()).Should().Be(1);
        }
        public async Task AlterSchemaAsync(string sql)
        {
            await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(Database.MigrationConnectionString);
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
        public async Task InsertInboxAsync(ProofEvent fact)
        {
            Accessor.Current = new ProbeContext(SchemaFixture.TenantA, SchemaFixture.OrgA1);
            try
            {
                await using var scope = _host.Services.CreateAsyncScope();
                var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                await using var frame = await unit.BeginTransactionAsync();
                await unit.SetTenantContextAsync(scope.ServiceProvider.GetRequiredService<ITenantContext>());
                (await scope.ServiceProvider.GetRequiredService<IInboxGuard>()
                    .TryClaimAsync(TenantId.From(fact.TenantId), Subscription, fact.EventId)).Should().BeTrue();
                await frame.CompleteAsync();
            }
            finally { Accessor.Current = null; }
        }
        public async Task<IReadOnlyList<(Guid Causation, Guid Actor)>> OutboxOriginsAsync(ProofEvent fact)
        {
            await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(Database.AppConnectionString);
            await using var transaction = await connection.BeginTransactionAsync();
            await SchemaQueries.AssertApplicationRoleAsync(connection);
            await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);
            await SchemaQueries.SetSettingAsync(connection, transaction, "app.organization_id", SchemaFixture.OrgA1.ToString());
            await using var command = new NpgsqlCommand("""
                SELECT causation_id, actor_user_id FROM outbox_messages WHERE payload ->> 'SubjectId' = @subject
                """, connection, transaction);
            command.Parameters.AddWithValue("subject", fact.SubjectId.ToString());
            await using var read = await command.ExecuteReaderAsync();
            var rows = new List<(Guid, Guid)>();
            while (await read.ReadAsync()) rows.Add((read.GetGuid(0), read.GetGuid(1)));
            return rows;
        }
        public async Task<(Guid Actor, Guid Causation, Guid CausalActor)> FailedAuditOriginAsync()
        {
            await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(Database.AppConnectionString);
            await using var transaction = await connection.BeginTransactionAsync();
            await SchemaQueries.AssertApplicationRoleAsync(connection);
            await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);
            await SchemaQueries.SetSettingAsync(connection, transaction, "app.organization_id", SchemaFixture.OrgA1.ToString());
            await using var command = new NpgsqlCommand("""
                SELECT actor_user_id, (metadata ->> 'causation_id')::uuid,
                       (metadata ->> 'causal_actor_user_id')::uuid
                FROM audit_log WHERE operation = @operation AND outcome = 'failed'
                """, connection, transaction);
            command.Parameters.AddWithValue("operation", Operation);
            await using var read = await command.ExecuteReaderAsync();
            (await read.ReadAsync()).Should().BeTrue();
            var origin = (read.GetGuid(0), read.GetGuid(1), read.GetGuid(2));
            (await read.ReadAsync()).Should().BeFalse();
            return origin;
        }
        public async Task<DurableState> StateAsync(ProofEvent fact, string subscription = Subscription)
        {
            await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(Database.AppConnectionString);
            await using var transaction = await connection.BeginTransactionAsync();
            await SchemaQueries.AssertApplicationRoleAsync(connection);
            await SchemaQueries.SetTenantAsync(connection, transaction, fact.TenantId);
            await SchemaQueries.SetSettingAsync(connection, transaction, "app.organization_id", SchemaFixture.OrgA1.ToString());
            await using var command = new NpgsqlCommand("""
                SELECT
                  (SELECT count(*) FROM tenant_content_types WHERE id = @subject),
                  (SELECT count(*) FROM inbox_messages WHERE event_id = @event AND subscription_id = @subscription),
                  (SELECT count(*) FROM outbox_messages WHERE payload ->> 'SubjectId' = @subject_text),
                  (SELECT count(*) FROM audit_log WHERE operation = @operation AND outcome = 'success' AND entity_id = @subject_text),
                  (SELECT processed_at IS NOT NULL FROM event_deliveries WHERE event_id = @event AND subscription_id = @subscription),
                  (SELECT causation_id FROM outbox_messages WHERE payload ->> 'SubjectId' = @subject_text LIMIT 1),
                  (SELECT actor_user_id FROM outbox_messages WHERE payload ->> 'SubjectId' = @subject_text LIMIT 1),
                  (SELECT actor_user_id FROM audit_log WHERE operation = @operation AND outcome = 'success' AND entity_id = @subject_text LIMIT 1),
                  (SELECT correlation_id FROM audit_log WHERE operation = @operation AND outcome = 'success' AND entity_id = @subject_text LIMIT 1),
                  (SELECT organization_id FROM audit_log WHERE operation = @operation AND outcome = 'success' AND entity_id = @subject_text LIMIT 1),
                  (SELECT locked_by FROM event_deliveries WHERE event_id = @event AND subscription_id = @subscription),
                  (SELECT (metadata ->> 'causation_id')::uuid FROM audit_log WHERE operation = @operation AND outcome = 'success' AND entity_id = @subject_text LIMIT 1),
                  (SELECT (metadata ->> 'causal_actor_user_id')::uuid FROM audit_log WHERE operation = @operation AND outcome = 'success' AND entity_id = @subject_text LIMIT 1),
                  (SELECT correlation_id FROM outbox_messages WHERE payload ->> 'SubjectId' = @subject_text LIMIT 1),
                  (SELECT tenant_id FROM outbox_messages WHERE payload ->> 'SubjectId' = @subject_text LIMIT 1),
                  (SELECT organization_id FROM outbox_messages WHERE payload ->> 'SubjectId' = @subject_text LIMIT 1),
                  (SELECT partition_key FROM outbox_messages WHERE payload ->> 'SubjectId' = @subject_text LIMIT 1),
                  (SELECT count(*) FROM audit_log WHERE operation = @operation AND outcome = 'indeterminate'
                      AND entity_id = @subject_text AND (metadata ->> 'causation_id')::uuid = @event)
                """, connection, transaction);
            command.Parameters.AddWithValue("subject", fact.SubjectId);
            command.Parameters.AddWithValue("subject_text", fact.SubjectId.ToString());
            command.Parameters.AddWithValue("event", fact.EventId);
            command.Parameters.AddWithValue("subscription", subscription);
            command.Parameters.AddWithValue("operation", Operation);
            await using var read = await command.ExecuteReaderAsync();
            (await read.ReadAsync()).Should().BeTrue();
            return new DurableState(read.GetInt64(0), read.GetInt64(1), read.GetInt64(2), read.GetInt64(3),
                !read.IsDBNull(4) && read.GetBoolean(4), read.IsDBNull(5) ? null : read.GetGuid(5),
                read.IsDBNull(6) ? null : read.GetGuid(6), read.IsDBNull(7) ? null : read.GetGuid(7),
                read.IsDBNull(8) ? null : read.GetString(8), read.IsDBNull(9) ? null : read.GetGuid(9),
                read.IsDBNull(10) ? null : read.GetString(10), read.IsDBNull(11) ? null : read.GetGuid(11),
                read.IsDBNull(12) ? null : read.GetGuid(12), read.IsDBNull(13) ? null : read.GetString(13),
                read.IsDBNull(14) ? null : read.GetGuid(14), read.IsDBNull(15) ? null : read.GetGuid(15),
                read.IsDBNull(16) ? null : read.GetString(16), read.GetInt64(17));
        }
        public async ValueTask DisposeAsync()
        {
            await _host.DisposeAsync();
            if (_ownsDatabase) await Database.DisposeAsync();
        }
    }
}
