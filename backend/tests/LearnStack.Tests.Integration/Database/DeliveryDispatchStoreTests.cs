using System.Data.Common;
using System.Text.Json.Nodes;
using FluentAssertions;
using LearnStack.Infrastructure.Messaging;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Messaging;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Time;
using LearnStack.Tools.Seeder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class DeliveryDispatchStoreTests(SchemaFixture schema)
{
    private const string First = "test.first.v1";
    private const string Second = "test.second.v1";
    private const string Pending = "test.pending.v1";
    private const string Privileged = "test.privileged.v1";
    private const string Alias = "declared.previous.delivery";
    private const string Trace = "00-11111111111111111111111111111111-2222222222222222-01";
    private const string AttemptTrace = "00-33333333333333333333333333333333-4444444444444444-01";
    private static readonly string TypeIdentity = typeof(DeliveryEvent).AssemblyQualifiedName!;
    private static readonly DateTimeOffset Occurred = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] NoBlocked = [];

    [Fact]
    public async Task Two_stores_acquire_one_tuple_once_and_siblings_spend_independent_budgets()
    {
        await using var test = await TestDatabase.CreateAsync(schema.Postgres);
        var id = await test.InsertAsync();
        var claims = await Task.WhenAll(test.Store().TryClaimAsync(NoBlocked), test.Store().TryClaimAsync(NoBlocked));
        var first = claims.OfType<DeliveryClaim>().Should().ContainSingle().Which;
        first.EventId.Should().Be(id);
        first.SubscriptionId.Should().Be(First);
        first.Attempts.Should().Be(1);
        first.Token.Should().Be(Guid.ParseExact(first.Token, "D").ToString("D"));
        (await test.Store().TryClaimAsync(NoBlocked)).Should().BeNull();
        await test.InsertAsync(id, Second);
        var sibling = (await test.Store().TryClaimAsync(NoBlocked))!;
        sibling.SubscriptionId.Should().Be(Second);
        sibling.Attempts.Should().Be(1);
        (await test.Store().FailAsync(first, DeliveryFailureReason.ExecutionFailed)).Should().BeTrue();
        (await test.ReadAsync(id, Second)).Token.Should().Be(sibling.Token);
        (await test.ReadAsync(id, Second)).Attempts.Should().Be(1);
        (await test.Store().FailAsync(first with { SubscriptionId = Second }, DeliveryFailureReason.ExecutionFailed))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Normal_unknown_commit_and_expired_recovery_have_distinct_stored_diagnostics()
    {
        await using var test = await TestDatabase.CreateAsync(schema.Postgres);
        var id = await test.InsertAsync();
        var store = test.Store();
        var claim = (await store.TryClaimAsync(NoBlocked))!;

        (await store.FailAsync(claim, DeliveryFailureReason.UnknownOutcome)).Should().BeTrue();
        (await test.ReadAsync(id)).Error.Should().Be("execute:unknown:commit");

        await test.DueAsync(id);
        (await store.TryClaimAsync(NoBlocked)).Should().NotBeNull();
        await test.ExpireAsync(id);
        var recovery = (await store.TryAcquireRecoveryAsync(NoBlocked))!;
        (await store.FailAsync(recovery, DeliveryFailureReason.UnknownOutcome)).Should().BeTrue();
        (await test.ReadAsync(id)).Error.Should().Be("recovery:unknown:expired");
    }

    [Fact]
    public async Task Normal_claim_cannot_terminalize_without_its_separate_acquisition()
    {
        await using var test = await TestDatabase.CreateAsync(schema.Postgres);
        var id = await test.InsertAsync();
        var store = test.Store();
        var claim = (await store.TryClaimAsync(NoBlocked))!;
        var before = await test.ReadAsync(id);

        var rejected = () => store.TryTerminalizeAsync(claim, AttemptTrace);
        await rejected.Should().ThrowAsync<ArgumentException>();

        (await test.ReadAsync(id)).Should().Be(before);
        (await test.AuditCountAsync()).Should().Be(0);
        (await store.RequestTerminalAsync(claim, DeliveryTerminalReason.InvalidEnvelope)).Should().BeTrue();
        var terminal = (await store.TryClaimTerminalAsync(NoBlocked))!;
        (await store.TryTerminalizeAsync(terminal, AttemptTrace)).Should().BeTrue();
    }

    [Fact]
    public async Task Terminal_claim_cannot_spend_an_execution_retry()
    {
        await using var test = await TestDatabase.CreateAsync(schema.Postgres);
        var id = await test.InsertAsync();
        var store = test.Store();
        var claim = (await store.TryClaimAsync(NoBlocked))!;
        (await store.RequestTerminalAsync(claim, DeliveryTerminalReason.InvalidEnvelope)).Should().BeTrue();
        var terminal = (await store.TryClaimTerminalAsync(NoBlocked))!;
        var before = await test.ReadAsync(id);

        var rejected = () => store.FailAsync(terminal, DeliveryFailureReason.ExecutionFailed);
        await rejected.Should().ThrowAsync<ArgumentException>();

        (await test.ReadAsync(id)).Should().Be(before);
        (await test.AuditCountAsync()).Should().Be(0);
        (await store.TryTerminalizeAsync(terminal, AttemptTrace)).Should().BeTrue();
    }

    [Fact]
    public async Task Actual_claims_follow_all_five_index_order_keys()
    {
        await using var test = await TestDatabase.CreateAsync(schema.Postgres);
        var firstId = Guid.Parse("11111111-1111-7111-8111-111111111111");
        var laterId = Guid.Parse("22222222-2222-7222-8222-222222222222");
        await test.InsertAsync(firstId, Second);
        await test.InsertAsync(firstId, First, tenant: SchemaFixture.TenantB);
        await test.InsertAsync(firstId, First);
        await test.InsertAsync(laterId, First);
        var laterOccurrence = await test.InsertAsync(occurred: Occurred.AddHours(1));
        var laterAvailability = await test.InsertAsync();
        await test.SetOrderingAsync(laterAvailability);
        var store = test.Store();
        var claims = new List<DeliveryClaim>();
        while (await store.TryClaimAsync(NoBlocked) is { } claim) claims.Add(claim);
        claims.Select(claim => (claim.EventId, claim.TenantId, claim.SubscriptionId)).Should().Equal(
            (firstId, SchemaFixture.TenantA, First), (firstId, SchemaFixture.TenantA, Second),
            (firstId, SchemaFixture.TenantB, First), (laterId, SchemaFixture.TenantA, First),
            (laterOccurrence, SchemaFixture.TenantA, First), (laterAvailability, SchemaFixture.TenantA, First));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 5)]
    [InlineData(3, 30)]
    [InlineData(4, 300)]
    [InlineData(5, 3600)]
    public async Task Normal_failure_uses_each_database_delay_and_clears_ownership(int attempt, int seconds)
    {
        await using var test = await TestDatabase.CreateAsync(schema.Postgres);
        var id = await test.InsertAsync();
        await test.UpdateAsync(id, First, $"attempts = {attempt - 1}");
        var store = test.Store();
        var claim = (await store.TryClaimAsync(NoBlocked))!;
        var before = await test.NowAsync();
        (await store.FailAsync(claim, DeliveryFailureReason.ExecutionFailed)).Should().BeTrue();
        var after = await test.NowAsync();
        var row = await test.ReadAsync(id);
        row.Available.Should().BeOnOrAfter(before.AddSeconds(seconds)).And.BeOnOrBefore(after.AddSeconds(seconds));
        row.Token.Should().BeNull();
        row.LockedUntil.Should().BeNull();
        row.Attempts.Should().Be(attempt);
        row.Error.Should().Be("execute:failed:handler");
    }

    [Fact]
    public async Task Pair_specific_holds_preserve_pending_and_privileged_but_allow_active_pending_sibling_and_unknown_pair()
    {
        await using var test = await TestDatabase.CreateAsync(schema.Postgres);
        var pending = await test.InsertAsync(subscription: Pending, type: Alias);
        var privileged = await test.InsertAsync(subscription: Privileged);
        var active = await test.InsertAsync();
        var unknown = await test.InsertAsync(subscription: Pending, type: "private.unknown.type");
        var blocked = await test.InsertAsync(partition: "local-block");
        var store = test.Store();
        var snapshot = await store.ReadSnapshotAsync(["local-block"]);
        snapshot.PendingCount.Should().Be(5);
        snapshot.DeferredCount.Should().Be(2);
        var claimed = new[] { (await store.TryClaimAsync(["local-block"]))!, (await store.TryClaimAsync(["local-block"]))! };
        claimed.Select(c => c.EventId).Should().BeEquivalentTo([active, unknown]);
        (await store.TryClaimAsync(["local-block"])).Should().BeNull();
        (await test.ReadAsync(pending, Pending)).Attempts.Should().Be(0);
        (await test.ReadAsync(privileged, Privileged)).Attempts.Should().Be(0);
        (await store.TryClaimAsync(NoBlocked))!.EventId.Should().Be(blocked);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 5)]
    [InlineData(3, 30)]
    [InlineData(4, 300)]
    [InlineData(5, 3600)]
    public async Task Recovery_preserves_attempt_budget_and_sets_database_time_backoff(int attempt, int delay)
    {
        await using var test = await TestDatabase.CreateAsync(schema.Postgres);
        var id = await test.InsertAsync();
        await test.UpdateAsync(id, First, $"attempts = {attempt - 1}");
        var store = test.Store();
        var stale = (await store.TryClaimAsync(NoBlocked))!;
        await test.ExpireAsync(id);
        (await store.FailAsync(stale, DeliveryFailureReason.ExecutionFailed)).Should().BeFalse();
        (await store.RequestTerminalAsync(stale, DeliveryTerminalReason.InvalidEnvelope)).Should().BeFalse();
        (await store.TryClaimAsync(NoBlocked)).Should().BeNull();
        var recovery = (await store.TryAcquireRecoveryAsync(NoBlocked))!;
        recovery.Kind.Should().Be(DeliveryClaimKind.Recovery);
        recovery.Token.Should().NotBe(stale.Token);
        recovery.Attempts.Should().Be(attempt);
        (await store.FailAsync(stale, DeliveryFailureReason.ExecutionFailed)).Should().BeFalse();
        var before = await test.NowAsync();
        (await store.FailAsync(recovery, DeliveryFailureReason.UnknownOutcome)).Should().BeTrue();
        var after = await test.NowAsync();
        var row = await test.ReadAsync(id);
        row.Available.Should().BeOnOrAfter(before.AddSeconds(delay)).And.BeOnOrBefore(after.AddSeconds(delay));
        row.Attempts.Should().Be(attempt);
        row.Token.Should().BeNull();
        row.TerminalReason.Should().BeNull();
        row.Error.Should().Be("recovery:unknown:expired");
        (await store.TryClaimAsync(NoBlocked)).Should().BeNull();
        await test.DueAsync(id);
        (await store.TryClaimAsync(NoBlocked))!.Attempts.Should().Be(attempt + 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sixth_failure_or_recovery_is_terminal_only_and_never_spends_a_seventh_claim(bool recovery)
    {
        await using var test = await TestDatabase.CreateAsync(schema.Postgres);
        var id = await test.InsertAsync();
        await test.UpdateAsync(id, First, "attempts = 5");
        var store = test.Store();
        var claim = (await store.TryClaimAsync(NoBlocked))!;
        claim.Attempts.Should().Be(6);
        if (recovery)
        {
            await test.ExpireAsync(id);
            claim = (await store.TryAcquireRecoveryAsync(NoBlocked))!;
        }
        (await store.FailAsync(claim, recovery ? DeliveryFailureReason.UnknownOutcome : DeliveryFailureReason.ExecutionTimeout))
            .Should().BeTrue();
        (await store.TryClaimAsync(NoBlocked)).Should().BeNull();
        (await store.TryAcquireRecoveryAsync(NoBlocked)).Should().BeNull();
        var terminal = (await store.TryClaimTerminalAsync(NoBlocked))!;
        terminal.Attempts.Should().Be(6);
        (await store.TryTerminalizeAsync(terminal, AttemptTrace)).Should().BeTrue();
        (await test.ReadAsync(id)).TerminalReason.Should().Be("retry_exhausted");
        (await test.AuditCountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Completed_sixth_attempt_with_inert_lease_is_excluded_from_recovery_without_inbox_access()
    {
        await using var test = await TestDatabase.CreateAsync(schema.Postgres);
        var id = await test.InsertAsync();
        await test.UpdateAsync(id, First, "attempts = 5");
        var store = test.Store();
        var claim = (await store.TryClaimAsync(NoBlocked))!;
        await test.CompleteAsync(id, First, inbox: true);
        await test.ExpireAsync(id);
        (await store.TryAcquireRecoveryAsync(NoBlocked)).Should().BeNull();
        (await store.TryClaimAsync(NoBlocked)).Should().BeNull();
        (await store.TryClaimTerminalAsync(NoBlocked)).Should().BeNull();
        (await store.FailAsync(claim, DeliveryFailureReason.UnknownOutcome)).Should().BeFalse();
        (await store.ReplayAsync(SchemaFixture.TenantA, First, id)).Should().BeFalse();
        (await test.InboxCountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("request")]
    [InlineData("terminal")]
    public async Task Unchanged_row_lock_wait_cannot_use_pre_wait_lease_time(string action)
    {
        await using var test = await TestDatabase.CreateAsync(schema.Postgres);
        var id = await test.InsertAsync();
        var store = test.Store();
        var claim = (await store.TryClaimAsync(NoBlocked))!;
        if (action == "terminal")
        {
            (await store.RequestTerminalAsync(claim, DeliveryTerminalReason.InvalidEnvelope)).Should().BeTrue();
            claim = (await store.TryClaimTerminalAsync(NoBlocked))!;
        }
        await test.UpdateAsync(id, First, "locked_until = clock_timestamp() + interval '1 second'");
        await using var connection = await test.Dispatch.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var rowLock = new NpgsqlCommand("SELECT event_id FROM event_deliveries WHERE event_id = @id FOR UPDATE", connection, transaction);
        rowLock.Parameters.AddWithValue("id", id);
        await rowLock.ExecuteScalarAsync();
        var transition = action switch
        {
            "failure" => store.FailAsync(claim, DeliveryFailureReason.ExecutionFailed),
            "request" => store.RequestTerminalAsync(claim, DeliveryTerminalReason.InvalidEnvelope),
            _ => store.TryTerminalizeAsync(claim, AttemptTrace),
        };
        await test.WaitForLockWaitAsync();
        using var expiryDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while ((await test.ReadAsync(id, cancellationToken: expiryDeadline.Token)).LockedUntil
            > await test.NowAsync(expiryDeadline.Token))
        {
            await Task.Delay(20, expiryDeadline.Token);
        }
        await transaction.CommitAsync();
        (await transition).Should().BeFalse();
        (await test.ReadAsync(id)).Token.Should().Be(claim.Token);
        (await test.AuditCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Audit_refusal_rolls_back_transition_then_reacquires_terminal_only_with_actual_metadata()
    {
        await using var test = await TestDatabase.CreateAsync(schema.Postgres);
        var id = await test.InsertAsync(organization: SchemaFixture.OrgA1);
        var store = test.Store();
        var terminal = await TestDatabase.RequestTerminalAsync(store);
        var before = await test.ReadAsync(id);
        await test.AuditGrantAsync(false);
        var attempt = () => store.TryTerminalizeAsync(terminal, AttemptTrace);
        await attempt.Should().ThrowAsync<AuditWriteFailedException>();
        (await test.ReadAsync(id)).Should().Be(before);
        (await test.AuditCountAsync()).Should().Be(0);
        await test.AuditGrantAsync(true);
        await test.ExpireAsync(id);
        (await store.TryClaimAsync(NoBlocked)).Should().BeNull();
        var fresh = (await store.TryClaimTerminalAsync(NoBlocked))!;
        fresh.Token.Should().NotBe(terminal.Token);
        (await store.TryTerminalizeAsync(terminal, AttemptTrace)).Should().BeFalse();
        (await store.TryTerminalizeAsync(fresh, AttemptTrace)).Should().BeTrue();
        var audit = await test.ReadAuditAsync();
        audit.Tenant.Should().Be(SchemaFixture.TenantA);
        audit.Organization.Should().Be(SchemaFixture.OrgA1);
        audit.Actor.Should().Be(UserId.SystemActor.Value);
        audit.Correlation.Should().Be(AttemptTrace);
        audit.Operation.Should().Be("platform.delivery.dead_letter");
        audit.Class.Should().Be("Must");
        audit.Type.Should().Be("SecurityEvent");
        var metadata = JsonNode.Parse(audit.Metadata)!.AsObject();
        metadata.Select(pair => pair.Key).Should().BeEquivalentTo(["event_id", "subscription_id", "reason", "attempts"]);
        metadata["event_id"]!.GetValue<Guid>().Should().Be(id);
        metadata["subscription_id"]!.GetValue<string>().Should().Be(First);
        (await store.TryTerminalizeAsync(fresh, AttemptTrace)).Should().BeFalse();
        (await test.AuditCountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("organization")]
    [InlineData("event")]
    [InlineData("subscription")]
    [InlineData("operation")]
    [InlineData("class")]
    [InlineData("type")]
    [InlineData("actor")]
    [InlineData("trace")]
    [InlineData("transaction")]
    public async Task Substitution_against_actual_subscriber_witness_rolls_back_both_writes(string substitution)
    {
        await using var test = await TestDatabase.CreateAsync(schema.Postgres);
        var id = await test.InsertAsync(organization: SchemaFixture.OrgA1);
        var calls = 0;
        var store = test.Store(decorate: inner => new AuditDecorator(inner, async (draft, witness, connection, transaction, token) =>
        {
            calls++;
            witness.Kind.Should().Be("subscriber");
            witness.SubscriptionId.Should().Be(First);
            witness.EventId.Should().Be(id);
            await using var truth = new NpgsqlCommand("SELECT dead_lettered_at IS NOT NULL FROM event_deliveries WHERE event_id = @id",
                (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
            truth.Parameters.AddWithValue("id", id);
            (await truth.ExecuteScalarAsync(token)).Should().Be(true);
            if (substitution == "transaction")
            {
                await using var other = await test.Dispatch.OpenConnectionAsync(token);
                await using var otherTransaction = await other.BeginTransactionAsync(token);
                await inner.WriteEventTerminalAsync(draft, witness, other, otherTransaction, token);
                return;
            }
            var metadata = JsonNode.Parse(draft.Metadata!)!.AsObject();
            if (substitution == "event") metadata["event_id"] = Guid.NewGuid();
            if (substitution == "subscription") metadata["subscription_id"] = Second;
            var changed = substitution switch
            {
                "tenant" => draft with { TenantId = TenantId.From(SchemaFixture.TenantB) },
                "organization" => draft with { OrganizationId = OrganizationId.From(SchemaFixture.OrgA2) },
                "operation" => draft with { Operation = "platform.outbox.dead_letter" },
                "class" => draft with { OperationClass = OperationClass.Should },
                "type" => draft with { OperationType = OperationType.Update },
                "actor" => draft with { ActorUserId = UserId.From(Guid.CreateVersion7()) },
                "trace" => draft with { CorrelationId = Trace },
                _ => draft with { Metadata = metadata.ToJsonString() },
            };
            await inner.WriteEventTerminalAsync(changed, witness, connection, transaction, token);
        }));
        var terminal = await TestDatabase.RequestTerminalAsync(store);
        var before = await test.ReadAsync(id);
        var attempt = () => store.TryTerminalizeAsync(terminal, AttemptTrace);
        await attempt.Should().ThrowAsync<ArgumentException>();
        calls.Should().Be(1);
        (await test.ReadAsync(id)).Should().Be(before);
        (await test.AuditCountAsync()).Should().Be(0);
        (await test.Store().TryTerminalizeAsync(terminal, AttemptTrace)).Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lost_terminal_commit_ack_reconciles_once_or_preserves_both_failures(bool rejectProbe)
    {
        await using var test = await TestDatabase.CreateAsync(schema.Postgres);
        var id = await test.InsertAsync();
        var terminal = await TestDatabase.RequestTerminalAsync(test.Store());
        await using var proxy = new CommitResponseDroppingProxy(test.OutboxConnectionString, rejectReconciliationConnections: rejectProbe);
        await using var source = OutboxDispatcherDataSource.Build(proxy.ConnectionString);
        var store = test.Store(source: source);
        if (rejectProbe)
        {
            var attempt = () => store.TryTerminalizeAsync(terminal, AttemptTrace);
            var error = (await attempt.Should().ThrowAsync<AggregateException>()).Which;
            error.InnerExceptions.Should().HaveCount(2);
            error.InnerExceptions[0].StackTrace.Should().Contain("NpgsqlTransaction.Commit");
            error.InnerExceptions[1].StackTrace.Should().Contain("IsDurablyTerminalAsync");
        }
        else (await store.TryTerminalizeAsync(terminal, AttemptTrace)).Should().BeTrue();
        proxy.DroppedCommitResponses.Should().Be(1);
        proxy.AcceptedConnections.Should().BeGreaterThanOrEqualTo(2);
        (await test.ReadAsync(id)).DeadLettered.Should().NotBeNull();
        (await test.AuditCountAsync()).Should().Be(1);
        (await test.Store().TryTerminalizeAsync(terminal, AttemptTrace)).Should().BeFalse();
    }

    [Fact]
    public async Task Replay_resets_only_exact_active_terminal_tuple_and_keeps_completed_sibling_inbox_and_stale_fences()
    {
        await using var test = await TestDatabase.CreateAsync(schema.Postgres);
        var id = await test.InsertAsync();
        await test.InsertAsync(id, Second);
        var store = test.Store();
        var terminal = await TestDatabase.RequestTerminalAsync(store);
        // Mark only sibling completed through the actual app completion grant.
        await test.CompleteAsync(id, Second, inbox: true);
        var sibling = await test.ReadAsync(id, Second);
        (await store.TryTerminalizeAsync(terminal, AttemptTrace)).Should().BeTrue();
        (await store.ReplayAsync(SchemaFixture.TenantB, First, id)).Should().BeFalse();
        (await store.ReplayAsync(SchemaFixture.TenantA, Second, id)).Should().BeFalse();
        (await store.ReplayAsync(SchemaFixture.TenantA, First, id)).Should().BeTrue();
        var reset = await test.ReadAsync(id);
        reset.Attempts.Should().Be(0);
        reset.Error.Should().BeNull();
        reset.TerminalReason.Should().BeNull();
        reset.DeadLettered.Should().BeNull();
        reset.ReplayCount.Should().Be(1);
        reset.Replayed.Should().NotBeNull();
        reset.Token.Should().BeNull();
        (await test.ReadAsync(id, Second)).Should().Be(sibling);
        (await test.InboxCountAsync()).Should().Be(1);
        (await store.TryTerminalizeAsync(terminal, AttemptTrace)).Should().BeFalse();
        var fresh = (await store.TryClaimAsync(NoBlocked))!;
        fresh.SubscriptionId.Should().Be(First);
        fresh.Token.Should().NotBe(terminal.Token);
        fresh.Attempts.Should().Be(1);
        (await store.ReplayAsync(SchemaFixture.TenantA, First, id)).Should().BeFalse();
    }

    [Fact]
    public async Task Replay_preserves_other_terminal_rows_differing_in_each_tuple_component()
    {
        await using var test = await TestDatabase.CreateAsync(schema.Postgres);
        var id = await test.InsertAsync();
        await test.InsertAsync(id, Second);
        await test.InsertAsync(id, tenant: SchemaFixture.TenantB);
        var unrelatedId = await test.InsertAsync();
        var store = test.Store();
        for (var index = 0; index < 4; index++)
        {
            var terminal = await TestDatabase.RequestTerminalAsync(store);
            (await store.TryTerminalizeAsync(terminal, AttemptTrace)).Should().BeTrue();
        }
        (await store.TryClaimAsync(NoBlocked)).Should().BeNull();
        var target = await test.ReadAsync(id);
        var sibling = await test.ReadAsync(id, Second);
        var foreign = await test.ReadAsync(id, tenant: SchemaFixture.TenantB);
        var unrelated = await test.ReadAsync(unrelatedId);
        foreach (var row in new[] { target, sibling, foreign, unrelated })
            row.DeadLettered.Should().NotBeNull("every control must itself satisfy the terminal replay state predicate");

        (await store.ReplayAsync(SchemaFixture.TenantA, First, id)).Should().BeTrue();

        var reset = await test.ReadAsync(id);
        reset.Attempts.Should().Be(0);
        reset.TerminalReason.Should().BeNull();
        reset.DeadLettered.Should().BeNull();
        reset.Error.Should().BeNull();
        reset.Token.Should().BeNull();
        reset.LockedUntil.Should().BeNull();
        reset.ReplayCount.Should().Be(target.ReplayCount + 1);
        reset.Replayed.Should().NotBeNull();
        (await test.ReadAsync(id, Second)).Should().Be(sibling);
        (await test.ReadAsync(id, tenant: SchemaFixture.TenantB)).Should().Be(foreign);
        (await test.ReadAsync(unrelatedId)).Should().Be(unrelated);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("privileged")]
    [InlineData("unknown")]
    [InlineData("retired")]
    [InlineData("requested")]
    [InlineData("live")]
    public async Task Replay_refuses_nonadmitted_or_unfinished_state(string state)
    {
        await using var test = await TestDatabase.CreateAsync(schema.Postgres);
        var subscription = state == "pending" ? Pending : state == "privileged" ? Privileged : First;
        var type = state is "unknown" or "retired" ? state : TypeIdentity;
        var id = await test.InsertAsync(subscription: subscription, type: type);
        await test.UpdateAsync(id, subscription, state switch
        {
            "live" => "locked_by = '11111111-1111-4111-8111-111111111111', locked_until = clock_timestamp() + interval '1 hour'",
            "requested" => "terminal_reason = 'invalid_envelope'",
            _ => "terminal_reason = 'invalid_envelope', dead_lettered_at = clock_timestamp()",
        });
        var before = await test.ReadAsync(id, subscription);
        (await test.Store().ReplayAsync(SchemaFixture.TenantA, subscription, id)).Should().BeFalse();
        (await test.ReadAsync(id, subscription)).Should().Be(before);
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("correlation_id")]
    [InlineData("causation_id")]
    [InlineData("actor_user_id")]
    [InlineData("inbox")]
    public async Task Real_scheduler_role_cannot_read_payload_causality_or_inbox(string column)
    {
        await using var test = await TestDatabase.CreateAsync(schema.Postgres);
        await test.InsertAsync();
        await using var connection = await test.Dispatch.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(column == "inbox" ? "SELECT event_id FROM inbox_messages"
            : $"SELECT {column} FROM event_deliveries", connection);
        var read = () => command.ExecuteScalarAsync();
        (await read.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        (await test.Store().TryClaimAsync(NoBlocked)).Should().NotBeNull();
    }

    private static OutboxEventRegistry Registry()
    {
        var handlers = IntegrationEventHandlerRegistry.FromServiceDescriptors([
            ServiceDescriptor.Scoped<IIntegrationEventHandler<DeliveryEvent>, FirstHandler>(),
            ServiceDescriptor.Scoped<IIntegrationEventHandler<DeliveryEvent>, SecondHandler>(),
            ServiceDescriptor.Scoped<IIntegrationEventHandler<DeliveryEvent>, PrivilegedHandler>(),
        ]);
        return OutboxEventRegistry.Create([OutboxEventRegistration.Required(typeof(DeliveryEvent), [
            OutboxSubscriptionManifest.Active(First, typeof(FirstHandler)),
            OutboxSubscriptionManifest.Active(Second, typeof(SecondHandler)),
            OutboxSubscriptionManifest.Pending(Pending),
            OutboxSubscriptionManifest.Active(Privileged, typeof(PrivilegedHandler), OutboxSubscriptionExecutionScope.PrivilegedPlatform),
        ], Alias)], ["retired"], handlers, [typeof(DeliveryEvent).Assembly]);
    }

    [IntegrationEventPartition(nameof(TenantId), tenantWide: true)]
    public sealed record DeliveryEvent : IntegrationEventBase
    {
        public override string Topic => "learnstack.test.delivery";
        public override string PartitionKey => TenantId.ToString("D");
    }
    public sealed class FirstHandler : IIntegrationEventHandler<DeliveryEvent>
    {
        public Task HandleAsync(DeliveryEvent fact, CancellationToken cancellationToken) => Task.CompletedTask;
    }
    public sealed class SecondHandler : IIntegrationEventHandler<DeliveryEvent>
    {
        public Task HandleAsync(DeliveryEvent fact, CancellationToken cancellationToken) => Task.CompletedTask;
    }
    public sealed class PrivilegedHandler : IIntegrationEventHandler<DeliveryEvent>
    {
        public Task HandleAsync(DeliveryEvent fact, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed record Row(int Attempts, string? Token, DateTimeOffset? LockedUntil, DateTimeOffset Available,
        DateTimeOffset? Processed, string? TerminalReason, DateTimeOffset? DeadLettered, string? Error,
        int ReplayCount, DateTimeOffset? Replayed);
    private sealed record AuditRow(Guid Tenant, Guid? Organization, Guid Actor, string Operation,
        string Type, string Class, string Correlation, string Metadata);

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly DisposableSchemaDatabase _database;
        private readonly NpgsqlDataSource _app;
        private readonly ServiceProvider _provider;
        public NpgsqlDataSource Dispatch { get; }
        public string OutboxConnectionString => _database.OutboxConnectionString;

        private TestDatabase(DisposableSchemaDatabase database)
        {
            _database = database;
            _app = NpgsqlDataSource.Create(database.AppConnectionString);
            Dispatch = OutboxDispatcherDataSource.Build(database.OutboxConnectionString);
            _provider = SeedComposition.Build(_app, null, NullLoggerFactory.Instance);
        }

        public static async Task<TestDatabase> CreateAsync(PostgresFixture postgres)
        {
            var test = new TestDatabase(await DisposableSchemaDatabase.CreateAsync(postgres));
            try
            {
                await using var connection = await test._app.OpenConnectionAsync();
                await using var transaction = await connection.BeginTransactionAsync();
                await SchemaQueries.AssertApplicationRoleAsync(connection);
                await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);
                await SchemaQueries.ExecuteAsync(connection, transaction, """
                    INSERT INTO tenants (id, slug, display_name, status, created_at, created_by, row_version)
                    VALUES (@tenant, 'delivery-store', 'Delivery store', 'Trial', now(), @actor, 0);
                    INSERT INTO organizations (id, tenant_id, slug, display_name, status, created_at, created_by, row_version)
                    VALUES (@organization, @tenant, 'hq', 'HQ', 'Active', now(), @actor, 0);
                    """, ("tenant", SchemaFixture.TenantA), ("organization", SchemaFixture.OrgA1), ("actor", UserId.SystemActor.Value));
                await transaction.CommitAsync();
                return test;
            }
            catch { await test.DisposeAsync(); throw; }
        }

        public DeliveryDispatchStore Store(NpgsqlDataSource? source = null, Func<IAuditStore, IAuditStore>? decorate = null) =>
            new(new Lazy<NpgsqlDataSource>(() => source ?? Dispatch), Registry(), new DeliveryDispatchOptions(),
                new SystemGuidFactory(), new FixedClock(Occurred), decorate is null
                    ? _provider.GetRequiredService<IServiceScopeFactory>()
                    : new DecoratingScopes(_provider.GetRequiredService<IServiceScopeFactory>(), decorate));

        public async Task<Guid> InsertAsync(Guid? id = null, string subscription = First, string? type = null,
            Guid? organization = null, string? partition = null, Guid? tenant = null, DateTimeOffset? occurred = null)
        {
            var fact = new DeliveryEvent { EventId = id ?? Guid.CreateVersion7(), TenantId = tenant ?? SchemaFixture.TenantA, OccurredAt = occurred ?? Occurred };
            await using var connection = await _app.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await SchemaQueries.AssertApplicationRoleAsync(connection);
            await SchemaQueries.SetTenantAsync(connection, transaction, fact.TenantId);
            if (organization is { } org) await SchemaQueries.SetSettingAsync(connection, transaction, "app.organization_id", org.ToString("D"));
            await using var command = new NpgsqlCommand("""
                INSERT INTO event_deliveries (tenant_id, subscription_id, event_id, organization_id, occurred_at,
                    correlation_id, type, topic, partition_key, payload)
                VALUES (@tenant, @subscription, @event, @organization, @occurred, @trace, @type, @topic, @partition, @payload)
                """, connection, transaction);
            Bind(command, fact.EventId, subscription, fact.TenantId);
            command.Parameters.AddWithValue("organization", NpgsqlDbType.Uuid, (object?)organization ?? DBNull.Value);
            command.Parameters.AddWithValue("occurred", fact.OccurredAt);
            command.Parameters.AddWithValue("trace", Trace);
            command.Parameters.AddWithValue("type", type ?? TypeIdentity);
            command.Parameters.AddWithValue("topic", fact.Topic);
            command.Parameters.AddWithValue("partition", partition ?? fact.PartitionKey);
            command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, fact.ToPayloadJson());
            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
            return fact.EventId;
        }

        public async Task<Row> ReadAsync(Guid id, string subscription = First, Guid? tenant = null,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await Dispatch.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand("""
                SELECT attempts, locked_by, locked_until, available_after, processed_at, terminal_reason,
                    dead_lettered_at, last_error, replay_count, replayed_at
                FROM event_deliveries WHERE tenant_id = @tenant AND subscription_id = @subscription AND event_id = @event
                """, connection);
            Bind(command, id, subscription, tenant);
            await using var row = await command.ExecuteReaderAsync(cancellationToken);
            (await row.ReadAsync(cancellationToken)).Should().BeTrue();
            return new(row.GetInt32(0), Text(row, 1), Time(row, 2), row.GetFieldValue<DateTimeOffset>(3), Time(row, 4),
                Text(row, 5), Time(row, 6), Text(row, 7), row.GetInt32(8), Time(row, 9));
        }

        public async Task UpdateAsync(Guid id, string subscription, string assignments)
        {
            await using var connection = await Dispatch.OpenConnectionAsync();
            await using var command = new NpgsqlCommand($"UPDATE event_deliveries SET {assignments} WHERE tenant_id = @tenant AND subscription_id = @subscription AND event_id = @event", connection);
            Bind(command, id, subscription);
            (await command.ExecuteNonQueryAsync()).Should().Be(1);
        }
        public async Task SetOrderingAsync(Guid last)
        {
            await using var connection = await Dispatch.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                "UPDATE event_deliveries SET available_after = @time + CASE WHEN event_id = @last THEN interval '1 hour' ELSE interval '0 seconds' END", connection);
            command.Parameters.AddWithValue("time", Occurred);
            command.Parameters.AddWithValue("last", last);
            await command.ExecuteNonQueryAsync();
        }
        public Task ExpireAsync(Guid id) => UpdateAsync(id, First, "locked_until = clock_timestamp() - interval '1 second'");
        public Task DueAsync(Guid id) => UpdateAsync(id, First, "available_after = clock_timestamp() - interval '1 second'");
        public async Task<DateTimeOffset> NowAsync(CancellationToken cancellationToken = default)
        {
            await using var connection = await Dispatch.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT clock_timestamp()", connection);
            await using var row = await command.ExecuteReaderAsync(cancellationToken);
            await row.ReadAsync(cancellationToken);
            return row.GetFieldValue<DateTimeOffset>(0);
        }
        public static async Task<DeliveryClaim> RequestTerminalAsync(DeliveryDispatchStore store)
        {
            var claim = (await store.TryClaimAsync(NoBlocked))!;
            (await store.RequestTerminalAsync(claim, DeliveryTerminalReason.InvalidEnvelope)).Should().BeTrue();
            return (await store.TryClaimTerminalAsync(NoBlocked))!;
        }
        public async Task CompleteAsync(Guid id, string subscription, bool inbox)
        {
            await using var connection = await _app.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await SchemaQueries.AssertApplicationRoleAsync(connection);
            await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);
            await SchemaQueries.ExecuteAsync(connection, transaction, """
                UPDATE event_deliveries SET processed_at = clock_timestamp()
                WHERE tenant_id = @tenant AND subscription_id = @subscription AND event_id = @event
                """, ("tenant", SchemaFixture.TenantA), ("subscription", subscription), ("event", id));
            if (inbox) await SchemaQueries.ExecuteAsync(connection, transaction,
                "INSERT INTO inbox_messages (tenant_id, subscription_id, event_id) VALUES (@tenant, @subscription, @event)",
                ("tenant", SchemaFixture.TenantA), ("subscription", subscription), ("event", id));
            await transaction.CommitAsync();
        }
        public async Task AuditGrantAsync(bool grant)
        {
            await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.MigrationConnectionString);
            await using var command = new NpgsqlCommand(grant ? "GRANT INSERT ON audit_log TO learnstack_outbox_admin"
                : "REVOKE INSERT ON audit_log FROM learnstack_outbox_admin", connection);
            await command.ExecuteNonQueryAsync();
        }
        public Task<long> AuditCountAsync() => CountAsync("SELECT count(*) FROM audit_log WHERE operation = 'platform.delivery.dead_letter'");
        public Task<long> InboxCountAsync() => CountAsync("SELECT count(*) FROM inbox_messages");
        private async Task<long> CountAsync(string sql)
        {
            await using var connection = await _app.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await SchemaQueries.AssertApplicationRoleAsync(connection);
            await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);
            await SchemaQueries.SetSettingAsync(connection, transaction, "app.scope", "tenant");
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            return (long)(await command.ExecuteScalarAsync())!;
        }
        public async Task<AuditRow> ReadAuditAsync()
        {
            await using var connection = await _app.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);
            await SchemaQueries.SetSettingAsync(connection, transaction, "app.scope", "tenant");
            await using var command = new NpgsqlCommand("""
                SELECT tenant_id, organization_id, actor_user_id, operation, operation_type, operation_class, correlation_id, metadata::text
                FROM audit_log WHERE operation = 'platform.delivery.dead_letter'
                """, connection, transaction);
            await using var row = await command.ExecuteReaderAsync();
            (await row.ReadAsync()).Should().BeTrue();
            return new(row.GetGuid(0), row.IsDBNull(1) ? null : row.GetGuid(1), row.GetGuid(2), row.GetString(3),
                row.GetString(4), row.GetString(5), row.GetString(6), row.GetString(7));
        }
        public async Task WaitForLockWaitAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await using var connection = await Dispatch.OpenConnectionAsync();
            while (true)
            {
                await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock')", connection);
                if (await command.ExecuteScalarAsync(deadline.Token) is true) return;
                await Task.Delay(10, deadline.Token);
            }
        }
        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            await Dispatch.DisposeAsync();
            await _app.DisposeAsync();
            await _database.DisposeAsync();
        }
        private static void Bind(NpgsqlCommand command, Guid id, string subscription, Guid? tenant = null)
        {
            command.Parameters.AddWithValue("tenant", tenant ?? SchemaFixture.TenantA);
            command.Parameters.AddWithValue("subscription", subscription);
            command.Parameters.AddWithValue("event", id);
        }
        private static string? Text(NpgsqlDataReader row, int index) => row.IsDBNull(index) ? null : row.GetString(index);
        private static DateTimeOffset? Time(NpgsqlDataReader row, int index) => row.IsDBNull(index) ? null : row.GetFieldValue<DateTimeOffset>(index);
    }

    private sealed class DecoratingScopes(IServiceScopeFactory inner, Func<IAuditStore, IAuditStore> decorate) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new DecoratingScope(inner.CreateScope(), decorate);
    }
    private sealed class DecoratingScope(IServiceScope inner, Func<IAuditStore, IAuditStore> decorate) : IServiceScope, IServiceProvider, IAsyncDisposable
    {
        public IServiceProvider ServiceProvider => this;
        public object? GetService(Type type) => type == typeof(IAuditStore) ? decorate(inner.ServiceProvider.GetRequiredService<IAuditStore>()) : inner.ServiceProvider.GetService(type);
        public void Dispose() => inner.Dispose();
        public ValueTask DisposeAsync()
        {
            if (inner is IAsyncDisposable asyncScope) return asyncScope.DisposeAsync();
            inner.Dispose();
            return ValueTask.CompletedTask;
        }
    }
    private sealed class AuditDecorator(IAuditStore inner,
        Func<AuditEntryDraft, EventTerminalTransition, DbConnection, DbTransaction, CancellationToken, Task> terminal) : IAuditStore
    {
        public Task WritePendingAsync(IUnitOfWork unit, CancellationToken token = default) => inner.WritePendingAsync(unit, token);
        public Task WriteStandaloneAsync(AuditEntryDraft entry, CancellationToken token = default) => inner.WriteStandaloneAsync(entry, token);
        public Task WriteBestEffortAsync(AuditEntryDraft entry, CancellationToken token = default) => inner.WriteBestEffortAsync(entry, token);
        public Task WritePlatformScopeAsync(AuditEntryDraft entry, DbConnection connection, DbTransaction transaction, CancellationToken token = default) => inner.WritePlatformScopeAsync(entry, connection, transaction, token);
        public Task WriteEventTerminalAsync(AuditEntryDraft entry, EventTerminalTransition witness, DbConnection connection, DbTransaction transaction, CancellationToken token = default) => terminal(entry, witness, connection, transaction, token);
    }
}
