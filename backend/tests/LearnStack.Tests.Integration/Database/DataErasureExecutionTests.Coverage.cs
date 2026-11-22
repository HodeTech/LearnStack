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
        await using (var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString))
        await using (var transaction = await owner.BeginTransactionAsync())
        {
            await ExecuteAsync(owner, transaction, "ALTER TABLE public.data_subject_requests DISABLE TRIGGER tg_data_subject_requests_mutation_guard");
            await ScopeAsync(owner, transaction, SchemaFixture.TenantA, null);
            (await ScalarAsync<long>(owner, transaction, """
                WITH moved AS (
                    UPDATE public.data_subject_requests SET membership_snapshot=@snapshot::jsonb WHERE id=@id RETURNING 1)
                SELECT count(*) FROM moved
                """, ("id", erasure), ("snapshot", Snapshot(BranchMembershipA1)))).Should().Be(1);
            await ExecuteAsync(owner, transaction, "ALTER TABLE public.data_subject_requests ENABLE TRIGGER tg_data_subject_requests_mutation_guard");
            await transaction.CommitAsync();
        }

        await using var host = Host(services => services.AddSingleton<ILogEventSink>(logs));

        var result = await RunAsync(host.Services, await DataErasureTestKit.SubmitAsync(database, SchemaFixture.TenantA, null, erasure));

        result.CommitAcknowledged.Should().BeFalse();
        result.KnownRollback.Should().BeTrue();
        (await RequestAsync(SchemaFixture.TenantA, null, erasure)).Status.Should().Be("scheduled");
        (await MembershipAsync(SchemaFixture.TenantA, SchemaFixture.OrgA1, BranchMembershipA1)).Status.Should().Be("Active");
        (await MembershipAsync(SchemaFixture.TenantA, null, LearnerMembershipA)).Status.Should().Be("Active");
        logs.Rendered().Should().Contain("attempt failed (\"InvalidOperationException\")", "the service refused, not a database or a participant");
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
}
