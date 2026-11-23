using System.Diagnostics;
using FluentAssertions;
using LearnStack.Infrastructure.MultiTenancy;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Platform;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class AccountClosureBeginTests
{
    /// <summary>
    /// A Membership admission and a revocation for the person race a Begin held at its judgment: both wait on the person's
    /// User fence, the Begin commits Closing, then the admission is refused by the database's own Active check and the
    /// revocation proceeds. No wait is unbounded and nothing deadlocks.
    /// </summary>
    [Fact]
    public async Task A_Membership_write_that_waits_for_a_Begin_meets_a_Closing_person_and_an_admission_is_refused()
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var person = await kit.PersonAsync();
        await kit.MemberAsync(TenantA, null, person, BuiltInRoleKey.Learner);
        var branch = await kit.MemberAsync(TenantA, SchemaFixture.OrgA1, person, BuiltInRoleKey.Learner);
        var closure = await kit.ClosureAsync(TenantA, null, person);
        var holds = new GatedHolds(gate: true);
        var begin = kit.Owner(holds: holds).BeginAsync(AccountClosureBeginKit.Admission(), closure);
        await using var admitting = await DataExportTestKit.OpenAppAsync(kit.Database.AppConnectionString);
        await using var revoking = await DataExportTestKit.OpenAppAsync(kit.Database.AppConnectionString);
        await using var admission = await admitting.BeginTransactionAsync();
        await using var revocation = await revoking.BeginTransactionAsync();
        Task admitted, revoked;
        var watch = Stopwatch.StartNew();
        try
        {
            await holds.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await ScopeAsync(admitting, admission, TenantA, SchemaFixture.OrgA2);
            admitted = LockAsync(admitting, admission, TenantA, person, exclusive: false);
            await kit.WaitUntilALockIsAwaitedAsync("learnstack_app", admitted, "the admission waits on the person's User fence");
            await ScopeAsync(revoking, revocation, TenantA, SchemaFixture.OrgA1);
            revoked = LockAsync(revoking, revocation, TenantA, person, exclusive: true);
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            revoked.IsCompleted.Should().BeFalse("the revocation waits on the same fence");
        }
        finally
        {
            holds.Release.TrySetResult();
        }

        (await begin).Outcome.Should().Be(ClosureBeginOutcome.Begun);
        await admitted;
        var insert = () => InsertMemberAsync(admitting, admission, TenantA, SchemaFixture.OrgA2, Guid.CreateVersion7(), person,
            RoleId(TenantA, BuiltInRoleKey.Learner, PermissionScope.Organization));
        var refusal = (await insert.Should().ThrowAsync<PostgresException>()).Which;
        (refusal.SqlState, refusal.MessageText).Should().Be(("P0302", "identity membership target is unavailable"),
            "a Closing person is admitted to nothing");
        await admission.RollbackAsync();

        await revoked;
        await ExecuteAsync(revoking, revocation, """
            UPDATE public.memberships SET status='Revoked', deleted_at=clock_timestamp(), deleted_by=@system,
                updated_at=clock_timestamp(), updated_by=@system, row_version=row_version+1
            WHERE id=@member
            """, ("member", branch), ("system", UserId.SystemActor.Value));
        await ExecuteAsync(revoking, revocation, "SET CONSTRAINTS ALL IMMEDIATE");
        await revocation.CommitAsync();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(60), "every wait was bounded and nothing deadlocked");
        (await kit.UserAsync(person)).Status.Should().Be("Closing");
    }

    /// <summary>
    /// A Membership the person is being admitted to in a new tenant holds the person's User fence when the Begin starts:
    /// the Begin waits for it, then reads the participants under the fence, finds the new tenant, fences it too and fans it
    /// out. A Begin that read its participants before the fence would leave the new tenant unfenced, and the database would
    /// refuse the User's move.
    /// </summary>
    [Fact]
    public async Task A_Begin_that_waits_for_an_admission_reads_the_participants_under_the_fence_and_fences_the_new_tenant()
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var person = await kit.PersonAsync();
        await kit.MemberAsync(TenantA, null, person, BuiltInRoleKey.Learner);
        var closure = await kit.ClosureAsync(TenantA, null, person);
        await using var app = await DataExportTestKit.OpenAppAsync(kit.Database.AppConnectionString);
        await using var admission = await app.BeginTransactionAsync();
        await ScopeAsync(app, admission, TenantB, null);
        await LockAsync(app, admission, TenantB, person, exclusive: false);
        await InsertMemberAsync(app, admission, TenantB, null, Guid.CreateVersion7(), person,
            RoleId(TenantB, BuiltInRoleKey.Learner, PermissionScope.Tenant));
        await ExecuteAsync(app, admission, "SET CONSTRAINTS ALL IMMEDIATE");
        var fanOut = new RecordingFanOut();

        var begin = kit.Owner(fanOut).BeginAsync(AccountClosureBeginKit.Admission(), closure);
        await kit.WaitUntilALockIsAwaitedAsync("learnstack_platform", begin, "the Begin waits on the person's User fence");
        await admission.CommitAsync();

        var result = await begin;
        result.Outcome.Should().Be(ClosureBeginOutcome.Begun);
        result.Groups!.Served.Should().Be(2, "the tenant the person was admitted to while the Begin waited is a participant");
        fanOut.Calls.Single().Request.Groups.Should().Contain(new ClosureParticipantGroup(TenantId.From(TenantB), null));
    }

    /// <summary>
    /// A tenant key another act holds past the three-second bound fails the Begin as the retryable unavailable, with
    /// nothing changed and only the entry row; once the key is free, a repeat begins.
    /// </summary>
    [Fact]
    public async Task A_fence_held_past_its_bound_is_the_retryable_unavailable_and_changes_nothing()
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var person = await kit.PersonAsync();
        await kit.MemberAsync(TenantA, null, person, BuiltInRoleKey.Learner);
        var closure = await kit.ClosureAsync(TenantA, null, person);
        var request = await kit.RequestAsync(closure);
        var fanOut = new RecordingFanOut();
        await using var app = await DataExportTestKit.OpenAppAsync(kit.Database.AppConnectionString);
        await using (var holder = await app.BeginTransactionAsync())
        {
            // An ordinary tenant writer holds the tenant key shared; the Begin needs it exclusively.
            await ScopeAsync(app, holder, TenantA, null);
            await LockAsync(app, holder, TenantA, null, exclusive: false);
            var admission = AccountClosureBeginKit.Admission();
            var watch = Stopwatch.StartNew();

            var act = () => kit.Owner(fanOut).BeginAsync(admission, closure);

            var failure = (await act.Should().ThrowAsync<OperatorRecoveryUnavailableException>()).Which;
            watch.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(2.5)).And.BeLessThan(TimeSpan.FromSeconds(15),
                "the wait is bounded by the transaction's three-second lock timeout");
            failure.CommitOutcomeUnknown.Should().BeFalse();
            failure.InnerException.Should().BeOfType<PostgresException>().Which.SqlState.Should().Be("55P03");
            (await kit.AuditAsync(admission.Operator.CorrelationId!)).Should().ContainSingle()
                .Which.Operation.Should().Be(PlatformAdminScope.EnterOperation);
            await holder.RollbackAsync();
        }

        (await kit.RequestAsync(closure)).Should().Be(request);
        (await kit.UserAsync(person)).Status.Should().Be("Active");
        fanOut.Calls.Should().BeEmpty();
        (await kit.Owner(fanOut).BeginAsync(AccountClosureBeginKit.Admission(), closure)).Outcome.Should().Be(ClosureBeginOutcome.Begun);
    }

    /// <summary>
    /// A commit whose answer is lost is the unknown outcome and fans nothing out; the repeat finds the closure as it is. A
    /// fan-out that fails a group, or throws, leaves the committed Begin standing and answers it incomplete.
    /// </summary>
    [Fact]
    public async Task A_lost_commit_is_the_unknown_outcome_and_a_failed_fan_out_leaves_the_Begin_committed_and_incomplete()
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var person = await kit.PersonAsync();
        await kit.MemberAsync(TenantA, null, person, BuiltInRoleKey.Learner);
        await kit.MemberAsync(TenantA, SchemaFixture.OrgA1, person, BuiltInRoleKey.Learner);
        var closure = await kit.ClosureAsync(TenantA, null, person);
        var fanOut = new RecordingFanOut();

        var lost = () => kit.Owner(fanOut, scope: new LostCommitScope(kit.Scope())).BeginAsync(AccountClosureBeginKit.Admission(), closure);

        var failure = (await lost.Should().ThrowAsync<OperatorRecoveryUnavailableException>()).Which;
        failure.CommitOutcomeUnknown.Should().BeTrue("the commit was sent, so the act may have landed");
        failure.InnerException.Should().BeOfType<TimeoutException>();
        fanOut.Calls.Should().BeEmpty("a Begin whose commit is unknown fans nothing out");
        (await kit.RequestAsync(closure)).Status.Should().Be("scheduled", "this commit never reached the database");

        var branch = new ClosureParticipantGroup(TenantId.From(TenantA), OrganizationId.From(SchemaFixture.OrgA1));
        fanOut.Outcome = group => group == branch ? ClosureGroupOutcome.Failed : ClosureGroupOutcome.Started;
        var partial = await kit.Owner(fanOut).BeginAsync(AccountClosureBeginKit.Admission(), closure);

        partial.Should().Be(new ClosureBeginResult(ClosureBeginOutcome.Begun, "fulfilling", Groups: new ClosureBeginTally(2, 1, 0, 0, 1, 0)));
        partial.Groups!.Complete.Should().BeFalse("a failed group is owed a repeat");
        (await kit.RequestAsync(closure)).Status.Should().Be("fulfilling", "the Begin committed before the fan-out");
        (await kit.UserAsync(person)).Status.Should().Be("Closing");

        fanOut.Failure = new InvalidOperationException("forced fan-out defect");
        var thrown = await kit.Owner(fanOut).BeginAsync(AccountClosureBeginKit.Admission(), closure);
        thrown.Should().Be(new ClosureBeginResult(ClosureBeginOutcome.Resumed, "fulfilling", Groups: new ClosureBeginTally(2, 0, 0, 0, 2, 0)),
            "a fan-out that throws reports every group failed, and the closure stands");

        fanOut.Failure = null;
        fanOut.Outcome = group => group == branch ? ClosureGroupOutcome.Waiting : ClosureGroupOutcome.Adopted;
        var waiting = await kit.Owner(fanOut).BeginAsync(AccountClosureBeginKit.Admission(), closure);
        waiting.Groups.Should().Be(new ClosureBeginTally(2, 0, 1, 1, 0, 0));
        waiting.Groups!.Complete.Should().BeFalse("a waiting group is owed a repeat");
    }
}
