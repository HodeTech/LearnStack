using System.Text.Json;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Application.Messaging;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Application.Contracts.DataRequests;
using LearnStack.Modules.Identity.Application.DataRequests;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.DataRequests;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Time;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.DataRequestSchemaSeed;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// ADR-0107 § 5 through the actual job runtime and the actual composition root, as <c>learnstack_app</c> on the job's own
/// tenant transaction: a due erasure revokes the subject's exports, erases the Memberships of its snapshot and revokes the
/// invitations the subject issued, in exactly its group, and waits for the operator's privileged step; a hold, a sole
/// administrator, a cancellation, an early run and a dead job each end it as ADR-0106 § 7 and § 8 say. Each case owns a
/// disposable copy of the seeded schema (two tenants; person A in tenant A's tenant-wide group and two branches).
/// </summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed partial class DataErasureExecutionTests(SchemaFixture fixture)
{
    private static readonly string[] Participants = ["audit", "customization", "education", "identity", "tenancy"];
    private DisposableSchemaDatabase? _database;

    [Theory]
    [InlineData("tenant")]
    [InlineData("organization")]
    public async Task A_due_erasure_erases_exactly_the_subjects_group_and_waits_for_the_privileged_step(string scope)
    {
        await using var database = await OpenDatabaseAsync();
        var (organization, membership, artifact, otherOffer) = scope == "tenant"
            ? ((Guid?)null, LearnerMembershipA, TenantWideA.Artifact!.Value, InvitationSchemaSeed.TenantOfferA)
            : (SchemaFixture.OrgA1, BranchMembershipA1, BranchA1.Artifact!.Value, InvitationSchemaSeed.BranchOfferA1);
        var issued = await IssueInvitationAsync(SchemaFixture.TenantA, organization, PersonA);
        var export = await FileAsync(SchemaFixture.TenantA, organization, PersonA, [membership], "export");
        var erasure = await FileDueErasureAsync(SchemaFixture.TenantA, organization, PersonA, [membership]);
        await using var host = Host();

        (await RunAsync(host.Services, await DataErasureTestKit.SubmitAsync(database, SchemaFixture.TenantA, organization, erasure)))
            .Committed();

        var state = await RequestAsync(SchemaFixture.TenantA, organization, erasure);
        state.Status.Should().Be("awaiting_privileged_step");
        JsonDocument.Parse(state.Progress).RootElement.EnumerateObject().Select(entry => entry.Name).Should().BeEquivalentTo(Participants);
        JsonDocument.Parse(state.Result).RootElement.EnumerateObject().Select(entry => entry.Name).Should()
            .Contain(["request_receipt", "invitation_record", "attribution_reference", "consent_evidence", "audit_actor_reference"])
            .And.NotContain("invitation_envelope", "a kind no participant can count is absent, never zero");

        var erased = await MembershipAsync(SchemaFixture.TenantA, organization, membership);
        erased.Should().Be(new MembershipState("Revoked", SystemActor, null, null, "[]", 0));
        foreach (var (other, group) in new[] { (LearnerMembershipA, (Guid?)null), (BranchMembershipA1, SchemaFixture.OrgA1),
                     (BranchMembershipA2, SchemaFixture.OrgA2) }.Where(item => item.Item1 != membership))
            (await MembershipAsync(SchemaFixture.TenantA, group, other)).Should().Be(new MembershipState("Active", null, "profile-canary",
                "en", "[]", 1), "the same person's Membership of another group is not this request's");
        (await MembershipAsync(SchemaFixture.TenantB, SchemaFixture.OrgB1, BranchMembershipB)).Status.Should().Be("Active");

        (await RequestAsync(SchemaFixture.TenantA, organization, export)).Status.Should().Be("cancelled",
            "an export not yet stored is cancelled, so its job discards anything it built");
        (await ArtifactAsync(SchemaFixture.TenantA, organization, artifact)).Should().Be(("revoked", 0L));
        (await InvitationAsync(SchemaFixture.TenantA, organization, issued)).Should().Be(("Revoked", PersonA),
            "the person issued it; the aggregate's actor is the person, the audit actor the job");
        (await InvitationAsync(SchemaFixture.TenantA, organization, otherOffer)).Status.Should().Be("Pending",
            "another person's offer is not the subject's to erase");
        (await UserStatusAsync(PersonA)).Should().Be("Active", "tenant erasure never touches users");

        (await AuditAsync(SchemaFixture.TenantA, organization, "identity.data_request.fulfill_erasure", erasure)).Should().Be(1);
        (await AuditAsync(SchemaFixture.TenantA, organization, "identity.membership.erase", membership)).Should().Be(1);
        (await AuditAsync(SchemaFixture.TenantA, organization, "identity.invitation.revoke", issued)).Should().Be(1);
        (await AuditAsync(SchemaFixture.TenantA, organization, "identity.data_export.revoke", artifact)).Should().Be(1);

        // Repeating the job is a recorded no-op.
        (await RunAsync(host.Services, await DataErasureTestKit.SubmitAsync(database, SchemaFixture.TenantA, organization, erasure)))
            .Committed();
        (await RequestAsync(SchemaFixture.TenantA, organization, erasure)).Should().Be(state);
        (await AuditAsync(SchemaFixture.TenantA, organization, "identity.data_request.fulfill_erasure", null)).Should().Be(2);
    }

    /// <summary>
    /// A Suspended Membership is erased to the tombstone by the same path; an earlier-revoked one loses its profile and
    /// assignments through the guard's witnessed exception while its own row, which admits no UPDATE, keeps its stamps.
    /// </summary>
    [Theory]
    [InlineData("Suspended")]
    [InlineData("Revoked")]
    public async Task A_suspended_or_earlier_revoked_membership_is_erased_and_a_revoked_row_is_never_updated(string status)
    {
        await using var database = await OpenDatabaseAsync();
        await SetStatusAsync(SchemaFixture.TenantA, SchemaFixture.OrgA2, BranchMembershipA2, status);
        var before = await RowAsync(SchemaFixture.TenantA, SchemaFixture.OrgA2, BranchMembershipA2);
        var erasure = await FileDueErasureAsync(SchemaFixture.TenantA, SchemaFixture.OrgA2, PersonA, [BranchMembershipA2]);
        await using var host = Host();

        (await RunAsync(host.Services, await DataErasureTestKit.SubmitAsync(database, SchemaFixture.TenantA, SchemaFixture.OrgA2, erasure)))
            .Committed();

        (await RequestAsync(SchemaFixture.TenantA, SchemaFixture.OrgA2, erasure)).Status.Should().Be("awaiting_privileged_step");
        var erased = await MembershipAsync(SchemaFixture.TenantA, SchemaFixture.OrgA2, BranchMembershipA2);
        (erased.Status, erased.DisplayName, erased.Locale, erased.CustomFields, erased.Assignments)
            .Should().Be(("Revoked", (string?)null, (string?)null, "[]", 0L));
        var after = await RowAsync(SchemaFixture.TenantA, SchemaFixture.OrgA2, BranchMembershipA2);
        if (status == "Revoked")
            after.Should().Be(before, "the tombstone's own row is not written; its erasure is on the audit row");
        else
            after.Version.Should().BeGreaterThan(before.Version);
    }

    /// <summary>
    /// ADR-0044 Amendment 6: a request about one instance writes one row. The erasure changes two Memberships of one person
    /// (the earlier tombstone and the current row admitted after it) and two invitations the person issued, so each row
    /// names the first instance it changed and carries the other in its changes; without the designation the composer
    /// refuses the pair and the whole erasure rolls back.
    /// </summary>
    [Fact]
    public async Task Several_instances_of_one_type_are_recorded_on_one_row_about_the_first()
    {
        await using var database = await OpenDatabaseAsync();
        await SetStatusAsync(SchemaFixture.TenantA, null, LearnerMembershipA, "Revoked");
        var current = Guid.CreateVersion7();
        await using (var app = await DataExportTestKit.OpenAppAsync(database.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await ScopeAsync(app, transaction, SchemaFixture.TenantA, null);
            await LockAsync(app, transaction, SchemaFixture.TenantA, PersonA, false);
            await InsertMemberAsync(app, transaction, SchemaFixture.TenantA, null, current, PersonA,
                RoleId(SchemaFixture.TenantA, LearnStack.SharedKernel.Permissions.BuiltInRoleKey.Learner,
                    LearnStack.SharedKernel.Permissions.PermissionScope.Tenant));
            await transaction.CommitAsync();
        }

        var invitations = new[] { await IssueInvitationAsync(SchemaFixture.TenantA, null, PersonA),
            await IssueInvitationAsync(SchemaFixture.TenantA, null, PersonA) }.Order().ToArray();
        var erasure = await FileDueErasureAsync(SchemaFixture.TenantA, null, PersonA, [LearnerMembershipA, current]);
        await using var host = Host();

        (await RunAsync(host.Services, await DataErasureTestKit.SubmitAsync(database, SchemaFixture.TenantA, null, erasure)))
            .Committed();

        foreach (var membership in new[] { LearnerMembershipA, current })
            (await MembershipAsync(SchemaFixture.TenantA, null, membership)).Should().Be(new MembershipState("Revoked",
                SystemActor, null, null, "[]", 0));
        foreach (var invitation in invitations)
            (await InvitationAsync(SchemaFixture.TenantA, null, invitation)).Status.Should().Be("Revoked");
        (await AuditChangesAsync(SchemaFixture.TenantA, "identity.membership.erase", LearnerMembershipA))
            .Should().Contain(current.ToString("D"), "the second Membership travels in the first one's row");
        (await AuditAsync(SchemaFixture.TenantA, null, "identity.membership.erase", current)).Should().Be(0);
        (await AuditChangesAsync(SchemaFixture.TenantA, "identity.invitation.revoke", invitations[0]))
            .Should().Contain(invitations[1].ToString("D"), "the second invitation travels in the first one's row");
        (await AuditAsync(SchemaFixture.TenantA, null, "identity.invitation.revoke", invitations[1])).Should().Be(0);
    }

    [Theory]
    [InlineData("sole_administrator")]
    [InlineData("hold")]
    public async Task A_sole_administrator_or_a_hold_ends_the_erasure_blocked_at_execution_and_nothing_is_erased(string variant)
    {
        await using var database = await OpenDatabaseAsync();
        var (subject, membership) = variant == "hold" ? (PersonA, LearnerMembershipA) : (AdminA, AdminMembershipA);
        // Filed as if creation had passed: the re-check at execution is what decides (ADR-0106 § 7).
        var erasure = await FileDueErasureAsync(SchemaFixture.TenantA, null, subject, [membership]);
        await using var host = Host(variant == "hold" ? services => services.AddSingleton<IDataRequestHoldSource>(new FixedHold()) : null);

        (await RunAsync(host.Services, await DataErasureTestKit.SubmitAsync(database, SchemaFixture.TenantA, null, erasure)))
            .Committed("the refusal is the committed business outcome");

        var state = await RequestAsync(SchemaFixture.TenantA, null, erasure);
        state.Status.Should().Be("blocked");
        state.Refusal.Should().Be(variant == "hold" ? "blocked_hold" : "blocked_last_administrator");
        (await MembershipAsync(SchemaFixture.TenantA, null, membership)).Status.Should().Be("Active");
        (await MembershipAsync(SchemaFixture.TenantA, null, membership)).DisplayName.Should().Be("profile-canary");
        (await ArtifactAsync(SchemaFixture.TenantA, null, TenantWideA.Artifact!.Value)).Status.Should().Be("available");
        (await AuditAsync(SchemaFixture.TenantA, null, "identity.data_request.fulfill_erasure", erasure)).Should().Be(1);
    }

    /// <summary>
    /// The runtime has no cancel call: a cancelled erasure's job is a recorded no-op. A run that wakes before the window ends
    /// fails its attempt for the runtime's retry and changes nothing.
    /// </summary>
    [Theory]
    [InlineData("cancelled")]
    [InlineData("not_due")]
    public async Task A_cancelled_or_not_yet_due_erasure_changes_nothing(string variant)
    {
        await using var database = await OpenDatabaseAsync();
        var erasure = await FileAsync(SchemaFixture.TenantA, null, PersonA, [LearnerMembershipA], "erasure");
        if (variant == "cancelled")
        {
            await using var app = await DataExportTestKit.OpenAppAsync(database.AppConnectionString);
            await using var transaction = await app.BeginTransactionAsync();
            await FenceAsync(app, transaction, SchemaFixture.TenantA, null, null);
            await StepAsync(app, transaction, erasure, "status='cancelled'");
            await transaction.CommitAsync();
        }

        var logs = new ErasureLogCapture();
        await using var host = Host(services => services.AddSingleton<Serilog.Core.ILogEventSink>(logs));

        var result = await RunAsync(host.Services, await DataErasureTestKit.SubmitAsync(database, SchemaFixture.TenantA, null, erasure));

        result.CommitAcknowledged.Should().Be(variant == "cancelled");
        if (variant == "not_due")
        {
            result.KnownRollback.Should().BeTrue();
            logs.Rendered().Should().Contain("is not yet due", "the failure is the early run, not any other fault");
        }
        else
        {
            logs.Rendered().Should().NotContain("attempt failed", "a cancelled erasure is a recorded no-op, not a failure");
        }

        (await RequestAsync(SchemaFixture.TenantA, null, erasure)).Status.Should().Be(variant == "cancelled" ? "cancelled" : "scheduled");
        (await MembershipAsync(SchemaFixture.TenantA, null, LearnerMembershipA)).Status.Should().Be("Active");
        (await ArtifactAsync(SchemaFixture.TenantA, null, TenantWideA.Artifact!.Value)).Status.Should().Be("available");
    }

    /// <summary>
    /// ADR-0107 § 5 is silent on the tenant's lifecycle; the job runtime runs a transactional job of a tenant that does not
    /// allow ordinary work like any other, so a due erasure of a Suspended tenant erases and waits for the privileged step.
    /// </summary>
    [Fact]
    public async Task A_due_erasure_of_a_suspended_tenant_runs_as_the_runtime_runs_every_transactional_job()
    {
        await using var database = await OpenDatabaseAsync();
        var erasure = await FileDueErasureAsync(SchemaFixture.TenantA, SchemaFixture.OrgA1, PersonA, [BranchMembershipA1]);
        await using (var app = await DataExportTestKit.OpenAppAsync(database.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
            await TenancyLifecycleFixture.LockAsync(app, transaction, SchemaFixture.TenantA);
            await ExecuteAsync(app, transaction, "UPDATE tenants SET status='Suspended' WHERE id=@tenant", ("tenant", SchemaFixture.TenantA));
            await transaction.CommitAsync();
        }

        await using var host = Host();

        (await RunAsync(host.Services, await DataErasureTestKit.SubmitAsync(database, SchemaFixture.TenantA, SchemaFixture.OrgA1, erasure)))
            .Committed();

        (await RequestAsync(SchemaFixture.TenantA, SchemaFixture.OrgA1, erasure)).Status.Should().Be("awaiting_privileged_step");
        (await MembershipAsync(SchemaFixture.TenantA, SchemaFixture.OrgA1, BranchMembershipA1)).Status.Should().Be("Revoked");
    }

    /// <summary>
    /// ADR-0107 § 5.1: an export filed before the erasure is cancelled by it; its job, running after, reads its own request
    /// cancelled under its fence and discards the bundle it built, so no artifact is stored over the erasure.
    /// </summary>
    [Fact]
    public async Task An_export_filed_before_the_erasure_is_discarded_by_its_own_job_after_the_erasure()
    {
        await using var database = await OpenDatabaseAsync();
        var export = await FileAsync(SchemaFixture.TenantA, SchemaFixture.OrgA2, PersonA, [BranchMembershipA2], "export");
        var exportJob = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString, "identity.data_export_organization.v1",
            organization: SchemaFixture.OrgA2, tenant: SchemaFixture.TenantA, parameters: BackgroundJobPayloadContract.Serialize(
                new DataExportJobParameters(export), typeof(DataExportJobParameters), BackgroundJobPayloadKind.Parameters));
        var erasure = await FileDueErasureAsync(SchemaFixture.TenantA, SchemaFixture.OrgA2, PersonA, [BranchMembershipA2]);
        await using var host = Host();

        (await RunAsync(host.Services, await DataErasureTestKit.SubmitAsync(database, SchemaFixture.TenantA, SchemaFixture.OrgA2, erasure)))
            .Committed();
        (await RunAsync(host.Services, exportJob)).Committed();

        (await RequestAsync(SchemaFixture.TenantA, SchemaFixture.OrgA2, export)).Status.Should().Be("cancelled");
        await using var app = await DataExportTestKit.OpenAppAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA, SchemaFixture.OrgA2);
        (await ScalarAsync<long>(app, transaction, "SELECT count(*) FROM public.data_export_artifacts WHERE request_id=@request",
            ("request", export))).Should().Be(0, "the export's bundle is discarded");
    }

    /// <summary>
    /// One transaction per attempt: a participant failing after the Identity participant erased rolls every write back, and
    /// the runtime's retry erases once and records every participant, the failing one included.
    /// </summary>
    [Fact]
    public async Task A_failed_attempt_rolls_every_erasure_write_back_and_the_retry_completes_once()
    {
        await using var database = await OpenDatabaseAsync();
        var erasure = await FileDueErasureAsync(SchemaFixture.TenantA, null, PersonA, [LearnerMembershipA]);
        await using var host = Host(services => services.AddSingleton<IUserDataParticipant>(new FailingParticipant()));

        var failed = await RunAsync(host.Services, await DataErasureTestKit.SubmitAsync(database, SchemaFixture.TenantA, null, erasure));

        failed.CommitAcknowledged.Should().BeFalse();
        failed.KnownRollback.Should().BeTrue();
        (await RequestAsync(SchemaFixture.TenantA, null, erasure)).Status.Should().Be("scheduled");
        (await MembershipAsync(SchemaFixture.TenantA, null, LearnerMembershipA)).Status.Should().Be("Active",
            "the Identity participant's erasure rolled back with the attempt");
        (await ArtifactAsync(SchemaFixture.TenantA, null, TenantWideA.Artifact!.Value)).Status.Should().Be("available");

        (await RunAsync(host.Services, await DataErasureTestKit.SubmitAsync(database, SchemaFixture.TenantA, null, erasure)))
            .Committed();
        var state = await RequestAsync(SchemaFixture.TenantA, null, erasure);
        state.Status.Should().Be("awaiting_privileged_step");
        JsonDocument.Parse(state.Progress).RootElement.EnumerateObject().Select(entry => entry.Name).Should()
            .BeEquivalentTo(Participants.Append("probe"));
        (await MembershipAsync(SchemaFixture.TenantA, null, LearnerMembershipA)).Status.Should().Be("Revoked");
        (await AuditAsync(SchemaFixture.TenantA, null, "identity.membership.erase", LearnerMembershipA)).Should().Be(1,
            "the rolled-back attempt left no row of a change that did not commit");
    }

    /// <summary>
    /// ADR-0106 § 8: a scheduled erasure whose job is dead reads failed, and the reconciler stores it failed with
    /// <c>execution_failed</c> once its window has ended, which frees the slot; before then it cannot start, so it stays.
    /// </summary>
    [Fact]
    public async Task A_dead_erasure_job_is_stored_failed_once_its_window_has_ended()
    {
        await using var database = await OpenDatabaseAsync();
        var due = await FileDueErasureAsync(SchemaFixture.TenantA, null, PersonA, [LearnerMembershipA]);
        var early = await FileAsync(SchemaFixture.TenantA, SchemaFixture.OrgA1, PersonA, [BranchMembershipA1], "erasure");
        foreach (var (organization, request) in new[] { ((Guid?)null, due), (SchemaFixture.OrgA1, early) })
        {
            var job = await DataErasureTestKit.SubmitAsync(database, SchemaFixture.TenantA, organization, request);
            await using var dispatcher = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.OutboxConnectionString);
            await SchemaQueries.ExecuteAsync(dispatcher, null,
                "UPDATE background_jobs SET phase='quarantined', quarantine_reason='unknown_outcome' WHERE id=@id", ("id", job.Value));
        }

        foreach (var (organization, request, stored) in new[] { ((Guid?)null, due, true), (SchemaFixture.OrgA1, early, false) })
        {
            await using var scope = await DataExportTestKit.OpenAsync(database.AppConnectionString, SchemaFixture.TenantA, organization);
            var store = new DataRequestStore(scope.Db, scope.Unit, new ProducerExecution(scope.Unit), scope.Context);
            var phases = new DataRequestJobPhaseReader(scope.Unit, scope.Context);
            var reconciler = new DataRequestFailureReconciler(store, phases, new IdentityMutationLocks(scope.Unit), scope.Context,
                new SystemClock());
            var loaded = (await store.FindLockedAsync(DataSubjectRequestId.From(request), CancellationToken.None))!;
            var phase = await phases.ReadAsync(loaded.Id, loaded.Kind, CancellationToken.None);
            phase.Should().Be(DataRequestJobPhase.Dead);
            DataRequestFailureReconciler.EffectiveStatus(loaded, phase).Should().Be(DataRequestStatus.Failed);
            (await reconciler.ReconcileAsync(loaded, CancellationToken.None)).Should().Be(stored);
            await scope.Frame.CompleteAsync();
        }

        var failed = await RequestAsync(SchemaFixture.TenantA, null, due);
        (failed.Status, failed.Failure).Should().Be(("failed", "execution_failed"));
        (await RequestAsync(SchemaFixture.TenantA, SchemaFixture.OrgA1, early)).Status.Should().Be("scheduled");
        var next = await FileAsync(SchemaFixture.TenantA, null, PersonA, [LearnerMembershipA], "erasure");
        (await RequestAsync(SchemaFixture.TenantA, null, next)).Status.Should().Be("scheduled", "the stored failure freed the slot");
    }

    private async Task<DisposableSchemaDatabase> OpenDatabaseAsync() =>
        _database = await DataExportTestKit.CreateSeededDatabaseAsync(fixture.Postgres);

    private WebApplicationFactory<Program> Host(Action<IServiceCollection>? configure = null) =>
        new ConstructionOnlyApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Development);
            builder.UseSetting("ConnectionStrings:Default", _database!.AppConnectionString);
            builder.UseSetting("ConnectionStrings:OutboxDispatcher", _database!.OutboxConnectionString);
            if (configure is not null) builder.ConfigureTestServices(configure);
        });

    private async Task<JobBusinessExecutionResult> RunAsync(IServiceProvider services, BackgroundJobId job)
    {
        var executor = services.GetRequiredService<JobExecutor>();
        var start = await executor.ExecuteAsync(await DataExportTestKit.RoutingClaim.ClaimAsync(_database!.OutboxConnectionString, job),
            BackgroundJobTestRun.Create());
        start.Outcome.Should().Be(JobStartExecutionOutcome.Started);
        return await executor.ExecuteBusinessAsync(start.Receipt!, start.Receipt!.Run.OpenBoundary(BackgroundJobCheckpointKind.Transactional));
    }

    /// <summary>A self-filed request in its initial state, under the canonical fences.</summary>
    private async Task<Guid> FileAsync(Guid tenant, Guid? organization, Guid subject, Guid[] memberships, string kind)
    {
        var id = Guid.CreateVersion7();
        await using var app = await DataExportTestKit.OpenAppAsync(_database!.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await FenceAsync(app, transaction, tenant, organization, subject);
        await InsertRequestAsync(app, transaction, id, tenant, organization, subject, memberships, kind);
        await transaction.CommitAsync();
        return id;
    }

    /// <summary>A self-filed erasure whose cancellable window has already ended by the database clock.</summary>
    private async Task<Guid> FileDueErasureAsync(Guid tenant, Guid? organization, Guid subject, Guid[] memberships)
    {
        var id = await FileAsync(tenant, organization, subject, memberships, "erasure");
        await DataErasureTestKit.BackdateAsync(_database!.MigrationConnectionString, tenant, organization, id);
        return id;
    }

    /// <summary>A pending offer the person issued in the group, written by the application role under the tenant fence.</summary>
    private async Task<Guid> IssueInvitationAsync(Guid tenant, Guid? organization, Guid inviter)
    {
        var id = Guid.CreateVersion7();
        await using var app = await DataExportTestKit.OpenAppAsync(_database!.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, tenant, organization);
        await LockAsync(app, transaction, tenant, null, false);
        await ExecuteAsync(app, transaction, """
            INSERT INTO public.invitations(id,tenant_id,organization_id,recipient_email,inviter_user_id,role_id,
                pinned_role_version,status,generation_id,generation,generation_created_at,expires_at,
                verifier_digest,entry_origin,entry_locale,current_delivery_job_id,submission_status,created_at,created_by)
            VALUES(@id,@tenant,@organization,@email,@inviter,@role,0,'Pending',@generation,1,@at,
                @at+interval '14 days',@digest,'https://manage.invitation.invalid','en',@job,'Queued',@at,@inviter)
            """, ("id", id), ("tenant", tenant), ("organization", organization), ("email", "erased-issuer-" + id.ToString("N") + "@example.invalid"),
            ("inviter", inviter), ("role", RoleId(tenant, LearnStack.SharedKernel.Permissions.BuiltInRoleKey.Learner, organization is null
                ? LearnStack.SharedKernel.Permissions.PermissionScope.Tenant : LearnStack.SharedKernel.Permissions.PermissionScope.Organization)),
            ("generation", Guid.CreateVersion7()), ("job", Guid.CreateVersion7()), ("digest", InvitationSchemaSeed.Digest(id)),
            ("at", DateTimeOffset.UtcNow));
        await transaction.CommitAsync();
        return id;
    }

    /// <summary>A Membership moved out of Active by the application role under the canonical fences, as an administrator would.</summary>
    private async Task SetStatusAsync(Guid tenant, Guid? organization, Guid membership, string status)
    {
        await using var app = await DataExportTestKit.OpenAppAsync(_database!.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, tenant, organization);
        await LockAsync(app, transaction, tenant, PersonA, false);
        await ExecuteAsync(app, transaction, status == "Revoked"
            ? "UPDATE public.memberships SET status='Revoked', deleted_at=clock_timestamp(), deleted_by=@system, updated_at=clock_timestamp(), updated_by=@system, row_version=row_version+1 WHERE id=@member"
            : "UPDATE public.memberships SET status='Suspended', updated_at=clock_timestamp(), updated_by=@system, row_version=row_version+1 WHERE id=@member",
            ("member", membership), ("system", SystemActor));
        await transaction.CommitAsync();
    }

    private sealed record RequestState(string Status, string Progress, string Result, string? Failure, string? Refusal);

    private sealed record MembershipState(string Status, Guid? DeletedBy, string? DisplayName, string? Locale, string CustomFields,
        long Assignments);

    private sealed record RowState(long Version, DateTime? UpdatedAt, Guid? UpdatedBy, DateTime? DeletedAt);

    private async Task<RequestState> RequestAsync(Guid tenant, Guid? organization, Guid request)
    {
        await using var app = await DataExportTestKit.OpenAppAsync(_database!.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, tenant, organization);
        await using var command = new NpgsqlCommand(
            "SELECT status, progress::text, result::text, failure_reason, last_refusal FROM public.data_subject_requests WHERE id=@id",
            app, transaction);
        command.Parameters.AddWithValue("id", request);
        await using var row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        return new RequestState(row.GetString(0), row.GetString(1), row.GetString(2), row.IsDBNull(3) ? null : row.GetString(3),
            row.IsDBNull(4) ? null : row.GetString(4));
    }

    private async Task<MembershipState> MembershipAsync(Guid tenant, Guid? organization, Guid membership)
    {
        await using var app = await DataExportTestKit.OpenAppAsync(_database!.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, tenant, organization);
        await using var command = new NpgsqlCommand("""
            SELECT m.status, m.deleted_by, p.display_name, p.preferred_locale, p.custom_fields::text,
                (SELECT count(*) FROM public.membership_role_assignments a WHERE a.tenant_id=m.tenant_id AND a.membership_id=m.id)
            FROM public.memberships m JOIN public.membership_profiles p ON p.tenant_id=m.tenant_id AND p.membership_id=m.id
            WHERE m.id=@id
            """, app, transaction);
        command.Parameters.AddWithValue("id", membership);
        await using var row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        return new MembershipState(row.GetString(0), row.IsDBNull(1) ? null : row.GetGuid(1), row.IsDBNull(2) ? null : row.GetString(2),
            row.IsDBNull(3) ? null : row.GetString(3), row.GetString(4), row.GetInt64(5));
    }

    private async Task<RowState> RowAsync(Guid tenant, Guid? organization, Guid membership)
    {
        await using var app = await DataExportTestKit.OpenAppAsync(_database!.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, tenant, organization);
        await using var command = new NpgsqlCommand(
            "SELECT row_version, updated_at, updated_by, deleted_at FROM public.memberships WHERE id=@id", app, transaction);
        command.Parameters.AddWithValue("id", membership);
        await using var row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        return new RowState(row.GetInt64(0), row.IsDBNull(1) ? null : row.GetDateTime(1), row.IsDBNull(2) ? null : row.GetGuid(2),
            row.IsDBNull(3) ? null : row.GetDateTime(3));
    }

    private async Task<(string Status, long Chunks)> ArtifactAsync(Guid tenant, Guid? organization, Guid artifact)
    {
        await using var app = await DataExportTestKit.OpenAppAsync(_database!.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, tenant, organization);
        await using var command = new NpgsqlCommand("""
            SELECT a.status, (SELECT count(*) FROM public.data_export_chunks c WHERE c.tenant_id=a.tenant_id AND c.artifact_id=a.id)
            FROM public.data_export_artifacts a WHERE a.id=@id
            """, app, transaction);
        command.Parameters.AddWithValue("id", artifact);
        await using var row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        return (row.GetString(0), row.GetInt64(1));
    }

    private async Task<(string Status, Guid? UpdatedBy)> InvitationAsync(Guid tenant, Guid? organization, Guid invitation)
    {
        await using var app = await DataExportTestKit.OpenAppAsync(_database!.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, tenant, organization);
        await using var command = new NpgsqlCommand("SELECT status, updated_by FROM public.invitations WHERE id=@id", app, transaction);
        command.Parameters.AddWithValue("id", invitation);
        await using var row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        return (row.GetString(0), row.IsDBNull(1) ? null : row.GetGuid(1));
    }

    /// <summary>The <c>changes</c> of the one success row about <paramref name="entity"/>.</summary>
    private async Task<string> AuditChangesAsync(Guid tenant, string operation, Guid entity)
    {
        await using var app = await DataExportTestKit.OpenAppAsync(_database!.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, tenant, null);
        return await ScalarAsync<string>(app, transaction, """
            SELECT changes::text FROM public.audit_log
            WHERE operation=@operation AND outcome='success' AND organization_id IS NULL AND entity_id=@entity
            """, ("operation", operation), ("entity", entity.ToString("D")));
    }

    /// <summary>The application role reads only its own session's User, so the binding is read as the owner.</summary>
    private async Task<string> UserStatusAsync(Guid user)
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database!.MigrationConnectionString);
        return await ScalarAsync<string>(owner, null, "SELECT status FROM public.users WHERE id=@id", ("id", user));
    }

    private async Task<long> AuditAsync(Guid tenant, Guid? organization, string operation, Guid? entity)
    {
        await using var app = await DataExportTestKit.OpenAppAsync(_database!.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, tenant, organization);
        return await ScalarAsync<long>(app, transaction, """
            SELECT count(*) FROM public.audit_log
            WHERE operation=@operation AND outcome='success' AND organization_id IS NOT DISTINCT FROM @organization::uuid
              AND (@entity::text IS NULL OR entity_id=@entity::text)
            """, ("operation", operation), ("organization", (object?)organization ?? DBNull.Value),
            ("entity", (object?)entity?.ToString("D") ?? DBNull.Value));
    }

    /// <summary>Fails its first erasure and succeeds afterwards: a transient failure after the Identity participant erased.</summary>
    private sealed class FailingParticipant : IUserDataParticipant
    {
        private int _calls;

        public string Key => "probe";

        public Task ExportAsync(UserDataSubject subject, IUserDataExportWriter writer, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EraseAsync(UserDataSubject subject, CancellationToken cancellationToken) =>
            Interlocked.Increment(ref _calls) == 1
                ? throw new InvalidOperationException("Synthetic transient erasure failure.")
                : Task.CompletedTask;

        public Task<IReadOnlyCollection<RetainedReferenceCount>> DescribeRetainedAsync(UserDataSubject subject,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyCollection<RetainedReferenceCount>>([]);
    }

    private sealed class FixedHold : IDataRequestHoldSource
    {
        public Task<bool> HasHoldAsync(DataRequestHoldQuery query, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}

internal static class DataErasureJobResults
{
    /// <summary>The job's attempt committed; a failure names the runtime's disposition and code.</summary>
    internal static void Committed(this JobBusinessExecutionResult result, string because = "the attempt commits") =>
        result.CommitAcknowledged.Should().BeTrue(because + " (disposition {0}, code {1})", result.Decision.Disposition,
            result.Decision.ErrorCode);
}
