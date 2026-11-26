using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using LearnStack.SharedKernel.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Serilog.Core;
using Serilog.Events;
using Xunit;
using static LearnStack.Tests.Integration.Database.DataRequestSchemaSeed;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The erasure's isolation across a person's groups, its resume from recorded progress, its early-run retry and the branches
/// of <c>DataErasureService</c> the first cases do not reach (ADR-0107 § 5), through the same job runtime and composition
/// root as the neighbouring cases.
/// </summary>
public sealed partial class DataErasureExecutionTests
{
    /// <summary>What the person owns in one group: an offer they issued, a readable export and a request not yet stored.</summary>
    private sealed record Footprint(Guid Tenant, Guid? Organization, Guid Invitation, Guid Artifact, Guid Export);

    /// <summary>
    /// ADR-0107 § 5 erases exactly the request's group. The person holds, in each of their other groups and in another
    /// tenant, a pending invitation they issued, an available artifact and an export that is only <c>requested</c>; the job
    /// withdraws those of the erased group and leaves every other one, and every audit row about them, absent.
    /// </summary>
    [Theory]
    [InlineData("tenant")]
    [InlineData("organization")]
    public async Task Erasing_one_group_withdraws_its_offers_and_exports_and_touches_none_of_the_persons_other_groups(string scope)
    {
        await using var database = await OpenDatabaseAsync();
        var groups = new (Guid? Organization, Guid Membership, Row Seeded)[]
        {
            (null, LearnerMembershipA, TenantWideA), (SchemaFixture.OrgA1, BranchMembershipA1, BranchA1),
            (SchemaFixture.OrgA2, BranchMembershipA2, BranchA2),
        };
        var footprints = new List<Footprint>();
        foreach (var (organization, membership, seeded) in groups)
        {
            footprints.Add(new Footprint(SchemaFixture.TenantA, organization, await IssueInvitationAsync(SchemaFixture.TenantA, organization, PersonA),
                seeded.Artifact!.Value, await FileAsync(SchemaFixture.TenantA, organization, PersonA, [membership], "export")));
        }

        // The same person in another tenant: tenant B's isolation is the policy's, not the job's own predicates.
        var foreignMembership = Guid.CreateVersion7();
        await using (var app = await DataExportTestKit.OpenAppAsync(database.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await ScopeAsync(app, transaction, SchemaFixture.TenantB, null);
            await LockAsync(app, transaction, SchemaFixture.TenantB, PersonA, false);
            await InsertMemberAsync(app, transaction, SchemaFixture.TenantB, null, foreignMembership, PersonA,
                RoleId(SchemaFixture.TenantB, LearnStack.SharedKernel.Permissions.BuiltInRoleKey.Learner,
                    LearnStack.SharedKernel.Permissions.PermissionScope.Tenant));
            await transaction.CommitAsync();
        }

        var foreign = new Footprint(SchemaFixture.TenantB, null, await IssueInvitationAsync(SchemaFixture.TenantB, null, PersonA),
            await SeedArtifactAsync(SchemaFixture.TenantB, null, PersonA, foreignMembership), Guid.Empty);
        foreign = foreign with { Export = await FileAsync(SchemaFixture.TenantB, null, PersonA, [foreignMembership], "export") };
        footprints.Add(foreign);

        var targetIndex = scope == "tenant" ? 0 : 1;
        var target = footprints[targetIndex];
        var erasure = await FileDueErasureAsync(target.Tenant, target.Organization, PersonA, [groups[targetIndex].Membership]);
        foreach (var footprint in footprints)
        {
            (await InvitationAsync(footprint.Tenant, footprint.Organization, footprint.Invitation)).Status.Should().Be("Pending", "arranged");
            (await ArtifactAsync(footprint.Tenant, footprint.Organization, footprint.Artifact)).Should().Be(("available", 1L), "arranged");
            (await RequestAsync(footprint.Tenant, footprint.Organization, footprint.Export)).Status.Should().Be("requested", "arranged");
        }

        await using var host = Host();
        (await RunAsync(host.Services, await DataErasureTestKit.SubmitAsync(database, target.Tenant, target.Organization, erasure))).Committed();

        (await RequestAsync(target.Tenant, target.Organization, erasure)).Status.Should().Be("awaiting_privileged_step");
        (await InvitationAsync(target.Tenant, target.Organization, target.Invitation)).Status.Should().Be("Revoked");
        (await ArtifactAsync(target.Tenant, target.Organization, target.Artifact)).Should().Be(("revoked", 0L));
        (await RequestAsync(target.Tenant, target.Organization, target.Export)).Status.Should().Be("cancelled");
        foreach (var other in footprints.Where(footprint => footprint != target))
        {
            var group = other.Tenant == SchemaFixture.TenantB ? "another tenant" : other.Organization is null ? "the tenant-wide group" : "a branch";
            (await InvitationAsync(other.Tenant, other.Organization, other.Invitation)).Should().Be(("Pending", (Guid?)null),
                "the person's offer in " + group + " is not this erasure's");
            (await ArtifactAsync(other.Tenant, other.Organization, other.Artifact)).Should().Be(("available", 1L),
                "the person's readable export in " + group + " stays readable");
            (await RequestAsync(other.Tenant, other.Organization, other.Export)).Status.Should().Be("requested",
                "the person's pending export in " + group + " is not cancelled");
            (await AuditAsync(other.Tenant, other.Organization, "identity.invitation.revoke", other.Invitation)).Should().Be(0);
            (await AuditAsync(other.Tenant, other.Organization, "identity.data_export.revoke", other.Artifact)).Should().Be(0);
            (await AuditAsync(other.Tenant, other.Organization, "identity.data_request.cancel", other.Export)).Should().Be(0);
        }
    }

    /// <summary>
    /// ADR-0107 § 1: a request already <c>fulfilling</c> (a retried one, or a closure's fan-out erasure that starts there)
    /// skips the participants its progress records and runs the rest once; a recorded real participant's work is not
    /// repeated, which shows as its effect being absent.
    /// </summary>
    [Theory]
    [InlineData("recorded_prefix")]
    [InlineData("identity_recorded")]
    [InlineData("empty_progress")]
    public async Task A_fulfilling_erasure_skips_the_participants_its_progress_records_and_runs_the_rest_once(string variant)
    {
        await using var database = await OpenDatabaseAsync();
        var recorded = variant switch
        {
            "recorded_prefix" => new[] { "alpha", "audit", "beta", "customization", "education" },
            "identity_recorded" => ["alpha", "audit", "identity"],
            _ => Array.Empty<string>(),
        };
        var counters = new[] { new CountingParticipant("alpha"), new CountingParticipant("beta"), new CountingParticipant("zeta") };
        var erasure = await FileDueErasureAsync(SchemaFixture.TenantA, null, PersonA, [LearnerMembershipA]);
        await StartFulfillingAsync(SchemaFixture.TenantA, null, PersonA, erasure, recorded);
        await using var host = Host(services =>
        {
            foreach (var counter in counters) services.AddSingleton<IUserDataParticipant>(counter);
        });

        (await RunAsync(host.Services, await DataErasureTestKit.SubmitAsync(database, SchemaFixture.TenantA, null, erasure))).Committed();

        var state = await RequestAsync(SchemaFixture.TenantA, null, erasure);
        state.Status.Should().Be("awaiting_privileged_step");
        JsonDocument.Parse(state.Progress).RootElement.EnumerateObject().Select(entry => entry.Name).Should()
            .BeEquivalentTo(Participants.Concat(["alpha", "beta", "zeta"]), "every participant is recorded once the erasure ends");
        counters.ToDictionary(counter => counter.Key, counter => counter.Erased).Should().Equal(
            new Dictionary<string, int> { ["alpha"] = recorded.Contains("alpha") ? 0 : 1, ["beta"] = recorded.Contains("beta") ? 0 : 1, ["zeta"] = 1 },
            "a recorded participant is not run again, every other runs exactly once");
        (await MembershipAsync(SchemaFixture.TenantA, null, LearnerMembershipA)).Status.Should().Be(
            recorded.Contains("identity") ? "Active" : "Revoked",
            "the Identity participant's erasure ran if and only if its progress did not record it");
    }

    /// <summary>
    /// ADR-0106 § 8: a run that wakes before the window ends fails its attempt for the runtime's retry. The failure is that
    /// one, nothing runs and the request stays scheduled; the same job then completes once the clock has passed the time.
    /// </summary>
    [Fact]
    public async Task A_run_that_wakes_before_the_window_ends_fails_for_the_retry_runs_nothing_and_completes_once_due()
    {
        await using var database = await OpenDatabaseAsync();
        var counter = new CountingParticipant("alpha");
        var logs = new ErasureLogCapture();
        var erasure = await FileAsync(SchemaFixture.TenantA, null, PersonA, [LearnerMembershipA], "erasure");
        await using var host = Host(services =>
        {
            services.AddSingleton<IUserDataParticipant>(counter);
            services.AddSingleton<ILogEventSink>(logs);
        });

        var early = await RunAsync(host.Services, await DataErasureTestKit.SubmitAsync(database, SchemaFixture.TenantA, null, erasure));

        early.CommitAcknowledged.Should().BeFalse();
        early.KnownRollback.Should().BeTrue("the attempt failed, it did not merely not commit");
        early.Decision.ErrorCode.Should().NotBeNull("the runtime recorded a failure for its retry, not a success");
        logs.Rendered().Should().Contain("is not yet due", "the recorded cause is the early run, not any other failure")
            .And.Contain(erasure.ToString("D")).And.NotContain("finished (", "the early run reached no outcome");
        counter.Erased.Should().Be(0, "no participant ran");
        (await RequestAsync(SchemaFixture.TenantA, null, erasure)).Status.Should().Be("scheduled");
        (await MembershipAsync(SchemaFixture.TenantA, null, LearnerMembershipA)).Status.Should().Be("Active");

        await DataErasureTestKit.BackdateAsync(database.MigrationConnectionString, SchemaFixture.TenantA, null, erasure);
        (await RunAsync(host.Services, await DataErasureTestKit.SubmitAsync(database, SchemaFixture.TenantA, null, erasure))).Committed();

        counter.Erased.Should().Be(1, "the retry, once due, ran every participant once");
        (await RequestAsync(SchemaFixture.TenantA, null, erasure)).Status.Should().Be("awaiting_privileged_step");
        (await MembershipAsync(SchemaFixture.TenantA, null, LearnerMembershipA)).Status.Should().Be("Revoked");
    }

    /// <summary>
    /// A second erasure whose snapshot holds a Membership an earlier erasure already made the tombstone skips it without
    /// writing it, and erases the one admitted since.
    /// </summary>
    [Fact]
    public async Task A_second_erasure_over_an_already_erased_tombstone_writes_nothing_to_it_and_erases_the_rest()
    {
        await using var database = await OpenDatabaseAsync();
        await SetStatusAsync(SchemaFixture.TenantA, null, LearnerMembershipA, "Revoked");
        var first = await FileDueErasureAsync(SchemaFixture.TenantA, null, PersonA, [LearnerMembershipA]);
        await using var host = Host();
        (await RunAsync(host.Services, await DataErasureTestKit.SubmitAsync(database, SchemaFixture.TenantA, null, first))).Committed();
        var tombstone = await RowAsync(SchemaFixture.TenantA, null, LearnerMembershipA);
        await CompleteAsOwnerAsync(database, SchemaFixture.TenantA, null, first);

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

        var second = await FileDueErasureAsync(SchemaFixture.TenantA, null, PersonA, [LearnerMembershipA, current]);
        (await RunAsync(host.Services, await DataErasureTestKit.SubmitAsync(database, SchemaFixture.TenantA, null, second))).Committed();

        (await RequestAsync(SchemaFixture.TenantA, null, second)).Status.Should().Be("awaiting_privileged_step");
        (await RowAsync(SchemaFixture.TenantA, null, LearnerMembershipA)).Should().Be(tombstone,
            "the tombstone is not written again: its version, stamps and status are as the first erasure left them");
        (await MembershipAsync(SchemaFixture.TenantA, null, current)).Should().Be(new MembershipState("Revoked", SystemActor, null, null, "[]", 0));
        (await AuditAsync(SchemaFixture.TenantA, null, "identity.membership.erase", LearnerMembershipA)).Should().Be(1,
            "only the first erasure wrote it");
        (await AuditAsync(SchemaFixture.TenantA, null, "identity.membership.erase", current)).Should().Be(1,
            "the second erasure's one row names the Membership it changed");
    }

    /// <summary>
    /// A request whose snapshot names a Membership that is not the subject's in the request's group is not an erasure the
    /// service performs: the attempt fails, rolls back, and the Membership is untouched. The request guard refuses to store
    /// such a snapshot, so the case is the service's own defence in depth, reached by corrupting the stored snapshot as the
    /// owner with the guard paused and restored.
    /// </summary>
    [Fact]
    public async Task A_snapshot_membership_that_is_not_the_subjects_in_its_group_fails_the_attempt_and_changes_nothing()
    {
        await using var database = await OpenDatabaseAsync();
        var logs = new ErasureLogCapture();
        // Person A's branch Membership, named by a request of person A's tenant-wide group.
        var erasure = await FileDueErasureAsync(SchemaFixture.TenantA, null, PersonA, [LearnerMembershipA]);
        async Task CorruptSnapshotAsync(Guid membership)
        {
            await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
            await using var transaction = await owner.BeginTransactionAsync();
            await ExecuteAsync(owner, transaction, "ALTER TABLE public.data_subject_requests DISABLE TRIGGER tg_data_subject_requests_mutation_guard");
            await ScopeAsync(owner, transaction, SchemaFixture.TenantA, null);
            (await ScalarAsync<long>(owner, transaction, """
                WITH moved AS (
                    UPDATE public.data_subject_requests SET membership_snapshot=@snapshot::jsonb WHERE id=@id RETURNING 1)
                SELECT count(*) FROM moved
                """, ("id", erasure), ("snapshot", Snapshot(membership)))).Should().Be(1);
            await ExecuteAsync(owner, transaction, "ALTER TABLE public.data_subject_requests ENABLE TRIGGER tg_data_subject_requests_mutation_guard");
            await transaction.CommitAsync();
        }

        await CorruptSnapshotAsync(BranchMembershipA1);

        await using var host = Host(services => services.AddSingleton<ILogEventSink>(logs));

        var result = await RunAsync(host.Services, await DataErasureTestKit.SubmitAsync(database, SchemaFixture.TenantA, null, erasure));

        result.CommitAcknowledged.Should().BeFalse();
        result.KnownRollback.Should().BeTrue();
        (await RequestAsync(SchemaFixture.TenantA, null, erasure)).Status.Should().Be("scheduled");
        (await MembershipAsync(SchemaFixture.TenantA, SchemaFixture.OrgA1, BranchMembershipA1)).Status.Should().Be("Active");
        (await MembershipAsync(SchemaFixture.TenantA, null, LearnerMembershipA)).Status.Should().Be("Active");
        logs.Rendered().Should().Contain("attempt failed (\"InvalidOperationException\")", "the service refused, not a database or a participant");

        // An InvalidOperationException has other sources (the window not ended, the fences, a transition the job's own
        // preconditions admitted), so the cause is pinned two ways. The not-due refusal logs its own line, which is absent;
        // and the same request, due and fenced exactly as before, with only its snapshot restored to the subject's own
        // Membership, runs to the end: nothing but the snapshot made the first attempt fail.
        logs.Rendered().Should().NotContain("is not yet due", "the window had ended: the attempt did not fail for being early");
        await CorruptSnapshotAsync(LearnerMembershipA);

        (await RunAsync(host.Services, await DataErasureTestKit.SubmitAsync(database, SchemaFixture.TenantA, null, erasure))).Committed();

        (await RequestAsync(SchemaFixture.TenantA, null, erasure)).Status.Should().Be("awaiting_privileged_step");
        (await MembershipAsync(SchemaFixture.TenantA, null, LearnerMembershipA)).Status.Should().Be("Revoked");
        (await MembershipAsync(SchemaFixture.TenantA, SchemaFixture.OrgA1, BranchMembershipA1)).Status.Should().Be("Active",
            "the other group's Membership was never the subject's to erase here");
    }

    /// <summary>
    /// CHARACTERIZATION of a decision still owed (amendments-owed item 34, ADR-0106 § 3): "an administrator erases only for a
    /// Revoked Membership" holds at creation only. An administrator files for a Revoked Membership M1; the person is then
    /// admitted again as M2 (Active) inside the 24-hour window; the job runs. Today the request's snapshot names only M1,
    /// so M2 is not touched: it stays Active with its profile and assignment. But the withdrawal of exports, artifacts and
    /// issued invitations is by subject and group, not by snapshot, so the NEW admission's artifact, pending export and
    /// issued invitation are withdrawn, and the request ends <c>awaiting_privileged_step</c>: an erasure the person never
    /// asked for takes the data of an admission it was not filed for. If the maintainer adds a closed refusal at execution
    /// (<c>blocked_membership_current</c>) or accepts this, this case changes with that decision; an accidental change in
    /// either direction fails it. The operator's audit redaction of the group is a separate act and not run here.
    /// </summary>
    [Fact]
    public async Task An_administrator_erasure_of_a_revoked_membership_withdraws_what_a_person_admitted_again_in_the_window_owns_and_leaves_the_new_membership_active()
    {
        await using var database = await OpenDatabaseAsync();
        await SetStatusAsync(SchemaFixture.TenantA, null, LearnerMembershipA, "Revoked");
        var erasure = Guid.CreateVersion7();
        await using (var app = await DataExportTestKit.OpenAppAsync(database.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await FenceAsync(app, transaction, SchemaFixture.TenantA, null, PersonA);
            await ExecuteAsync(app, transaction, """
                INSERT INTO public.data_subject_requests(id,tenant_id,organization_id,kind,origin,subject_user_id,requester_user_id,
                    status,membership_snapshot,execute_at,created_at,created_by)
                VALUES(@id,@tenant,NULL,'erasure','administrator',@subject,@requester,'scheduled',@snapshot::jsonb,
                    now() + interval '24 hours',now(),@requester)
                """, ("id", erasure), ("tenant", SchemaFixture.TenantA), ("subject", PersonA), ("requester", AdminA),
                ("snapshot", Snapshot(LearnerMembershipA)));
            await transaction.CommitAsync();
        }

        // Inside the window the person is admitted again, with a pending export, an offer they issued and the artifact the
        // seeded export left; the snapshot, frozen at filing, does not name the new Membership.
        var readmitted = Guid.CreateVersion7();
        await using (var app = await DataExportTestKit.OpenAppAsync(database.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await ScopeAsync(app, transaction, SchemaFixture.TenantA, null);
            await LockAsync(app, transaction, SchemaFixture.TenantA, PersonA, false);
            await InsertMemberAsync(app, transaction, SchemaFixture.TenantA, null, readmitted, PersonA,
                RoleId(SchemaFixture.TenantA, LearnStack.SharedKernel.Permissions.BuiltInRoleKey.Learner,
                    LearnStack.SharedKernel.Permissions.PermissionScope.Tenant));
            await transaction.CommitAsync();
        }

        var export = await FileAsync(SchemaFixture.TenantA, null, PersonA, [LearnerMembershipA, readmitted], "export");
        var issued = await IssueInvitationAsync(SchemaFixture.TenantA, null, PersonA);
        await DataErasureTestKit.BackdateAsync(database.MigrationConnectionString, SchemaFixture.TenantA, null, erasure);
        await using var host = Host();

        (await RunAsync(host.Services, await DataErasureTestKit.SubmitAsync(database, SchemaFixture.TenantA, null, erasure))).Committed();

        (await RequestAsync(SchemaFixture.TenantA, null, erasure)).Status.Should().Be("awaiting_privileged_step");
        (await MembershipAsync(SchemaFixture.TenantA, null, LearnerMembershipA)).Should().Be(
            new MembershipState("Revoked", SystemActor, null, null, "[]", 0), "the Membership the request names is erased");
        (await MembershipAsync(SchemaFixture.TenantA, null, readmitted)).Should().Be(
            new MembershipState("Active", null, "profile-canary", "en", "[]", 1),
            "the new admission is not in the snapshot: it stays Active with its profile and its role");
        (await AuditAsync(SchemaFixture.TenantA, null, "identity.membership.erase", LearnerMembershipA)).Should().Be(1);
        (await AuditAsync(SchemaFixture.TenantA, null, "identity.membership.erase", readmitted)).Should().Be(0);
        (await ArtifactAsync(SchemaFixture.TenantA, null, TenantWideA.Artifact!.Value)).Should().Be(("revoked", 0L),
            "withdrawn by subject and group: the person's artifact is revoked though the person is a current member again");
        (await RequestAsync(SchemaFixture.TenantA, null, export)).Status.Should().Be("cancelled",
            "the pending export of the person in the group is cancelled by subject and group");
        (await InvitationAsync(SchemaFixture.TenantA, null, issued)).Status.Should().Be("Revoked",
            "the offer the person issued after being admitted again is revoked by subject and group");
    }

    /// <summary>
    /// Two erasures of two different people of one tenant, run at once through the real job runtime on one host: each takes
    /// its subject's User fence and then the tenant key exclusively, in that order, so the second waits for the first and
    /// neither deadlocks. The first is held inside its transaction (a participant that waits on a gate) until the second is
    /// seen waiting on an advisory lock in <c>pg_locks</c>, so the contention is observed, not hoped for; then both
    /// commit within a bounded wait, each erased only its own person's Membership, invitations and request, and neither
    /// touched the other's.
    /// </summary>
    [Fact]
    public async Task Two_erasures_of_two_people_of_one_tenant_run_at_once_serialize_on_the_tenant_key_and_both_complete()
    {
        await using var database = await OpenDatabaseAsync();
        var (personC, membershipC) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await InsertUserAsync(database.AppConnectionString, personC);
        await using (var app = await DataExportTestKit.OpenAppAsync(database.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await ScopeAsync(app, transaction, SchemaFixture.TenantA, null);
            await LockAsync(app, transaction, SchemaFixture.TenantA, personC, false);
            await InsertMemberAsync(app, transaction, SchemaFixture.TenantA, null, membershipC, personC,
                RoleId(SchemaFixture.TenantA, LearnStack.SharedKernel.Permissions.BuiltInRoleKey.Learner,
                    LearnStack.SharedKernel.Permissions.PermissionScope.Tenant), "profile-canary-c");
            await transaction.CommitAsync();
        }

        var (invitationA, invitationC) = (await IssueInvitationAsync(SchemaFixture.TenantA, null, PersonA),
            await IssueInvitationAsync(SchemaFixture.TenantA, null, personC));
        var (erasureA, erasureC) = (await FileDueErasureAsync(SchemaFixture.TenantA, null, PersonA, [LearnerMembershipA]),
            await FileDueErasureAsync(SchemaFixture.TenantA, null, personC, [membershipC]));
        var (jobA, jobC) = (await DataErasureTestKit.SubmitAsync(database, SchemaFixture.TenantA, null, erasureA),
            await DataErasureTestKit.SubmitAsync(database, SchemaFixture.TenantA, null, erasureC));
        var gate = new GatedParticipant(PersonA);
        await using var host = Host(services => services.AddSingleton<IUserDataParticipant>(gate));

        var first = Task.Run(() => RunAsync(host.Services, jobA));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var second = Task.Run(() => RunAsync(host.Services, jobC));
        await using (var observer = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString))
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!await ScalarAsync<bool>(observer, null,
                       "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_locks WHERE locktype = 'advisory' AND NOT granted)"))
            {
                second.IsCompleted.Should().BeFalse("the second erasure cannot finish while the first holds the tenant key");
                DateTime.UtcNow.Should().BeBefore(deadline, "the second erasure must be seen waiting on the first's tenant key");
                await Task.Delay(TimeSpan.FromMilliseconds(20));
            }
        }

        gate.Release.TrySetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(60));

        results[0].Committed("the first erasure commits when released");
        results[1].Committed("the second erasure commits once the first has released the tenant key: no deadlock");
        (await RequestAsync(SchemaFixture.TenantA, null, erasureA)).Status.Should().Be("awaiting_privileged_step");
        (await RequestAsync(SchemaFixture.TenantA, null, erasureC)).Status.Should().Be("awaiting_privileged_step");
        var tombstone = new MembershipState("Revoked", SystemActor, null, null, "[]", 0);
        (await MembershipAsync(SchemaFixture.TenantA, null, LearnerMembershipA)).Should().Be(tombstone);
        (await MembershipAsync(SchemaFixture.TenantA, null, membershipC)).Should().Be(tombstone);
        (await InvitationAsync(SchemaFixture.TenantA, null, invitationA)).Status.Should().Be("Revoked");
        (await InvitationAsync(SchemaFixture.TenantA, null, invitationC)).Status.Should().Be("Revoked");
        (await MembershipAsync(SchemaFixture.TenantA, SchemaFixture.OrgA1, BranchMembershipA1)).Status.Should().Be("Active",
            "person A's other groups are not these requests'");
        foreach (var (request, membership) in new[] { (erasureA, LearnerMembershipA), (erasureC, membershipC) })
        {
            (await AuditAsync(SchemaFixture.TenantA, null, "identity.data_request.fulfill_erasure", request)).Should().Be(1);
            (await AuditAsync(SchemaFixture.TenantA, null, "identity.membership.erase", membership)).Should().Be(1,
                "each erasure's audit row names its own person's Membership once");
        }

        JsonDocument.Parse((await RequestAsync(SchemaFixture.TenantA, null, erasureC)).Result).RootElement.GetProperty("request_receipt")
            .GetInt64().Should().Be(1, "person C's receipt counts person C's request alone");
    }

    /// <summary>
    /// ADR-0107 § 5.3: the pending invitations the subject issued are revoked a page at a time until none is left; one more
    /// than a page is the case that ends a loop that stops after the first.
    /// </summary>
    [Fact]
    public async Task More_pending_invitations_than_one_page_are_all_revoked_by_one_erasure()
    {
        await using var database = await OpenDatabaseAsync();
        var invitations = await IssueInvitationsAsync(SchemaFixture.TenantA, null, PersonA, 101);
        var erasure = await FileDueErasureAsync(SchemaFixture.TenantA, null, PersonA, [LearnerMembershipA]);
        await using var host = Host();

        (await RunAsync(host.Services, await DataErasureTestKit.SubmitAsync(database, SchemaFixture.TenantA, null, erasure))).Committed();

        invitations.Should().HaveCount(101);
        (await PendingInvitationsAsync(SchemaFixture.TenantA, null, PersonA)).Should().Be(0, "every page was revoked");
        (await CountRevokedAsync(SchemaFixture.TenantA, null, invitations)).Should().Be(101);
        (await AuditCountOperationAsync(SchemaFixture.TenantA, null, "identity.invitation.revoke")).Should().Be(1,
            "one row about the first instance; the others travel in its changes");
    }

    /// <summary>Moves a scheduled request, whose window has ended, to <c>fulfilling</c> with the given progress recorded.</summary>
    private async Task StartFulfillingAsync(Guid tenant, Guid? organization, Guid subject, Guid request, string[] recorded)
    {
        var progress = "{" + string.Join(",", recorded.Select(key => "\"" + key + "\":\"2026-11-21T10:00:00.0000000Z\"")) + "}";
        await using var app = await DataExportTestKit.OpenAppAsync(_database!.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await FenceAsync(app, transaction, tenant, organization, subject);
        await StepAsync(app, transaction, request, "status='fulfilling'");
        if (recorded.Length > 0) await StepAsync(app, transaction, request, "progress='" + progress + "'::jsonb");
        await transaction.CommitAsync();
    }

    /// <summary>The operator's completion stood in for by the owner, with the request guard paused and restored.</summary>
    private static async Task CompleteAsOwnerAsync(DisposableSchemaDatabase database, Guid tenant, Guid? organization, Guid request)
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();
        await ExecuteAsync(owner, transaction, "ALTER TABLE public.data_subject_requests DISABLE TRIGGER tg_data_subject_requests_mutation_guard");
        await ScopeAsync(owner, transaction, tenant, organization);
        (await ScalarAsync<long>(owner, transaction, """
            WITH moved AS (
                UPDATE public.data_subject_requests SET status='completed', completed_at=now(), updated_at=now(), row_version=row_version+1
                WHERE id=@id AND status='awaiting_privileged_step' RETURNING 1)
            SELECT count(*) FROM moved
            """, ("id", request))).Should().Be(1);
        await ExecuteAsync(owner, transaction, "ALTER TABLE public.data_subject_requests ENABLE TRIGGER tg_data_subject_requests_mutation_guard");
        await transaction.CommitAsync();
    }

    /// <summary>A completed export of the person in the group, written under the canonical fences, with its available artifact.</summary>
    private async Task<Guid> SeedArtifactAsync(Guid tenant, Guid? organization, Guid subject, Guid membership)
    {
        var row = new Row(tenant, organization, subject, membership, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await using var app = await DataExportTestKit.OpenAppAsync(_database!.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await FenceAsync(app, transaction, tenant, organization, subject);
        await InsertRequestAsync(app, transaction, row.Request, tenant, organization, subject, [row.Membership]);
        await CompleteExportAsync(app, transaction, row, row.Artifact!.Value, row.Ticket!.Value);
        await transaction.CommitAsync();
        return row.Artifact!.Value;
    }

    /// <summary>Many pending offers the person issued, written in one transaction.</summary>
    private async Task<List<Guid>> IssueInvitationsAsync(Guid tenant, Guid? organization, Guid inviter, int count)
    {
        var ids = new List<Guid>();
        await using var app = await DataExportTestKit.OpenAppAsync(_database!.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, tenant, organization);
        await LockAsync(app, transaction, tenant, null, false);
        var role = RoleId(tenant, LearnStack.SharedKernel.Permissions.BuiltInRoleKey.Learner, organization is null
            ? LearnStack.SharedKernel.Permissions.PermissionScope.Tenant : LearnStack.SharedKernel.Permissions.PermissionScope.Organization);
        for (var index = 0; index < count; index++)
        {
            var id = Guid.CreateVersion7();
            ids.Add(id);
            await ExecuteAsync(app, transaction, """
                INSERT INTO public.invitations(id,tenant_id,organization_id,recipient_email,inviter_user_id,role_id,
                    pinned_role_version,status,generation_id,generation,generation_created_at,expires_at,
                    verifier_digest,entry_origin,entry_locale,current_delivery_job_id,submission_status,created_at,created_by)
                VALUES(@id,@tenant,@organization,@email,@inviter,@role,0,'Pending',@generation,1,@at,
                    @at+interval '14 days',@digest,'https://manage.invitation.invalid','en',@job,'Queued',@at,@inviter)
                """, ("id", id), ("tenant", tenant), ("organization", organization), ("email", "page-" + id.ToString("N") + "@example.invalid"),
                ("inviter", inviter), ("role", role), ("generation", Guid.CreateVersion7()), ("job", Guid.CreateVersion7()),
                ("digest", InvitationSchemaSeed.Digest(id)), ("at", DateTimeOffset.UtcNow));
        }

        await transaction.CommitAsync();
        return ids;
    }

    private async Task<long> PendingInvitationsAsync(Guid tenant, Guid? organization, Guid inviter)
    {
        await using var app = await DataExportTestKit.OpenAppAsync(_database!.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, tenant, organization);
        return await ScalarAsync<long>(app, transaction,
            "SELECT count(*) FROM public.invitations WHERE inviter_user_id=@inviter AND status='Pending'", ("inviter", inviter));
    }

    private async Task<long> CountRevokedAsync(Guid tenant, Guid? organization, List<Guid> invitations)
    {
        await using var app = await DataExportTestKit.OpenAppAsync(_database!.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, tenant, organization);
        return await ScalarAsync<long>(app, transaction,
            "SELECT count(*) FROM public.invitations WHERE id = ANY(@ids) AND status='Revoked'", ("ids", invitations.ToArray()));
    }

    private Task<long> AuditCountOperationAsync(Guid tenant, Guid? organization, string operation) => AuditAsync(tenant, organization, operation, null);

    /// <summary>Counts its erasures; its retained report is empty.</summary>
    private sealed class CountingParticipant(string key) : IUserDataParticipant
    {
        private int _erased;

        public string Key { get; } = key;

        public int Erased => Volatile.Read(ref _erased);

        public Task ExportAsync(UserDataSubject subject, IUserDataExportWriter writer, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EraseAsync(UserDataSubject subject, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _erased);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyCollection<RetainedReferenceCount>> DescribeRetainedAsync(UserDataSubject subject,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyCollection<RetainedReferenceCount>>([]);
    }

    private sealed class ErasureLogCapture : ILogEventSink
    {
        private readonly ConcurrentQueue<LogEvent> _entries = new();

        public void Emit(LogEvent logEvent) => _entries.Enqueue(logEvent);

        public string Rendered() => string.Join('\n', _entries.Select(entry => entry.RenderMessage(CultureInfo.InvariantCulture)));
    }

    /// <summary>Holds the erasure of one subject inside its transaction until released: the first of two concurrent runs.</summary>
    private sealed class GatedParticipant(Guid held) : IUserDataParticipant
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Key => "probe";

        public Task ExportAsync(UserDataSubject subject, IUserDataExportWriter writer, CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task EraseAsync(UserDataSubject subject, CancellationToken cancellationToken)
        {
            if (subject.SubjectUserId != held)
            {
                return;
            }

            Entered.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
        }

        public Task<IReadOnlyCollection<RetainedReferenceCount>> DescribeRetainedAsync(UserDataSubject subject,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyCollection<RetainedReferenceCount>>([]);
    }
}
