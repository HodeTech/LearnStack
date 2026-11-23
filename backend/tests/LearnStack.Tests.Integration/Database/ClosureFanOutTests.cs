using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Modules.Identity.Application.Contracts.DataRequests;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Platform;
using LearnStack.Tests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.DataRequestSchemaSeed;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// ADR-0107 § 7's fan-out through the actual composition root, the actual gate, the actual operator act executor and the
/// actual pipeline, on real PostgreSQL as <c>learnstack_app</c>: one admitted act per participant group, announced for the
/// group's tenant and organization (ADR-0102 Amendment 2), creates the closing person's fan-out erasure and enqueues its
/// job, or adopts what the group already holds; the job then erases the group's every Membership of the person and awaits
/// the privileged step.
/// </summary>
/// <remarks>
/// <para>
/// Each case owns a disposable copy of the seeded schema, because a User a closure moved never returns to Active. Person A
/// holds Memberships in tenant A's tenant-wide group and its two branches, and the test gives them a Revoked tombstone and a
/// Suspended Membership in tenant B's tenant-wide group, so person A has four participant groups across two tenants. Begin is
/// another unit's; here the person's User is moved to Closing by the platform role under the closure's fences, exactly as
/// Begin's acquirer holds them, before the fan-out runs.
/// </para>
/// <para>
/// The gate is the production one over a grant source double that holds or withholds the operator's
/// <c>platform.data_request.write</c> grant, so the per-group admission is the real decision.
/// </para>
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed partial class ClosureFanOutTests(SchemaFixture fixture)
{
    private const string Operation = "identity.data_request.fan_out_closure";
    private const string Ticket = "CHG-107-7";
    private DisposableSchemaDatabase? _database;

    private static readonly ClosureParticipantGroup TenantWideA = new(TenantId.From(SchemaFixture.TenantA), null);
    private static readonly ClosureParticipantGroup BranchA1 = new(TenantId.From(SchemaFixture.TenantA), OrganizationId.From(SchemaFixture.OrgA1));
    private static readonly ClosureParticipantGroup BranchA2 = new(TenantId.From(SchemaFixture.TenantA), OrganizationId.From(SchemaFixture.OrgA2));
    private static readonly ClosureParticipantGroup TenantWideB = new(TenantId.From(SchemaFixture.TenantB), null);
    private static readonly ClosureParticipantGroup[] Groups = [TenantWideA, BranchA1, BranchA2, TenantWideB];

    private sealed record Arranged(Guid TombstoneB, Guid SuspendedB);

    /// <summary>
    /// Every served group gets one fan-out erasure, <c>fulfilling</c>, the system actor's, with no window and the group's
    /// every Membership of the person frozen, and one erasure job of its own scope on the same transaction; one MUST row per
    /// group in that tenant names the operator and the closed reason and carries no ticket; a group's rows are invisible to
    /// every other group. A repeat adopts each group's request and writes nothing new while each job is still queued.
    /// </summary>
    [Fact]
    public async Task Every_served_group_gets_one_fan_out_erasure_and_its_job_and_a_repeat_adopts_them()
    {
        var arranged = await ArrangeAsync();
        await CloseAsync(PersonA);
        await using var host = Host(new Grants(true));

        // Listed out of the canonical order on purpose.
        var reports = await FanOutAsync(host, [TenantWideB, BranchA2, TenantWideA, BranchA1]);

        reports.Should().Equal(Groups.Select(group => new ClosureGroupReport(group, ClosureGroupOutcome.Started)),
            "every group is started, in the fan-out's own order");
        var snapshots = new Dictionary<ClosureParticipantGroup, Guid[]>
        {
            [TenantWideA] = [LearnerMembershipA],
            [BranchA1] = [BranchMembershipA1],
            [BranchA2] = [BranchMembershipA2],
            [TenantWideB] = new[] { arranged.TombstoneB, arranged.SuspendedB }.Order().ToArray(),
        };
        var requests = new Dictionary<ClosureParticipantGroup, Guid>();
        foreach (var group in Groups)
        {
            var request = (await RequestsAsync(group)).Should().ContainSingle().Subject;
            request.Should().BeEquivalentTo(new RequestRow(request.Id, "erasure", "closure_fanout", "fulfilling", null, false, SystemActor,
                Snapshot(snapshots[group])), group.ToString());
            requests[group] = request.Id;
            (await JobsAsync(group, request.Id)).Should().Be(1, "the fan-out's job is enqueued on the transaction that created it");
            var row = (await AuditRowsAsync(group)).Should().ContainSingle().Subject;
            row.Should().Be(new AuditRow(request.Id.ToString("D"), "success", "closure_fan_out", true, false, null), group.ToString());
        }

        // Each group's rows live under its own announcement only: the canonical row policy admits an organization the
        // tenant-wide rows, never another organization's or another tenant's.
        foreach (var group in Groups)
            (await VisibleAsync(group, requests[group])).Should().Be(1, $"{group} sees its own request, so a zero below is isolation and not an unreadable table");
        (await VisibleAsync(BranchA1, requests[BranchA2])).Should().Be(0);
        (await VisibleAsync(BranchA2, requests[BranchA1])).Should().Be(0);
        (await VisibleAsync(TenantWideB, requests[TenantWideA])).Should().Be(0);
        (await VisibleAsync(TenantWideA, requests[TenantWideB])).Should().Be(0);
        (await VisibleAsync(TenantWideA, requests[BranchA1])).Should().Be(0, "a tenant-wide announcement shows no organization's rows");

        var repeat = await FanOutAsync(host, Groups);

        repeat.Should().Equal(Groups.Select(group => new ClosureGroupReport(group, ClosureGroupOutcome.Adopted)));
        foreach (var group in Groups)
        {
            (await RequestsAsync(group)).Should().ContainSingle("the one-active index holds and nothing is filed twice")
                .Which.Id.Should().Be(requests[group]);
            (await JobsAsync(group, requests[group])).Should().Be(1, "a queued job is not enqueued again");
            (await AuditRowsAsync(group)).Select(row => row.Entity).Should().Equal([requests[group].ToString("D"), requests[group].ToString("D")],
                "the repeat's row names the request it adopted");
        }

        (await UserStatusAsync(PersonA)).Should().Be("Closing");
    }

    /// <summary>
    /// The fan-out erasure runs through the actual erasure job for a Closing person: it starts at <c>fulfilling</c>, so no
    /// eligibility is asked again and even a hold the eligibility port would report does not block it; it erases the group's
    /// every Membership of the person, the Revoked tombstone's profile and the Suspended one included, records the retained
    /// report and waits for the privileged step. Another tenant's Memberships of the same person are untouched.
    /// </summary>
    [Fact]
    public async Task The_fan_out_job_erases_the_closing_persons_every_membership_of_the_group_and_awaits_the_privileged_step()
    {
        var arranged = await ArrangeAsync();
        await CloseAsync(PersonA);
        await using var host = Host(new Grants(true), services =>
        {
            services.RemoveAll<IDataRequestHoldSource>();
            services.AddSingleton<IDataRequestHoldSource>(new EveryHold());
        });
        (await FanOutAsync(host, [TenantWideB])).Should().Equal([new ClosureGroupReport(TenantWideB, ClosureGroupOutcome.Started)]);
        var request = (await RequestsAsync(TenantWideB)).Single().Id;

        (await RunJobAsync(host, TenantWideB, request)).Committed();

        var state = (await RequestsAsync(TenantWideB)).Single();
        state.Status.Should().Be("awaiting_privileged_step", "a closure's erasure is never blocked: its eligibility is Begin's");
        var (progress, result) = await RequestDocumentsAsync(TenantWideB, request);
        JsonDocument.Parse(progress).RootElement.EnumerateObject().Select(entry => entry.Name).Should()
            .BeEquivalentTo(["audit", "customization", "education", "identity", "tenancy"]);
        JsonDocument.Parse(result).RootElement.EnumerateObject().Select(entry => entry.Name).Should().Contain("request_receipt");
        foreach (var membership in new[] { arranged.TombstoneB, arranged.SuspendedB })
            (await MembershipAsync(TenantWideB, membership)).Should().Be(("Revoked", (string?)null, 0L),
                "every Membership of the group, of any status, loses its profile and assignments and is the Revoked tombstone");
        (await MembershipAsync(TenantWideA, LearnerMembershipA)).Should().Be(("Active", "profile-canary", 1L),
            "another tenant's Membership of the person is that tenant's fan-out's");
        (await UserStatusAsync(PersonA)).Should().Be("Closing", "the erasure never touches users");
        (await AuditCountAsync(TenantWideB, "identity.data_request.fulfill_erasure", request)).Should().Be(1);
    }

    /// <summary>
    /// ADR-0107 § 7's adoption, group by group, of what each group already held when the closure began: a person's erasure
    /// still in its window is cancelled by the system actor and replaced in the same transaction, the cancellation travelling
    /// in the fan-out row's changes; one past its window waits for its own job; a terminal one is replaced by a new fan-out;
    /// and one past its window whose job is dead is stored failed first, as every creation path stores it, then replaced.
    /// </summary>
    [Fact]
    public async Task Each_group_adopts_cancels_waits_for_or_replaces_what_it_already_holds()
    {
        var arranged = await ArrangeAsync();
        var inWindow = await FileAsync(TenantWideA, PersonA, [LearnerMembershipA], "erasure");
        var pastWindow = await FileAsync(BranchA1, PersonA, [BranchMembershipA1], "erasure");
        await DataErasureTestKit.BackdateAsync(_database!.MigrationConnectionString, SchemaFixture.TenantA, SchemaFixture.OrgA1, pastWindow);
        var cancelled = await FileAsync(BranchA2, PersonA, [BranchMembershipA2], "erasure");
        await CancelAsync(BranchA2, cancelled);
        var deadJob = await FileAsync(TenantWideB, PersonA, [arranged.TombstoneB, arranged.SuspendedB], "erasure");
        await DataErasureTestKit.BackdateAsync(_database!.MigrationConnectionString, SchemaFixture.TenantB, null, deadJob);
        await DataErasureTestKit.SubmitAsync(_database!, SchemaFixture.TenantB, null, deadJob);
        await KillJobAsync(TenantWideB, deadJob);
        await CloseAsync(PersonA);
        await using var host = Host(new Grants(true));

        var reports = await FanOutAsync(host, [TenantWideA, BranchA1, BranchA2, TenantWideB]);

        reports.Should().Equal(new ClosureGroupReport(TenantWideA, ClosureGroupOutcome.Started),
            new ClosureGroupReport(BranchA1, ClosureGroupOutcome.Waiting), new ClosureGroupReport(BranchA2, ClosureGroupOutcome.Started),
            new ClosureGroupReport(TenantWideB, ClosureGroupOutcome.Started));
        var tenantB = await RequestsAsync(TenantWideB);
        tenantB.Single(row => row.Id == deadJob).Status.Should().Be("failed", "a dead job's erasure is stored failed before it is replaced");
        tenantB.Should().ContainSingle(row => row.Origin == "closure_fanout" && row.Status == "fulfilling");

        var tenantWide = await RequestsAsync(TenantWideA);
        tenantWide.Single(row => row.Id == inWindow).Should().Match<RequestRow>(row => row.Status == "cancelled" && row.UpdatedBy == SystemActor,
            "the person's erasure in its window is cancelled by the system actor");
        var replacement = tenantWide.Should().ContainSingle(row => row.Origin == "closure_fanout").Subject;
        replacement.Status.Should().Be("fulfilling");
        var changes = await AuditChangesAsync(TenantWideA, replacement.Id);
        changes.Should().Contain(inWindow.ToString("D"), "the cancelled erasure travels in the fan-out row's changes");

        (await RequestsAsync(BranchA1)).Should().ContainSingle("a person's erasure past its window is its own job's; nothing is added")
            .Which.Should().Match<RequestRow>(row => row.Id == pastWindow && row.Status == "scheduled");
        var branch = (await AuditRowsAsync(BranchA1)).Should().ContainSingle().Subject;
        branch.Entity.Should().Be(pastWindow.ToString("D"), "the waiting group's row names the erasure it waits for");

        (await RequestsAsync(BranchA2)).Should().HaveCount(2).And.ContainSingle(row => row.Origin == "closure_fanout" && row.Status == "fulfilling",
            "a terminal erasure is replaced by a new fan-out");
    }

    /// <summary>
    /// A group's fan-out awaiting the privileged step is adopted as it is; one still <c>fulfilling</c> whose job is dead gets
    /// its job enqueued again by the repeat, and the second job resumes it to the privileged step.
    /// </summary>
    [Fact]
    public async Task A_repeat_adopts_an_awaiting_erasure_and_enqueues_a_dead_fan_out_job_again()
    {
        await ArrangeAsync();
        await CloseAsync(PersonA);
        await using var host = Host(new Grants(true));
        (await FanOutAsync(host, [TenantWideA, BranchA1])).Should().OnlyContain(report => report.Outcome == ClosureGroupOutcome.Started);
        var awaiting = (await RequestsAsync(TenantWideA)).Single().Id;
        (await RunJobAsync(host, TenantWideA, awaiting)).Committed();
        var dead = (await RequestsAsync(BranchA1)).Single().Id;
        await KillJobAsync(BranchA1, dead);

        var repeat = await FanOutAsync(host, [TenantWideA, BranchA1]);

        repeat.Should().Equal(new ClosureGroupReport(TenantWideA, ClosureGroupOutcome.Adopted), new ClosureGroupReport(BranchA1, ClosureGroupOutcome.Adopted));
        (await JobsAsync(TenantWideA, awaiting)).Should().Be(1, "an erasure awaiting its privileged step needs no job");
        (await JobsAsync(BranchA1, dead)).Should().Be(2, "the dead job's request is enqueued again");
        (await RequestsAsync(BranchA1)).Should().ContainSingle().Which.Status.Should().Be("fulfilling");

        (await RunJobAsync(host, BranchA1, dead, newest: true)).Committed();
        (await RequestsAsync(BranchA1)).Single().Status.Should().Be("awaiting_privileged_step", "the second job resumes the request");
    }

    /// <summary>
    /// The gate decides per act: an operator without <c>platform.data_request.write</c> starts no group, and nothing is
    /// written anywhere. A group whose tenant stopped allowing ordinary work after Begin judged it fails alone, with its MUST
    /// row and its refusal, and every other group is still started; the person not Closing is refused by the database.
    /// </summary>
    [Fact]
    public async Task The_gate_refuses_every_group_for_an_ungranted_operator_and_a_failed_group_does_not_stop_the_others()
    {
        await ArrangeAsync();
        await using (var refused = Host(new Grants(false)))
        {
            // Person A is still Active: no admission means not even the database's refusal is reached.
            (await FanOutAsync(refused, Groups)).Should().OnlyContain(report => report.Outcome == ClosureGroupOutcome.Failed);
            foreach (var group in Groups)
            {
                (await RequestsAsync(group)).Should().BeEmpty();
                (await AuditRowsAsync(group)).Should().BeEmpty("a refused admission runs no act and writes no row");
            }
        }

        await using var host = Host(new Grants(true));
        (await FanOutAsync(host, [TenantWideA])).Should().Equal([new ClosureGroupReport(TenantWideA, ClosureGroupOutcome.Failed)],
            "an Active person has no fan-out: the request guard refuses the subject");
        (await RequestsAsync(TenantWideA)).Should().BeEmpty();
        var refusal = (await AuditRowsAsync(TenantWideA)).Should().ContainSingle().Subject;
        refusal.Should().Be(new AuditRow(refusal.Entity, "failed", "closure_fan_out", true, false, "lockey_business_rule_violation"),
            "the refused group's row records the refusal, naming the request it would have created, which was never stored");
        refusal.Entity.Should().NotBeNull();

        await CloseAsync(PersonA);
        await SetTenantStatusAsync(SchemaFixture.TenantB, "Suspended");
        var reports = await FanOutAsync(host, Groups);

        reports.Should().Equal(new ClosureGroupReport(TenantWideA, ClosureGroupOutcome.Started), new ClosureGroupReport(BranchA1, ClosureGroupOutcome.Started),
            new ClosureGroupReport(BranchA2, ClosureGroupOutcome.Started), new ClosureGroupReport(TenantWideB, ClosureGroupOutcome.Failed));
        (await RequestsAsync(TenantWideB)).Should().BeEmpty("a group whose tenant no longer allows ordinary work is not started");
        (await AuditRowsAsync(TenantWideB)).Should().ContainSingle().Which.Should().Be(new AuditRow(null, "failed", "closure_fan_out", true, false,
            "lockey_not_found"));
    }

    /// <summary>Person A's tombstone and Suspended Membership in tenant B's tenant-wide group, written while person A is Active.</summary>
    private async Task<Arranged> ArrangeAsync()
    {
        _database = await DataExportTestKit.CreateSeededDatabaseAsync(fixture.Postgres);
        var arranged = new Arranged(Guid.CreateVersion7(), Guid.CreateVersion7());
        var learner = RoleId(SchemaFixture.TenantB, BuiltInRoleKey.Learner, PermissionScope.Tenant);
        await using var app = await DataExportTestKit.OpenAppAsync(_database.AppConnectionString);
        foreach (var step in new Func<NpgsqlTransaction, Task>[]
        {
            transaction => InsertMemberAsync(app, transaction, SchemaFixture.TenantB, null, arranged.TombstoneB, PersonA, learner, "tombstone-canary"),
            transaction => ExecuteAsync(app, transaction, """
                UPDATE public.memberships SET status='Revoked', deleted_at=clock_timestamp(), deleted_by=@system,
                    updated_at=clock_timestamp(), updated_by=@system, row_version=row_version+1 WHERE id=@member
                """, ("member", arranged.TombstoneB), ("system", SystemActor)),
            transaction => InsertMemberAsync(app, transaction, SchemaFixture.TenantB, null, arranged.SuspendedB, PersonA, learner, "suspended-canary"),
            transaction => ExecuteAsync(app, transaction, """
                UPDATE public.memberships SET status='Suspended', updated_at=clock_timestamp(), updated_by=@system,
                    row_version=row_version+1 WHERE id=@member
                """, ("member", arranged.SuspendedB), ("system", SystemActor)),
        })
        {
            await using var transaction = await app.BeginTransactionAsync();
            await ScopeAsync(app, transaction, SchemaFixture.TenantB);
            await LockAsync(app, transaction, SchemaFixture.TenantB, PersonA, false);
            await step(transaction);
            await ExecuteAsync(app, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
            await transaction.CommitAsync();
        }

        return arranged;
    }

    /// <summary>
    /// The person's User Active to Closing on the platform role, under the closure's fences as Begin's acquirer takes them:
    /// the User key, then each participant tenant's key ascending, all transaction-scoped and exclusive.
    /// </summary>
    private async Task CloseAsync(Guid user)
    {
        await using var platform = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database!.PlatformConnectionString);
        await using var transaction = await platform.BeginTransactionAsync();
        await ExecuteAsync(platform, transaction, "SELECT pg_catalog.pg_advisory_xact_lock(@key)",
            ("key", IdentityAdvisoryLockKey.ForUser(UserId.From(user))));
        foreach (var key in new[] { SchemaFixture.TenantA, SchemaFixture.TenantB }
                     .Select(tenant => IdentityAdvisoryLockKey.ForTenant(TenantId.From(tenant))).Order())
            await ExecuteAsync(platform, transaction, "SELECT pg_catalog.pg_advisory_xact_lock(@key)", ("key", key));
        await ExecuteAsync(platform, transaction, "UPDATE public.users SET status='Closing' WHERE id=@user", ("user", user));
        await transaction.CommitAsync();
    }

    /// <summary>The API composition over the disposable database, with the operator's grant held or withheld.</summary>
    private WebApplicationFactory<Program> Host(Grants grants, Action<IServiceCollection>? configure = null) =>
        new ConstructionOnlyApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Development);
            builder.UseSetting("ConnectionStrings:Default", _database!.AppConnectionString);
            builder.UseSetting("ConnectionStrings:OutboxDispatcher", _database!.OutboxConnectionString);
            builder.UseSetting("ConnectionStrings:PlatformAdmin", _database!.PlatformConnectionString);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IPlatformOperatorGrantSource>();
                services.AddSingleton<IPlatformOperatorGrantSource>(grants);
                configure?.Invoke(services);
            });
        });

    /// <summary>Begin's call, from a request scope with the trace the HTTP request would carry.</summary>
    private static async Task<IReadOnlyList<ClosureGroupReport>> FanOutAsync(WebApplicationFactory<Program> host,
        IReadOnlyList<ClosureParticipantGroup> groups)
    {
        using var activity = new Activity("closure-fan-out-test").Start();
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IClosureFanOut>().FanOutAsync(TestPlatformAdmissions.Witness(), Ticket,
            new ClosureFanOutRequest(UserId.From(PersonA), groups));
    }

    /// <summary>Runs the request's newest (or only) erasure job through the actual runtime.</summary>
    private async Task<JobBusinessExecutionResult> RunJobAsync(WebApplicationFactory<Program> host, ClosureParticipantGroup group, Guid request,
        bool newest = false)
    {
        var job = await JobIdAsync(group, request, newest);
        var executor = host.Services.GetRequiredService<JobExecutor>();
        var start = await executor.ExecuteAsync(await DataExportTestKit.RoutingClaim.ClaimAsync(_database!.OutboxConnectionString, job),
            BackgroundJobTestRun.Create());
        start.Outcome.Should().Be(JobStartExecutionOutcome.Started);
        return await executor.ExecuteBusinessAsync(start.Receipt!, start.Receipt!.Run.OpenBoundary(BackgroundJobCheckpointKind.Transactional));
    }

    private sealed record RequestRow(Guid Id, string Kind, string Origin, string Status, Guid? Requester, bool Windowed, Guid CreatedBy,
        string Snapshot)
    {
        public Guid? UpdatedBy { get; init; }
    }

    /// <summary>
    /// Every erasure of person A in the group, read under the group's own announcement; the seeded exports are not this
    /// proof's.
    /// </summary>
    private async Task<IReadOnlyList<RequestRow>> RequestsAsync(ClosureParticipantGroup group)
    {
        await using var app = await DataExportTestKit.OpenAppAsync(_database!.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, group.TenantId.Value, group.OrganizationId?.Value);
        await using var command = new NpgsqlCommand("""
            SELECT id, kind, origin, status, requester_user_id, execute_at IS NOT NULL, created_by, membership_snapshot::text, updated_by
            FROM public.data_subject_requests
            WHERE subject_user_id=@subject AND tenant_id=@tenant AND organization_id IS NOT DISTINCT FROM @organization::uuid
              AND kind='erasure'
            ORDER BY id
            """, app, transaction);
        command.Parameters.AddWithValue("subject", PersonA);
        command.Parameters.AddWithValue("tenant", group.TenantId.Value);
        command.Parameters.AddWithValue("organization", (object?)group.OrganizationId?.Value ?? DBNull.Value);
        var rows = new List<RequestRow>();
        await using var row = await command.ExecuteReaderAsync();
        while (await row.ReadAsync())
            rows.Add(new RequestRow(row.GetGuid(0), row.GetString(1), row.GetString(2), row.GetString(3), row.IsDBNull(4) ? null : row.GetGuid(4),
                row.GetBoolean(5), row.GetGuid(6), Normalize(row.GetString(7)))
            { UpdatedBy = row.IsDBNull(8) ? null : row.GetGuid(8) });
        return rows;
    }

    private static string Snapshot(Guid[] memberships) => Normalize(DataRequestSchemaSeed.Snapshot(memberships));

    private static string Normalize(string json) =>
        JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement.EnumerateArray().Select(item => item.GetString()).Order(StringComparer.Ordinal));

    private async Task<(string Progress, string Result)> RequestDocumentsAsync(ClosureParticipantGroup group, Guid request)
    {
        await using var app = await DataExportTestKit.OpenAppAsync(_database!.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, group.TenantId.Value, group.OrganizationId?.Value);
        await using var command = new NpgsqlCommand("SELECT progress::text, result::text FROM public.data_subject_requests WHERE id=@id",
            app, transaction);
        command.Parameters.AddWithValue("id", request);
        await using var row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        return (row.GetString(0), row.GetString(1));
    }

    /// <summary>
    /// How many rows of the request the group's own announcement shows, as the application role: the plain count, so a row
    /// the policy leaked is one however it is scoped.
    /// </summary>
    private async Task<long> VisibleAsync(ClosureParticipantGroup group, Guid request)
    {
        await using var app = await DataExportTestKit.OpenAppAsync(_database!.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, group.TenantId.Value, group.OrganizationId?.Value);
        return await ScalarAsync<long>(app, transaction, "SELECT count(*) FROM public.data_subject_requests WHERE id=@id", ("id", request));
    }

    /// <summary>The erasure jobs of the request in its group, by the exact route the scheduler stored.</summary>
    private async Task<long> JobsAsync(ClosureParticipantGroup group, Guid request)
    {
        await using var app = await DataExportTestKit.OpenAppAsync(_database!.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, group.TenantId.Value, group.OrganizationId?.Value);
        return await ScalarAsync<long>(app, transaction, """
            SELECT count(*) FROM public.background_jobs
            WHERE tenant_id=@tenant AND organization_id IS NOT DISTINCT FROM @organization::uuid AND definition_key=@definition
              AND parameters=@parameters
            """, ("tenant", group.TenantId.Value), ("organization", (object?)group.OrganizationId?.Value ?? DBNull.Value),
            ("definition", Definition(group)), ("parameters", DataErasureTestKit.Parameters(request)));
    }

    private async Task<BackgroundJobId> JobIdAsync(ClosureParticipantGroup group, Guid request, bool newest)
    {
        await using var app = await DataExportTestKit.OpenAppAsync(_database!.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, group.TenantId.Value, group.OrganizationId?.Value);
        var id = await ScalarAsync<Guid>(app, transaction, $"""
            SELECT id FROM public.background_jobs
            WHERE tenant_id=@tenant AND organization_id IS NOT DISTINCT FROM @organization::uuid AND definition_key=@definition
              AND parameters=@parameters
            ORDER BY created_at {(newest ? "DESC" : "ASC")}, id {(newest ? "DESC" : "ASC")} LIMIT 1
            """, ("tenant", group.TenantId.Value), ("organization", (object?)group.OrganizationId?.Value ?? DBNull.Value),
            ("definition", Definition(group)), ("parameters", DataErasureTestKit.Parameters(request)));
        return BackgroundJobId.From(id);
    }

    /// <summary>Quarantines the request's job as the runtime does with an unknown outcome: the job is dead.</summary>
    private async Task KillJobAsync(ClosureParticipantGroup group, Guid request)
    {
        var job = await JobIdAsync(group, request, newest: true);
        await using var dispatcher = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database!.OutboxConnectionString);
        (await ScalarAsync<long>(dispatcher, null, """
            WITH moved AS (UPDATE public.background_jobs SET phase='quarantined', quarantine_reason='unknown_outcome' WHERE id=@job RETURNING 1)
            SELECT count(*) FROM moved
            """, ("job", job.Value))).Should().Be(1);
    }

    private static string Definition(ClosureParticipantGroup group) => group.OrganizationId is null
        ? "identity.data_erasure_tenant.v1" : "identity.data_erasure_organization.v1";

    private sealed record AuditRow(string? Entity, string Outcome, string? Reason, bool Operator, bool Ticket, string? ErrorKey);

    /// <summary>The fan-out's rows in exactly the group, read under the group's own announcement.</summary>
    private async Task<IReadOnlyList<AuditRow>> AuditRowsAsync(ClosureParticipantGroup group)
    {
        await using var app = await DataExportTestKit.OpenAppAsync(_database!.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, group.TenantId.Value, group.OrganizationId?.Value);
        await using var command = new NpgsqlCommand("""
            SELECT entity_id, outcome, reason, metadata ? 'operator', metadata::text LIKE '%' || @ticket || '%', error_key
            FROM public.audit_log
            WHERE operation=@operation AND organization_id IS NOT DISTINCT FROM @organization::uuid
            ORDER BY timestamp, id
            """, app, transaction);
        command.Parameters.AddWithValue("operation", Operation);
        command.Parameters.AddWithValue("ticket", Ticket);
        command.Parameters.AddWithValue("organization", (object?)group.OrganizationId?.Value ?? DBNull.Value);
        var rows = new List<AuditRow>();
        await using var row = await command.ExecuteReaderAsync();
        while (await row.ReadAsync())
            rows.Add(new AuditRow(row.IsDBNull(0) ? null : row.GetString(0), row.GetString(1), row.IsDBNull(2) ? null : row.GetString(2),
                row.GetBoolean(3), row.GetBoolean(4), row.IsDBNull(5) ? null : row.GetString(5)));
        return rows;
    }

    private async Task<string> AuditChangesAsync(ClosureParticipantGroup group, Guid request)
    {
        await using var app = await DataExportTestKit.OpenAppAsync(_database!.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, group.TenantId.Value, group.OrganizationId?.Value);
        return await ScalarAsync<string>(app, transaction, """
            SELECT coalesce(changes::text, '') FROM public.audit_log
            WHERE operation=@operation AND outcome='success' AND entity_id=@entity
            """, ("operation", Operation), ("entity", request.ToString("D")));
    }

    private async Task<long> AuditCountAsync(ClosureParticipantGroup group, string operation, Guid entity)
    {
        await using var app = await DataExportTestKit.OpenAppAsync(_database!.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, group.TenantId.Value, group.OrganizationId?.Value);
        return await ScalarAsync<long>(app, transaction, """
            SELECT count(*) FROM public.audit_log WHERE operation=@operation AND outcome='success' AND entity_id=@entity
            """, ("operation", operation), ("entity", entity.ToString("D")));
    }

    private async Task<(string Status, string? DisplayName, long Assignments)> MembershipAsync(ClosureParticipantGroup group, Guid membership)
    {
        await using var app = await DataExportTestKit.OpenAppAsync(_database!.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, group.TenantId.Value, group.OrganizationId?.Value);
        await using var command = new NpgsqlCommand("""
            SELECT m.status, p.display_name,
                (SELECT count(*) FROM public.membership_role_assignments a WHERE a.tenant_id=m.tenant_id AND a.membership_id=m.id)
            FROM public.memberships m JOIN public.membership_profiles p ON p.tenant_id=m.tenant_id AND p.membership_id=m.id
            WHERE m.id=@id
            """, app, transaction);
        command.Parameters.AddWithValue("id", membership);
        await using var row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        return (row.GetString(0), row.IsDBNull(1) ? null : row.GetString(1), row.GetInt64(2));
    }

    private async Task<string> UserStatusAsync(Guid user)
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database!.MigrationConnectionString);
        return await ScalarAsync<string>(owner, null, "SELECT status FROM public.users WHERE id=@id", ("id", user));
    }

    /// <summary>A self-filed request in its initial state, under the canonical fences, while the person is Active.</summary>
    private async Task<Guid> FileAsync(ClosureParticipantGroup group, Guid subject, Guid[] memberships, string kind)
    {
        var id = Guid.CreateVersion7();
        await using var app = await DataExportTestKit.OpenAppAsync(_database!.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await FenceAsync(app, transaction, group.TenantId.Value, group.OrganizationId?.Value, subject);
        await InsertRequestAsync(app, transaction, id, group.TenantId.Value, group.OrganizationId?.Value, subject, memberships, kind);
        await transaction.CommitAsync();
        return id;
    }

    /// <summary>The person cancels a scheduled erasure in its window, as the application role does.</summary>
    private async Task CancelAsync(ClosureParticipantGroup group, Guid request)
    {
        await using var app = await DataExportTestKit.OpenAppAsync(_database!.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await FenceAsync(app, transaction, group.TenantId.Value, group.OrganizationId?.Value, null);
        await StepAsync(app, transaction, request, "status='cancelled'");
        await transaction.CommitAsync();
    }

    private async Task SetTenantStatusAsync(Guid tenant, string status)
    {
        await using var app = await DataExportTestKit.OpenAppAsync(_database!.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(app, transaction, tenant, null);
        await TenancyLifecycleFixture.LockAsync(app, transaction, tenant);
        await ExecuteAsync(app, transaction, "UPDATE tenants SET status = @status WHERE id = @tenant", ("status", status), ("tenant", tenant));
        await transaction.CommitAsync();
    }

    /// <summary>The operator's grant of the one key every fan-out act needs, held or withheld.</summary>
    private sealed class Grants(bool held) : IPlatformOperatorGrantSource
    {
        public ValueTask<bool> IsGrantedAsync(string subjectDigest, string permissionKey, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(held && permissionKey == PlatformPermissions.DataRequestWrite.Key
                && subjectDigest == TestPlatformAdmissions.Witness().SubjectDigest);
    }

    /// <summary>A hold the eligibility port would report for every request: a closure's erasure never asks it.</summary>
    private sealed class EveryHold : IDataRequestHoldSource
    {
        public Task<bool> HasHoldAsync(DataRequestHoldQuery query, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
