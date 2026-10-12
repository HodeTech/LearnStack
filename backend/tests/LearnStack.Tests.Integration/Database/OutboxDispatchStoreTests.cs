using System.Data.Common;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Modules.Tenancy.Application.Contracts.IntegrationEvents;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Messaging;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Time;
using LearnStack.Tools.Seeder;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Real committed leases, fenced transitions and witness-bound terminal audit.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class OutboxDispatchStoreTests(SchemaFixture schema)
{
    private const string Traceparent = "00-11111111111111111111111111111111-2222222222222222-01";
    private const string AttemptTraceparent = "00-33333333333333333333333333333333-4444444444444444-01";
    private const string EarlierTenantIdentity = "LearnStack.Previous.TenantCreatedIntegrationEventV1, LearnStack.Previous.Contracts";
    private static readonly DateTimeOffset Occurred = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlyCollection<string> NoBlockedPartitions = Array.Empty<string>();

    [Fact]
    public async Task Two_independent_stores_contend_for_one_committed_live_lease()
    {
        await using var test = await TestDatabase.CreateAsync(schema.Postgres);
        var id = await test.InsertAsync();
        var first = test.Store();
        var second = test.Store();

        var claims = await Task.WhenAll(first.TryClaimAsync(NoBlockedPartitions), second.TryClaimAsync(NoBlockedPartitions));

        var claim = claims.OfType<OutboxDispatchClaim>().Should().ContainSingle().Which;
        claim.Id.Should().Be(id);
        claim.Kind.Should().Be(OutboxClaimKind.Normal);
        claim.Attempts.Should().Be(1);
        Guid.ParseExact(claim.Token, "D").Should().NotBeEmpty();
        var durable = await test.ReadAsync(id);
        durable.Token.Should().Be(claim.Token);
        durable.Attempts.Should().Be(1);
        durable.LockedUntil.Should().Be(claim.LockedUntil);
        // Reading the winner must not release the database lease. This second poll
        // catches a release-on-read implementation even if the first race serialized.
        (await first.TryClaimAsync(NoBlockedPartitions)).Should().BeNull();
        (await second.TryClaimAsync(NoBlockedPartitions)).Should().BeNull();
        (await test.ReadAsync(id)).Should().Be(durable);
    }

    [Fact]
    public async Task Concurrent_stores_claim_distinct_rows_without_owning_a_batch()
    {
        await using var test = await TestDatabase.CreateAsync(schema.Postgres);
        var ids = new[] { await test.InsertAsync(), await test.InsertAsync() };

        var claims = await Task.WhenAll(test.Store().TryClaimAsync(NoBlockedPartitions), test.Store().TryClaimAsync(NoBlockedPartitions));

        claims.Should().OnlyContain(claim => claim != null);
        claims.Select(claim => claim!.Id).Should().BeEquivalentTo(ids);
        claims.Select(claim => claim!.Token).Should().OnlyHaveUniqueItems();
        (await test.Store().TryClaimAsync(NoBlockedPartitions)).Should().BeNull();
    }

    [Fact]
    public async Task A_live_success_commits_processed_state_and_clears_its_lease_once()
    {
        await using var test = await TestDatabase.CreateAsync(schema.Postgres);
        var id = await test.InsertAsync();
        var store = test.Store();
        var claim = (await store.TryClaimAsync(NoBlockedPartitions))!;

        (await store.CompleteAsync(claim)).Should().BeTrue();

        var row = await test.ReadAsync(id);
        row.ProcessedAt.Should().NotBeNull();
        row.DeadLetteredAt.Should().BeNull();
        row.TerminalReason.Should().BeNull();
        row.Token.Should().BeNull();
        row.LockedUntil.Should().BeNull();
        row.Attempts.Should().Be(1);
        (await store.CompleteAsync(claim)).Should().BeFalse();
        (await store.FailAsync(claim, OutboxFailureReason.PublishFailed)).Should().BeFalse();
        (await store.TryClaimAsync(NoBlockedPartitions)).Should().BeNull();
        (await test.ReadAsync(id)).Should().Be(row);
        (await test.AuditCountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("terminal-request")]
    public async Task Expiry_alone_refuses_every_transition_without_changing_state(string transition)
    {
        await using var test = await TestDatabase.CreateAsync(schema.Postgres);
        var id = await test.InsertAsync();
        var store = test.Store();
        var claim = (await store.TryClaimAsync(NoBlockedPartitions))!;
        await test.ExpireAsync(id);
        var before = await test.ReadAsync(id);

        (await TransitionAsync(store, claim, transition)).Should().BeFalse();

        (await test.ReadAsync(id)).Should().Be(before);
        (await test.AuditCountAsync()).Should().Be(0);
        (await store.TryClaimAsync(NoBlockedPartitions)).Should().BeNull("an expired claim requires recovery before publication");
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("terminal-request")]
    public async Task Recovery_by_the_same_store_replaces_the_token_and_fences_every_stale_transition(string transition)
    {
        await using var test = await TestDatabase.CreateAsync(schema.Postgres);
        var id = await test.InsertAsync();
        var store = test.Store();
        var stale = (await store.TryClaimAsync(NoBlockedPartitions))!;
        await test.ExpireAsync(id);

        var recovery = (await store.TryAcquireRecoveryAsync(NoBlockedPartitions))!;

        recovery.Id.Should().Be(id);
        recovery.Kind.Should().Be(OutboxClaimKind.Recovery);
        recovery.Token.Should().NotBe(stale.Token);
        recovery.Attempts.Should().Be(stale.Attempts);
        var before = await test.ReadAsync(id);
        (await TransitionAsync(store, stale, transition)).Should().BeFalse();
        (await test.ReadAsync(id)).Should().Be(before);
        (await test.AuditCountAsync()).Should().Be(0);
        (await store.FailAsync(recovery, OutboxFailureReason.UnknownOutcome)).Should().BeTrue();
        await test.MakeDueAsync(id);
        var fresh = (await store.TryClaimAsync(NoBlockedPartitions))!;
        fresh.Token.Should().NotBe(stale.Token).And.NotBe(recovery.Token);
        fresh.Attempts.Should().Be(2);
        (await store.CompleteAsync(fresh)).Should().BeTrue();
    }

    [Fact]
    public async Task Expired_terminal_acquisition_loses_its_fence_before_and_after_fresh_terminal_reacquisition()
    {
        await using var test = await TestDatabase.CreateAsync(schema.Postgres);
        var id = await test.InsertAsync();
        var store = test.Store();
        var normal = (await store.TryClaimAsync(NoBlockedPartitions))!;
        (await store.RequestTerminalAsync(normal, OutboxTerminalReason.InvalidEnvelope)).Should().BeTrue();
        var stale = (await store.TryClaimTerminalAsync(NoBlockedPartitions))!;
        await test.ExpireAsync(id);
        var expired = await test.ReadAsync(id);

        (await store.TryTerminalizeAsync(stale, AttemptTraceparent)).Should().BeFalse();
        (await test.ReadAsync(id)).Should().Be(expired);
        var fresh = (await store.TryClaimTerminalAsync(NoBlockedPartitions))!;
        fresh.Token.Should().NotBe(stale.Token);
        fresh.Attempts.Should().Be(1);
        var reacquired = await test.ReadAsync(id);
        (await store.TryTerminalizeAsync(stale, AttemptTraceparent)).Should().BeFalse();
        (await test.ReadAsync(id)).Should().Be(reacquired);
        (await test.AuditCountAsync()).Should().Be(0);
        (await store.TryTerminalizeAsync(fresh, AttemptTraceparent)).Should().BeTrue();
        (await test.AuditCountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 5)]
    [InlineData(3, 30)]
    [InlineData(4, 300)]
    [InlineData(5, 3600)]
    public async Task Committed_failures_schedule_the_five_database_time_backoffs_without_sleeping(int attempt, int seconds)
    {
        await using var test = await TestDatabase.CreateAsync(schema.Postgres);
        var id = await test.InsertAsync(attempts: attempt - 1);
        // A caller clock decades away must not decide expiry or availability.
        var store = test.Store(clock: new FixedClock(Occurred.AddYears(50)));
        var claim = (await store.TryClaimAsync(NoBlockedPartitions))!;
        claim.Attempts.Should().Be(attempt);
        var before = await test.DatabaseNowAsync();

        (await store.FailAsync(claim, OutboxFailureReason.PublishFailed)).Should().BeTrue();

        var after = await test.DatabaseNowAsync();
        var row = await test.ReadAsync(id);
        AssertBackoff(row, attempt, seconds, before, after);
        (await store.TryClaimAsync(NoBlockedPartitions)).Should().BeNull();
        await test.MakeDueAsync(id);
        (await store.TryClaimAsync(NoBlockedPartitions))!.Attempts.Should().Be(attempt + 1);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 5)]
    [InlineData(3, 30)]
    [InlineData(4, 300)]
    [InlineData(5, 3600)]
    public async Task Expired_claims_recover_into_backoff_without_spending_another_attempt(int attempt, int seconds)
    {
        await using var test = await TestDatabase.CreateAsync(schema.Postgres);
        var id = await test.InsertAsync(attempts: attempt - 1);
        var store = test.Store();
        var crashed = (await store.TryClaimAsync(NoBlockedPartitions))!;
        await test.ExpireAsync(id);
        (await store.TryClaimAsync(NoBlockedPartitions)).Should().BeNull();
        var recovery = (await store.TryAcquireRecoveryAsync(NoBlockedPartitions))!;
        recovery.Attempts.Should().Be(attempt);
        recovery.Token.Should().NotBe(crashed.Token);
        var before = await test.DatabaseNowAsync();

        (await store.FailAsync(recovery, OutboxFailureReason.UnknownOutcome)).Should().BeTrue();

        AssertBackoff(await test.ReadAsync(id), attempt, seconds, before, await test.DatabaseNowAsync());
        (await store.TryAcquireRecoveryAsync(NoBlockedPartitions)).Should().BeNull();
        (await store.TryClaimAsync(NoBlockedPartitions)).Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_crashed_sixth_claim_requests_terminalization_and_never_claims_a_seventh(bool crash)
    {
        await using var test = await TestDatabase.CreateAsync(schema.Postgres);
        var id = await test.InsertAsync();
        var store = test.Store();
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var earlier = (await store.TryClaimAsync(NoBlockedPartitions))!;
            earlier.Attempts.Should().Be(attempt);
            (await store.FailAsync(earlier, OutboxFailureReason.PublishFailed)).Should().BeTrue();
            await test.MakeDueAsync(id);
        }
        var sixth = (await store.TryClaimAsync(NoBlockedPartitions))!;
        sixth.Attempts.Should().Be(6);
        if (crash)
        {
            await test.ExpireAsync(id);
            (await store.TryClaimAsync(NoBlockedPartitions)).Should().BeNull();
            sixth = (await store.TryAcquireRecoveryAsync(NoBlockedPartitions))!;
            sixth.Kind.Should().Be(OutboxClaimKind.Recovery);
        }

        (await store.FailAsync(sixth, crash ? OutboxFailureReason.UnknownOutcome : OutboxFailureReason.PublishTimeout)).Should().BeTrue();

        var requested = await test.ReadAsync(id);
        requested.Attempts.Should().Be(6);
        requested.TerminalReason.Should().Be("retry_exhausted");
        requested.DeadLetteredAt.Should().BeNull();
        requested.Token.Should().BeNull();
        await test.MakeDueAsync(id);
        (await store.TryClaimAsync(NoBlockedPartitions)).Should().BeNull();
        var terminal = (await store.TryClaimTerminalAsync(NoBlockedPartitions))!;
        terminal.Kind.Should().Be(OutboxClaimKind.Terminal);
        terminal.Attempts.Should().Be(6);
        (await store.TryTerminalizeAsync(terminal, AttemptTraceparent)).Should().BeTrue();
        (await store.TryClaimAsync(NoBlockedPartitions)).Should().BeNull();
        (await test.ReadAsync(id)).Attempts.Should().Be(6);
        (await test.AuditCountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Exhausted_unleased_legacy_work_is_terminal_only_without_an_extra_attempt()
    {
        await using var test = await TestDatabase.CreateAsync(schema.Postgres);
        var id = await test.InsertAsync(attempts: 8);
        var store = test.Store();

        (await store.TryClaimAsync(NoBlockedPartitions)).Should().BeNull();
        var recovery = (await store.TryAcquireRecoveryAsync(NoBlockedPartitions))!;
        recovery.Attempts.Should().Be(8);
        (await store.FailAsync(recovery, OutboxFailureReason.UnknownOutcome)).Should().BeTrue();
        var terminal = (await store.TryClaimTerminalAsync(NoBlockedPartitions))!;

        terminal.Id.Should().Be(id);
        terminal.Attempts.Should().Be(8);
        (await store.TryTerminalizeAsync(terminal, AttemptTraceparent)).Should().BeTrue();
        (await test.ReadAsync(id)).TerminalReason.Should().Be("retry_exhausted");
        (await test.ReadAsync(id)).Attempts.Should().Be(8);
    }

    [Theory]
    [InlineData(OutboxTerminalReason.UnknownType, "unknown_type")]
    [InlineData(OutboxTerminalReason.RetiredType, "retired_type")]
    [InlineData(OutboxTerminalReason.InvalidEnvelope, "invalid_envelope")]
    public async Task A_terminal_request_before_six_attempts_cannot_be_claimed_for_publication(OutboxTerminalReason reason, string storedReason)
    {
        await using var test = await TestDatabase.CreateAsync(schema.Postgres);
        var id = await test.InsertAsync();
        var store = test.Store();
        var claim = (await store.TryClaimAsync(NoBlockedPartitions))!;

        (await store.RequestTerminalAsync(claim, reason)).Should().BeTrue();

        var row = await test.ReadAsync(id);
        row.TerminalReason.Should().Be(storedReason);
        row.Attempts.Should().Be(1);
        row.ProcessedAt.Should().BeNull();
        row.DeadLetteredAt.Should().BeNull();
        row.Token.Should().BeNull();
        row.LockedUntil.Should().BeNull();
        await test.MakeDueAsync(id);
        (await store.TryClaimAsync(NoBlockedPartitions)).Should().BeNull();
        (await store.TryAcquireRecoveryAsync(NoBlockedPartitions)).Should().BeNull();
        var terminal = (await store.TryClaimTerminalAsync(NoBlockedPartitions))!;
        terminal.Attempts.Should().Be(1);
        terminal.Token.Should().NotBe(claim.Token);
        (await store.TryTerminalizeAsync(terminal, AttemptTraceparent)).Should().BeTrue();
    }

    [Fact]
    public async Task Refused_audit_rolls_back_the_terminal_update_and_retains_the_request_for_audited_retry()
    {
        await using var test = await TestDatabase.CreateAsync(schema.Postgres);
        var id = await test.InsertAsync(organization: SchemaFixture.OrgA1);
        var store = test.Store();
        var normal = (await store.TryClaimAsync(NoBlockedPartitions))!;
        (await store.RequestTerminalAsync(normal, OutboxTerminalReason.InvalidEnvelope)).Should().BeTrue();
        var terminal = (await store.TryClaimTerminalAsync(NoBlockedPartitions))!;
        var before = await test.ReadAsync(id);

        try
        {
            await test.AuditInsertGrantAsync(grant: false);
            var terminalize = () => store.TryTerminalizeAsync(terminal, AttemptTraceparent);
            await terminalize.Should().ThrowAsync<AuditWriteFailedException>();
            (await test.ReadAsync(id)).Should().Be(before, "the failed INSERT and terminal UPDATE share one transaction");
            (await test.AuditCountAsync()).Should().Be(0);
            var snapshot = await store.ReadSnapshotAsync(NoBlockedPartitions);
            snapshot.PendingCount.Should().Be(1);
            snapshot.DeferredCount.Should().Be(0);
            snapshot.OldestEligibleAgeSeconds.Should().Be(0);
            await test.MakeDueAsync(id);
            (await store.TryClaimAsync(NoBlockedPartitions)).Should().BeNull();
            await test.ExpireAsync(id);
            (await store.TryClaimAsync(NoBlockedPartitions)).Should().BeNull("terminal intent survives time advancing");
        }
        finally
        {
            await test.AuditInsertGrantAsync(grant: true);
        }

        var retry = (await store.TryClaimTerminalAsync(NoBlockedPartitions))!;
        retry.Token.Should().NotBe(terminal.Token);
        retry.Attempts.Should().Be(1);
        (await store.TryTerminalizeAsync(retry, AttemptTraceparent)).Should().BeTrue();
        (await test.AuditCountAsync()).Should().Be(1);
        (await test.ReadAsync(id)).DeadLetteredAt.Should().NotBeNull();
        (await store.TryTerminalizeAsync(terminal, AttemptTraceparent)).Should().BeFalse();
        (await store.TryTerminalizeAsync(retry, AttemptTraceparent)).Should().BeFalse();
        (await store.TryClaimTerminalAsync(NoBlockedPartitions)).Should().BeNull();
        (await test.AuditCountAsync()).Should().Be(1, "a committed terminal retry cannot append another audit");
    }

    [Fact]
    public async Task A_lost_physical_COMMIT_response_reconciles_durable_terminal_state_without_duplicate_audit()
    {
        await using var test = await TestDatabase.CreateAsync(schema.Postgres);
        var id = await test.InsertAsync(organization: SchemaFixture.OrgA1);
        var direct = test.Store();
        var normal = (await direct.TryClaimAsync(NoBlockedPartitions))!;
        (await direct.RequestTerminalAsync(normal, OutboxTerminalReason.InvalidEnvelope)).Should().BeTrue();
        var terminal = (await direct.TryClaimTerminalAsync(NoBlockedPartitions))!;
        // Acquisitions use the direct source. The first explicit COMMIT crossing
        // this separate proxy can therefore only be the audited terminal COMMIT.
        await using var proxy = new CommitResponseDroppingProxy(test.OutboxConnectionString);
        await using var source = OutboxDispatcherDataSource.Build(proxy.ConnectionString);
        var uncertain = test.Store(dispatchSource: source);

        (await uncertain.TryTerminalizeAsync(terminal, AttemptTraceparent).WaitAsync(TimeSpan.FromSeconds(15)))
            .Should().BeTrue("the fresh reconciliation connection observes the already committed terminal state");

        proxy.DroppedCommitResponses.Should().Be(1, "a real server COMMIT acknowledgement must have been suppressed");
        proxy.AcceptedConnections.Should().BeGreaterThanOrEqualTo(2,
            "the broken commit connection cannot answer the durability reconciliation");
        var row = await test.ReadAsync(id);
        row.DeadLetteredAt.Should().NotBeNull();
        row.ProcessedAt.Should().BeNull();
        row.Token.Should().BeNull();
        (await test.AuditCountAsync()).Should().Be(1);
        (await uncertain.TryTerminalizeAsync(terminal, AttemptTraceparent)).Should().BeFalse();
        (await uncertain.TryClaimTerminalAsync(NoBlockedPartitions)).Should().BeNull();
        (await test.AuditCountAsync()).Should().Be(1);
        proxy.DroppedCommitResponses.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Terminal_audit_uses_the_stored_tenant_organization_and_validated_causal_metadata(bool invalidCausalMetadata)
    {
        await using var test = await TestDatabase.CreateAsync(schema.Postgres);
        var causation = invalidCausalMetadata ? Guid.Empty : Guid.CreateVersion7();
        var causalActor = invalidCausalMetadata ? Guid.Empty : SchemaFixture.Actor;
        var id = await test.InsertAsync(organization: SchemaFixture.OrgA1,
            correlation: invalidCausalMetadata ? "private-invalid-trace" : Traceparent,
            causation: causation, actor: causalActor, payload: "{}");
        var store = test.Store();
        var claim = (await store.TryClaimAsync(NoBlockedPartitions))!;
        (await store.RequestTerminalAsync(claim, OutboxTerminalReason.InvalidEnvelope)).Should().BeTrue();
        var terminal = (await store.TryClaimTerminalAsync(NoBlockedPartitions))!;

        (await store.TryTerminalizeAsync(terminal, AttemptTraceparent)).Should().BeTrue();

        var audit = await test.ReadAuditAsync();
        audit.Tenant.Should().Be(SchemaFixture.TenantA);
        audit.Organization.Should().Be(SchemaFixture.OrgA1);
        audit.Actor.Should().Be(UserId.SystemActor.Value);
        audit.Module.Should().Be("platform");
        audit.Operation.Should().Be("platform.outbox.dead_letter");
        audit.Class.Should().Be("Must");
        audit.Type.Should().Be("SecurityEvent");
        audit.Outcome.Should().Be("success");
        audit.Reason.Should().BeNull("the platform-entry reason field is not a terminal descriptor");
        audit.Correlation.Should().Be(invalidCausalMetadata ? AttemptTraceparent : Traceparent);
        using var metadata = JsonDocument.Parse(audit.Metadata);
        metadata.RootElement.GetProperty("event_id").GetGuid().Should().Be(id);
        metadata.RootElement.GetProperty("reason").GetString().Should().Be("invalid_envelope");
        metadata.RootElement.GetProperty("attempts").GetInt32().Should().Be(1);
        if (invalidCausalMetadata)
        {
            metadata.RootElement.TryGetProperty("causation_id", out _).Should().BeFalse();
            metadata.RootElement.TryGetProperty("causal_actor_user_id", out _).Should().BeFalse();
            audit.Metadata.Should().NotContain("private-invalid-trace");
        }
        else
        {
            metadata.RootElement.GetProperty("causation_id").GetGuid().Should().Be(causation);
            metadata.RootElement.GetProperty("causal_actor_user_id").GetGuid().Should().Be(causalActor);
        }
        (await test.ReadAsync(id)).DeadLetteredAt.Should().NotBeNull();
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("organization")]
    [InlineData("event")]
    [InlineData("operation")]
    [InlineData("class")]
    [InlineData("type")]
    [InlineData("transaction")]
    public async Task Substituting_a_draft_or_transaction_against_the_actual_UPDATE_witness_rolls_back_both_writes(string substitution)
    {
        await using var test = await TestDatabase.CreateAsync(schema.Postgres);
        var id = await test.InsertAsync(organization: SchemaFixture.OrgA1);
        var calls = 0;
        var store = test.Store(decorate: inner => new TerminalAuditDecorator(inner,
            async (draft, witness, connection, transaction, cancellationToken) =>
            {
                calls++;
                witness.EventId.Should().Be(id);
                witness.TenantId.Should().Be(SchemaFixture.TenantA);
                witness.OrganizationId.Should().Be(SchemaFixture.OrgA1);
                // This witness came from the real successful UPDATE: its state is
                // visible on that live bypass-role transaction before the INSERT.
                await using var truth = new NpgsqlCommand(
                    "SELECT dead_lettered_at IS NOT NULL FROM outbox_messages WHERE id = @id",
                    (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
                truth.Parameters.AddWithValue("id", id);
                (await truth.ExecuteScalarAsync(cancellationToken)).Should().Be(true);

                if (substitution == "transaction")
                {
                    await using var otherConnection = await test.DispatchSource.OpenConnectionAsync(cancellationToken);
                    await using var otherTransaction = await otherConnection.BeginTransactionAsync(cancellationToken);
                    await inner.WriteEventTerminalAsync(draft, witness, otherConnection, otherTransaction, cancellationToken);
                    return;
                }

                var changed = substitution switch
                {
                    "tenant" => draft with { TenantId = TenantId.From(SchemaFixture.TenantB) },
                    "organization" => draft with { OrganizationId = OrganizationId.From(SchemaFixture.OrgA2) },
                    "event" => draft with { Metadata = ReplaceEventIdentity(draft.Metadata!) },
                    "operation" => draft with { Operation = "platform.delivery.dead_letter" },
                    "class" => draft with { OperationClass = OperationClass.Should },
                    "type" => draft with { OperationType = OperationType.Update },
                    _ => throw new ArgumentOutOfRangeException(nameof(substitution)),
                };
                await inner.WriteEventTerminalAsync(changed, witness, connection, transaction, cancellationToken);
            }));
        var normal = (await store.TryClaimAsync(NoBlockedPartitions))!;
        (await store.RequestTerminalAsync(normal, OutboxTerminalReason.InvalidEnvelope)).Should().BeTrue();
        var terminal = (await store.TryClaimTerminalAsync(NoBlockedPartitions))!;
        var before = await test.ReadAsync(id);

        var attempt = () => store.TryTerminalizeAsync(terminal, AttemptTraceparent);
        await attempt.Should().ThrowAsync<ArgumentException>();

        calls.Should().Be(1, "the refusal must exercise the real audit seam after a successful terminal UPDATE");
        (await test.ReadAsync(id)).Should().Be(before);
        (await test.AuditCountAsync()).Should().Be(0);
        // The same still-live terminal claim succeeds with the real unmodified seam.
        (await test.Store().TryTerminalizeAsync(terminal, AttemptTraceparent)).Should().BeTrue();
        (await test.AuditCountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_witness_from_a_committed_terminal_transaction_cannot_be_reused_on_a_later_real_transition()
    {
        await using var test = await TestDatabase.CreateAsync(schema.Postgres);
        EventTerminalTransition? previous = null;
        var calls = 0;
        var store = test.Store(decorate: inner => new TerminalAuditDecorator(inner,
            async (draft, witness, connection, transaction, cancellationToken) =>
            {
                calls++;
                var supplied = previous ?? witness;
                await inner.WriteEventTerminalAsync(draft, supplied, connection, transaction, cancellationToken);
                previous = witness;
            }));
        var firstId = await test.InsertAsync();
        var first = (await store.TryClaimAsync(NoBlockedPartitions))!;
        (await store.RequestTerminalAsync(first, OutboxTerminalReason.InvalidEnvelope)).Should().BeTrue();
        var firstTerminal = (await store.TryClaimTerminalAsync(NoBlockedPartitions))!;
        (await store.TryTerminalizeAsync(firstTerminal, AttemptTraceparent)).Should().BeTrue();
        previous.Should().NotBeNull();
        previous!.EventId.Should().Be(firstId);
        var secondId = await test.InsertAsync();
        var second = (await store.TryClaimAsync(NoBlockedPartitions))!;
        (await store.RequestTerminalAsync(second, OutboxTerminalReason.InvalidEnvelope)).Should().BeTrue();
        var secondTerminal = (await store.TryClaimTerminalAsync(NoBlockedPartitions))!;
        var before = await test.ReadAsync(secondId);

        var attempt = () => store.TryTerminalizeAsync(secondTerminal, AttemptTraceparent);
        await attempt.Should().ThrowAsync<ArgumentException>();

        calls.Should().Be(2);
        (await test.ReadAsync(secondId)).Should().Be(before);
        (await test.AuditCountAsync()).Should().Be(1);
        (await test.Store().TryTerminalizeAsync(secondTerminal, AttemptTraceparent)).Should().BeTrue();
        (await test.AuditCountAsync()).Should().Be(2);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Real_TenantCreated_contracts_and_aliases_are_held_before_any_payload_validation(bool alias, bool malformed)
    {
        await using var test = await TestDatabase.CreateAsync(schema.Postgres, seedTenant: false);
        await using (var scope = test.Provider.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new ProvisionTenantCommand(
                TenantId.From(SchemaFixture.TenantA), "dispatch-probe", "Dispatch probe",
                OrganizationId.From(SchemaFixture.OrgA1), "hq", "HQ"));
            result.IsSuccess.Should().BeTrue();
        }
        var produced = await test.SingleIdAsync();
        var fact = new TenantCreatedIntegrationEventV1
        {
            EventId = Guid.CreateVersion7(),
            TenantId = SchemaFixture.TenantA,
            OccurredAt = Occurred,
        };
        var id = await test.InsertAsync(fact: fact,
            type: alias ? EarlierTenantIdentity : typeof(TenantCreatedIntegrationEventV1).AssemblyQualifiedName,
            payload: malformed ? "{}" : fact.ToPayloadJson());
        var beforeProduced = await test.ReadAsync(produced);
        var before = await test.ReadAsync(id);
        var store = test.Store(registry: PendingRegistry());

        for (var poll = 0; poll < 3; poll++)
        {
            (await store.TryClaimAsync(NoBlockedPartitions)).Should().BeNull();
            (await store.TryAcquireRecoveryAsync(NoBlockedPartitions)).Should().BeNull();
            (await store.TryClaimTerminalAsync(NoBlockedPartitions)).Should().BeNull();
        }

        (await test.ReadAsync(produced)).Should().Be(beforeProduced);
        (await test.ReadAsync(id)).Should().Be(before);
        before.Attempts.Should().Be(0);
        before.Token.Should().BeNull();
        before.TerminalReason.Should().BeNull();
        var snapshot = await store.ReadSnapshotAsync(NoBlockedPartitions);
        snapshot.PendingCount.Should().Be(2);
        snapshot.DeferredCount.Should().Be(2);
        snapshot.OldestEligibleAgeSeconds.Should().Be(0);
        (await test.AuditCountAsync()).Should().Be(0, "provisioning audits are separate from the terminal operation");
    }

    [Fact]
    public async Task An_intentionally_unsubscribed_contract_remains_eligible_for_explicit_acknowledgement()
    {
        await using var test = await TestDatabase.CreateAsync(schema.Postgres);
        var id = await test.InsertAsync();
        var registry = EligibleRegistry();
        registry.Resolve(typeof(DispatchEventV1).AssemblyQualifiedName).Disposition
            .Should().Be(OutboxEventDisposition.IntentionallyUnsubscribed);
        var store = test.Store(registry: registry);

        var claim = (await store.TryClaimAsync(NoBlockedPartitions))!;
        claim.Id.Should().Be(id);
        (await store.CompleteAsync(claim)).Should().BeTrue();

        (await test.ReadAsync(id)).ProcessedAt.Should().NotBeNull();
        (await test.AuditCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Selection_orders_by_availability_then_occurrence_then_ID_regardless_of_insert_order()
    {
        await using var test = await TestDatabase.CreateAsync(schema.Postgres);
        var low = Guid.Parse("10000000-0000-7000-8000-000000000001");
        var high = Guid.Parse("20000000-0000-7000-8000-000000000001");
        var olderOccurrence = Guid.Parse("30000000-0000-7000-8000-000000000001");
        var earlierAvailability = Guid.Parse("40000000-0000-7000-8000-000000000001");
        await test.InsertAsync(id: high, available: Occurred, occurred: Occurred);
        await test.InsertAsync(id: low, available: Occurred, occurred: Occurred);
        await test.InsertAsync(id: olderOccurrence, available: Occurred, occurred: Occurred.AddMinutes(-1));
        await test.InsertAsync(id: earlierAvailability, available: Occurred.AddMinutes(-1), occurred: Occurred.AddMinutes(1));
        var store = test.Store();
        var selected = new List<Guid>();

        for (var poll = 0; poll < 4; poll++)
        {
            var claim = (await store.TryClaimAsync(NoBlockedPartitions))!;
            selected.Add(claim.Id);
            (await store.CompleteAsync(claim)).Should().BeTrue();
        }

        selected.Should().Equal(earlierAvailability, olderOccurrence, low, high);
    }

    [Fact]
    public async Task Delayed_failure_allows_later_work_on_the_same_partition_to_overtake()
    {
        await using var test = await TestDatabase.CreateAsync(schema.Postgres);
        var partition = Guid.CreateVersion7();
        var first = await test.InsertAsync(partition: partition, available: Occurred);
        var second = await test.InsertAsync(partition: partition, available: Occurred.AddMinutes(1));
        var store = test.Store();
        var failed = (await store.TryClaimAsync(NoBlockedPartitions))!;
        failed.Id.Should().Be(first);
        (await store.FailAsync(failed, OutboxFailureReason.PublishFailed)).Should().BeTrue();

        var later = (await store.TryClaimAsync(NoBlockedPartitions))!;

        later.Id.Should().Be(second);
        later.PartitionKey.Should().Be(failed.PartitionKey);
        (await store.CompleteAsync(later)).Should().BeTrue();
        (await test.ReadAsync(first)).ProcessedAt.Should().BeNull();
    }

    [Fact]
    public async Task Locally_blocked_partitions_exclude_normal_recovery_and_terminal_acquisition()
    {
        await using var test = await TestDatabase.CreateAsync(schema.Postgres);
        var blocked = Guid.CreateVersion7();
        var id = await test.InsertAsync(partition: blocked);
        IReadOnlyCollection<string> partitions = [blocked.ToString("D")];
        var store = test.Store();
        (await store.TryClaimAsync(partitions)).Should().BeNull();
        var normal = (await store.TryClaimAsync(NoBlockedPartitions))!;
        await test.ExpireAsync(id);
        (await store.TryAcquireRecoveryAsync(partitions)).Should().BeNull();
        var recovery = (await store.TryAcquireRecoveryAsync(NoBlockedPartitions))!;
        (await store.RequestTerminalAsync(recovery, OutboxTerminalReason.InvalidEnvelope)).Should().BeTrue();
        (await store.TryClaimTerminalAsync(partitions)).Should().BeNull();
        var terminal = (await store.TryClaimTerminalAsync(NoBlockedPartitions))!;
        terminal.Token.Should().NotBe(normal.Token).And.NotBe(recovery.Token);
        (await store.TryTerminalizeAsync(terminal, AttemptTraceparent)).Should().BeTrue();
    }

    [Fact]
    public async Task Queue_snapshot_counts_every_pending_state_but_ages_only_publish_eligible_work()
    {
        await using var test = await TestDatabase.CreateAsync(schema.Postgres);
        var now = await test.DatabaseNowAsync();
        var due = await test.InsertAsync(occurred: now.AddMinutes(-5));
        await test.InsertAsync(available: now.AddHours(1), occurred: now.AddHours(-10));
        var leased = await test.InsertAsync(occurred: now.AddHours(-9));
        await test.SetStateAsync(leased, "locked_by = '11111111-1111-4111-8111-111111111111', locked_until = now() + interval '30 seconds'");
        var expired = await test.InsertAsync(occurred: now.AddHours(-8));
        await test.SetStateAsync(expired, "locked_by = '22222222-2222-4222-8222-222222222222', locked_until = now() - interval '1 second'");
        var requested = await test.InsertAsync(occurred: now.AddHours(-7));
        await test.SetStateAsync(requested, "terminal_reason = 'invalid_envelope'");
        await test.InsertAsync(attempts: 6, occurred: now.AddHours(-6));
        await test.InsertAsync(fact: new TenantCreatedIntegrationEventV1
        {
            EventId = Guid.CreateVersion7(),
            TenantId = SchemaFixture.TenantA,
            OccurredAt = now.AddHours(-5),
        });
        var blockedPartition = Guid.CreateVersion7();
        await test.InsertAsync(partition: blockedPartition, occurred: now.AddHours(-4));
        var processed = await test.InsertAsync(occurred: now.AddHours(-3));
        await test.SetStateAsync(processed, "processed_at = now()");
        var terminal = await test.InsertAsync(occurred: now.AddHours(-2));
        await test.SetStateAsync(terminal, "terminal_reason = 'unknown_type', dead_lettered_at = now()");
        var registry = OutboxEventRegistry.Create(
            [OutboxEventRegistration.IntentionallyUnsubscribed(typeof(DispatchEventV1)),
                OutboxEventRegistration.Required(typeof(TenantCreatedIntegrationEventV1),
                    [OutboxSubscriptionManifest.Pending("customization.initialize-content-type-default.v1")])],
            [], IntegrationEventHandlerRegistry.Discover(),
            [typeof(DispatchEventV1).Assembly, typeof(TenantCreatedIntegrationEventV1).Assembly]);
        var store = test.Store(registry: registry);
        IReadOnlyCollection<string> blocked = [blockedPartition.ToString("D")];
        var before = await test.DatabaseNowAsync();

        var snapshot = await store.ReadSnapshotAsync(blocked);

        var after = await test.DatabaseNowAsync();
        snapshot.PendingCount.Should().Be(8, "delayed, leased, expired, requested, exhausted, deferred and blocked rows are pending");
        snapshot.DeferredCount.Should().Be(1);
        snapshot.OldestEligibleAgeSeconds.Should().BeInRange(
            (before - now.AddMinutes(-5)).TotalSeconds, (after - now.AddMinutes(-5)).TotalSeconds);
        var claim = (await store.TryClaimAsync(blocked))!;
        claim.Id.Should().Be(due, "snapshot eligibility must equal normal acquisition eligibility");
        (await store.CompleteAsync(claim)).Should().BeTrue();
        var excludedOnly = await store.ReadSnapshotAsync(blocked);
        excludedOnly.PendingCount.Should().Be(7);
        excludedOnly.DeferredCount.Should().Be(1);
        excludedOnly.OldestEligibleAgeSeconds.Should().Be(0);
        (await store.ReadSnapshotAsync(NoBlockedPartitions)).OldestEligibleAgeSeconds.Should().BeGreaterThan(14_000,
            "removing the local hold admits the old blocked partition");
    }

    [Fact]
    public async Task Empty_and_future_occurrence_snapshots_never_report_negative_eligible_age()
    {
        await using var test = await TestDatabase.CreateAsync(schema.Postgres);
        var store = test.Store();
        (await store.ReadSnapshotAsync(NoBlockedPartitions)).Should().Be(new OutboxQueueSnapshot(0, 0, 0));
        var now = await test.DatabaseNowAsync();
        await test.InsertAsync(occurred: now.AddHours(1));

        var snapshot = await store.ReadSnapshotAsync(NoBlockedPartitions);

        snapshot.PendingCount.Should().Be(1);
        snapshot.DeferredCount.Should().Be(0);
        snapshot.OldestEligibleAgeSeconds.Should().Be(0);
    }

    [Fact]
    public async Task Reserved_subscriber_terminal_operation_remains_unregistered()
    {
        await using var test = await TestDatabase.CreateAsync(schema.Postgres);
        var catalog = test.Provider.GetRequiredService<IAuditCatalog>();
        catalog.TryGetOffPath("platform.outbox.dead_letter", out var producer).Should().BeTrue();
        producer.OperationClass.Should().Be(OperationClass.Must);
        producer.OperationType.Should().Be(OperationType.SecurityEvent);
        catalog.TryGetOffPath("platform.delivery.dead_letter", out _).Should().BeFalse();
    }

    private static Task<bool> TransitionAsync(OutboxDispatchStore store, OutboxDispatchClaim claim, string transition) => transition switch
    {
        "success" => store.CompleteAsync(claim),
        "failure" => store.FailAsync(claim, OutboxFailureReason.PublishFailed),
        "terminal-request" => store.RequestTerminalAsync(claim, OutboxTerminalReason.InvalidEnvelope),
        _ => throw new ArgumentOutOfRangeException(nameof(transition)),
    };

    private static void AssertBackoff(Row row, int attempts, int seconds, DateTimeOffset before, DateTimeOffset after)
    {
        // Both endpoints are PostgreSQL readings, not elapsed wall-clock assertions.
        // The interval must be the documented delay added by the intervening SQL.
        row.AvailableAfter.Should().BeOnOrAfter(before.AddSeconds(seconds));
        row.AvailableAfter.Should().BeOnOrBefore(after.AddSeconds(seconds));
        row.Attempts.Should().Be(attempts);
        row.Token.Should().BeNull();
        row.LockedUntil.Should().BeNull();
        row.TerminalReason.Should().BeNull();
        row.ProcessedAt.Should().BeNull();
        row.LastError.Should().NotBeNullOrWhiteSpace();
    }

    private static string ReplaceEventIdentity(string metadata)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(metadata)!;
        values["event_id"] = JsonSerializer.SerializeToElement(Guid.CreateVersion7());
        return JsonSerializer.Serialize(values);
    }

    private static OutboxEventRegistry EligibleRegistry() => OutboxEventRegistry.Create(
        [OutboxEventRegistration.IntentionallyUnsubscribed(typeof(DispatchEventV1))], [],
        IntegrationEventHandlerRegistry.Discover(), [typeof(DispatchEventV1).Assembly]);

    private static OutboxEventRegistry PendingRegistry() => OutboxEventRegistry.Create(
        [OutboxEventRegistration.Required(typeof(TenantCreatedIntegrationEventV1),
        [
            OutboxSubscriptionManifest.Pending("customization.initialize-content-type-default.v1"),
            OutboxSubscriptionManifest.Pending("customization.initialize-taxonomy-default.v1"),
        ], EarlierTenantIdentity)], [], IntegrationEventHandlerRegistry.Discover(),
        [typeof(TenantCreatedIntegrationEventV1).Assembly]);

    [IntegrationEventPartition(nameof(SubjectId))]
    public sealed record DispatchEventV1 : IntegrationEventBase
    {
        public required Guid SubjectId { get; init; }
        public override string Topic => "learnstack.test.dispatch";
        public override string PartitionKey => SubjectId.ToString("D");
    }

    private sealed record Row(
        Guid Id, int Attempts, string? Token, DateTimeOffset? LockedUntil,
        DateTimeOffset AvailableAfter, DateTimeOffset? ProcessedAt,
        string? TerminalReason, DateTimeOffset? DeadLetteredAt, string? LastError,
        string Type, string Payload, string? Metadata);

    private sealed record AuditRow(
        Guid Tenant, Guid? Organization, Guid Actor, string Module, string Operation,
        string Type, string Class, string Outcome, string? Reason, string Correlation, string Metadata);

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly DisposableSchemaDatabase _database;
        private readonly NpgsqlDataSource _appSource;

        private TestDatabase(DisposableSchemaDatabase database)
        {
            _database = database;
            _appSource = NpgsqlDataSource.Create(database.AppConnectionString);
            DispatchSource = OutboxDispatcherDataSource.Build(database.OutboxConnectionString);
            Provider = SeedComposition.Build(_appSource, null, NullLoggerFactory.Instance);
        }

        public NpgsqlDataSource DispatchSource { get; }
        public ServiceProvider Provider { get; }
        public string OutboxConnectionString => _database.OutboxConnectionString;

        public static async Task<TestDatabase> CreateAsync(PostgresFixture postgres, bool seedTenant = true)
        {
            var test = new TestDatabase(await DisposableSchemaDatabase.CreateAsync(postgres));
            try
            {
                if (seedTenant) await test.SeedTenantAsync();
                return test;
            }
            catch
            {
                await test.DisposeAsync();
                throw;
            }
        }

        public OutboxDispatchStore Store(
            OutboxEventRegistry? registry = null, IClock? clock = null,
            Func<IAuditStore, IAuditStore>? decorate = null, NpgsqlDataSource? dispatchSource = null) => new(
                new Lazy<NpgsqlDataSource>(() => dispatchSource ?? DispatchSource), registry ?? EligibleRegistry(),
                new OutboxDispatchOptions(), new SystemGuidFactory(), clock ?? new FixedClock(Occurred),
                decorate is null ? Provider.GetRequiredService<IServiceScopeFactory>()
                    : new DecoratingScopeFactory(Provider.GetRequiredService<IServiceScopeFactory>(), decorate));

        public async Task<Guid> InsertAsync(
            Guid? id = null, Guid? organization = null, int attempts = 0,
            DateTimeOffset? available = null, DateTimeOffset? occurred = null, Guid? partition = null,
            string? type = null, string? payload = null, string? correlation = null,
            Guid? causation = null, Guid? actor = null, IntegrationEventBase? fact = null,
            string? metadata = null)
        {
            fact ??= new DispatchEventV1
            {
                EventId = id ?? Guid.CreateVersion7(),
                TenantId = SchemaFixture.TenantA,
                OccurredAt = occurred ?? Occurred,
                SubjectId = partition ?? Guid.CreateVersion7(),
            };
            await using var connection = await _appSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await SchemaQueries.AssertApplicationRoleAsync(connection);
            await SchemaQueries.SetTenantAsync(connection, transaction, fact.TenantId);
            if (organization is { } org)
                await SchemaQueries.SetSettingAsync(connection, transaction, "app.organization_id", org.ToString("D"));
            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO outbox_messages
                    (id, tenant_id, organization_id, occurred_at, correlation_id,
                     causation_id, actor_user_id, type, topic, partition_key,
                     payload, metadata, attempts, available_after)
                VALUES (@id, @tenant, @organization, @occurred, @correlation,
                        @causation, @actor, @type, @topic, @partition,
                        @payload, @metadata, @attempts, @available)
                """, connection, transaction);
            insert.Parameters.AddWithValue("id", fact.EventId);
            insert.Parameters.AddWithValue("tenant", fact.TenantId);
            insert.Parameters.Add(new NpgsqlParameter("organization", NpgsqlDbType.Uuid) { Value = (object?)organization ?? DBNull.Value });
            insert.Parameters.AddWithValue("occurred", fact.OccurredAt);
            insert.Parameters.AddWithValue("correlation", correlation ?? Traceparent);
            insert.Parameters.Add(new NpgsqlParameter("causation", NpgsqlDbType.Uuid) { Value = (object?)causation ?? DBNull.Value });
            insert.Parameters.Add(new NpgsqlParameter("actor", NpgsqlDbType.Uuid) { Value = (object?)actor ?? DBNull.Value });
            insert.Parameters.AddWithValue("type", type ?? fact.GetType().AssemblyQualifiedName!);
            insert.Parameters.AddWithValue("topic", fact.Topic);
            insert.Parameters.AddWithValue("partition", fact.PartitionKey);
            insert.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = payload ?? fact.ToPayloadJson() });
            insert.Parameters.Add(new NpgsqlParameter("metadata", NpgsqlDbType.Jsonb) { Value = (object?)metadata ?? DBNull.Value });
            insert.Parameters.AddWithValue("attempts", attempts);
            insert.Parameters.AddWithValue("available", available ?? Occurred);
            (await insert.ExecuteNonQueryAsync()).Should().Be(1);
            await transaction.CommitAsync();
            return fact.EventId;
        }

        public async Task<Row> ReadAsync(Guid id)
        {
            await using var connection = await DispatchSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                """
                SELECT id, attempts, locked_by, locked_until, available_after,
                       processed_at, terminal_reason, dead_lettered_at, last_error,
                       type, payload::text, metadata::text
                FROM outbox_messages WHERE id = @id
                """, connection);
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            return new Row(reader.GetGuid(0), reader.GetInt32(1), Text(reader, 2), Timestamp(reader, 3),
                reader.GetFieldValue<DateTimeOffset>(4), Timestamp(reader, 5), Text(reader, 6), Timestamp(reader, 7),
                Text(reader, 8), reader.GetString(9), reader.GetString(10), Text(reader, 11));
        }

        public async Task<Guid> SingleIdAsync()
        {
            await using var connection = await DispatchSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("SELECT id FROM outbox_messages", connection);
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            var id = reader.GetGuid(0);
            (await reader.ReadAsync()).Should().BeFalse();
            return id;
        }

        public Task ExpireAsync(Guid id) => UpdateAsync(id, "locked_until = now() - interval '1 second'");
        public Task MakeDueAsync(Guid id) => UpdateAsync(id, "available_after = now() - interval '1 second'");
        public Task SetStateAsync(Guid id, string assignment) => UpdateAsync(id, assignment);

        public async Task<DateTimeOffset> DatabaseNowAsync()
        {
            await using var connection = await DispatchSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("SELECT clock_timestamp()", connection);
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            return reader.GetFieldValue<DateTimeOffset>(0);
        }

        public async Task AuditInsertGrantAsync(bool grant)
        {
            await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.MigrationConnectionString);
            await using var command = new NpgsqlCommand(grant
                ? "GRANT INSERT ON audit_log TO learnstack_outbox_admin"
                : "REVOKE INSERT ON audit_log FROM learnstack_outbox_admin", connection);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<long> AuditCountAsync()
        {
            await using var connection = await _appSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await SchemaQueries.AssertApplicationRoleAsync(connection);
            await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);
            await SchemaQueries.SetSettingAsync(connection, transaction, "app.scope", "tenant");
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM audit_log WHERE operation = 'platform.outbox.dead_letter'", connection, transaction);
            return (long)(await command.ExecuteScalarAsync())!;
        }

        public async Task<AuditRow> ReadAuditAsync()
        {
            await using var connection = await _appSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await SchemaQueries.AssertApplicationRoleAsync(connection);
            await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);
            await SchemaQueries.SetSettingAsync(connection, transaction, "app.organization_id", SchemaFixture.OrgA1.ToString("D"));
            await using var command = new NpgsqlCommand(
                """
                SELECT tenant_id, organization_id, actor_user_id, module, operation,
                       operation_type, operation_class, outcome, reason, correlation_id, metadata::text
                FROM audit_log WHERE operation = 'platform.outbox.dead_letter'
                """, connection, transaction);
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            var row = new AuditRow(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.GetGuid(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                Text(reader, 8), reader.GetString(9), reader.GetString(10));
            (await reader.ReadAsync()).Should().BeFalse();
            return row;
        }

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            await DispatchSource.DisposeAsync();
            await _appSource.DisposeAsync();
            await _database.DisposeAsync();
        }

        private async Task UpdateAsync(Guid id, string assignment)
        {
            // Assignments are closed test-owned statements; values remain parameters.
            await using var connection = await DispatchSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand($"UPDATE outbox_messages SET {assignment} WHERE id = @id", connection);
            command.Parameters.AddWithValue("id", id);
            (await command.ExecuteNonQueryAsync()).Should().Be(1);
        }

        private async Task SeedTenantAsync()
        {
            await using var connection = await _appSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await SchemaQueries.AssertApplicationRoleAsync(connection);
            await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);
            await SchemaQueries.ExecuteAsync(connection, transaction,
                """
                INSERT INTO tenants (id, slug, display_name, status, created_at, created_by, row_version)
                VALUES (@tenant, 'dispatch-probe', 'Dispatch probe', 'Trial', now(), @actor, 0);
                INSERT INTO organizations (id, tenant_id, slug, display_name, status, created_at, created_by, row_version)
                VALUES (@organization, @tenant, 'hq', 'HQ', 'Active', now(), @actor, 0);
                """, ("tenant", SchemaFixture.TenantA), ("organization", SchemaFixture.OrgA1), ("actor", UserId.SystemActor.Value));
            await transaction.CommitAsync();
        }

        private static string? Text(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        private static DateTimeOffset? Timestamp(NpgsqlDataReader reader, int ordinal) =>
            reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);
    }

    private sealed class DecoratingScopeFactory(IServiceScopeFactory inner, Func<IAuditStore, IAuditStore> decorate) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new DecoratingScope(inner.CreateScope(), decorate);
    }

    private sealed class DecoratingScope : IServiceScope, IAsyncDisposable
    {
        private readonly IServiceScope _inner;

        public DecoratingScope(IServiceScope inner, Func<IAuditStore, IAuditStore> decorate)
        {
            _inner = inner;
            ServiceProvider = new DecoratingProvider(inner.ServiceProvider, decorate);
        }

        public IServiceProvider ServiceProvider { get; }
        public void Dispose() => _inner.Dispose();
        public ValueTask DisposeAsync() => _inner is IAsyncDisposable asyncDisposable
            ? asyncDisposable.DisposeAsync() : DisposeSynchronously();

        private ValueTask DisposeSynchronously()
        {
            _inner.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DecoratingProvider(IServiceProvider inner, Func<IAuditStore, IAuditStore> decorate) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IAuditStore)
            ? decorate(inner.GetRequiredService<IAuditStore>()) : inner.GetService(serviceType);
    }

    private sealed class TerminalAuditDecorator(
        IAuditStore inner,
        Func<AuditEntryDraft, EventTerminalTransition, DbConnection, DbTransaction, CancellationToken, Task> terminal) : IAuditStore
    {
        public Task WritePendingAsync(IUnitOfWork unitOfWork, CancellationToken cancellationToken = default) =>
            inner.WritePendingAsync(unitOfWork, cancellationToken);
        public Task WriteStandaloneAsync(AuditEntryDraft entry, CancellationToken cancellationToken = default) =>
            inner.WriteStandaloneAsync(entry, cancellationToken);
        public Task WriteBestEffortAsync(AuditEntryDraft entry, CancellationToken cancellationToken = default) =>
            inner.WriteBestEffortAsync(entry, cancellationToken);
        public Task WritePlatformScopeAsync(AuditEntryDraft entry, DbConnection connection, DbTransaction transaction,
            CancellationToken cancellationToken = default) => inner.WritePlatformScopeAsync(entry, connection, transaction, cancellationToken);
        public Task WriteEventTerminalAsync(AuditEntryDraft entry, EventTerminalTransition transition, DbConnection connection,
            DbTransaction transaction, CancellationToken cancellationToken = default) =>
            terminal(entry, transition, connection, transaction, cancellationToken);
    }
}
