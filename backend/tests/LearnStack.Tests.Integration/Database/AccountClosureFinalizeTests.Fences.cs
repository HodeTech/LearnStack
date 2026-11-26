using FluentAssertions;
using LearnStack.Infrastructure.MultiTenancy;
using LearnStack.Modules.Identity.Infrastructure.DataRequests;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.Tests.Fixtures;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// Finalize's fences and the states another act or a lifecycle change leaves between its reads: the first transaction holds
/// the person's User fence alone while it redacts, an erasure the operator's erasure completion finishes while the second
/// transaction waits for it is taken as it completed, an unserved group whose fan-out erasure is still open keeps the act
/// waiting, a result row that cannot be written after the provider step is the retryable unavailable, and a participant set
/// past the closure's bounds is a defect named by its closed name.
/// </summary>
public sealed partial class AccountClosureFinalizeTests
{
    /// <summary>
    /// While the first transaction is held at its first redaction, the act holds the person's User key and nothing else, and
    /// an identity write in each participant tenant that takes the tenant's key exclusively, as an administrator's write and
    /// an erasure job do, commits within its own two-second bound. The act then finishes.
    /// </summary>
    [Fact]
    public async Task The_first_transaction_holds_the_User_fence_alone_so_participant_tenants_write_while_it_redacts()
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var world = await BegunAsync(kit);
        var gated = new GatedRedactionScope(kit.Scope());
        var finalize = kit.Owner(scope: gated).FinalizeAsync(Admission(), world.Closure);
        try
        {
            await gated.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            (await kit.PlatformAdvisoryLocksAsync()).Should().Equal(
                [(AccountClosureBeginKit.UserKey(world.Person), "ExclusiveLock")],
                "the redaction changes no User and no request, so no participant tenant's key is held while it runs");

            foreach (var tenant in new[] { TenantA, TenantB })
            {
                // The writer takes the tenant's key with a two-second lock timeout of its own, so a key the act still held
                // would fail it loudly; the elapsed time of the whole call (a cold connection included) proves nothing more.
                await AdmitSomeoneElseAsync(kit, tenant).WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        finally
        {
            gated.Release.TrySetResult();
        }

        (await finalize.WaitAsync(TimeSpan.FromSeconds(60))).Outcome.Should().Be(ClosureFinalizeOutcome.Finalized);
        (await kit.UserAsync(world.Person)).Status.Should().Be("Closed");
    }

    /// <summary>
    /// The operator's erasure completion finishes the person's own erasure while the second transaction waits for its row:
    /// the act takes it as it completed, completes only the erasures still awaiting, and finalizes, with no defect logged.
    /// </summary>
    [Fact]
    public async Task An_erasure_the_operator_completes_while_the_second_transaction_waits_is_taken_as_it_completed()
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var world = await OwnErasureWorldAsync(kit);
        var logs = new CapturingLogger<AccountClosureOperatorOperations>();
        NpgsqlConnection? side = null;
        NpgsqlTransaction? completing = null;
        var removal = new RecordingAccountRemoval
        {
            Probe = async () =>
            {
                // Between the transactions: Step 2's completion of the person's own erasure, locked and written, not committed.
                side = await AccountClosureBeginKit.OpenAsync(kit.Database.PlatformConnectionString);
                completing = await side.BeginTransactionAsync();
                await ExecuteAsync(side, completing, """
                    UPDATE public.data_subject_requests SET status = 'completed', row_version = row_version + 1
                    WHERE id = @id AND status = 'awaiting_privileged_step'
                    """, ("id", world.OwnErasure));
            },
        };
        try
        {
            var finalize = kit.Owner(removal: removal, logger: logs).FinalizeAsync(Admission(), world.Closure);
            await kit.WaitUntilALockIsAwaitedAsync("learnstack_platform", finalize,
                "the second transaction waits for the erasure the operator's completion holds");
            await completing!.CommitAsync();

            var result = await finalize.WaitAsync(TimeSpan.FromSeconds(30));

            result.Outcome.Should().Be(ClosureFinalizeOutcome.Finalized);
            result.Groups.Should().Be(new ClosureFinalizeTally(2, 2, 0, 0, ErasuresCompleted: 1, ClosuresCompleted: 1),
                "the erasure the operator completed is ready and is not completed again");
        }
        finally
        {
            if (completing is not null) await completing.DisposeAsync();
            if (side is not null) await side.DisposeAsync();
        }

        (await RequestStateAsync(kit, TenantWideB, world.OwnErasure)).Status.Should().Be("completed");
        (await RequestStateAsync(kit, TenantWideA, world.FanOut)).Status.Should().Be("completed");
        logs.Lines.Should().NotContain(line => line.StartsWith("7122 ", StringComparison.Ordinal), "an admitted path wrote it, so it is no defect");
    }

    /// <summary>
    /// A branch archived after its fan-out erasure started is unserved, and its erasure may never finish: its job can be dead
    /// and nothing re-queues a job in an archived group. Finalize does not wait for it, so the closure cannot stick: the person
    /// is closed, the branch is reported with the data it still holds, and its erasure is left as it is.
    /// </summary>
    [Fact]
    public async Task An_unserved_group_whose_fan_out_erasure_is_still_open_is_never_waited_for_and_is_reported()
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var person = await kit.PersonAsync();
        var tenantWide = await kit.MemberAsync(TenantA, null, person, BuiltInRoleKey.Learner);
        var branch = await kit.MemberAsync(TenantA, SchemaFixture.OrgA1, person, BuiltInRoleKey.Learner);
        var closure = await kit.ClosureAsync(TenantA, null, person);
        (await kit.Owner().BeginAsync(AccountClosureBeginKit.Admission(), closure)).Outcome.Should().Be(ClosureBeginOutcome.Begun);
        var served = await FanOutErasureAsync(kit, TenantWideA, person, tenantWide, awaiting: true);
        var running = await FanOutErasureAsync(kit, BranchA1, person, branch, awaiting: false);
        await kit.OrganizationStatusAsync(TenantA, SchemaFixture.OrgA1, "Archived");
        var removal = new RecordingAccountRemoval();

        var finalized = await kit.Owner(removal: removal).FinalizeAsync(Admission(), closure);

        finalized.Outcome.Should().Be(ClosureFinalizeOutcome.Finalized, "a closure that waited for an archived branch could not finish");
        finalized.Groups.Should().Be(new ClosureFinalizeTally(Served: 1, Ready: 1, Pending: 0, Unserved: 1, ErasuresCompleted: 1, ClosuresCompleted: 1),
            "the archived branch is not required, and its data is still there");
        finalized.UnservedGroups.Should().Equal(BranchA1);
        removal.Subjects.Should().HaveCount(1, "the provider account is deleted");
        (await RequestStateAsync(kit, BranchA1, running)).Status.Should().Be("fulfilling", "an erasure nothing can finish is left as it is");
        (await RequestStateAsync(kit, TenantWideA, served)).Status.Should().Be("completed");
    }

    /// <summary>
    /// The same branch, archived after its job reached the privileged step: Finalize completes that erasure with the others,
    /// and the branch is not reported as kept, because its data was erased.
    /// </summary>
    [Fact]
    public async Task An_unserved_group_whose_fan_out_erasure_awaits_its_privileged_step_is_completed_and_not_reported()
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var person = await kit.PersonAsync();
        var tenantWide = await kit.MemberAsync(TenantA, null, person, BuiltInRoleKey.Learner);
        var branch = await kit.MemberAsync(TenantA, SchemaFixture.OrgA1, person, BuiltInRoleKey.Learner);
        var closure = await kit.ClosureAsync(TenantA, null, person);
        (await kit.Owner().BeginAsync(AccountClosureBeginKit.Admission(), closure)).Outcome.Should().Be(ClosureBeginOutcome.Begun);
        var served = await FanOutErasureAsync(kit, TenantWideA, person, tenantWide, awaiting: true);
        var running = await FanOutErasureAsync(kit, BranchA1, person, branch, awaiting: false);
        await kit.OrganizationStatusAsync(TenantA, SchemaFixture.OrgA1, "Archived");

        // Its job finishes: written by the owner with the guards paused, because an archived branch takes no ordinary write.
        await kit.SqlAsync($$"""
            SET session_replication_role = replica;
            UPDATE public.data_subject_requests SET status = 'awaiting_privileged_step', result = '{"consent_evidence":1}'::jsonb,
                row_version = row_version + 2, updated_at = now(), updated_by = '{{UserId.SystemActor.Value}}'
            WHERE id = '{{running}}'
            """);

        var finalized = await kit.Owner(removal: new RecordingAccountRemoval()).FinalizeAsync(Admission(), closure);

        finalized.Outcome.Should().Be(ClosureFinalizeOutcome.Finalized);
        finalized.Groups.Should().Be(new ClosureFinalizeTally(1, 1, 0, 0, ErasuresCompleted: 2, ClosuresCompleted: 1),
            "ready counts the required groups; the branch's data was erased, so nothing is reported as kept");
        finalized.UnservedGroups.Should().BeEmpty();
        (await RequestStateAsync(kit, BranchA1, running)).Status.Should().Be("completed", "no fan-out erasure is stranded");
        (await RequestStateAsync(kit, TenantWideA, served)).Status.Should().Be("completed");
    }

    /// <summary>
    /// The result row of the second transaction cannot be written after the provider removed the account: the act answers
    /// the retryable unavailable, never the audit refusal that says the provider was not asked; the closure's state rolled
    /// back with the row, and a repeat finds the account absent and finishes.
    /// </summary>
    [Fact]
    public async Task A_result_row_refused_after_the_provider_step_is_the_retryable_unavailable_and_a_repeat_finishes()
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var world = await BegunAsync(kit);
        var removal = new RecordingAccountRemoval { Answer = call => call == 1 ? AccountRemovalOutcome.Removed : AccountRemovalOutcome.Absent };
        var admission = Admission();

        var act = () => kit.Owner(removal: removal, scope: new RefusedFinalResultScope(kit.Scope())).FinalizeAsync(admission, world.Closure);

        var failure = (await act.Should().ThrowAsync<OperatorRecoveryUnavailableException>()).Which;
        failure.CommitOutcomeUnknown.Should().BeFalse("the commit was never sent");
        failure.InnerException.Should().BeOfType<AuditWriteFailedException>();
        removal.Subjects.Should().ContainSingle("the account was removed before the row was refused");
        (await kit.UserAsync(world.Person)).Status.Should().Be("Closing", "the second transaction rolled back with its row");
        (await kit.RequestAsync(world.Closure)).Status.Should().Be("fulfilling");
        (await kit.AuditAsync(admission.Operator.CorrelationId!)).Select(row => row.Operation).Should().Equal(
            PlatformAdminScope.EnterOperation, Attempt);

        var repeat = await kit.Owner(removal: removal).FinalizeAsync(Admission(), world.Closure);

        repeat.Outcome.Should().Be(ClosureFinalizeOutcome.Finalized);
        (await kit.UserAsync(world.Person)).Status.Should().Be("Closed");
    }

    /// <summary>
    /// A participant set past the closure's bounds, which Begin refuses and no admitted path makes once the person is Closing
    /// (written here by the superuser with the guards paused, after the Begin), is refused as a defect logged by its closed
    /// name alone before anything is redacted or asked: groups past the bound, and a tenant whose Memberships one redaction
    /// pass refuses.
    /// </summary>
    [Theory]
    [InlineData("participants_beyond_bound")]
    [InlineData("redaction_plan_refused")]
    public async Task A_participant_set_past_the_bounds_after_the_Begin_is_a_defect_named_without_the_person(string defect)
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var world = await OwnErasureWorldAsync(kit);
        var organization = Guid.CreateVersion7();
        await kit.SqlAsync(defect == "participants_beyond_bound"
            ? $"""
                SET session_replication_role = replica;
                INSERT INTO public.organizations(id,tenant_id,slug,display_name,status,created_at,created_by,row_version)
                SELECT uuidv7(), '{TenantB}', 'past-' || n, 'Past', 'Archived', clock_timestamp(), '{UserId.SystemActor.Value}', 0
                FROM generate_series(1, {GroupsPastTheBound}) AS n;
                INSERT INTO public.memberships(id,tenant_id,organization_id,user_id,status,created_at,created_by)
                SELECT uuidv7(), '{TenantB}', o.id, '{world.Person}', 'Active', clock_timestamp(), '{UserId.SystemActor.Value}'
                FROM public.organizations AS o WHERE o.tenant_id = '{TenantB}' AND o.slug LIKE 'past-%';
                """
            : $"""
                SET session_replication_role = replica;
                INSERT INTO public.organizations(id,tenant_id,slug,display_name,status,created_at,created_by,row_version)
                VALUES ('{organization}', '{TenantB}', 'past-tombstones', 'Past', 'Archived', clock_timestamp(), '{UserId.SystemActor.Value}', 0);
                INSERT INTO public.memberships(id,tenant_id,organization_id,user_id,status,created_at,created_by,deleted_at,deleted_by)
                SELECT uuidv7(), '{TenantB}', '{organization}', '{world.Person}', 'Revoked', clock_timestamp(), '{UserId.SystemActor.Value}',
                    clock_timestamp(), '{UserId.SystemActor.Value}'
                FROM generate_series(1, {MembershipsPastTheBound});
                """);
        var logs = new CapturingLogger<AccountClosureOperatorOperations>();
        var removal = new RecordingAccountRemoval();
        var admission = Admission();

        var act = () => kit.Owner(removal: removal, logger: logs).FinalizeAsync(admission, world.Closure);

        await act.Should().ThrowAsync<InvalidOperationException>();
        logs.Lines.Should().ContainSingle().Which.Should().StartWith("7122 ").And.Contain(defect).And.NotContain(world.Person.ToString("D"));
        removal.Subjects.Should().BeEmpty("the provider is never asked for a closure past its bounds");
        (await kit.AuditAsync(admission.Operator.CorrelationId!)).Select(row => row.Operation).Should().Equal(
            [PlatformAdminScope.EnterOperation], "nothing was redacted and no intent row was written");
        (await kit.UserAsync(world.Person)).Status.Should().Be("Closing");
    }

    /// <summary>
    /// Begin's grouped participant read uses the person's Membership index, never a tenant's or a group's, whatever the
    /// other people of the person's groups hold.
    /// </summary>
    [Fact]
    public async Task Begins_participant_read_counts_the_person_s_Memberships_through_the_person_s_index()
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        await kit.SqlAsync($"""
            SET session_replication_role = replica;
            INSERT INTO public.memberships(id,tenant_id,organization_id,user_id,status,created_at,created_by)
            SELECT uuidv7(), '{TenantB}', NULL, uuidv7(), 'Active', clock_timestamp(), '{UserId.SystemActor.Value}'
            FROM generate_series(1, 4000);
            ANALYZE public.memberships;
            """);
        await using var platform = await AccountClosureBeginKit.OpenAsync(kit.Database.PlatformConnectionString);
        await using var command = new NpgsqlCommand(
            "SET enable_seqscan = off; EXPLAIN (FORMAT JSON) " + AccountClosureOperatorOperations.ParticipantsSql, platform);
        command.Parameters.AddWithValue("subject", IdentityMembershipFixture.PersonA);
        command.Parameters.AddWithValue("limit", GroupsPastTheBound);

        var plan = (await command.ExecuteScalarAsync())!.ToString()!;

        plan.Should().Contain("ix_memberships_user_id").And.NotContain("Seq Scan");
    }

    /// <summary>One group past the closure's bound.</summary>
    private const int GroupsPastTheBound = LearnStack.SharedKernel.DataProtection.ClosureLimits.MaximumParticipantGroups + 1;

    /// <summary>One Membership past what one tenant pass of the redaction compares.</summary>
    private const int MembershipsPastTheBound = LearnStack.SharedKernel.DataProtection.ClosureLimits.MaximumTenantMemberships + 1;

    /// <summary>A begun closure whose tenant B group stands on the person's own erasure, which awaits the privileged step.</summary>
    private sealed record OwnErasureWorld(Guid Person, Guid Closure, Guid FanOut, Guid OwnErasure);

    /// <summary>
    /// A person in tenant A's and tenant B's tenant-wide groups with their own erasure of tenant B already awaiting the
    /// privileged step (the fan-out adopts it as it is), a due closure filed in tenant A, the Begin, and tenant A's fan-out
    /// erasure awaiting the privileged step.
    /// </summary>
    private static async Task<OwnErasureWorld> OwnErasureWorldAsync(AccountClosureBeginKit kit)
    {
        var person = await kit.PersonAsync();
        var tenantWide = await kit.MemberAsync(TenantA, null, person, BuiltInRoleKey.Learner);
        await kit.MemberAsync(TenantB, null, person, BuiltInRoleKey.Learner);
        var own = await kit.RequestRowAsync(TenantB, person, "erasure", "awaiting_privileged_step");
        var closure = await kit.ClosureAsync(TenantA, null, person);
        (await kit.Owner().BeginAsync(AccountClosureBeginKit.Admission(), closure)).Outcome.Should().Be(ClosureBeginOutcome.Begun);
        var fanOut = await FanOutErasureAsync(kit, TenantWideA, person, tenantWide, awaiting: true);
        return new OwnErasureWorld(person, closure, fanOut, own);
    }

    /// <summary>
    /// Another person's Membership in the tenant, admitted as an administrator admits one: under that person's User key and
    /// the tenant's key exclusively, each wait bounded as the application's own writers bound it.
    /// </summary>
    private static async Task AdmitSomeoneElseAsync(AccountClosureBeginKit kit, Guid tenant)
    {
        var someone = await kit.PersonAsync();
        await using var app = await DataExportTestKit.OpenAppAsync(kit.Database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, tenant);
        await ExecuteAsync(app, transaction, "SET LOCAL lock_timeout = '2s'");
        await LockAsync(app, transaction, tenant, someone, exclusive: true);
        await InsertMemberAsync(app, transaction, tenant, null, Guid.CreateVersion7(), someone,
            RoleId(tenant, BuiltInRoleKey.Learner, PermissionScope.Tenant));
        await ExecuteAsync(app, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
        await transaction.CommitAsync();
    }

    /// <summary>The real scope with a handle that refuses the act's final result row as an unavailable audit store does.</summary>
    private sealed class RefusedFinalResultScope(IPlatformAdminScope inner) : IPlatformAdminScope
    {
        public async Task<IPlatformAdminScopeHandle> EnterAsync(
            PlatformAdmission admission, CancellationToken cancellationToken = default,
            [System.Runtime.CompilerServices.CallerMemberName] string? callerMember = null,
            [System.Runtime.CompilerServices.CallerFilePath] string? callerFile = null,
            [System.Runtime.CompilerServices.CallerLineNumber] int callerLine = 0) =>
            new Handle(await inner.EnterAsync(admission, cancellationToken, callerMember, callerFile, callerLine));

        private sealed class Handle(IPlatformAdminScopeHandle real) : ForwardingHandle(real)
        {
            public override Task WriteResultAsync(PlatformResult result, CancellationToken cancellationToken = default) =>
                result.AfterState?.Contains("\"finalized\"", StringComparison.Ordinal) == true
                    ? Task.FromException(new AuditWriteFailedException("forced refusal of the final result row"))
                    : base.WriteResultAsync(result, cancellationToken);
        }
    }
}
