using System.Diagnostics;
using System.Text.Json.Nodes;
using FluentAssertions;
using LearnStack.Infrastructure.MultiTenancy;
using LearnStack.Modules.Identity.Application.DataRequests;
using LearnStack.Modules.Identity.Infrastructure.DataRequests;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Platform;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The Finalize's branches the first suites left unreached, each arranged in the real database through the seams the
/// suite already uses: a participant tenant that holds only an unserved group, the retained counts each request records by
/// value, two operators finalizing one closure, every defect the act names, a result row that cannot be written, a second
/// closure that began, a person with groups of organizations alone, and a Membership revoked after the Begin.
/// </summary>
public sealed partial class AccountClosureFinalizeTests
{
    // Built from the fixture's constants: static fields of the other parts of this class may not be assigned yet.
    private static readonly ClosureParticipantGroup BranchB1 = new(TenantId.From(SchemaFixture.TenantB), OrganizationId.From(SchemaFixture.OrgB1));

    /// <summary>The audit kinds a report retained, by closed code.</summary>
    private static Dictionary<string, long> RetainedCodes(AuditRedactionReport report) =>
        report.Retained.ToDictionary(entry => DataRequestCodes.RetainedKinds.ToCode(entry.Kind), entry => entry.Count, StringComparer.Ordinal);

    /// <summary>
    /// A request's recorded result carries exactly the retained audit counts of <paramref name="report"/>, by value: the
    /// kinds the redaction reported replace whatever the request held, and a non-vacuous report names at least the actor kind.
    /// </summary>
    private static void AssertRetainedCounts(IReadOnlyDictionary<string, long> recorded, AuditRedactionReport report, string because)
    {
        var expected = RetainedCodes(report);
        expected.Should().ContainKey("audit_actor_reference").WhoseValue.Should().BePositive("the planted rows make the count worth comparing");
        foreach (var (kind, count) in expected)
        {
            recorded.Should().ContainKey(kind, because).WhoseValue.Should().Be(count, $"{because}: {kind}");
        }
    }

    private static void AssertRetainedCounts(JsonObject recorded, AuditRedactionReport report, string because) =>
        AssertRetainedCounts(
            recorded.ToDictionary(entry => entry.Key, entry => entry.Value!.GetValue<long>(), StringComparer.Ordinal), report, because);

    /// <summary>The transaction state of one backend of this database, read by its process.</summary>
    private static async Task<string> BackendStateAsync(AccountClosureBeginKit kit, int backend)
    {
        await using var observer = await AccountClosureBeginKit.OpenAsync(kit.Database.PlatformConnectionString);
        await using var command = new NpgsqlCommand(
            "SELECT COALESCE(state, '') FROM pg_catalog.pg_stat_activity WHERE datname = current_database() AND pid = @pid", observer);
        command.Parameters.AddWithValue("pid", backend);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// A participant tenant in which the person holds only an unserved group (the tenant stopped allowing ordinary work
    /// before the Begin) is redacted like every other participant tenant: its audit rows that name the person lose their
    /// address, agent and e-mail, and the tenant's own report is among the redaction's. A redaction that reached only the
    /// tenants of served groups, or only the closure's own tenant, leaves that tenant's rows as they were.
    /// </summary>
    [Fact]
    public async Task A_tenant_whose_only_group_is_unserved_is_redacted_like_every_other_participant_tenant()
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var person = await kit.PersonAsync();
        var member = await kit.MemberAsync(TenantA, null, person, BuiltInRoleKey.Learner);
        await kit.MemberAsync(TenantB, null, person, BuiltInRoleKey.Learner);
        var closure = await kit.ClosureAsync(TenantA, null, person);
        await kit.TenantStatusAsync(TenantB, "Suspended");
        (await kit.Owner().BeginAsync(AccountClosureBeginKit.Admission(), closure)).Groups.Should().Be(new ClosureBeginTally(1, 1, 0, 0, 0, 1));
        var world = new World(
            person, "fixture-subject-" + person.ToString("N"), closure, Guid.Empty,
            new Dictionary<ClosureParticipantGroup, Guid> { [TenantWideA] = await FanOutErasureAsync(kit, TenantWideA, person, member, awaiting: true) },
            new Dictionary<string, Guid>
            {
                ["tenant A"] = await PlantAsync(kit, TenantA, person),
                ["tenant B (unserved only)"] = await PlantAsync(kit, TenantB, person),
                ["sentinel"] = await PlantAsync(kit, TenantId.PlatformSentinel.Value, person),
            });
        (await PlantedAsync(kit, world)).Values.Should().OnlyContain(row => row.Address != null, "the planted rows name the person before the act");

        var result = await kit.Owner().FinalizeAsync(Admission(), closure);

        result.Outcome.Should().Be(ClosureFinalizeOutcome.Finalized);
        result.Groups.Should().Be(new ClosureFinalizeTally(Served: 1, Ready: 1, Pending: 0, Unserved: 1, ErasuresCompleted: 1, ClosuresCompleted: 1));
        result.UnservedGroups.Should().Equal(TenantWideB);
        result.Redaction!.Tenants.Keys.Should().BeEquivalentTo([TenantId.From(TenantA), TenantId.From(TenantB)]);
        result.Redaction.Tenants[TenantId.From(TenantB)].RowsSelected.Should().Be(1, "the tenant's own pass found the row that names the person");
        (await PlantedAsync(kit, world)).Should().ContainKey("tenant B (unserved only)").WhoseValue.Should().Be(
            new PlantedRow(null, null, null, person), "the unserved-only tenant's row lost its address, agent and e-mail");
        (await PlantedAsync(kit, world)).Values.Should().OnlyContain(row => row.Address == null && row.Agent == null && row.Email == null);
    }

    /// <summary>
    /// The retained audit counts each completed request records are the redaction's, by value and by tenant: a fan-out
    /// erasure records its own tenant's counts and a closure the total of every pass. The tenants hold different numbers of
    /// rows that name the person, so the tenant's count, the other tenant's and the total are three different numbers, and
    /// none of them is the job's.
    /// </summary>
    [Fact]
    public async Task Each_erasure_records_its_own_tenant_s_retained_counts_and_each_closure_the_total_by_value()
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var world = await BegunAsync(kit);
        await PlantAsync(kit, TenantA, world.Person);
        await PlantAsync(kit, TenantA, world.Person);

        var result = await kit.Owner().FinalizeAsync(Admission(), world.Closure);

        result.Outcome.Should().Be(ClosureFinalizeOutcome.Finalized);
        var tenantA = RetainedCodes(result.Redaction!.Tenants[TenantId.From(TenantA)]);
        var tenantB = RetainedCodes(result.Redaction.Tenants[TenantId.From(TenantB)]);
        var total = RetainedCodes(result.Redaction.Total);
        tenantA["audit_actor_reference"].Should().Be(3);
        tenantB["audit_actor_reference"].Should().Be(1);
        total["audit_actor_reference"].Should().BeGreaterThan(tenantA["audit_actor_reference"], "the total sums every pass, the sentinel's included");
        foreach (var group in Served)
        {
            var recorded = JsonNode.Parse((await RequestStateAsync(kit, group, world.Erasures[group])).Result)!.AsObject();
            AssertRetainedCounts(recorded, result.Redaction.Tenants[group.TenantId], group + " records its own tenant's counts");
            recorded["consent_evidence"]!.GetValue<long>().Should().Be(2, "the tenant side's record is kept");
            recorded["audit_actor_reference"]!.GetValue<long>().Should().NotBe(9, "the job's own count is replaced");
        }

        foreach (var (group, request) in new[] { (TenantWideA, world.Closure), (TenantWideB, world.SecondClosure) })
        {
            var recorded = JsonNode.Parse((await RequestStateAsync(kit, group, request)).Result)!.AsObject();
            AssertRetainedCounts(recorded, result.Redaction.Total, "a closure records the total");
            recorded["unserved_group"]!.GetValue<long>().Should().Be(1);
        }

        AssertRetainedCounts(result.Retained!, result.Redaction.Total, "the answer carries the closure's recorded counts");
    }

    /// <summary>
    /// A Finalize that waited for the fences of another's second transaction, which then completed the closure, answers what
    /// was recorded and writes nothing: the provider is not asked, the person is not touched again and only the entry row is
    /// its record.
    /// </summary>
    [Fact]
    public async Task A_Finalize_that_waited_for_the_fences_of_another_that_completed_answers_it_and_writes_nothing()
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var world = await BegunAsync(kit);
        var atCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gated = new TappedScope(kit.Scope())
        {
            BeforeCommit = async () =>
            {
                atCommit.TrySetResult();
                await release.Task;
            },
        };
        var first = new RecordingAccountRemoval();
        var second = new RecordingAccountRemoval();
        var waiting = Admission();

        var winner = kit.Owner(scope: gated, removal: first).FinalizeAsync(Admission(), world.Closure);
        Task<ClosureFinalizeResult>? loser = null;
        try
        {
            await atCommit.Task.WaitAsync(TimeSpan.FromSeconds(30));
            loser = kit.Owner(removal: second).FinalizeAsync(waiting, world.Closure);
            await kit.WaitUntilALockIsAwaitedAsync("learnstack_platform", loser, "the second Finalize waits for the first's second transaction's fences");
        }
        finally
        {
            release.TrySetResult();
        }

        var completed = await winner;
        var answered = await loser!;

        completed.Outcome.Should().Be(ClosureFinalizeOutcome.Finalized);
        answered.Should().BeEquivalentTo(new ClosureFinalizeResult(ClosureFinalizeOutcome.AlreadyFinalized, "completed", Retained: completed.Retained));
        second.Subjects.Should().BeEmpty("the second Finalize never reaches the provider");
        first.Subjects.Should().HaveCount(1);
        (await kit.AuditAsync(waiting.Operator.CorrelationId!)).Select(row => row.Operation).Should().Equal([PlatformAdminScope.EnterOperation]);
    }

    /// <summary>
    /// A closure another operator's Finalize completed while this one called the provider is found completed under the second
    /// fences: this attempt records <c>already_finalized</c> with its own redaction, writes no state, and answers the
    /// closure's recorded counts.
    /// </summary>
    [Fact]
    public async Task A_Finalize_whose_closure_another_operator_completed_during_its_provider_call_records_already_finalized()
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var world = await BegunAsync(kit);
        var other = new RecordingAccountRemoval();
        ClosureFinalizeResult? inner = null;
        var removal = new RecordingAccountRemoval
        {
            Probe = async () => inner = await kit.Owner(removal: other).FinalizeAsync(Admission(), world.Closure),
        };
        var admission = Admission();

        var result = await kit.Owner(removal: removal).FinalizeAsync(admission, world.Closure);

        inner!.Outcome.Should().Be(ClosureFinalizeOutcome.Finalized);
        result.Outcome.Should().Be(ClosureFinalizeOutcome.AlreadyFinalized);
        result.Status.Should().Be("completed");
        result.Groups.Should().BeNull("this attempt completed nothing");
        result.Redaction.Should().NotBeNull("this attempt's own redaction committed with its intent row");
        result.Retained.Should().BeEquivalentTo(inner.Retained);
        other.Subjects.Should().HaveCount(1);
        removal.Subjects.Should().HaveCount(1);
        var rows = await kit.AuditAsync(admission.Operator.CorrelationId!);
        rows.Select(row => row.Operation).Should().Equal(PlatformAdminScope.EnterOperation, Attempt, Result);
        var recorded = JsonNode.Parse(rows[^1].After)!;
        (recorded["outcome"]!.GetValue<string>(), recorded["provider"]!.GetValue<string>()).Should().Be(("already_finalized", "removed"));
        (await kit.UserAsync(world.Person)).Status.Should().Be("Closed");
    }

    /// <summary>
    /// Anything that changed the closure or its person between the two transactions is a broken invariant, named and logged
    /// by its closed name: the provider's account is already gone, nothing of the second transaction committed, and no result
    /// row is written. Each disjunct of the second transaction's judgment is driven alone.
    /// </summary>
    [Theory]
    [InlineData("version")]
    [InlineData("stamp")]
    [InlineData("status")]
    [InlineData("account")]
    [InlineData("group")]
    public async Task A_closure_changed_between_the_two_transactions_is_a_defect_after_the_account_is_gone(string change)
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var world = await BegunAsync(kit);
        var changed = change switch
        {
            "version" => $"UPDATE public.data_subject_requests SET row_version = row_version + 1 WHERE id = '{world.Closure}'",
            "stamp" => $"UPDATE public.data_subject_requests SET updated_at = updated_at + interval '1 second' WHERE id = '{world.Closure}'",
            "status" => $"UPDATE public.data_subject_requests SET status = 'cancelled' WHERE id = '{world.Closure}'",
            "account" => $"UPDATE public.users SET status = 'Active' WHERE id = '{world.Person}'",
            _ => $"""
                INSERT INTO public.memberships(id,tenant_id,organization_id,user_id,status,created_at,created_by,deleted_at,deleted_by)
                VALUES('{Guid.CreateVersion7()}','{TenantB}','{SchemaFixture.OrgB1}','{world.Person}','Revoked',clock_timestamp(),
                    '{UserId.SystemActor.Value}',clock_timestamp(),'{UserId.SystemActor.Value}')
                """,
        };
        var removal = new RecordingAccountRemoval { Probe = () => kit.SqlAsync("SET session_replication_role = replica; " + changed) };
        var logs = new CapturingLogger<AccountClosureOperatorOperations>();
        var admission = Admission();

        var act = () => kit.Owner(removal: removal, logger: logs).FinalizeAsync(admission, world.Closure);

        await act.Should().ThrowAsync<InvalidOperationException>();
        logs.Lines.Should().ContainSingle().Which.Should().StartWith("7122 Error ").And.Contain("closure_changed_between_transactions")
            .And.NotContain(world.Person.ToString("D")).And.NotContain(world.ProviderSubject);
        removal.Subjects.Should().Equal([world.ProviderSubject], "the account was deleted before the second transaction judged");
        (await kit.UserAsync(world.Person)).Status.Should().Be(change == "account" ? "Active" : "Closing", "nothing of the second transaction committed");
        foreach (var group in Served)
        {
            (await RequestStateAsync(kit, group, world.Erasures[group])).Status.Should().Be("awaiting_privileged_step");
        }

        (await kit.RequestAsync(world.Closure)).Status.Should().Be(change == "status" ? "cancelled" : "fulfilling");
        (await kit.AuditAsync(admission.Operator.CorrelationId!)).Select(row => row.Operation).Should()
            .Equal([PlatformAdminScope.EnterOperation, Attempt], "the intent row committed with the redaction and no result row was written");
    }

    /// <summary>
    /// A row another session changed under the second transaction (the User no longer Closing when the act closes it) is a
    /// defect by its closed name, and the whole second transaction rolls back: no earlier statement of it, the Closed User
    /// included, survives. An awaiting erasure cannot change under it: the transaction locks them first, so one the
    /// operator's completion finished meanwhile is accepted as completed (the fences test of this class).
    /// </summary>
    [Theory]
    [InlineData("account_not_closed")]
    public async Task A_row_changed_under_the_second_transaction_is_a_defect_that_rolls_the_whole_transaction_back(string defect)
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var world = await BegunAsync(kit);
        var armed = true;
        var scope = new TappedScope(kit.Scope())
        {
            BeforeStatement = async sql =>
            {
                if (!armed)
                {
                    return;
                }

                if (defect == "account_not_closed" && sql.Contains("SET status = 'Closed'", StringComparison.Ordinal))
                {
                    armed = false;
                    await kit.SqlAsync($"SET session_replication_role = replica; UPDATE public.users SET status = 'Active' WHERE id = '{world.Person}'");
                }
            },
        };
        var removal = new RecordingAccountRemoval();
        var logs = new CapturingLogger<AccountClosureOperatorOperations>();
        var admission = Admission();

        var act = () => kit.Owner(removal: removal, scope: scope, logger: logs).FinalizeAsync(admission, world.Closure);

        await act.Should().ThrowAsync<InvalidOperationException>();
        armed.Should().BeFalse("the hook found the statement it was set for");
        logs.Lines.Should().ContainSingle().Which.Should().StartWith("7122 Error ").And.Contain(defect).And.NotContain(world.Person.ToString("D"));
        removal.Subjects.Should().HaveCount(1);
        var user = await kit.UserAsync(world.Person);
        (user.Status, user.SubjectPresent).Should().Be(("Active", true),
            "the User's move to Closed was rolled back with the rest");
        (await kit.RequestAsync(world.Closure)).Status.Should().Be("fulfilling");
        foreach (var group in Served)
        {
            (await RequestStateAsync(kit, group, world.Erasures[group])).Status.Should().Be("awaiting_privileged_step");
        }

        (await kit.AuditAsync(admission.Operator.CorrelationId!)).Select(row => row.Operation).Should().Equal([PlatformAdminScope.EnterOperation, Attempt]);
    }

    /// <summary>
    /// A person with one more open closure beside the finalized one than the bound is refused after the provider call: the
    /// closures are read last, in the second transaction, and the defect rolls the whole of it back. Exactly the bound many
    /// others complete with the closure. The world already holds one other (the second closure in its window), so the cases
    /// add the bound less one and the bound.
    /// </summary>
    [Theory]
    [InlineData(ClosureLimits.MaximumParticipantGroups - 1, true)]
    [InlineData(ClosureLimits.MaximumParticipantGroups, false)]
    public async Task The_closures_a_person_holds_beyond_the_bound_are_a_defect_that_rolls_the_second_transaction_back(int added, bool finalized)
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var world = await BegunAsync(kit);
        var membership = (await kit.MembershipsAsync(TenantB, null, world.Person))[0];
        // Open closures in groups of no organization that exists, written by the owner with the guards paused; the tenant is one
        // of the person's participants, so the second transaction holds its fence.
        await kit.SqlAsync($"""
            SET session_replication_role = replica;
            INSERT INTO public.data_subject_requests(id,tenant_id,organization_id,kind,origin,subject_user_id,requester_user_id,status,
                membership_snapshot,execute_at,created_at,created_by,row_version)
            SELECT uuidv7(), '{TenantB}', uuidv7(), 'closure', 'self', '{world.Person}', '{world.Person}', 'scheduled',
                '["{membership}"]'::jsonb, created.at + interval '24 hours', created.at, '{world.Person}', 0
            FROM (SELECT now() - interval '2 days' AS at) AS created, generate_series(1, {added}) AS series
            """);
        var logs = new CapturingLogger<AccountClosureOperatorOperations>();
        var admission = Admission();

        var act = () => kit.Owner(logger: logs).FinalizeAsync(admission, world.Closure);

        if (finalized)
        {
            var result = await act();
            result.Outcome.Should().Be(ClosureFinalizeOutcome.Finalized);
            result.Groups!.ClosuresCompleted.Should().Be(ClosureLimits.MaximumParticipantGroups + 1,
                "the bound many others and the closure itself complete: the bound itself is not beyond the bound");
            return;
        }

        await act.Should().ThrowAsync<InvalidOperationException>();
        logs.Lines.Should().ContainSingle().Which.Should().StartWith("7122 Error ").And.Contain("open_closures_beyond_bound");
        (await kit.UserAsync(world.Person)).Status.Should().Be("Closing", "the second transaction rolled back");
        (await kit.RequestAsync(world.Closure)).Status.Should().Be("fulfilling");
        foreach (var group in Served)
        {
            (await RequestStateAsync(kit, group, world.Erasures[group])).Status.Should().Be("awaiting_privileged_step");
        }
    }

    /// <summary>
    /// A Closing account whose raw provider subject is already gone cannot be removed at the provider: the act is abandoned
    /// before anything is redacted or asked, with the closed name logged.
    /// </summary>
    [Fact]
    public async Task A_Closing_account_without_a_provider_subject_is_a_defect_before_any_redaction()
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var world = await BegunAsync(kit);
        await kit.SqlAsync($"SET session_replication_role = replica; UPDATE public.users SET provider_subject = NULL WHERE id = '{world.Person}'");
        var removal = new RecordingAccountRemoval();
        var logs = new CapturingLogger<AccountClosureOperatorOperations>();
        var admission = Admission();

        var act = () => kit.Owner(removal: removal, logger: logs).FinalizeAsync(admission, world.Closure);

        await act.Should().ThrowAsync<InvalidOperationException>();
        logs.Lines.Should().ContainSingle().Which.Should().StartWith("7122 Error ").And.Contain("closing_account_without_provider_subject");
        removal.Subjects.Should().BeEmpty();
        (await PlantedAsync(kit, world)).Values.Should().OnlyContain(row => row.Address != null, "nothing was redacted");
        (await kit.AuditAsync(admission.Operator.CorrelationId!)).Select(row => row.Operation).Should().Equal([PlatformAdminScope.EnterOperation]);
    }

    /// <summary>
    /// A result row recording a stop at the provider that cannot be written, because the write fails or outlasts its
    /// three-second budget, changes no answer: the act answers the stop it found, the intent row stands alone, the failure is
    /// logged critical by the closed outcome, and a repeat finishes.
    /// </summary>
    [Theory]
    [InlineData("fails")]
    [InlineData("outlasts_the_budget")]
    public async Task A_result_row_that_cannot_be_written_leaves_the_answer_unchanged_and_is_logged_critical(string how)
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var world = await BegunAsync(kit);
        var removal = new RecordingAccountRemoval { Answer = call => call == 1 ? AccountRemovalOutcome.Refused : AccountRemovalOutcome.Absent };
        var scope = new TappedScope(kit.Scope())
        {
            ReplaceWriteResult = how == "fails"
                ? (_, _) => Task.FromException(new InvalidOperationException("forced result failure"))
                : (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token),
        };
        var logs = new CapturingLogger<AccountClosureOperatorOperations>();
        var admission = Admission();
        var clock = Stopwatch.StartNew();

        var result = await kit.Owner(removal: removal, scope: scope, logger: logs).FinalizeAsync(admission, world.Closure);

        clock.Stop();
        result.Outcome.Should().Be(ClosureFinalizeOutcome.ProviderRefused, "the answer is what the act found");
        result.Redaction.Should().NotBeNull();
        if (how != "fails")
        {
            clock.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(2.5)).And.BeLessThan(TimeSpan.FromSeconds(15), "the write is given three seconds");
        }

        logs.Lines.Should().Contain(line => line.StartsWith("7121 Warning ", StringComparison.Ordinal))
            .And.ContainSingle(line => line.StartsWith("7123 Critical ", StringComparison.Ordinal) && line.Contains("provider_refused", StringComparison.Ordinal))
            .Which.Should().NotContain(world.Person.ToString("D")).And.NotContain(world.ProviderSubject);
        (await kit.AuditAsync(admission.Operator.CorrelationId!)).Select(row => row.Operation).Should()
            .Equal([PlatformAdminScope.EnterOperation, Attempt], "only the intent row stands");
        (await kit.UserAsync(world.Person)).Status.Should().Be("Closing");

        (await kit.Owner(removal: removal).FinalizeAsync(Admission(), world.Closure)).Outcome.Should().Be(ClosureFinalizeOutcome.Finalized);
        (await kit.UserAsync(world.Person)).Status.Should().Be("Closed");
    }

    /// <summary>
    /// A second closure of the person that already began (fulfilling) is completed by the first's Finalize from fulfilling,
    /// with the closure's retained counts, as one never begun is completed from scheduled.
    /// </summary>
    [Fact]
    public async Task A_second_closure_that_began_is_completed_by_the_first_s_Finalize()
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var world = await BegunAsync(kit);
        await kit.BackdateAsync(world.SecondClosure, TenantB, null);
        (await kit.Owner().BeginAsync(AccountClosureBeginKit.Admission(), world.SecondClosure)).Outcome.Should().Be(ClosureBeginOutcome.Resumed);
        (await kit.RequestAsync(world.SecondClosure)).Status.Should().Be("fulfilling");

        var result = await kit.Owner().FinalizeAsync(Admission(), world.Closure);

        result.Outcome.Should().Be(ClosureFinalizeOutcome.Finalized);
        result.Groups!.ClosuresCompleted.Should().Be(2);
        (await kit.RequestAsync(world.SecondClosure)).Status.Should().Be("completed");
        var recorded = JsonNode.Parse((await RequestStateAsync(kit, TenantWideB, world.SecondClosure)).Result)!.AsObject();
        AssertRetainedCounts(recorded, result.Redaction!.Total, "the begun second closure records the total");
        recorded["unserved_group"]!.GetValue<long>().Should().Be(1);
    }

    /// <summary>
    /// A person whose groups are all organizations (no tenant-wide Membership in either tenant) is finalized through each
    /// organization's erasure: the closure is filed in an organization, every group's fan-out erasure is read by its own
    /// group, both tenants are redacted and the person is closed.
    /// </summary>
    [Fact]
    public async Task A_person_with_groups_of_organizations_alone_is_finalized_through_each_group_s_erasure()
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var person = await kit.PersonAsync();
        var memberships = new Dictionary<ClosureParticipantGroup, Guid>
        {
            [BranchA1] = await kit.MemberAsync(TenantA, SchemaFixture.OrgA1, person, BuiltInRoleKey.Learner),
            [new(TenantId.From(TenantA), OrganizationId.From(SchemaFixture.OrgA2))] =
                await kit.MemberAsync(TenantA, SchemaFixture.OrgA2, person, BuiltInRoleKey.Learner),
            [BranchB1] = await kit.MemberAsync(TenantB, SchemaFixture.OrgB1, person, BuiltInRoleKey.Learner),
        };
        var closure = await kit.ClosureAsync(TenantA, SchemaFixture.OrgA1, person);
        (await kit.Owner().BeginAsync(AccountClosureBeginKit.Admission(), closure)).Groups.Should().Be(new ClosureBeginTally(3, 3, 0, 0, 0, 0));
        var erasures = new Dictionary<ClosureParticipantGroup, Guid>();
        foreach (var (group, member) in memberships)
        {
            erasures[group] = await FanOutErasureAsync(kit, group, person, member, awaiting: true);
        }

        var world = new World(person, "fixture-subject-" + person.ToString("N"), closure, Guid.Empty, erasures,
            new Dictionary<string, Guid> { ["tenant A"] = await PlantAsync(kit, TenantA, person), ["tenant B"] = await PlantAsync(kit, TenantB, person) });

        var result = await kit.Owner().FinalizeAsync(Admission(), closure);

        result.Outcome.Should().Be(ClosureFinalizeOutcome.Finalized);
        result.Groups.Should().Be(new ClosureFinalizeTally(Served: 3, Ready: 3, Pending: 0, Unserved: 0, ErasuresCompleted: 3, ClosuresCompleted: 1));
        result.UnservedGroups.Should().BeEmpty();
        foreach (var (group, request) in erasures)
        {
            (await RequestStateAsync(kit, group, request)).Status.Should().Be("completed", group.ToString());
        }

        (await RequestStateAsync(kit, BranchA1, closure)).Status.Should().Be("completed");
        (await PlantedAsync(kit, world)).Values.Should().OnlyContain(row => row.Address == null);
        (await kit.UserAsync(person)).Status.Should().Be("Closed");
    }

    /// <summary>
    /// A Membership revoked after the Begin is still the person's: its group stays a participant and ready, and the
    /// redaction compares the rows of the tenant with the revoked Membership too, so a row that names only that Membership
    /// (no actor, no reference to the person) is selected and its payload reference is redacted.
    /// </summary>
    [Fact]
    public async Task A_Membership_revoked_after_the_Begin_is_still_a_reference_the_redaction_compares()
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var world = await BegunAsync(kit);
        var membership = (await kit.MembershipsAsync(TenantA, SchemaFixture.OrgA1, world.Person)).Single();
        await AccountClosureBeginTests.RevokeAsync(kit, TenantA, SchemaFixture.OrgA1, world.Person, membership);
        await AccountClosureCanary.PlantMembershipReferenceRowAsync(kit.Database.PlatformConnectionString, TenantA, membership);
        (await DataErasureExecutionTests.IdentifiersInPayloadsAsync(kit.Database.PlatformConnectionString, TenantA, [membership.ToString("D")]))
            .Should().NotBeEmpty("the row names the revoked Membership before the act");

        var result = await kit.Owner().FinalizeAsync(Admission(), world.Closure);

        result.Outcome.Should().Be(ClosureFinalizeOutcome.Finalized);
        result.Groups.Should().Be(new ClosureFinalizeTally(3, 3, 0, 1, ErasuresCompleted: 3, ClosuresCompleted: 2),
            "the revoked Membership's group is still a served participant");
        result.Redaction!.Tenants[TenantId.From(TenantA)].RowsSelected.Should().Be(2,
            "the planted actor row and the row that names only the revoked Membership");
        (await DataErasureExecutionTests.IdentifiersInPayloadsAsync(kit.Database.PlatformConnectionString, TenantA, [membership.ToString("D")]))
            .Should().BeEmpty("the revoked Membership's reference was redacted");
    }
}
