using FluentAssertions;
using LearnStack.Application.Messaging;
using LearnStack.Infrastructure.Messaging;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Platform;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.DataRequestSchemaSeed;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// ADR-0107 §§ 7 and 9 and ADR-0103 Amendment 2, the closure half of the replay floor, through the actual provider-event
/// path and the actual operator recovery owner: a subject-linked delivery dead-lettered while the person was Active, then
/// replayed after the person's User became Closing, or Closed by the person's real Finalize, re-runs the real handler, whose subject rule now refuses
/// the person. The delivery completes as a recorded no-op: no audit row names the person, nothing fails or is retried, the
/// replay counter advanced once, and the transport trail is otherwise untouched.
/// </summary>
public sealed partial class DataErasureReplayFloorTests
{
    [Theory]
    [InlineData("Closing")]
    [InlineData("Closed")]
    public async Task A_replay_after_the_persons_account_began_closing_runs_the_handler_which_refuses_and_writes_nothing(string status)
    {
        await using var world = await ArrangeDeliveriesAsync();
        world.Host = CreateHost(world.Database);
        await CloseAsync(world.Database, status);
        await ReplayIsRefusedAsync(world, status);
    }

    /// <summary>
    /// The same floor after the person's real closure: Begin fans it out to person A's three groups with the actual fan-out
    /// act, each group's actual erasure job runs, and the actual Finalize redacts, removes the account at the stand-in
    /// provider and closes the person. The observation accepted while the person was Active is retained, by entity and
    /// provider metadata, with nothing of the person in its redacted columns; the replay of the dead-lettered fact writes
    /// nothing and resets nothing.
    /// </summary>
    [Fact]
    public async Task A_replay_after_the_persons_closure_was_finalized_runs_the_handler_which_refuses_and_writes_nothing()
    {
        await using var world = await ArrangeDeliveriesAsync();
        var removal = new RecordingAccountRemoval();
        world.Host = AccountClosureFlow.Host(world.Database, removal);
        var closure = Guid.CreateVersion7();
        await using (var app = await DataExportTestKit.OpenAppAsync(world.Database.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await FenceAsync(app, transaction, SchemaFixture.TenantA, null, PersonA);
            await InsertRequestAsync(app, transaction, closure, SchemaFixture.TenantA, null, PersonA, [LearnerMembershipA], "closure");
            await transaction.CommitAsync();
        }

        await DataErasureTestKit.BackdateAsync(world.Database.MigrationConnectionString, SchemaFixture.TenantA, null, closure);
        var begun = await AccountClosureFlow.BeginAsync(world.Host, closure);
        begun.Groups.Should().Be(new ClosureBeginTally(Served: 3, Started: 3, Adopted: 0, Waiting: 0, Failed: 0, Unserved: 0));
        foreach (var group in new ClosureParticipantGroup[]
                 {
                     new(TenantId.From(SchemaFixture.TenantA), null),
                     new(TenantId.From(SchemaFixture.TenantA), OrganizationId.From(SchemaFixture.OrgA1)),
                     new(TenantId.From(SchemaFixture.TenantA), OrganizationId.From(SchemaFixture.OrgA2)),
                 })
        {
            await AccountClosureFlow.RunErasureJobAsync(world.Host, world.Database, group,
                await AccountClosureFlow.FanOutRequestAsync(world.Database, group, PersonA));
        }

        var finalized = await AccountClosureFlow.FinalizeAsync(world.Host,
            AccountClosureFlow.Admission(PlatformOperations.ClosureFinalize, "INC-REPLAY-CLOSURE"), closure);
        finalized.Outcome.Should().Be(ClosureFinalizeOutcome.Finalized);
        removal.Subjects.Should().ContainSingle();
        (await ObservationAsync(world, world.CompletedFact)).Should().NotBeNull("the earlier observation is retained, redacted, never deleted");

        await ReplayIsRefusedAsync(world, "Closed");
    }

    /// <summary>The floor itself, once the person's User is <paramref name="status"/>: the replay re-runs the handler, which refuses.</summary>
    private static async Task ReplayIsRefusedAsync(World world, string status)
    {
        world.Before = await TrailAsync(world);
        world.Before["inbox_messages"].Should().StartWith("1:", "only the completed delivery left an inbox row");
        (await ObservationsAsync(world, world.CompletedFact)).Should().Be(1, "the fact accepted and delivered while the person was Active");
        (await ObservationsAsync(world, world.FailedFact)).Should().Be(0);
        (await DeliveryAsync(world)).Should().Be(new DeliveryState(0, "retry_exhausted", true));
        // The definer the fact reader asks: the status, for this tenant's enqueued target only, under the person's User key
        // held shared until the asking transaction ends; nothing for another tenant or an unknown target.
        (await SubjectStatusAsync(world, SchemaFixture.TenantA, world.FailedTarget)).Should().Be((status, true));
        (await SubjectStatusAsync(world, SchemaFixture.TenantB, world.FailedTarget)).Should().Be(((string?)null, false),
            "another tenant's announcement names no target of tenant A");
        (await SubjectStatusAsync(world, SchemaFixture.TenantA, Guid.CreateVersion7())).Should().Be(((string?)null, false));
        var fence = await InspectedFenceAsync(world);

        var replay = await world.Host.Services.GetRequiredService<IOperatorRecovery>().ReplayDeliveryAsync(
            Admission(PlatformOperations.DeliveryReplay), new DeliveryReplayRequest(SchemaFixture.TenantA, Subscription,
                world.FailedEvent, fence.ReplayCount, fence.DeadLetteredAt!.Value, fence.TerminalReason!));
        replay.Should().Be(new OperatorRecoveryResult(OperatorRecoveryOutcome.Applied, 1),
            "a closure does not reset replay, and the Active tenant admits it");

        var claim = await world.Host.Services.GetRequiredService<DeliveryDispatchStore>().TryClaimAsync([]);
        claim.Should().NotBeNull("the reset row is due at once");
        claim!.EventId.Should().Be(world.FailedEvent);
        var gate = new DeliveryCommitGate();
        var execution = await world.Host.Services.GetRequiredService<DeliveryExecutor>().ExecuteAsync(claim, gate);

        execution.Should().Be(new DeliveryExecutionResult(DeliveryExecutionOutcome.Completed, true, false),
            "the handler ran, refused the person and completed as a recorded no-op rather than a failure");
        gate.CommitAcknowledged.Should().BeTrue();
        (await ObservationsAsync(world, world.FailedFact)).Should().Be(0, "the refused observation is withheld: no row names the person");
        (await HandlerRowsAsync(world)).Should().Be(0, "the replayed delivery wrote no audit row at all");
        (await ObservationsAsync(world, world.CompletedFact)).Should().Be(1,
            "the earlier observation is not the replay's to touch; the closure's Finalize redacts it");
        (await DeliveryAsync(world)).Should().Be(new DeliveryState(1, null, false),
            "replay advanced once; nothing is dead-lettered or failed, so nothing is retried");
        var after = await TrailAsync(world);
        after["outbox_messages"].Should().Be(world.Before["outbox_messages"]);
        after["background_jobs"].Should().Be(world.Before["background_jobs"]);
        after["provider_targets"].Should().Be(world.Before["provider_targets"]);
        after["inbox_messages"].Should().StartWith("2:", "the inbox row records that the refused delivery ran; it is the only addition");
        (await UserStatusAsync(world)).Should().Be(status, "a delivery never moves a User");
    }

    /// <summary>
    /// Person A's User moved by the platform role under the closure's fences as Begin's and Finalize's acquirer takes them:
    /// the User key, then the key of the one tenant person A holds Memberships in, both transaction-scoped and exclusive.
    /// </summary>
    private static async Task CloseAsync(DisposableSchemaDatabase database, string status)
    {
        foreach (var sql in status == "Closed"
                     ? new[] { "UPDATE public.users SET status='Closing' WHERE id=@user",
                         "UPDATE public.users SET status='Closed', provider_subject=NULL WHERE id=@user" }
                     : ["UPDATE public.users SET status='Closing' WHERE id=@user"])
        {
            await using var platform = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.PlatformConnectionString);
            await using var transaction = await platform.BeginTransactionAsync();
            await ExecuteAsync(platform, transaction, "SELECT pg_catalog.pg_advisory_xact_lock(@key)",
                ("key", IdentityAdvisoryLockKey.ForUser(UserId.From(PersonA))));
            await ExecuteAsync(platform, transaction, "SELECT pg_catalog.pg_advisory_xact_lock(@key)",
                ("key", IdentityAdvisoryLockKey.ForTenant(TenantId.From(SchemaFixture.TenantA))));
            await ExecuteAsync(platform, transaction, sql, ("user", PersonA));
            await transaction.CommitAsync();
        }
    }

    /// <summary>Every audit row the replayed delivery's handler could have caused: those whose causation is its event.</summary>
    private static async Task<long> HandlerRowsAsync(World world)
    {
        await using var app = await DataExportTestKit.OpenAppAsync(world.Database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        return await ScalarAsync<long>(app, transaction, "SELECT count(*) FROM public.audit_log WHERE metadata->>'causation_id' = @event",
            ("event", world.FailedEvent.ToString("D")));
    }

    /// <summary>
    /// What the subject status definer answers on an application transaction announced for <paramref name="tenant"/>, and
    /// whether that transaction then holds the affected person's User key shared.
    /// </summary>
    private static async Task<(string? Status, bool SharedFence)> SubjectStatusAsync(World world, Guid tenant, Guid target)
    {
        await using var app = await DataExportTestKit.OpenAppAsync(world.Database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, tenant);
        await using var command = new NpgsqlCommand("SELECT public.fn_identity_provider_event_subject_status(@target)", app, transaction);
        command.Parameters.AddWithValue("target", target);
        var status = await command.ExecuteScalarAsync() as string;
        var shared = await ScalarAsync<bool>(app, transaction, """
            SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_locks AS held
                WHERE held.pid = pg_catalog.pg_backend_pid() AND held.locktype = 'advisory' AND held.granted
                  AND held.mode = 'ShareLock' AND held.objsubid = 1
                  AND ((held.classid::bigint << 32) | held.objid::bigint) = @key)
            """, ("key", IdentityAdvisoryLockKey.ForUser(UserId.From(PersonA))));
        await transaction.RollbackAsync();
        return (status, shared);
    }

    private static async Task<string> UserStatusAsync(World world)
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(world.Database.MigrationConnectionString);
        return await ScalarAsync<string>(owner, null, "SELECT status FROM public.users WHERE id = @id", ("id", PersonA));
    }
}
