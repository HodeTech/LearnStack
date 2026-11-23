using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using LearnStack.Infrastructure.MultiTenancy;
using LearnStack.Modules.Identity.Infrastructure.DataRequests;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Platform;
using LearnStack.Tests.Fixtures;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Xunit;
using static LearnStack.Tests.Integration.Database.DataRequestSchemaSeed;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The closure's Finalize owner (ADR-0107 § 7, ADR-0108 § 8) against a real database, the real platform scope with the real
/// audit store and the real redaction executor over the production locators, the real Tenancy lifecycle read, the request
/// guard's closure arms and the <c>users</c> trigger exception. The identity provider is a recording stand-in; each group's
/// fan-out erasure is written as the group's act and its job leave it, by the application role under the canonical fences.
/// Every statement of the act runs as <c>learnstack_platform</c> through the scope.
/// </summary>
/// <remarks>
/// <para>
/// Each test runs in its own disposable copy of the seeded schema and closes a fresh person, because a User a closure moved
/// never returns to Active. The person holds Memberships in tenant A's tenant-wide group and branch A1 and in tenant B's
/// tenant-wide group (three served groups) and in branch A2, which is archived after the person filed (unserved). Their
/// closure was filed in tenant A's tenant-wide group and is due; a second closure, filed in tenant B and still in its
/// window, was never begun.
/// </para>
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed partial class AccountClosureFinalizeTests(SchemaFixture schema)
{
    private const string Attempt = "platform.closure.finalize_attempt";
    private const string Result = "platform.closure.finalize";
    private static readonly Guid TenantA = SchemaFixture.TenantA;
    private static readonly Guid TenantB = SchemaFixture.TenantB;
    private static readonly ClosureParticipantGroup TenantWideA = new(TenantId.From(TenantA), null);
    private static readonly ClosureParticipantGroup BranchA1 = new(TenantId.From(TenantA), OrganizationId.From(SchemaFixture.OrgA1));
    private static readonly ClosureParticipantGroup BranchA2 = new(TenantId.From(TenantA), OrganizationId.From(SchemaFixture.OrgA2));
    private static readonly ClosureParticipantGroup TenantWideB = new(TenantId.From(TenantB), null);
    private static readonly ClosureParticipantGroup[] Served = [TenantWideA, BranchA1, TenantWideB];

    /// <summary>
    /// The whole act: the first transaction redacts every participant tenant and the sentinel and commits with the intent
    /// row; the provider is called with the raw subject between the transactions, while no fence and no transaction is
    /// held; the second moves the User to Closed with the raw subject removed, completes every served group's erasure, the
    /// closure and the person's never-begun second closure, and commits with the result row. A repeat answers what was
    /// recorded and writes nothing but its entry row. No state, log line or answer names the person or the raw subject.
    /// </summary>
    [Fact]
    public async Task A_Finalize_redacts_removes_the_account_closes_the_person_and_completes_every_open_request_in_two_transactions()
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var world = await BegunAsync(kit);
        var removal = new RecordingAccountRemoval();
        var logs = new CapturingLogger<AccountClosureOperatorOperations>();
        var between = new List<string>();
        removal.Probe = async () =>
        {
            // Between the transactions: the redaction and its intent row committed, nothing else moved, and the platform
            // connection holds no fence and no open transaction while the provider call runs.
            (await kit.UserAsync(world.Person)).Status.Should().Be("Closing");
            (await kit.RequestAsync(world.Closure)).Status.Should().Be("fulfilling");
            (await kit.PlatformAdvisoryLocksAsync()).Should().BeEmpty("the first transaction's keys ended at its commit");
            between.AddRange(await PlatformBackendStatesAsync(kit));
            (await PlantedAsync(kit, world)).Values.Should().OnlyContain(row => row.Address == null && row.Agent == null && row.Email == null,
                "the redaction committed with the intent row, before the provider call");
        };
        var keyBefore = await SubjectKeyAsync(kit, world.Person);
        var admission = Admission("INC-801");

        var result = await kit.Owner(removal: removal, logger: logs).FinalizeAsync(admission, world.Closure);

        result.Outcome.Should().Be(ClosureFinalizeOutcome.Finalized);
        result.Status.Should().Be("completed");
        result.Groups.Should().Be(new ClosureFinalizeTally(Served: 3, Ready: 3, Pending: 0, Unserved: 1, ErasuresCompleted: 3, ClosuresCompleted: 2));
        result.UnservedGroups.Should().Equal(BranchA2);
        result.Redaction!.Tenants.Keys.Should().BeEquivalentTo([TenantId.From(TenantA), TenantId.From(TenantB)],
            "every participant tenant is redacted, the unserved group's tenant included");
        result.Redaction.Total.Redacted[AuditRedactedColumn.IpAddress].Should().Be(3, "the two planted tenant rows and the sentinel one");
        result.Retained.Should().ContainKey("unserved_group").WhoseValue.Should().Be(1);
        removal.Subjects.Should().Equal([world.ProviderSubject], "the raw subject is read once and passed to the port");
        between.Should().Contain("idle", "the act's own platform connection is observed while the provider runs")
            .And.NotContain("idle in transaction", "the transaction the checkpoint opened is not begun on the server until its first statement");

        // The person: Closed, the raw subject gone, the key and the creation evidence kept as the tombstone.
        var user = await kit.UserAsync(world.Person);
        (user.Status, user.SubjectPresent).Should().Be(("Closed", false));
        (await SubjectKeyAsync(kit, world.Person)).Should().Be(keyBefore, "the subject key is the permanent tombstone");
        (await CreationRowsAsync(kit, world.Person)).Should().Be(1, "the creation row stays, redacted, never deleted");

        // Every served group's erasure completed with its tenant's retained audit kinds; the closure and the second one too.
        foreach (var group in Served)
        {
            var erasure = await RequestStateAsync(kit, group, world.Erasures[group]);
            erasure.Status.Should().Be("completed", group.ToString());
            var recorded = JsonNode.Parse(erasure.Result)!.AsObject();
            recorded["consent_evidence"]!.GetValue<long>().Should().Be(2, "the tenant side's record stays as its participants wrote it");
            recorded.ContainsKey("audit_actor_reference").Should().BeTrue("the redaction's retained kinds replace the job's");
        }

        foreach (var (group, closure) in new[] { (TenantWideA, world.Closure), (TenantWideB, world.SecondClosure) })
        {
            var completed = await RequestStateAsync(kit, group, closure);
            completed.Status.Should().Be("completed", "the never-begun second closure completes with the one finalized");
            JsonNode.Parse(completed.Result)!["unserved_group"]!.GetValue<long>().Should().Be(1);
        }

        // The evidence: the entry, the intent with the summed counts and the result, all under the sentinel with the ticket.
        var rows = await kit.AuditAsync(admission.Operator.CorrelationId!);
        rows.Select(row => row.Operation).Should().Equal(PlatformAdminScope.EnterOperation, Attempt, Result);
        rows.Should().OnlyContain(row => row.Tenant == TenantId.PlatformSentinel.Value && row.Actor == null
            && row.Reason == "closure_finalize" && row.Metadata.Contains("INC-801"));
        var intent = rows.Single(row => row.Operation == Attempt);
        JsonNode.DeepEquals(JsonNode.Parse(intent.After), JsonNode.Parse(result.Redaction.ToClosedJson())).Should().BeTrue(intent.After);
        var recordedResult = JsonNode.Parse(rows.Single(row => row.Operation == Result).After)!;
        recordedResult["outcome"]!.GetValue<string>().Should().Be("finalized");
        recordedResult["account"]!.GetValue<string>().Should().Be("closed");
        recordedResult["provider"]!.GetValue<string>().Should().Be("removed");
        recordedResult["groups"]!["closures_completed"]!.GetValue<int>().Should().Be(2);
        foreach (var row in rows)
        {
            (row.Before + row.After + row.Metadata).Should().NotContain(world.Person.ToString("D")).And.NotContain(world.ProviderSubject)
                .And.NotContain(keyBefore, "no row of the act names the person, the raw subject or the key");
        }

        string.Join(' ', JsonSerializer.Serialize(result.Groups), JsonSerializer.Serialize(result.Retained), result.Redaction.ToClosedJson(),
                string.Join(',', result.UnservedGroups!.Select(group => group.TenantId.Value + "/" + group.OrganizationId?.Value)))
            .Should().NotContain(world.Person.ToString("D")).And.NotContain(world.ProviderSubject, "the answer names no person");
        logs.Lines.Should().NotContain(line => line.Contains(world.Person.ToString("D"), StringComparison.Ordinal)
            || line.Contains(world.ProviderSubject, StringComparison.Ordinal) || line.Contains("INC-801", StringComparison.Ordinal));

        // A repeat finds what landed: it answers the recorded result, asks the provider nothing and writes only its entry row.
        var repeat = Admission("INC-802");
        var again = await kit.Owner(removal: removal).FinalizeAsync(repeat, world.Closure);
        again.Outcome.Should().Be(ClosureFinalizeOutcome.AlreadyFinalized);
        again.Retained.Should().BeEquivalentTo(result.Retained);
        removal.Subjects.Should().HaveCount(1);
        (await kit.AuditAsync(repeat.Operator.CorrelationId!)).Select(row => row.Operation).Should().Equal(PlatformAdminScope.EnterOperation);
    }

    /// <summary>
    /// The provider's three stops and a port that throws: each leaves the person Closing and the closure fulfilling, with
    /// the redaction committed under its intent row and a result row saying where the act stopped. A repeat redacts again,
    /// which changes no row, writes its own intent row, finds the account absent, which is success, and finalizes.
    /// </summary>
    [Theory]
    [InlineData(AccountRemovalOutcome.Refused, ClosureFinalizeOutcome.ProviderRefused, "provider_refused")]
    [InlineData(AccountRemovalOutcome.Unavailable, ClosureFinalizeOutcome.ProviderUnavailable, "provider_unavailable")]
    [InlineData(AccountRemovalOutcome.Uncertain, ClosureFinalizeOutcome.ProviderUncertain, "provider_uncertain")]
    [InlineData((AccountRemovalOutcome)0, ClosureFinalizeOutcome.ProviderUncertain, "provider_uncertain")]
    public async Task A_provider_stop_leaves_the_closure_fulfilling_with_its_evidence_and_a_repeat_finishes_it(
        AccountRemovalOutcome stop, ClosureFinalizeOutcome expected, string token)
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var world = await BegunAsync(kit);
        var removal = new RecordingAccountRemoval { Answer = call => call == 1 ? stop : AccountRemovalOutcome.Absent };
        if (stop == 0)
        {
            removal.Failure = new HttpRequestException("the transport failed after the deletion may have been sent");
        }

        var logs = new CapturingLogger<AccountClosureOperatorOperations>();
        var first = Admission("INC-811");

        var stopped = await kit.Owner(removal: removal, logger: logs).FinalizeAsync(first, world.Closure);

        stopped.Outcome.Should().Be(expected);
        stopped.Status.Should().Be("fulfilling");
        (await kit.UserAsync(world.Person)).Should().Match<AccountClosureBeginKit.UserRow>(row => row.Status == "Closing" && row.SubjectPresent);
        (await kit.RequestAsync(world.Closure)).Status.Should().Be("fulfilling");
        foreach (var group in Served)
            (await RequestStateAsync(kit, group, world.Erasures[group])).Status.Should().Be("awaiting_privileged_step");
        var rows = await kit.AuditAsync(first.Operator.CorrelationId!);
        rows.Select(row => (row.Operation, row.After.Contains(token, StringComparison.Ordinal))).Should().Equal(
            (PlatformAdminScope.EnterOperation, false), (Attempt, false), (Result, true));
        logs.Lines.Should().Contain(line => line.Contains(token, StringComparison.Ordinal));
        logs.Lines.Should().NotContain(line => line.Contains(world.ProviderSubject, StringComparison.Ordinal)
            || line.Contains("deletion may have been sent", StringComparison.Ordinal));
        var redacted = await PlantedAsync(kit, world);
        redacted.Values.Should().OnlyContain(row => row.Address == null, "the redaction committed before the provider was asked");

        removal.Failure = null;
        var second = Admission("INC-812");
        var finished = await kit.Owner(removal: removal).FinalizeAsync(second, world.Closure);

        finished.Outcome.Should().Be(ClosureFinalizeOutcome.Finalized);
        finished.Redaction!.Total.Redacted.Values.Should().AllSatisfy(count => count.Should().Be(0, "a repeat's redaction changes no row"));
        (await PlantedAsync(kit, world)).Should().BeEquivalentTo(redacted, "the second run rewrote nothing");
        (await kit.UserAsync(world.Person)).Status.Should().Be("Closed");
        removal.Subjects.Should().Equal([world.ProviderSubject, world.ProviderSubject]);
        var repeated = await kit.AuditAsync(second.Operator.CorrelationId!);
        repeated.Select(row => row.Operation).Should().Equal(PlatformAdminScope.EnterOperation, Attempt, Result);
        JsonNode.Parse(repeated[^1].After)!["provider"]!.GetValue<string>().Should().Be("absent", "a missing account is success");
    }

    /// <summary>
    /// Every served group must be ready, and nothing is written while one is not: a group whose fan-out erasure is still
    /// running, a group with no erasure, a group whose newest erasure completed before this closure began (a person admitted
    /// again since), and a group whose erasure does not cover a Membership the person holds there. The provider is never
    /// asked and the scope's entry row is the only record.
    /// </summary>
    [Theory]
    [InlineData("running")]
    [InlineData("missing")]
    [InlineData("completed_before_begin")]
    [InlineData("snapshot_short")]
    public async Task A_Finalize_before_every_served_group_is_ready_answers_closed_tallies_and_writes_nothing(string variant)
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var world = await BegunAsync(kit, pendingGroup: TenantWideB, variant: variant);
        var removal = new RecordingAccountRemoval();
        var admission = Admission();

        var result = await kit.Owner(removal: removal).FinalizeAsync(admission, world.Closure);

        result.Outcome.Should().Be(ClosureFinalizeOutcome.GroupsPending, variant);
        result.Status.Should().Be("fulfilling");
        result.Groups.Should().Be(new ClosureFinalizeTally(Served: 3, Ready: 2, Pending: 1, Unserved: 1));
        result.UnservedGroups.Should().BeNull("a refusal names no group");
        removal.Subjects.Should().BeEmpty("the provider is asked only once every served group is ready");
        (await kit.AuditAsync(admission.Operator.CorrelationId!)).Select(row => row.Operation).Should().Equal(PlatformAdminScope.EnterOperation);
        (await PlantedAsync(kit, world)).Values.Should().OnlyContain(row => row.Address != null, "nothing was redacted");
        (await kit.UserAsync(world.Person)).Status.Should().Be("Closing");
    }

    /// <summary>
    /// The guard of the stale erasure hazard, from the other side: a person's own erasure that awaited its privileged step when
    /// the closure began and was completed by the operator's erasure completion after it stands for its group, and Finalize
    /// leaves it as it completed.
    /// </summary>
    [Fact]
    public async Task An_erasure_completed_after_the_closure_began_stands_for_its_group()
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var world = await BegunAsync(kit, pendingGroup: TenantWideB, variant: "completed_after_begin");

        var result = await kit.Owner().FinalizeAsync(Admission(), world.Closure);

        result.Outcome.Should().Be(ClosureFinalizeOutcome.Finalized);
        result.Groups.Should().Be(new ClosureFinalizeTally(3, 3, 0, 1, ErasuresCompleted: 2, ClosuresCompleted: 2),
            "the group's erasure was already completed and is not completed again");
    }

    /// <summary>
    /// Requests that are not begun closures are refused before any fence, the provider is never asked, and the scope's entry
    /// row is the only record: no request, an export, an erasure, a closure never begun and a cancelled one.
    /// </summary>
    [Fact]
    public async Task Requests_that_are_not_begun_closures_are_refused_before_any_fence_and_write_nothing()
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var person = await kit.PersonAsync();
        await kit.MemberAsync(TenantA, null, person, BuiltInRoleKey.Learner);
        var export = await kit.RequestRowAsync(TenantA, person, "export", "requested");
        var erasure = await kit.RequestRowAsync(TenantA, person, "erasure", "awaiting_privileged_step");
        var cancelled = await kit.RequestRowAsync(TenantA, person, "closure", "cancelled");
        var scheduled = await kit.ClosureAsync(TenantA, null, person);
        var removal = new RecordingAccountRemoval();

        foreach (var (request, outcome, status) in new (Guid, ClosureFinalizeOutcome, string?)[]
        {
            (Guid.CreateVersion7(), ClosureFinalizeOutcome.NotFound, null),
            (export, ClosureFinalizeOutcome.NotClosure, "requested"),
            (erasure, ClosureFinalizeOutcome.NotClosure, "awaiting_privileged_step"),
            (cancelled, ClosureFinalizeOutcome.NotFinalizable, "cancelled"),
            (scheduled, ClosureFinalizeOutcome.NotFinalizable, "scheduled"),
        })
        {
            var admission = Admission();
            var result = await kit.Owner(removal: removal).FinalizeAsync(admission, request);
            (result.Outcome, result.Status).Should().Be((outcome, status), outcome.ToString());
            (await kit.AuditAsync(admission.Operator.CorrelationId!)).Select(row => row.Operation).Should()
                .Equal([PlatformAdminScope.EnterOperation], "a refusal writes nothing beyond the entry row");
        }

        removal.Subjects.Should().BeEmpty();
        (await kit.UserAsync(person)).Status.Should().Be("Active");
    }

    /// <summary>
    /// A state no admitted path writes is refused closed and logged by its closed name alone: a closure fulfilling while its
    /// person is Active (here forced by the owner with the guards paused).
    /// </summary>
    [Fact]
    public async Task A_fulfilling_closure_whose_person_is_not_Closing_is_a_defect_logged_without_the_person()
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var person = await kit.PersonAsync();
        await kit.MemberAsync(TenantA, null, person, BuiltInRoleKey.Learner);
        var closure = await kit.RequestRowAsync(TenantA, person, "closure", "fulfilling");
        var logs = new CapturingLogger<AccountClosureOperatorOperations>();
        var removal = new RecordingAccountRemoval();

        var act = () => kit.Owner(removal: removal, logger: logs).FinalizeAsync(Admission(), closure);

        await act.Should().ThrowAsync<InvalidOperationException>();
        logs.Lines.Should().ContainSingle().Which.Should().Contain("fulfilling_closure_account_not_closing")
            .And.NotContain(person.ToString("D"));
        removal.Subjects.Should().BeEmpty();
    }

    private static PlatformAdmission Admission(string? ticket = null) => TestPlatformAdmissions.Admission(
        PlatformOperations.ClosureFinalize, ticket, correlationId: "00-closure-finalize-" + Guid.NewGuid().ToString("N"));

    /// <summary>A begun closure and where its groups stand, and the planted audit rows that name the person.</summary>
    private sealed record World(
        Guid Person, string ProviderSubject, Guid Closure, Guid SecondClosure, Dictionary<ClosureParticipantGroup, Guid> Erasures,
        Dictionary<string, Guid> Planted);

    /// <summary>
    /// The person, their Memberships, the due closure and the second one in its window, the archived branch, the Begin, and
    /// each served group's fan-out erasure awaiting the privileged step, unless <paramref name="pendingGroup"/> is left as
    /// <paramref name="variant"/> says. One row in each participant tenant and one in the sentinel name the person as actor.
    /// </summary>
    private static async Task<World> BegunAsync(
        AccountClosureBeginKit kit, ClosureParticipantGroup? pendingGroup = null, string variant = "ready")
    {
        var person = await kit.PersonAsync();
        var memberships = new Dictionary<ClosureParticipantGroup, Guid>
        {
            [TenantWideA] = await kit.MemberAsync(TenantA, null, person, BuiltInRoleKey.Learner),
            [BranchA1] = await kit.MemberAsync(TenantA, SchemaFixture.OrgA1, person, BuiltInRoleKey.Learner),
            [TenantWideB] = await kit.MemberAsync(TenantB, null, person, BuiltInRoleKey.Learner),
            [BranchA2] = await kit.MemberAsync(TenantA, SchemaFixture.OrgA2, person, BuiltInRoleKey.Learner),
        };
        if (variant == "completed_before_begin")
        {
            // An erasure of the group that completed before the closure began: the person was admitted to the group again
            // afterwards (the fixture's Membership stands for that later one), and the group's fan-out never ran.
            await kit.RequestRowAsync(TenantB, person, "erasure", "completed");
        }

        Guid? ownErasure = null;
        if (variant == "completed_after_begin")
        {
            ownErasure = await kit.RequestRowAsync(TenantB, person, "erasure", "awaiting_privileged_step");
        }

        var closure = await kit.ClosureAsync(TenantA, null, person);
        var second = await kit.ClosureAsync(TenantB, null, person, due: false);
        await kit.OrganizationStatusAsync(TenantA, SchemaFixture.OrgA2, "Archived");
        (await kit.Owner().BeginAsync(AccountClosureBeginKit.Admission(), closure)).Outcome.Should().Be(ClosureBeginOutcome.Begun);

        var erasures = new Dictionary<ClosureParticipantGroup, Guid>();
        foreach (var group in Served)
        {
            if (group == pendingGroup && variant is "missing" or "completed_before_begin")
            {
                continue;
            }

            if (group == pendingGroup && variant == "completed_after_begin")
            {
                var completed = await new DataRequestOperatorOperations(kit.Scope()).CompleteErasureAsync(
                    TestPlatformAdmissions.Admission(PlatformOperations.ErasureComplete), ownErasure!.Value);
                completed.Outcome.Should().Be(ErasureCompletionOutcome.Completed);
                erasures[group] = ownErasure.Value;
                continue;
            }

            erasures[group] = await FanOutErasureAsync(kit, group, person, memberships[group],
                awaiting: !(group == pendingGroup && variant == "running"));
            if (group == pendingGroup && variant == "snapshot_short")
            {
                // A Membership of the group the erasure's frozen snapshot does not hold, as an erasure adopted from before a
                // re-admission would: written by the owner with the guards paused.
                await kit.SqlAsync($"""
                    SET session_replication_role = replica;
                    INSERT INTO public.memberships(id,tenant_id,organization_id,user_id,status,created_at,created_by,deleted_at,deleted_by)
                    VALUES('{Guid.CreateVersion7()}','{TenantB}',NULL,'{person}','Revoked',clock_timestamp(),'{UserId.SystemActor.Value}',
                        clock_timestamp(),'{UserId.SystemActor.Value}')
                    """);
            }
        }

        var planted = new Dictionary<string, Guid>
        {
            ["tenant A"] = await PlantAsync(kit, TenantA, person),
            ["tenant B"] = await PlantAsync(kit, TenantB, person),
            ["sentinel"] = await PlantAsync(kit, TenantId.PlatformSentinel.Value, person),
        };
        return new World(person, "fixture-subject-" + person.ToString("N"), closure, second, erasures, planted);
    }

    /// <summary>
    /// A served group's fan-out erasure as the group's act creates it (born <c>fulfilling</c> by the application role under
    /// the person's User fence) and, unless still running, as its job leaves it: awaiting the privileged step with the
    /// tenant side's retained record.
    /// </summary>
    private static async Task<Guid> FanOutErasureAsync(
        AccountClosureBeginKit kit, ClosureParticipantGroup group, Guid person, Guid membership, bool awaiting)
    {
        var id = Guid.CreateVersion7();
        var (tenant, organization) = (group.TenantId.Value, group.OrganizationId?.Value);
        await using var app = await DataExportTestKit.OpenAppAsync(kit.Database.AppConnectionString);
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await FenceAsync(app, transaction, tenant, organization, person);
            await ExecuteAsync(app, transaction, """
                INSERT INTO public.data_subject_requests(id,tenant_id,organization_id,kind,origin,subject_user_id,requester_user_id,
                    status,membership_snapshot,execute_at,created_at,created_by)
                VALUES(@id,@tenant,@organization,'erasure','closure_fanout',@subject,NULL,'fulfilling',@snapshot::jsonb,NULL,now(),@system)
                """, ("id", id), ("tenant", tenant), ("organization", organization), ("subject", person), ("snapshot", Snapshot(membership)),
                ("system", UserId.SystemActor.Value));
            await transaction.CommitAsync();
        }

        if (awaiting)
        {
            await using var transaction = await app.BeginTransactionAsync();
            await FenceAsync(app, transaction, tenant, organization, null);
            await StepAsync(app, transaction, id, "result='{\"consent_evidence\":2,\"audit_actor_reference\":9}'");
            await StepAsync(app, transaction, id, "status='awaiting_privileged_step'");
            await transaction.CommitAsync();
        }

        return id;
    }

    /// <summary>A row naming the person as its actor, with an address, an agent and an e-mail, as the platform role writes it.</summary>
    private static async Task<Guid> PlantAsync(AccountClosureBeginKit kit, Guid tenant, Guid person)
    {
        var id = Guid.CreateVersion7();
        await using var platform = await AccountClosureBeginKit.OpenAsync(kit.Database.PlatformConnectionString);
        await using var command = new NpgsqlCommand("""
            INSERT INTO public.audit_log (id, "timestamp", tenant_id, organization_id, actor_user_id, actor_email, module, operation,
                operation_type, operation_class, entity_type, entity_id, outcome, correlation_id, ip_address, user_agent)
            VALUES (@id, clock_timestamp(), @tenant, NULL, @actor, 'person@example.test', 'canary', 'canary.closure.row', 'Update', 'Must',
                'Course', @entity, 'success', @correlation, '198.51.100.7'::inet, 'Canary/1.0')
            """, platform);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("actor", person);
        command.Parameters.AddWithValue("entity", Guid.CreateVersion7().ToString("D"));
        command.Parameters.AddWithValue("correlation", "00-" + id.ToString("N") + "-01");
        await command.ExecuteNonQueryAsync();
        return id;
    }

    private sealed record PlantedRow(string? Address, string? Agent, string? Email, Guid? Actor);

    private static async Task<Dictionary<string, PlantedRow>> PlantedAsync(AccountClosureBeginKit kit, World world)
    {
        await using var platform = await AccountClosureBeginKit.OpenAsync(kit.Database.PlatformConnectionString);
        var rows = new Dictionary<string, PlantedRow>();
        foreach (var (label, id) in world.Planted)
        {
            await using var command = new NpgsqlCommand(
                "SELECT host(ip_address), user_agent, actor_email, actor_user_id FROM public.audit_log WHERE id = @id", platform);
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue(label);
            rows[label] = new PlantedRow(reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetGuid(3));
        }

        rows.Values.Should().OnlyContain(row => row.Actor == world.Person, "the actor is retained: redaction rewrites six columns only");
        return rows;
    }

    private sealed record RequestState(string Status, string Result);

    /// <summary>A request in its own group, as the owner reads it.</summary>
    private static async Task<RequestState> RequestStateAsync(AccountClosureBeginKit kit, ClosureParticipantGroup group, Guid id)
    {
        await using var owner = await AccountClosureBeginKit.OpenAsync(kit.Database.MigrationConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();
        await ScopeAsync(owner, transaction, group.TenantId.Value, group.OrganizationId?.Value);
        await using var command = new NpgsqlCommand(
            "SELECT status, result::text FROM public.data_subject_requests WHERE id=@id", owner, transaction);
        command.Parameters.AddWithValue("id", id);
        await using var row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        return new RequestState(row.GetString(0), row.GetString(1));
    }

    private static async Task<string> SubjectKeyAsync(AccountClosureBeginKit kit, Guid person)
    {
        await using var owner = await AccountClosureBeginKit.OpenAsync(kit.Database.MigrationConnectionString);
        return await ScalarAsync<string>(owner, null, "SELECT encode(subject_key, 'hex') FROM public.users WHERE id=@id", ("id", person));
    }

    private static async Task<long> CreationRowsAsync(AccountClosureBeginKit kit, Guid person)
    {
        await using var platform = await AccountClosureBeginKit.OpenAsync(kit.Database.PlatformConnectionString);
        return await ScalarAsync<long>(platform, null,
            "SELECT count(*) FROM public.audit_log WHERE operation = 'identity.user.create' AND entity_id = @id",
            ("id", person.ToString("D")));
    }

    /// <summary>The transaction state of every other platform-role backend of this database.</summary>
    private static async Task<List<string>> PlatformBackendStatesAsync(AccountClosureBeginKit kit)
    {
        await using var observer = await AccountClosureBeginKit.OpenAsync(kit.Database.PlatformConnectionString);
        await using var command = new NpgsqlCommand("""
            SELECT COALESCE(state, '') FROM pg_catalog.pg_stat_activity
            WHERE datname = current_database() AND usename = 'learnstack_platform' AND pid <> pg_backend_pid()
            """, observer);
        await using var reader = await command.ExecuteReaderAsync();
        var states = new List<string>();
        while (await reader.ReadAsync()) states.Add(reader.GetString(0));
        return states;
    }
}

/// <summary>A logger that keeps every formatted line and its exception's text, to prove what an act never logs.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();

    public IReadOnlyList<string> Lines => [.. _lines];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        _lines.Enqueue(eventId.Id + " " + logLevel + " " + formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
}
