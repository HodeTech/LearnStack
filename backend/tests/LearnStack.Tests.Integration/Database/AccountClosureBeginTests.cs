using System.Text.Json;
using FluentAssertions;
using LearnStack.Infrastructure.MultiTenancy;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Platform;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The closure's Begin owner (ADR-0107 § 7) against a real database, the real platform scope, the real audit store, the real
/// Tenancy lifecycle read and the request guard's and the <c>users</c> trigger's closure arms. The fan-out and the hold
/// source are recording stand-ins: the fan-out's own act is proven by its own suite, and a hold source has no production
/// implementation yet. Every statement under test runs as <c>learnstack_platform</c> through the scope.
/// </summary>
/// <remarks>
/// Each test runs in its own disposable copy of the seeded schema and closes fresh persons, because a User a closure moved
/// never returns to Active.
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed partial class AccountClosureBeginTests(SchemaFixture schema)
{
    private static readonly Guid TenantA = SchemaFixture.TenantA;
    private static readonly Guid TenantB = SchemaFixture.TenantB;

    /// <summary>
    /// A person in two tenants and four groups: tenant A's tenant-wide group and two branches, one of them archived, and
    /// tenant B's tenant-wide group in a suspended tenant. One transaction fences, judges and moves the User and the request
    /// and writes the sentinel result row; only then, with the fences released, the two served groups are fanned out.
    /// </summary>
    [Fact]
    public async Task A_Begin_moves_the_person_and_the_request_records_it_under_the_sentinel_and_then_fans_out_the_served_groups()
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var person = await kit.PersonAsync();
        await kit.MemberAsync(TenantA, null, person, BuiltInRoleKey.Learner);
        await kit.MemberAsync(TenantA, SchemaFixture.OrgA1, person, BuiltInRoleKey.Learner);
        await kit.MemberAsync(TenantA, SchemaFixture.OrgA2, person, BuiltInRoleKey.Learner);
        await kit.MemberAsync(TenantB, null, person, BuiltInRoleKey.Learner);
        var closure = await kit.ClosureAsync(TenantA, null, person);
        await kit.OrganizationStatusAsync(TenantA, SchemaFixture.OrgA2, "Archived");
        await kit.TenantStatusAsync(TenantB, "Suspended");
        var userBefore = await kit.UserAsync(person);
        var requestBefore = await kit.RequestAsync(closure);
        var fanOut = new RecordingFanOut();
        fanOut.Probe = async () =>
        {
            // What the fan-out meets: the Begin committed and its fences ended with it.
            (await kit.UserAsync(person)).Status.Should().Be("Closing", "the fan-out runs after the Begin committed");
            (await kit.RequestAsync(closure)).Status.Should().Be("fulfilling");
            (await kit.PlatformAdvisoryLocksAsync()).Should().BeEmpty("no platform connection still holds a fence");
        };
        var admission = AccountClosureBeginKit.Admission("INC-701");

        var result = await kit.Owner(fanOut).BeginAsync(admission, closure);

        result.Outcome.Should().Be(ClosureBeginOutcome.Begun);
        result.Status.Should().Be("fulfilling");
        result.Groups.Should().Be(new ClosureBeginTally(Served: 2, Started: 2, Adopted: 0, Waiting: 0, Failed: 0, Unserved: 2));
        result.Groups!.Complete.Should().BeTrue();

        var call = fanOut.Calls.Should().ContainSingle().Which;
        call.Request.SubjectUserId.Value.Should().Be(person);
        call.Request.Groups.Should().BeEquivalentTo(new[]
        {
            new ClosureParticipantGroup(TenantId.From(TenantA), null),
            new ClosureParticipantGroup(TenantId.From(TenantA), OrganizationId.From(SchemaFixture.OrgA1)),
        }, "an archived branch and a suspended tenant are unserved and are not fanned out");
        call.Operator.Should().BeSameAs(admission.Operator, "each group's act is admitted for the same operator");
        call.Ticket.Should().Be("INC-701");

        var user = await kit.UserAsync(person);
        user.Should().Be(userBefore with { Status = "Closing", Version = userBefore.Version + 1 },
            "Active to Closing keeps the raw subject until Finalize");
        (await kit.RequestAsync(closure)).Should().Be(requestBefore with { Status = "fulfilling", Version = requestBefore.Version + 1 });

        var rows = await kit.AuditAsync(admission.Operator.CorrelationId!);
        rows.Should().HaveCount(2, "the scope's entry row and one result row");
        var entry = rows.Single(row => row.Operation == PlatformAdminScope.EnterOperation);
        (entry.Tenant, entry.Reason).Should().Be((TenantId.PlatformSentinel.Value, "closure_begin"));
        var recorded = rows.Single(row => row.Operation == "platform.closure.begin");
        (recorded.Tenant, recorded.Module, recorded.Reason, recorded.Actor, recorded.EntityType, recorded.EntityId).Should().Be(
            (TenantId.PlatformSentinel.Value, "platform", "closure_begin", (Guid?)null, "DataSubjectRequest", closure.ToString("D")));
        recorded.Metadata.Should().Contain("INC-701", "a sentinel row no tenant reads carries the ticket");
        SameJson(recorded.Before, """{"status":"scheduled","account":"active"}""");
        SameJson(recorded.After, """{"status":"fulfilling","account":"closing","served_groups":2,"unserved_groups":2}""");
        foreach (var row in rows)
        {
            (row.Before + row.After).Should().NotContain(person.ToString("D"), "no state names the person");
        }
    }

    /// <summary>
    /// While a Begin is held at its judgment, its backend holds the person's User key and the key of every participant
    /// tenant, the Revoked tombstone's tenant included, exclusively and as nothing else; they end with its commit.
    /// </summary>
    [Fact]
    public async Task The_fences_are_the_User_key_and_every_participant_tenant_key_held_exclusively_until_the_commit()
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var person = await kit.PersonAsync();
        await kit.MemberAsync(TenantA, SchemaFixture.OrgA1, person, BuiltInRoleKey.Learner);
        var tombstone = await kit.MemberAsync(TenantB, null, person, BuiltInRoleKey.Learner);
        await RevokeAsync(kit, TenantB, null, person, tombstone);
        var closure = await kit.ClosureAsync(TenantA, SchemaFixture.OrgA1, person);
        var holds = new GatedHolds(gate: true);

        var begin = kit.Owner(holds: holds).BeginAsync(AccountClosureBeginKit.Admission(), closure);
        try
        {
            await holds.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            (await kit.PlatformAdvisoryLocksAsync()).Should().BeEquivalentTo(new[]
            {
                (AccountClosureBeginKit.UserKey(person), "ExclusiveLock"),
                (AccountClosureBeginKit.TenantKey(TenantA), "ExclusiveLock"),
                (AccountClosureBeginKit.TenantKey(TenantB), "ExclusiveLock"),
            }, "the User key and both participant tenants' keys, the tombstone's tenant included, and nothing else");
        }
        finally
        {
            holds.Release.TrySetResult();
        }

        (await begin).Outcome.Should().Be(ClosureBeginOutcome.Begun);
        (await kit.PlatformAdvisoryLocksAsync()).Should().BeEmpty("transaction-scoped keys end at the commit");
        holds.Queries.Should().ContainSingle().Which.Should().Match<DataRequestHoldQuery>(query =>
            query.Kind == DataRequestKind.Closure && query.SubjectUserId == UserId.From(person)
            && query.Group.TenantId == TenantId.From(TenantA) && query.Group.OrganizationId == OrganizationId.From(SchemaFixture.OrgA1));
    }

    /// <summary>
    /// A closure in its window is refused <c>blocked_window</c> by the database clock: the code is recorded on the request
    /// with a result row, and the User is untouched. Refused again for the same reason, nothing changes and no second row is
    /// written; refused for a hold, the code is replaced with a row of its own.
    /// </summary>
    [Fact]
    public async Task A_refusal_is_recorded_on_the_request_once_per_code_and_changes_nothing_else()
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var person = await kit.PersonAsync();
        await kit.MemberAsync(TenantA, null, person, BuiltInRoleKey.Learner);
        var closure = await kit.ClosureAsync(TenantA, null, person, due: false);
        var user = await kit.UserAsync(person);
        var fanOut = new RecordingFanOut();
        var request = await kit.RequestAsync(closure);

        var first = AccountClosureBeginKit.Admission("INC-702");
        var window = await kit.Owner(fanOut).BeginAsync(first, closure);

        window.Should().Be(new ClosureBeginResult(ClosureBeginOutcome.RefusedWindow, "scheduled", "blocked_window"));
        (await kit.RequestAsync(closure)).Should().Be(request with { LastRefusal = "blocked_window", Version = request.Version + 1 });
        var recorded = (await kit.AuditAsync(first.Operator.CorrelationId!)).Single(row => row.Operation == "platform.closure.begin");
        (recorded.Tenant, recorded.EntityId).Should().Be((TenantId.PlatformSentinel.Value, closure.ToString("D")));
        SameJson(recorded.Before, """{"status":"scheduled","last_refusal":null}""");
        SameJson(recorded.After, """{"status":"scheduled","last_refusal":"blocked_window"}""");

        var repeat = AccountClosureBeginKit.Admission();
        (await kit.Owner(fanOut).BeginAsync(repeat, closure)).Outcome.Should().Be(ClosureBeginOutcome.RefusedWindow);
        (await kit.RequestAsync(closure)).Version.Should().Be(request.Version + 1, "the standing code is not recorded again");
        (await kit.AuditAsync(repeat.Operator.CorrelationId!)).Should().ContainSingle()
            .Which.Operation.Should().Be(PlatformAdminScope.EnterOperation, "a refusal that changes nothing ends by disposal");

        await kit.BackdateAsync(closure, TenantA, null);
        var held = new GatedHolds(hold: true);
        (await kit.Owner(fanOut, held).BeginAsync(AccountClosureBeginKit.Admission(), closure))
            .Should().Be(new ClosureBeginResult(ClosureBeginOutcome.RefusedHold, "scheduled", "blocked_hold"));
        (await kit.RequestAsync(closure)).Should().Be(request with { LastRefusal = "blocked_hold", Version = request.Version + 2 });
        (await kit.ResultRowsAsync(closure)).Should().Be(2, "each recorded refusal has its own row");

        (await kit.UserAsync(person)).Should().Be(user, "a refusal never moves the User");
        fanOut.Calls.Should().BeEmpty("a refused Begin fans nothing out");
    }

    /// <summary>
    /// The person is the sole eligible administrator of tenant A and a learner of tenant B, which has its own
    /// administrator: refused <c>blocked_last_administrator</c>. Tenant A suspended, it does not allow ordinary work and is
    /// not judged, so the same Begin succeeds with tenant A unserved.
    /// </summary>
    [Fact]
    public async Task The_sole_administrator_of_an_operable_tenant_is_refused_and_a_tenant_that_allows_no_work_is_not_judged()
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var administrator = await kit.PersonAsync();
        await kit.MemberAsync(TenantB, null, administrator, BuiltInRoleKey.Learner);
        await kit.MemberAsync(TenantA, null, administrator, BuiltInRoleKey.TenantAdmin);
        // The seeded administrator of tenant A leaves, so the person is its only eligible one.
        await RevokeAsync(kit, TenantA, null, IdentityMembershipFixture.AdminA, IdentityMembershipFixture.AdminMembershipA);
        var closure = await kit.ClosureAsync(TenantB, null, administrator);
        var fanOut = new RecordingFanOut();

        (await kit.Owner(fanOut).BeginAsync(AccountClosureBeginKit.Admission(), closure))
            .Should().Be(new ClosureBeginResult(ClosureBeginOutcome.RefusedLastAdministrator, "scheduled", "blocked_last_administrator"));
        (await kit.RequestAsync(closure)).LastRefusal.Should().Be("blocked_last_administrator");
        (await kit.UserAsync(administrator)).Status.Should().Be("Active");

        await kit.TenantStatusAsync(TenantA, "Suspended");
        var begun = await kit.Owner(fanOut).BeginAsync(AccountClosureBeginKit.Admission(), closure);

        begun.Outcome.Should().Be(ClosureBeginOutcome.Begun, "a suspended tenant has no ordinary work to protect");
        begun.Groups.Should().Be(new ClosureBeginTally(1, 1, 0, 0, 0, 1));
        fanOut.Calls.Should().ContainSingle().Which.Request.Groups.Should().Equal(new ClosureParticipantGroup(TenantId.From(TenantB), null));
        (await kit.UserAsync(administrator)).Status.Should().Be("Closing");
    }

    /// <summary>
    /// A repeat on a closure that began fences nothing, judges nothing and writes nothing but its entry row, and fans out
    /// again; a second closure of the same Closing person begins with the User left as it is.
    /// </summary>
    [Fact]
    public async Task A_repeat_resumes_without_a_write_and_a_second_closure_of_a_closing_person_begins_without_moving_the_User()
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var person = await kit.PersonAsync();
        await kit.MemberAsync(TenantA, null, person, BuiltInRoleKey.Learner);
        await kit.MemberAsync(TenantA, SchemaFixture.OrgA1, person, BuiltInRoleKey.Learner);
        var closure = await kit.ClosureAsync(TenantA, null, person);
        var second = await kit.ClosureAsync(TenantA, SchemaFixture.OrgA1, person);
        var fanOut = new RecordingFanOut();
        (await kit.Owner(fanOut).BeginAsync(AccountClosureBeginKit.Admission(), closure)).Outcome.Should().Be(ClosureBeginOutcome.Begun);
        var user = await kit.UserAsync(person);
        var request = await kit.RequestAsync(closure);

        var repeat = AccountClosureBeginKit.Admission();
        fanOut.Outcome = _ => ClosureGroupOutcome.Adopted;
        var resumed = await kit.Owner(fanOut, new GatedHolds(hold: true)).BeginAsync(repeat, closure);

        resumed.Should().Be(new ClosureBeginResult(
            ClosureBeginOutcome.Resumed, "fulfilling", Groups: new ClosureBeginTally(2, 0, 2, 0, 0, 0)),
            "a closure that began is not judged again, so a hold that appears later does not stop its fan-out");
        (await kit.RequestAsync(closure)).Should().Be(request);
        (await kit.UserAsync(person)).Should().Be(user);
        (await kit.AuditAsync(repeat.Operator.CorrelationId!)).Should().ContainSingle()
            .Which.Operation.Should().Be(PlatformAdminScope.EnterOperation, "a resume writes nothing, so its entry row is its record");
        fanOut.Calls.Should().HaveCount(2).And.OnlyContain(call => call.Request.Groups.Count == 2);

        var joined = AccountClosureBeginKit.Admission();
        fanOut.Outcome = _ => ClosureGroupOutcome.Adopted;
        (await kit.Owner(fanOut).BeginAsync(joined, second)).Should().Be(new ClosureBeginResult(
            ClosureBeginOutcome.Resumed, "fulfilling", Groups: new ClosureBeginTally(2, 0, 2, 0, 0, 0)));
        (await kit.RequestAsync(second)).Status.Should().Be("fulfilling");
        (await kit.UserAsync(person)).Should().Be(user, "the User is already Closing for the first closure");
        var row = (await kit.AuditAsync(joined.Operator.CorrelationId!)).Single(entry => entry.Operation == "platform.closure.begin");
        JsonDocument.Parse(row.Before).RootElement.GetProperty("account").GetString().Should().Be("closing");
    }

    /// <summary>
    /// What cannot begin is refused and changes nothing: a missing request, an export and an erasure, a completed and a
    /// cancelled closure, and a closure whose person is already Closed.
    /// </summary>
    [Fact]
    public async Task What_cannot_begin_is_refused_with_its_reason_and_only_the_entry_row_records_it()
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var person = await kit.PersonAsync();
        await kit.MemberAsync(TenantA, null, person, BuiltInRoleKey.Learner);
        var fanOut = new RecordingFanOut();
        var owner = kit.Owner(fanOut);
        var cases = new List<(Guid Request, ClosureBeginOutcome Outcome, string? Status)>
        {
            (Guid.CreateVersion7(), ClosureBeginOutcome.NotFound, null),
            (await kit.RequestRowAsync(TenantA, person, "export", "completed"), ClosureBeginOutcome.NotClosure, "completed"),
            (await kit.RequestRowAsync(TenantA, person, "erasure", "scheduled"), ClosureBeginOutcome.NotClosure, "scheduled"),
            (await kit.RequestRowAsync(TenantA, person, "closure", "completed"), ClosureBeginOutcome.NotBeginnable, "completed"),
            (await kit.RequestRowAsync(TenantA, person, "closure", "cancelled"), ClosureBeginOutcome.NotBeginnable, "cancelled"),
        };

        foreach (var (request, outcome, status) in cases)
        {
            var admission = AccountClosureBeginKit.Admission();
            var result = await owner.BeginAsync(admission, request);

            (result.Outcome, result.Status).Should().Be((outcome, status), outcome.ToString());
            (await kit.AuditAsync(admission.Operator.CorrelationId!)).Should().ContainSingle(outcome.ToString())
                .Which.Operation.Should().Be(PlatformAdminScope.EnterOperation);
        }

        (await kit.UserAsync(person)).Status.Should().Be("Active", "nothing above moved the person");

        // A person Closed by a finished closure: a closure of theirs left scheduled can never begin.
        var closed = await kit.PersonAsync();
        await kit.MemberAsync(TenantA, null, closed, BuiltInRoleKey.Learner);
        await kit.MemberAsync(TenantA, SchemaFixture.OrgA1, closed, BuiltInRoleKey.Learner);
        var first = await kit.ClosureAsync(TenantA, null, closed);
        var left = await kit.ClosureAsync(TenantA, SchemaFixture.OrgA1, closed);
        (await owner.BeginAsync(AccountClosureBeginKit.Admission(), first)).Outcome.Should().Be(ClosureBeginOutcome.Begun);
        await CloseAsync(kit, closed, TenantA);
        var refused = AccountClosureBeginKit.Admission();
        (await owner.BeginAsync(refused, left)).Should().Be(new ClosureBeginResult(ClosureBeginOutcome.SubjectNotAdmitted, "scheduled"));
        (await kit.RequestAsync(left)).Status.Should().Be("scheduled");
        (await kit.AuditAsync(refused.Operator.CorrelationId!)).Should().ContainSingle();
        fanOut.Calls.Should().ContainSingle("only the one Begin that committed fanned out");
    }

    /// <summary>A stored state equals the expected closed document, whatever the key order jsonb keeps.</summary>
    private static void SameJson(string actual, string expected) =>
        System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(actual), System.Text.Json.Nodes.JsonNode.Parse(expected))
            .Should().BeTrue($"{actual} is {expected}");

    /// <summary>Revokes one Membership as an administrator does, under the person's and the tenant's exclusive fences.</summary>
    internal static async Task RevokeAsync(AccountClosureBeginKit kit, Guid tenant, Guid? organization, Guid person, Guid membership)
    {
        await using var app = await DataExportTestKit.OpenAppAsync(kit.Database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(app, transaction, tenant, organization);
        await IdentityMembershipFixture.LockAsync(app, transaction, tenant, person, exclusive: true);
        await IdentityMembershipFixture.ExecuteAsync(app, transaction, """
            UPDATE public.memberships SET status='Revoked', deleted_at=clock_timestamp(), deleted_by=@system,
                updated_at=clock_timestamp(), updated_by=@system, row_version=row_version+1
            WHERE id=@member
            """, ("member", membership), ("system", UserId.SystemActor.Value));
        await IdentityMembershipFixture.ExecuteAsync(app, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
        await transaction.CommitAsync();
    }

    /// <summary>Closing to Closed as Finalize's second transaction does it, on the platform role under every fence.</summary>
    private static async Task CloseAsync(AccountClosureBeginKit kit, Guid person, params Guid[] tenants)
    {
        await using var platform = await AccountClosureBeginKit.OpenAsync(kit.Database.PlatformConnectionString);
        await using var transaction = await platform.BeginTransactionAsync();
        await IdentityMembershipFixture.ExecuteAsync(platform, transaction, "SELECT pg_catalog.pg_advisory_xact_lock(@key)",
            ("key", AccountClosureBeginKit.UserKey(person)));
        foreach (var tenant in tenants.OrderBy(AccountClosureBeginKit.TenantKey))
            await IdentityMembershipFixture.ExecuteAsync(platform, transaction, "SELECT pg_catalog.pg_advisory_xact_lock(@key)",
                ("key", AccountClosureBeginKit.TenantKey(tenant)));
        await IdentityMembershipFixture.ExecuteAsync(platform, transaction,
            "UPDATE public.users SET status='Closed', provider_subject=NULL WHERE id=@user", ("user", person));
        await transaction.CommitAsync();
    }
}
