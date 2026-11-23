using FluentAssertions;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Platform;
using Xunit;
using static LearnStack.Tests.Integration.Database.DataRequestSchemaSeed;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The fan-out's branches the first suite left unreached, and the disposal of each case's database: a person's or an
/// administrator's own erasure that already awaits its privileged step, a completed erasure the group still holds, an
/// organization that stopped allowing ordinary work, and an organization's group fanned out without its tenant-wide group.
/// </summary>
public sealed partial class ClosureFanOutTests : IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>Each case owns a disposable copy of the seeded schema; it is dropped when the case ends.</summary>
    public async Task DisposeAsync()
    {
        if (_database is { } database)
        {
            _database = null;
            await database.DisposeAsync();
        }
    }

    /// <summary>
    /// An erasure the person or an administrator filed and whose own job already ended at the privileged step is adopted
    /// as it is, whatever its origin: the group answers adopted, files nothing, enqueues no job and records the request it
    /// adopted.
    /// </summary>
    [Theory]
    [InlineData("self")]
    [InlineData("administrator")]
    public async Task An_erasure_the_person_or_an_administrator_filed_that_awaits_its_privileged_step_is_adopted_as_it_is(string origin)
    {
        await ArrangeAsync();
        var own = await AwaitingErasureAsync(TenantWideA, LearnerMembershipA, origin);
        await CloseAsync(PersonA);
        await using var host = Host(new Grants(true));

        var reports = await FanOutAsync(host, [TenantWideA]);

        reports.Should().Equal([new ClosureGroupReport(TenantWideA, ClosureGroupOutcome.Adopted)]);
        var held = (await RequestsAsync(TenantWideA)).Should().ContainSingle("nothing is filed beside the erasure the group holds").Subject;
        (held.Id, held.Origin, held.Status).Should().Be((own, origin, "awaiting_privileged_step"));
        (await JobsAsync(TenantWideA, own)).Should().Be(0, "an erasure awaiting its privileged step needs no job");
        (await AuditRowsAsync(TenantWideA)).Should().ContainSingle().Which.Should()
            .Be(new AuditRow(own.ToString("D"), "success", "closure_fan_out", true, false, null), "the row names the request the group adopted");
    }

    /// <summary>
    /// An erasure the group already completed (a person admitted again since) does not stand in for the closure's: the
    /// group starts a new fan-out erasure beside it.
    /// </summary>
    [Fact]
    public async Task A_completed_erasure_the_group_holds_is_replaced_by_a_new_fan_out()
    {
        await ArrangeAsync();
        var completed = Guid.CreateVersion7();
        await fixture.Postgres.ExecuteAsSuperuserAsync($"""
            SET session_replication_role = replica;
            INSERT INTO public.data_subject_requests(id,tenant_id,organization_id,kind,origin,subject_user_id,requester_user_id,status,
                membership_snapshot,execute_at,completed_at,created_at,created_by,updated_at,updated_by,row_version)
            VALUES('{completed}','{SchemaFixture.TenantA}',NULL,'erasure','self','{PersonA}','{PersonA}','completed',
                '["{LearnerMembershipA}"]'::jsonb,now() - interval '1 day',now() - interval '1 day',now() - interval '2 days','{PersonA}',
                now() - interval '1 day','{SystemActor}',2)
            """, RebindProofSupport.DatabaseOf(_database!.MigrationConnectionString));
        await CloseAsync(PersonA);
        await using var host = Host(new Grants(true));

        var reports = await FanOutAsync(host, [TenantWideA]);

        reports.Should().Equal([new ClosureGroupReport(TenantWideA, ClosureGroupOutcome.Started)]);
        var requests = await RequestsAsync(TenantWideA);
        requests.Should().HaveCount(2);
        requests.Single(row => row.Id == completed).Status.Should().Be("completed", "the earlier erasure is left as it ended");
        requests.Should().ContainSingle(row => row.Origin == "closure_fanout" && row.Status == "fulfilling");
    }

    /// <summary>
    /// An organization that stopped allowing ordinary work after the Begin classified it fails alone, as a tenant that did:
    /// no erasure is created in it, its MUST row records the refusal, and the other groups are still started.
    /// </summary>
    [Fact]
    public async Task An_organization_that_stopped_allowing_ordinary_work_fails_alone()
    {
        await ArrangeAsync();
        await CloseAsync(PersonA);
        await fixture.Postgres.ExecuteAsSuperuserAsync(
            $"SET session_replication_role = replica; UPDATE public.organizations SET status = 'Archived' "
            + $"WHERE tenant_id = '{SchemaFixture.TenantA}' AND id = '{SchemaFixture.OrgA1}'",
            RebindProofSupport.DatabaseOf(_database!.MigrationConnectionString));
        await using var host = Host(new Grants(true));

        var reports = await FanOutAsync(host, [TenantWideA, BranchA1, BranchA2]);

        reports.Should().Equal(new ClosureGroupReport(TenantWideA, ClosureGroupOutcome.Started),
            new ClosureGroupReport(BranchA1, ClosureGroupOutcome.Failed), new ClosureGroupReport(BranchA2, ClosureGroupOutcome.Started));
        (await RequestsAsync(BranchA1)).Should().BeEmpty("a group whose organization no longer allows ordinary work is not started");
        (await AuditRowsAsync(BranchA1)).Should().ContainSingle().Which.Should()
            .Be(new AuditRow(null, "failed", "closure_fan_out", true, false, "lockey_not_found"));
        (await RequestsAsync(TenantWideA)).Should().ContainSingle();
        (await RequestsAsync(BranchA2)).Should().ContainSingle();
    }

    /// <summary>
    /// An organization's group is fanned out and erased by itself: its erasure freezes the organization's Membership alone,
    /// the job erases that Membership and leaves the person's tenant-wide Membership and the other branch's as they were.
    /// </summary>
    [Fact]
    public async Task An_organization_group_is_fanned_out_and_erased_without_its_tenant_wide_group()
    {
        await ArrangeAsync();
        await CloseAsync(PersonA);
        await using var host = Host(new Grants(true));

        (await FanOutAsync(host, [BranchA1])).Should().Equal([new ClosureGroupReport(BranchA1, ClosureGroupOutcome.Started)]);

        var request = (await RequestsAsync(BranchA1)).Single();
        request.Snapshot.Should().Be(Snapshot([BranchMembershipA1]));
        (await RequestsAsync(TenantWideA)).Should().BeEmpty("the tenant-wide group was not fanned out");
        (await RunJobAsync(host, BranchA1, request.Id)).Committed();
        (await RequestsAsync(BranchA1)).Single().Status.Should().Be("awaiting_privileged_step");
        (await MembershipAsync(BranchA1, BranchMembershipA1)).Should().Be(("Revoked", (string?)null, 0L));
        (await MembershipAsync(TenantWideA, LearnerMembershipA)).Should().Be(("Active", "profile-canary", 1L),
            "the tenant-wide Membership is the tenant-wide group's");
        (await MembershipAsync(BranchA2, BranchMembershipA2)).Should().Be(("Active", "profile-canary", 1L), "so is the other branch's");
    }

    /// <summary>
    /// An erasure a person or an administrator filed in the group, taken by the app role through its own arrows to the
    /// privileged step as its job leaves it, while the person is still Active.
    /// </summary>
    private async Task<Guid> AwaitingErasureAsync(ClosureParticipantGroup group, Guid membership, string origin)
    {
        var id = Guid.CreateVersion7();
        var requester = origin == "self" ? PersonA : AdminA;
        var (tenant, organization) = (group.TenantId.Value, group.OrganizationId?.Value);
        await using var app = await DataExportTestKit.OpenAppAsync(_database!.AppConnectionString);
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await FenceAsync(app, transaction, tenant, organization, PersonA);
            await ExecuteAsync(app, transaction, """
                INSERT INTO public.data_subject_requests(id,tenant_id,organization_id,kind,origin,subject_user_id,requester_user_id,
                    status,membership_snapshot,execute_at,created_at,created_by)
                VALUES(@id,@tenant,@organization,'erasure',@origin,@subject,@requester,'scheduled',@snapshot::jsonb,
                    now() + interval '24 hours',now(),@requester)
                """, ("id", id), ("tenant", tenant), ("organization", organization), ("origin", origin), ("subject", PersonA),
                ("requester", requester), ("snapshot", DataRequestSchemaSeed.Snapshot(membership)));
            await transaction.CommitAsync();
        }

        await DataErasureTestKit.BackdateAsync(_database!.MigrationConnectionString, tenant, organization, id);
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await FenceAsync(app, transaction, tenant, organization, null);
            await StepAsync(app, transaction, id, "status='fulfilling'");
            await StepAsync(app, transaction, id, "result='{\"consent_evidence\":2}'");
            await StepAsync(app, transaction, id, "status='awaiting_privileged_step'");
            await transaction.CommitAsync();
        }

        return id;
    }
}
