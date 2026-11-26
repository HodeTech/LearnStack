using FluentAssertions;
using LearnStack.Infrastructure.MultiTenancy;
using LearnStack.Modules.Identity.Infrastructure.DataRequests;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Platform;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The Begin's branches the first suites left unreached: what the act finds after its fences were granted (a closure
/// cancelled, a second operator's Begin, a person closed while it waited), a fulfilling closure whose person is not
/// Closing, the order of the judgment with two refusals standing, a commit that landed and whose answer was lost, and every
/// way the fan-out's answer can be malformed or end.
/// </summary>
public sealed partial class AccountClosureBeginTests
{
    /// <summary>
    /// Waits, from <c>pg_locks</c>, until at least <paramref name="count"/> distinct backends of the role in this database wait
    /// on a lock, while none of the contenders has finished: a wait observed in the server, never a pause of the test.
    /// </summary>
    private static async Task WaitUntilBackendsAwaitLocksAsync(
        AccountClosureBeginKit kit, string role, int count, Task[] contenders, string because)
    {
        await using var observer = await AccountClosureBeginKit.OpenAsync(kit.Database.PlatformConnectionString);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            contenders.Should().OnlyContain(contender => !contender.IsCompleted, because);
            await using var command = new Npgsql.NpgsqlCommand(
                "SELECT count(DISTINCT l.pid) FROM pg_catalog.pg_locks l JOIN pg_catalog.pg_stat_activity a ON a.pid = l.pid "
                + "WHERE a.datname = current_database() AND a.usename = @role AND NOT l.granted", observer);
            command.Parameters.AddWithValue("role", role);
            if ((long)(await command.ExecuteScalarAsync())! >= count) return;
            DateTime.UtcNow.Should().BeBefore(deadline, because);
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }
    }

    // Built from the fixture's constants: static fields of the other parts of this class may not be assigned yet.
    private static readonly ClosureParticipantGroup WideA = new(TenantId.From(SchemaFixture.TenantA), null);
    private static readonly ClosureParticipantGroup BranchA1 = new(TenantId.From(SchemaFixture.TenantA), OrganizationId.From(SchemaFixture.OrgA1));

    /// <summary>
    /// The request is read before the fences and again, locked, under them: a closure the person cancelled while the act
    /// waited for the person's User fence is not begun. Nothing moves, nothing is fanned out, and the entry row is the only
    /// record.
    /// </summary>
    [Fact]
    public async Task A_closure_the_person_cancelled_while_the_Begin_waited_for_the_fences_is_not_begun()
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var person = await kit.PersonAsync();
        await kit.MemberAsync(TenantA, null, person, BuiltInRoleKey.Learner);
        var closure = await kit.ClosureAsync(TenantA, null, person);
        var userBefore = await kit.UserAsync(person);
        var requestBefore = await kit.RequestAsync(closure);
        var fanOut = new RecordingFanOut();
        var admission = AccountClosureBeginKit.Admission();
        await using var app = await DataExportTestKit.OpenAppAsync(kit.Database.AppConnectionString);
        await using var cancelling = await app.BeginTransactionAsync();
        await DataRequestSchemaSeed.FenceAsync(app, cancelling, TenantA, null, person);

        var begin = kit.Owner(fanOut).BeginAsync(admission, closure);
        await kit.WaitUntilALockIsAwaitedAsync("learnstack_platform", begin, "the Begin waits for the person's User fence");
        await DataRequestSchemaSeed.StepAsync(app, cancelling, closure, "status='cancelled'");
        await cancelling.CommitAsync();

        (await begin).Should().Be(new ClosureBeginResult(ClosureBeginOutcome.NotBeginnable, "cancelled"));
        (await kit.RequestAsync(closure)).Should().Be(requestBefore with { Status = "cancelled", Version = requestBefore.Version + 1 },
            "only the person's cancellation wrote the request");
        (await kit.UserAsync(person)).Should().Be(userBefore, "a closure that cannot begin never moves the person");
        fanOut.Calls.Should().BeEmpty();
        (await kit.AuditAsync(admission.Operator.CorrelationId!)).Select(row => row.Operation).Should()
            .Equal([PlatformAdminScope.EnterOperation], "a refusal that changes nothing ends by disposal");
        (await kit.ResultRowsAsync(closure)).Should().Be(0);
    }

    /// <summary>
    /// Two operators begin the same closure. The second waits for the first's fences; when the first commits, the second
    /// reads the closure fulfilling, writes nothing, and only fans out again. The request is stepped once, the User moved
    /// once, and one result row exists.
    /// </summary>
    [Fact]
    public async Task A_second_operator_s_Begin_that_waited_for_the_first_resumes_without_a_write()
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var person = await kit.PersonAsync();
        await kit.MemberAsync(TenantA, null, person, BuiltInRoleKey.Learner);
        var closure = await kit.ClosureAsync(TenantA, null, person);
        var userBefore = await kit.UserAsync(person);
        var requestBefore = await kit.RequestAsync(closure);
        var holds = new GatedHolds(gate: true);
        var firstFanOut = new RecordingFanOut();
        var secondFanOut = new RecordingFanOut { Outcome = _ => ClosureGroupOutcome.Adopted };
        var secondAdmission = AccountClosureBeginKit.Admission("INC-702");

        var first = kit.Owner(firstFanOut, holds).BeginAsync(AccountClosureBeginKit.Admission("INC-701"), closure);
        Task<ClosureBeginResult>? second = null;
        try
        {
            await holds.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            second = kit.Owner(secondFanOut).BeginAsync(secondAdmission, closure);
            await kit.WaitUntilALockIsAwaitedAsync("learnstack_platform", second, "the second Begin waits for the first's fences");
        }
        finally
        {
            holds.Release.TrySetResult();
        }

        (await first).Outcome.Should().Be(ClosureBeginOutcome.Begun);
        (await second!).Should().Be(new ClosureBeginResult(
            ClosureBeginOutcome.Resumed, "fulfilling", Groups: new ClosureBeginTally(1, 0, 1, 0, 0, 0)),
            "the closure began while the second waited, so it is not judged or written again");
        (await kit.RequestAsync(closure)).Should().Be(requestBefore with { Status = "fulfilling", Version = requestBefore.Version + 1 });
        (await kit.UserAsync(person)).Should().Be(userBefore with { Status = "Closing", Version = userBefore.Version + 1 });
        (await kit.ResultRowsAsync(closure)).Should().Be(1, "only the Begin that wrote has a result row");
        (await kit.AuditAsync(secondAdmission.Operator.CorrelationId!)).Select(row => row.Operation).Should()
            .Equal([PlatformAdminScope.EnterOperation]);
        secondFanOut.Calls.Should().ContainSingle().Which.Request.Groups.Should().Equal(WideA);
    }

    /// <summary>
    /// A person another closure's Finalize closed while this Begin waited for the fences is not admitted: the act reads the
    /// status again under the fences and ends with nothing written, the closure still scheduled.
    /// </summary>
    [Fact]
    public async Task A_person_closed_while_the_Begin_waited_for_the_fences_is_not_admitted()
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var person = await kit.PersonAsync();
        await kit.MemberAsync(TenantA, null, person, BuiltInRoleKey.Learner);
        await kit.MemberAsync(TenantA, SchemaFixture.OrgA1, person, BuiltInRoleKey.Learner);
        var first = await kit.ClosureAsync(TenantA, null, person);
        var second = await kit.ClosureAsync(TenantA, SchemaFixture.OrgA1, person);
        var fanOut = new RecordingFanOut();
        (await kit.Owner(fanOut).BeginAsync(AccountClosureBeginKit.Admission(), first)).Outcome.Should().Be(ClosureBeginOutcome.Begun);
        var requestBefore = await kit.RequestAsync(second);
        var admission = AccountClosureBeginKit.Admission();
        await using var platform = await AccountClosureBeginKit.OpenAsync(kit.Database.PlatformConnectionString);
        await using var closing = await platform.BeginTransactionAsync();
        await IdentityMembershipFixture.ExecuteAsync(platform, closing, "SELECT pg_catalog.pg_advisory_xact_lock(@key)",
            ("key", AccountClosureBeginKit.UserKey(person)));
        await IdentityMembershipFixture.ExecuteAsync(platform, closing, "SELECT pg_catalog.pg_advisory_xact_lock(@key)",
            ("key", AccountClosureBeginKit.TenantKey(TenantA)));

        var begin = kit.Owner(fanOut).BeginAsync(admission, second);
        await kit.WaitUntilALockIsAwaitedAsync("learnstack_platform", begin, "the Begin waits for the person's User fence");
        await IdentityMembershipFixture.ExecuteAsync(platform, closing,
            "UPDATE public.users SET status='Closed', provider_subject=NULL WHERE id=@user", ("user", person));
        await closing.CommitAsync();

        (await begin).Should().Be(new ClosureBeginResult(ClosureBeginOutcome.SubjectNotAdmitted, "scheduled"));
        (await kit.RequestAsync(second)).Should().Be(requestBefore, "the closure of a Closed person stays scheduled and untouched");
        (await kit.UserAsync(person)).Status.Should().Be("Closed");
        fanOut.Calls.Should().ContainSingle("only the first closure's Begin fanned out");
        (await kit.AuditAsync(admission.Operator.CorrelationId!)).Select(row => row.Operation).Should()
            .Equal([PlatformAdminScope.EnterOperation]);
    }

    /// <summary>
    /// A closure already fulfilling whose person is not Closing is a state no admitted path writes: the act is abandoned
    /// with a plain failure that does not name the person, nothing is fanned out and nothing is written.
    /// </summary>
    [Fact]
    public async Task A_fulfilling_closure_whose_person_is_not_Closing_is_a_defect_and_changes_nothing()
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var person = await kit.PersonAsync();
        await kit.MemberAsync(TenantA, null, person, BuiltInRoleKey.Learner);
        var closure = await kit.RequestRowAsync(TenantA, person, "closure", "fulfilling");
        var requestBefore = await kit.RequestAsync(closure);
        var fanOut = new RecordingFanOut();
        var admission = AccountClosureBeginKit.Admission();

        var act = () => kit.Owner(fanOut).BeginAsync(admission, closure);

        var failure = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        failure.Message.Should().Contain("not Closing").And.NotContain(person.ToString("D"));
        fanOut.Calls.Should().BeEmpty("a defect fans nothing out");
        (await kit.RequestAsync(closure)).Should().Be(requestBefore);
        (await kit.UserAsync(person)).Status.Should().Be("Active");
        (await kit.AuditAsync(admission.Operator.CorrelationId!)).Select(row => row.Operation).Should()
            .Equal([PlatformAdminScope.EnterOperation]);
    }

    /// <summary>
    /// The judgment's order is the ADR's: the window, then the hold, then the last administrator, and the first refusal
    /// stands. Each case here has every refusal below the expected one standing as well, so a swapped order answers
    /// differently.
    /// </summary>
    [Fact]
    public async Task The_judgment_answers_the_window_before_the_hold_before_the_last_administrator()
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var administrator = await kit.PersonAsync();
        await kit.MemberAsync(TenantB, null, administrator, BuiltInRoleKey.Learner);
        await kit.MemberAsync(TenantA, null, administrator, BuiltInRoleKey.TenantAdmin);
        // The seeded administrator of tenant A leaves, so the person is the only eligible one: the third refusal stands always.
        await RevokeAsync(kit, TenantA, null, IdentityMembershipFixture.AdminA, IdentityMembershipFixture.AdminMembershipA);
        var due = await kit.ClosureAsync(TenantB, null, administrator);
        var inWindow = await kit.ClosureAsync(TenantA, null, administrator, due: false);
        var fanOut = new RecordingFanOut();

        // Due, held and the last administrator: the hold answers.
        var held = new GatedHolds(hold: true);
        (await kit.Owner(fanOut, held).BeginAsync(AccountClosureBeginKit.Admission(), due))
            .Should().Be(new ClosureBeginResult(ClosureBeginOutcome.RefusedHold, "scheduled", "blocked_hold"));
        held.Queries.Should().ContainSingle();

        // In its window and the last administrator: the window answers, the hold source is never asked.
        var quiet = new GatedHolds();
        (await kit.Owner(fanOut, quiet).BeginAsync(AccountClosureBeginKit.Admission(), inWindow))
            .Should().Be(new ClosureBeginResult(ClosureBeginOutcome.RefusedWindow, "scheduled", "blocked_window"));
        quiet.Queries.Should().BeEmpty("the window is judged before the hold source is asked");

        // In its window, held and the last administrator: the window still answers.
        var everything = new GatedHolds(hold: true);
        (await kit.Owner(fanOut, everything).BeginAsync(AccountClosureBeginKit.Admission(), inWindow))
            .Should().Be(new ClosureBeginResult(ClosureBeginOutcome.RefusedWindow, "scheduled", "blocked_window"));
        everything.Queries.Should().BeEmpty();

        (await kit.RequestAsync(due)).LastRefusal.Should().Be("blocked_hold");
        (await kit.RequestAsync(inWindow)).LastRefusal.Should().Be("blocked_window");
        (await kit.UserAsync(administrator)).Status.Should().Be("Active");
        fanOut.Calls.Should().BeEmpty();
    }

    /// <summary>
    /// A commit that landed and whose answer was lost is the unknown outcome all the same: nothing is fanned out, the closure
    /// is fulfilling and its person Closing as the commit left them, and the repeat finds that and resumes.
    /// </summary>
    [Fact]
    public async Task A_commit_that_landed_and_whose_answer_was_lost_is_the_unknown_outcome_and_a_repeat_resumes()
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var person = await kit.PersonAsync();
        await kit.MemberAsync(TenantA, null, person, BuiltInRoleKey.Learner);
        var closure = await kit.ClosureAsync(TenantA, null, person);
        var fanOut = new RecordingFanOut();
        var landed = new TappedScope(kit.Scope()) { AfterCommit = () => Task.FromException(new TimeoutException("forced lost answer after the commit landed")) };

        var lost = () => kit.Owner(fanOut, scope: landed).BeginAsync(AccountClosureBeginKit.Admission(), closure);

        var failure = (await lost.Should().ThrowAsync<OperatorRecoveryUnavailableException>()).Which;
        failure.CommitOutcomeUnknown.Should().BeTrue("the commit was sent, so the act may have landed, and here it did");
        failure.InnerException.Should().BeOfType<TimeoutException>();
        fanOut.Calls.Should().BeEmpty("a Begin whose commit is unknown fans nothing out");
        (await kit.RequestAsync(closure)).Status.Should().Be("fulfilling", "this commit reached the database");
        (await kit.UserAsync(person)).Status.Should().Be("Closing");

        fanOut.Outcome = _ => ClosureGroupOutcome.Adopted;
        var repeat = await kit.Owner(fanOut).BeginAsync(AccountClosureBeginKit.Admission(), closure);

        repeat.Should().Be(new ClosureBeginResult(ClosureBeginOutcome.Resumed, "fulfilling", Groups: new ClosureBeginTally(1, 0, 1, 0, 0, 0)));
        (await kit.ResultRowsAsync(closure)).Should().Be(1, "the repeat wrote nothing");
    }

    /// <summary>
    /// A fan-out that answers badly is counted by what it reported, never by what it claimed: a group nobody handed over,
    /// a second report of a group, an element that is null and an outcome outside the closed set change no served group's
    /// count, and a group not reported as started, adopted or waiting is failed.
    /// </summary>
    [Fact]
    public async Task A_malformed_fan_out_answer_is_counted_only_for_the_served_groups_and_the_first_report_of_a_group_stands()
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var person = await kit.PersonAsync();
        await kit.MemberAsync(TenantA, null, person, BuiltInRoleKey.Learner);
        await kit.MemberAsync(TenantA, SchemaFixture.OrgA1, person, BuiltInRoleKey.Learner);
        var closure = await kit.ClosureAsync(TenantA, null, person);
        var notHandedOver = new ClosureParticipantGroup(TenantId.From(SchemaFixture.TenantB), null);
        var logs = new CapturingLogger<AccountClosureOperatorOperations>();
        var fanOut = new ScriptedFanOut((_, _) => Task.FromResult<IReadOnlyList<ClosureGroupReport>?>(new ClosureGroupReport[]
        {
            new(WideA, ClosureGroupOutcome.Started),
            new(WideA, ClosureGroupOutcome.Failed),
            null!,
            new(BranchA1, (ClosureGroupOutcome)99),
            new(notHandedOver, ClosureGroupOutcome.Started),
        }));

        var result = await kit.Owner(fanOut, logger: logs).BeginAsync(AccountClosureBeginKit.Admission(), closure);

        result.Groups.Should().Be(new ClosureBeginTally(Served: 2, Started: 1, Adopted: 0, Waiting: 0, Failed: 1, Unserved: 0),
            "the first report of the tenant-wide group stands, the undefined outcome and the unrequested group count for nothing");
        result.Groups!.Complete.Should().BeFalse();
        fanOut.Calls.Should().ContainSingle().Which.Groups.Should().BeEquivalentTo([WideA, BranchA1]);
        logs.Lines.Should().BeEmpty("a malformed answer is not an exception");
        (await kit.UserAsync(person)).Status.Should().Be("Closing", "the committed Begin stands");
    }

    /// <summary>A fan-out that answers no list at all fails every served group and is not an exception.</summary>
    [Fact]
    public async Task A_fan_out_that_answers_no_list_fails_every_served_group_without_a_log_line()
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var person = await kit.PersonAsync();
        await kit.MemberAsync(TenantA, null, person, BuiltInRoleKey.Learner);
        var closure = await kit.ClosureAsync(TenantA, null, person);
        var logs = new CapturingLogger<AccountClosureOperatorOperations>();
        var fanOut = new ScriptedFanOut((_, _) => Task.FromResult<IReadOnlyList<ClosureGroupReport>?>(null));

        var result = await kit.Owner(fanOut, logger: logs).BeginAsync(AccountClosureBeginKit.Admission(), closure);

        result.Groups.Should().Be(new ClosureBeginTally(1, 0, 0, 0, 1, 0));
        logs.Lines.Should().BeEmpty();
    }

    /// <summary>
    /// A fan-out that throws, and a cancellation that is not the caller's, fail every served group, log the closed
    /// fan-out failure once and never name the person; the committed Begin stands and a repeat retries.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_fan_out_that_throws_is_logged_once_by_its_closed_event_and_fails_every_served_group(bool cancellation)
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var person = await kit.PersonAsync();
        await kit.MemberAsync(TenantA, null, person, BuiltInRoleKey.Learner);
        var closure = await kit.ClosureAsync(TenantA, null, person);
        var logs = new CapturingLogger<AccountClosureOperatorOperations>();
        var fanOut = new ScriptedFanOut((_, _) => cancellation
            ? Task.FromException<IReadOnlyList<ClosureGroupReport>?>(new OperationCanceledException("a timeout of the fan-out's own"))
            : Task.FromException<IReadOnlyList<ClosureGroupReport>?>(new InvalidOperationException("forced fan-out defect")));

        var result = await kit.Owner(fanOut, logger: logs).BeginAsync(AccountClosureBeginKit.Admission(), closure);

        result.Should().Be(new ClosureBeginResult(ClosureBeginOutcome.Begun, "fulfilling", Groups: new ClosureBeginTally(1, 0, 0, 0, 1, 0)));
        logs.Lines.Should().ContainSingle().Which.Should().StartWith("7120 Error ").And.NotContain(person.ToString("D"));
    }

    /// <summary>
    /// The operator's own cancellation during the fan-out is theirs to see: it propagates, the log carries no fan-out failure,
    /// and the Begin that committed before it stands.
    /// </summary>
    [Fact]
    public async Task The_operator_s_cancellation_during_the_fan_out_propagates_and_the_committed_Begin_stands()
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var person = await kit.PersonAsync();
        await kit.MemberAsync(TenantA, null, person, BuiltInRoleKey.Learner);
        var closure = await kit.ClosureAsync(TenantA, null, person);
        var logs = new CapturingLogger<AccountClosureOperatorOperations>();
        using var operatorLeaves = new CancellationTokenSource();
        var fanOut = new ScriptedFanOut((_, token) =>
        {
            operatorLeaves.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ClosureGroupReport>?>([]);
        });

        var act = () => kit.Owner(fanOut, logger: logs).BeginAsync(AccountClosureBeginKit.Admission(), closure, operatorLeaves.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        logs.Lines.Should().BeEmpty("the operator's cancellation is not a fan-out failure");
        (await kit.RequestAsync(closure)).Status.Should().Be("fulfilling", "the Begin committed before the fan-out");
        (await kit.UserAsync(person)).Status.Should().Be("Closing");
    }
}
