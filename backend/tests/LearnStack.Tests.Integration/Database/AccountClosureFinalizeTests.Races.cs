using System.Diagnostics;
using System.Text.Json.Nodes;
using FluentAssertions;
using LearnStack.Infrastructure.MultiTenancy;
using LearnStack.Modules.Identity.Infrastructure.DataRequests;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Tenancy;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The Finalize's concurrency and failure seams: a group made pending between its two transactions, a fence another act
/// holds past the bound, a commit whose answer is lost before and after it landed, a Membership admission racing the first
/// transaction, and the plans of the statements it adds.
/// </summary>
public sealed partial class AccountClosureFinalizeTests
{
    /// <summary>
    /// The second transaction judges again under its own fences: a served group made pending while the provider was being
    /// called (here its erasure set back to running by the owner) stops the act after the account's deletion with the
    /// person still Closing, records where it stopped, and a repeat once the group is ready finds the account absent and
    /// finishes.
    /// </summary>
    [Fact]
    public async Task A_group_made_pending_between_the_two_transactions_is_found_again_under_the_second_fences()
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var world = await BegunAsync(kit);
        var erasure = world.Erasures[TenantWideB];
        var removal = new RecordingAccountRemoval
        {
            Answer = call => call == 1 ? AccountRemovalOutcome.Removed : AccountRemovalOutcome.Absent,
            Probe = () => kit.SqlAsync(
                $"SET session_replication_role = replica; UPDATE public.data_subject_requests SET status = 'fulfilling' WHERE id = '{erasure}'"),
        };
        var admission = Admission();

        var stopped = await kit.Owner(removal: removal).FinalizeAsync(admission, world.Closure);

        stopped.Outcome.Should().Be(ClosureFinalizeOutcome.GroupsPending);
        stopped.Groups.Should().Be(new ClosureFinalizeTally(Served: 3, Ready: 2, Pending: 1, Unserved: 1));
        (await kit.UserAsync(world.Person)).Should().Match<AccountClosureBeginKit.UserRow>(row => row.Status == "Closing" && row.SubjectPresent,
            "nothing of the second transaction committed");
        (await kit.RequestAsync(world.Closure)).Status.Should().Be("fulfilling");
        foreach (var group in new[] { TenantWideA, BranchA1 })
            (await RequestStateAsync(kit, group, world.Erasures[group])).Status.Should().Be("awaiting_privileged_step");
        var rows = await kit.AuditAsync(admission.Operator.CorrelationId!);
        rows.Select(row => row.Operation).Should().Equal(PlatformAdminScope.EnterOperation, Attempt, Result);
        var recorded = JsonNode.Parse(rows[^1].After)!;
        (recorded["outcome"]!.GetValue<string>(), recorded["provider"]!.GetValue<string>(), recorded["groups"]!["pending"]!.GetValue<int>())
            .Should().Be(("groups_pending", "removed", 1));

        removal.Probe = null;
        await kit.SqlAsync(
            $"SET session_replication_role = replica; UPDATE public.data_subject_requests SET status = 'awaiting_privileged_step' WHERE id = '{erasure}'");
        var finished = await kit.Owner(removal: removal).FinalizeAsync(Admission(), world.Closure);

        finished.Outcome.Should().Be(ClosureFinalizeOutcome.Finalized);
        (await kit.UserAsync(world.Person)).Status.Should().Be("Closed");
        removal.Subjects.Should().HaveCount(2);
    }

    /// <summary>
    /// A fence another act holds past the bound answers the retryable unavailable within the bound, with no commit sent. The
    /// person's User key is the first transaction's: the provider is never asked and the entry row is the only record. A
    /// participant tenant's key is the second transaction's alone: the first redacted and recorded its intent and the
    /// provider removed the account, and the second then waited and rolled back, leaving the person Closing.
    /// </summary>
    [Theory]
    [InlineData("user")]
    [InlineData("tenant")]
    public async Task A_fence_held_past_the_bound_is_the_retryable_unavailable_and_changes_nothing(string held)
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var world = await BegunAsync(kit);
        var removal = new RecordingAccountRemoval { Answer = call => call == 1 ? AccountRemovalOutcome.Removed : AccountRemovalOutcome.Absent };
        await using var holder = await AccountClosureBeginKit.OpenAsync(kit.Database.PlatformConnectionString);
        await using var holding = await holder.BeginTransactionAsync();
        await ExecuteAsync(holder, holding, "SELECT pg_catalog.pg_advisory_xact_lock(@key)",
            ("key", held == "user" ? AccountClosureBeginKit.UserKey(world.Person) : AccountClosureBeginKit.TenantKey(TenantB)));
        var admission = Admission();
        var clock = Stopwatch.StartNew();

        var act = () => kit.Owner(removal: removal).FinalizeAsync(admission, world.Closure);

        var failure = (await act.Should().ThrowAsync<OperatorRecoveryUnavailableException>()).Which;
        failure.CommitOutcomeUnknown.Should().BeFalse("no commit was sent");
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15), "each key waits at most three seconds");
        var rows = (await kit.AuditAsync(admission.Operator.CorrelationId!)).Select(row => row.Operation);
        if (held == "user")
        {
            removal.Subjects.Should().BeEmpty();
            rows.Should().Equal(PlatformAdminScope.EnterOperation);
        }
        else
        {
            removal.Subjects.Should().ContainSingle("the first transaction never waits for a participant tenant's key");
            rows.Should().Equal(PlatformAdminScope.EnterOperation, Attempt);
        }

        (await kit.UserAsync(world.Person)).Status.Should().Be("Closing", "nothing of the second transaction committed");
        await holding.RollbackAsync();

        (await kit.Owner(removal: removal).FinalizeAsync(Admission(), world.Closure)).Outcome.Should().Be(ClosureFinalizeOutcome.Finalized,
            "the repeat after the other act let go finishes");
    }

    /// <summary>
    /// A final commit whose answer is lost is the unknown outcome whether or not it landed: one that was never sent leaves
    /// the closure fulfilling and a repeat finishes it; one that landed leaves it completed and a repeat answers
    /// <c>already_finalized</c>.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_lost_final_commit_is_the_unknown_outcome_and_a_repeat_finds_what_landed(bool landed)
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var world = await BegunAsync(kit);
        var removal = new RecordingAccountRemoval { Answer = call => call == 1 ? AccountRemovalOutcome.Removed : AccountRemovalOutcome.Absent };
        IPlatformAdminScope lost = landed ? new LandedLostCommitScope(kit.Scope()) : new LostCommitScope(kit.Scope());

        var act = () => kit.Owner(removal: removal, scope: lost).FinalizeAsync(Admission(), world.Closure);

        (await act.Should().ThrowAsync<OperatorRecoveryUnavailableException>()).Which.CommitOutcomeUnknown.Should().BeTrue(
            "the final commit was sent, so it may have landed");
        (await kit.UserAsync(world.Person)).Status.Should().Be(landed ? "Closed" : "Closing");

        var repeat = await kit.Owner(removal: removal).FinalizeAsync(Admission(), world.Closure);

        repeat.Outcome.Should().Be(landed ? ClosureFinalizeOutcome.AlreadyFinalized : ClosureFinalizeOutcome.Finalized);
        (await kit.UserAsync(world.Person)).Status.Should().Be("Closed");
        removal.Subjects.Should().HaveCount(landed ? 1 : 2, "a landed finalization asks the provider nothing again");
    }

    /// <summary>
    /// A Membership admission for the person in a tenant of theirs, started while the first transaction holds the fences,
    /// waits for the person's User key, never deadlocks with the act, and once the act's checkpoint released the keys is
    /// refused by the database because the person is Closing. The act then finishes with the participants it read.
    /// </summary>
    [Fact]
    public async Task A_Membership_admission_racing_the_first_transaction_waits_and_is_refused_by_the_Closing_person()
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var world = await BegunAsync(kit);
        var gated = new GatedRedactionScope(kit.Scope());
        var finalize = kit.Owner(scope: gated).FinalizeAsync(Admission(), world.Closure);
        await gated.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await using var app = await DataExportTestKit.OpenAppAsync(kit.Database.AppConnectionString);
        await using var admission = await app.BeginTransactionAsync();
        await ScopeAsync(app, admission, TenantB, SchemaFixture.OrgB1);
        var waiting = LockAsync(app, admission, TenantB, world.Person, false);
        try
        {
            await kit.WaitUntilALockIsAwaitedAsync("learnstack_app", waiting, "the admission waits for the person's User key");
        }
        finally
        {
            gated.Release.TrySetResult();
        }

        await waiting.WaitAsync(TimeSpan.FromSeconds(30));
        var insert = () => InsertMemberAsync(app, admission, TenantB, SchemaFixture.OrgB1, Guid.CreateVersion7(), world.Person,
            RoleId(TenantB, BuiltInRoleKey.Learner, PermissionScope.Tenant));
        var refused = (await insert.Should().ThrowAsync<PostgresException>()).Which;
        (refused.SqlState, refused.MessageText).Should().Be(("P0302", "identity membership target is unavailable"));
        await admission.RollbackAsync();

        var result = await finalize.WaitAsync(TimeSpan.FromSeconds(30));
        result.Outcome.Should().Be(ClosureFinalizeOutcome.Finalized);
        result.Groups!.Served.Should().Be(3, "the refused admission added no group");
    }

    /// <summary>
    /// The statements Finalize adds read the person's own rows through the person's indexes, never a group's or a tenant's
    /// whole history: the readiness read and the open-closure read through the request subject index, the Membership reads
    /// through the Membership user index. Thousands of other people's requests and Memberships in the same groups make a
    /// scan by group visibly worse than one by person.
    /// </summary>
    [Fact]
    public async Task The_Finalize_statements_read_the_person_s_rows_through_the_person_s_indexes()
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var world = await BegunAsync(kit);
        await kit.SqlAsync($"""
            SET session_replication_role = replica;
            INSERT INTO public.memberships(id,tenant_id,organization_id,user_id,status,created_at,created_by)
            SELECT uuidv7(), '{TenantB}', NULL, uuidv7(), 'Active', clock_timestamp(), '{UserId.SystemActor.Value}'
            FROM generate_series(1, 4000);
            INSERT INTO public.data_subject_requests(id,tenant_id,organization_id,kind,origin,subject_user_id,requester_user_id,status,
                membership_snapshot,execute_at,created_at,created_by,row_version)
            SELECT uuidv7(), '{TenantB}', NULL, 'erasure', 'self', member.user_id, member.user_id, 'cancelled',
                jsonb_build_array(member.id::text), now(), now() - interval '24 hours', member.user_id, 1
            FROM public.memberships AS member WHERE member.tenant_id = '{TenantB}' AND member.user_id <> '{world.Person}';
            ANALYZE public.memberships;
            ANALYZE public.data_subject_requests;
            """);
        await using var platform = await AccountClosureBeginKit.OpenAsync(kit.Database.PlatformConnectionString);

        async Task<string> Plan(string sql)
        {
            await using var command = new NpgsqlCommand("SET enable_seqscan = off; EXPLAIN (FORMAT JSON) " + sql, platform);
            command.Parameters.AddWithValue("subject", world.Person);
            command.Parameters.AddWithValue("began", DateTimeOffset.UtcNow);
            command.Parameters.AddWithValue("limit", 1025);
            command.Parameters.AddWithValue("id", world.Closure);
            return (await command.ExecuteScalarAsync())!.ToString()!;
        }

        var readiness = await Plan(AccountClosureOperatorOperations.ReadinessSql);
        readiness.Should().Contain("ix_data_subject_requests_subject_user_id").And.Contain("ix_memberships_user_id").And.NotContain("Seq Scan");
        (await Plan(AccountClosureOperatorOperations.MembershipsSql)).Should().Contain("ix_memberships_user_id").And.NotContain("Seq Scan");
        (await Plan(AccountClosureOperatorOperations.OpenClosuresSql)).Should().Contain("ix_data_subject_requests_subject_user_id")
            .And.NotContain("Seq Scan");
    }

    /// <summary>The real scope with a handle whose final commit lands and whose answer is then lost.</summary>
    private sealed class LandedLostCommitScope(IPlatformAdminScope inner) : IPlatformAdminScope
    {
        public async Task<IPlatformAdminScopeHandle> EnterAsync(
            PlatformAdmission admission, CancellationToken cancellationToken = default,
            [System.Runtime.CompilerServices.CallerMemberName] string? callerMember = null,
            [System.Runtime.CompilerServices.CallerFilePath] string? callerFile = null,
            [System.Runtime.CompilerServices.CallerLineNumber] int callerLine = 0) =>
            new Handle(await inner.EnterAsync(admission, cancellationToken, callerMember, callerFile, callerLine));

        private sealed class Handle(IPlatformAdminScopeHandle real) : ForwardingHandle(real)
        {
            public override async Task CommitAsync(CancellationToken cancellationToken = default)
            {
                await base.CommitAsync(cancellationToken);
                throw new TimeoutException("forced lost answer after the commit landed");
            }
        }
    }

    /// <summary>
    /// The real scope with a handle that holds the act at its first redaction, when the first transaction holds every
    /// fence and has written nothing, until the test releases it.
    /// </summary>
    private sealed class GatedRedactionScope(IPlatformAdminScope inner) : IPlatformAdminScope
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IPlatformAdminScopeHandle> EnterAsync(
            PlatformAdmission admission, CancellationToken cancellationToken = default,
            [System.Runtime.CompilerServices.CallerMemberName] string? callerMember = null,
            [System.Runtime.CompilerServices.CallerFilePath] string? callerFile = null,
            [System.Runtime.CompilerServices.CallerLineNumber] int callerLine = 0) =>
            new Handle(await inner.EnterAsync(admission, cancellationToken, callerMember, callerFile, callerLine), this);

        private sealed class Handle(IPlatformAdminScopeHandle real, GatedRedactionScope gate) : ForwardingHandle(real)
        {
            public override async Task<AuditRedactionReport> RedactAuditAsync(AuditRedactionPlan plan, CancellationToken cancellationToken = default)
            {
                if (gate.Entered.TrySetResult())
                {
                    await gate.Release.Task;
                }

                return await base.RedactAuditAsync(plan, cancellationToken);
            }
        }
    }

    private abstract class ForwardingHandle(IPlatformAdminScopeHandle real) : IPlatformAdminScopeHandle
    {
        public Task<int> ExecuteAsync(string sql, IReadOnlyList<PlatformSqlParameter>? parameters = null, CancellationToken cancellationToken = default) =>
            real.ExecuteAsync(sql, parameters, cancellationToken);

        public Task<IReadOnlyList<T>> QueryAsync<T>(
            string sql, IReadOnlyList<PlatformSqlParameter>? parameters, Func<System.Data.Common.DbDataReader, T> map,
            CancellationToken cancellationToken = default) => real.QueryAsync(sql, parameters, map, cancellationToken);

        public Task WriteIntentAsync(PlatformResult intent, CancellationToken cancellationToken = default) =>
            real.WriteIntentAsync(intent, cancellationToken);

        public virtual Task<AuditRedactionReport> RedactAuditAsync(AuditRedactionPlan plan, CancellationToken cancellationToken = default) =>
            real.RedactAuditAsync(plan, cancellationToken);

        public virtual Task WriteResultAsync(PlatformResult result, CancellationToken cancellationToken = default) =>
            real.WriteResultAsync(result, cancellationToken);

        public virtual Task CommitAsync(CancellationToken cancellationToken = default) => real.CommitAsync(cancellationToken);

        public ValueTask DisposeAsync() => real.DisposeAsync();
    }
}
