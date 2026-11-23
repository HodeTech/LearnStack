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
/// What a closure could never finish is refused by Begin while the person is still Active (ADR-0107 § 7: Closing cannot
/// stick): a person past the closure's bounds (<see cref="ClosureLimits"/>), counted over every participant group served
/// or not and every Membership of any status, and a person whose identity-provider account the port could never delete.
/// Each refusal changes nothing: no refusal code on the request, no User move, no fan-out, and the scope's entry row is the
/// only record.
/// </summary>
public sealed partial class AccountClosureBeginTests
{
    /// <summary>
    /// One group past the bound is refused, served or not (the extra groups are archived branches, which Finalize still
    /// fences and redacts); exactly the bound begins and hands every served group to the fan-out in one call.
    /// </summary>
    [Fact]
    public async Task A_person_in_more_participant_groups_than_one_closure_is_admitted_for_is_refused_while_still_Active()
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var branches = await BranchesAsync(kit, TenantA, ClosureLimits.MaximumParticipantGroups, archived: true);
        var past = await PersonInGroupsAsync(kit, branches);
        var at = await PersonInGroupsAsync(kit, branches[..^1]);
        var removal = new RecordingAccountRemoval();
        var fanOut = new RecordingFanOut();

        await RefusedTooLargeAsync(kit, past, kit.Owner(fanOut, removal: removal), "one group past the bound");
        fanOut.Calls.Should().BeEmpty("a refused Begin fans nothing out");

        await ActivateAsync(kit, TenantA, branches);
        var begun = await kit.Owner(fanOut, removal: removal).BeginAsync(AccountClosureBeginKit.Admission(), at.Closure);

        begun.Outcome.Should().Be(ClosureBeginOutcome.Begun, "exactly the bound is admitted");
        begun.Groups.Should().Be(new ClosureBeginTally(ClosureLimits.MaximumParticipantGroups, ClosureLimits.MaximumParticipantGroups, 0, 0, 0, 0));
        fanOut.Calls.Should().ContainSingle().Which.Request.Groups.Should().HaveCount(ClosureLimits.MaximumParticipantGroups,
            "the fan-out is handed every served group at the bound it accepts");
        (await kit.UserAsync(at.Person)).Status.Should().Be("Closing");
    }

    /// <summary>
    /// One Membership past the tenant bound, here Revoked tombstones of one group, is refused although the person is in
    /// two groups only: a group's fan-out snapshot and a tenant's redaction pass both stop at that bound. Exactly the bound
    /// begins.
    /// </summary>
    [Fact]
    public async Task A_person_with_more_Memberships_in_one_tenant_than_a_pass_redacts_is_refused_while_still_Active()
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var past = await PersonWithTombstonesAsync(kit, ClosureLimits.MaximumTenantMemberships);
        var at = await PersonWithTombstonesAsync(kit, ClosureLimits.MaximumTenantMemberships - 1);
        var fanOut = new RecordingFanOut();

        await RefusedTooLargeAsync(kit, past, kit.Owner(fanOut), "one Membership past the tenant bound");
        fanOut.Calls.Should().BeEmpty();

        var begun = await kit.Owner(fanOut).BeginAsync(AccountClosureBeginKit.Admission(), at.Closure);

        begun.Outcome.Should().Be(ClosureBeginOutcome.Begun, "exactly the tenant bound is admitted");
        begun.Groups!.Served.Should().Be(2);
        (await kit.UserAsync(at.Person)).Status.Should().Be("Closing");
    }

    /// <summary>
    /// A port that could never delete the account (not configured, or refusing the person's subject before any call) refuses
    /// the Begin before any fence or write, asked with the raw subject the first read returned and nothing else; once the
    /// port accepts, the same closure begins.
    /// </summary>
    [Fact]
    public async Task A_closure_whose_account_the_provider_could_never_delete_is_refused_while_still_Active()
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var person = await kit.PersonAsync();
        await kit.MemberAsync(TenantA, null, person, BuiltInRoleKey.Learner);
        var closure = await kit.ClosureAsync(TenantA, null, person);
        var user = await kit.UserAsync(person);
        var request = await kit.RequestAsync(closure);
        var removal = new RecordingAccountRemoval { Accepts = _ => false };
        var holds = new GatedHolds();
        var fanOut = new RecordingFanOut();
        var admission = AccountClosureBeginKit.Admission();

        var refused = await kit.Owner(fanOut, holds, removal: removal).BeginAsync(admission, closure);

        refused.Should().Be(new ClosureBeginResult(ClosureBeginOutcome.RefusedProvider, "scheduled"));
        removal.Preflights.Should().Equal(["fixture-subject-" + person.ToString("N")], "the port is asked about the raw subject once");
        removal.Subjects.Should().BeEmpty("the preflight deletes nothing");
        holds.Queries.Should().BeEmpty("nothing is judged for a closure that could never finish");
        (await kit.RequestAsync(closure)).Should().Be(request, "no refusal code is recorded: it is not the person's to act on");
        (await kit.UserAsync(person)).Should().Be(user);
        fanOut.Calls.Should().BeEmpty();
        (await kit.AuditAsync(admission.Operator.CorrelationId!)).Should().ContainSingle()
            .Which.Operation.Should().Be(PlatformAdminScope.EnterOperation, "the refusal writes nothing beyond the entry row");

        removal.Accepts = _ => true;
        (await kit.Owner(fanOut, holds, removal: removal).BeginAsync(AccountClosureBeginKit.Admission(), closure)).Outcome
            .Should().Be(ClosureBeginOutcome.Begun, "a Begin after the port is repaired is judged afresh");
    }

    /// <summary>A refusal for the closure's size: nothing on the request, the User or the trail but the entry row.</summary>
    private static async Task RefusedTooLargeAsync(
        AccountClosureBeginKit kit, (Guid Person, Guid Closure) past, LearnStack.Modules.Identity.Infrastructure.DataRequests.AccountClosureOperatorOperations owner,
        string because)
    {
        var user = await kit.UserAsync(past.Person);
        var request = await kit.RequestAsync(past.Closure);
        var admission = AccountClosureBeginKit.Admission();

        var refused = await owner.BeginAsync(admission, past.Closure);

        refused.Should().Be(new ClosureBeginResult(ClosureBeginOutcome.RefusedTooLarge, "scheduled"), because);
        (await kit.RequestAsync(past.Closure)).Should().Be(request, "no refusal code is recorded and the closure stays scheduled");
        (await kit.UserAsync(past.Person)).Should().Be(user, "the person is never moved to a Closing no step could finish");
        (await kit.AuditAsync(admission.Operator.CorrelationId!)).Should().ContainSingle()
            .Which.Operation.Should().Be(PlatformAdminScope.EnterOperation, "the refusal writes nothing beyond the entry row");
        (await kit.PlatformAdvisoryLocksAsync()).Should().BeEmpty("the refusal's transaction ended with its scope");
    }

    /// <summary>Organizations of a tenant, written by the superuser with the guards paused, as a large franchise has them.</summary>
    private static async Task<Guid[]> BranchesAsync(AccountClosureBeginKit kit, Guid tenant, int count, bool archived)
    {
        var branches = Enumerable.Range(0, count).Select(_ => Guid.CreateVersion7()).ToArray();
        await kit.SqlAsync($"""
            SET session_replication_role = replica;
            INSERT INTO public.organizations(id,tenant_id,slug,display_name,status,created_at,created_by,row_version)
            SELECT branch, '{tenant}', 'bound-' || replace(branch::text, '-', ''), 'Bound', '{(archived ? "Archived" : "Active")}',
                clock_timestamp(), '{UserId.SystemActor.Value}', 0
            FROM unnest(ARRAY[{string.Join(',', branches.Select(branch => $"'{branch}'::uuid"))}]) AS branch;
            """);
        return branches;
    }

    private static Task ActivateAsync(AccountClosureBeginKit kit, Guid tenant, Guid[] branches) => kit.SqlAsync($"""
        SET session_replication_role = replica;
        UPDATE public.organizations SET status = 'Active'
        WHERE tenant_id = '{tenant}' AND id = ANY(ARRAY[{string.Join(',', branches.Select(branch => $"'{branch}'::uuid"))}]);
        """);

    /// <summary>
    /// A person with a learner Membership in tenant A's tenant-wide group, a due closure filed there, and one Membership in
    /// each branch, written by the superuser with the guards paused: no product path admits that many groups for a test.
    /// </summary>
    private static async Task<(Guid Person, Guid Closure)> PersonInGroupsAsync(AccountClosureBeginKit kit, Guid[] branches)
    {
        var person = await kit.PersonAsync();
        await kit.MemberAsync(TenantA, null, person, BuiltInRoleKey.Learner);
        var closure = await kit.ClosureAsync(TenantA, null, person);
        await kit.SqlAsync($"""
            SET session_replication_role = replica;
            INSERT INTO public.memberships(id,tenant_id,organization_id,user_id,status,created_at,created_by)
            SELECT uuidv7(), '{TenantA}', branch, '{person}', 'Active', clock_timestamp(), '{UserId.SystemActor.Value}'
            FROM unnest(ARRAY[{string.Join(',', branches.Select(branch => $"'{branch}'::uuid"))}]) AS branch;
            """);
        return (person, closure);
    }

    /// <summary>
    /// A person in two tenant-wide groups, tenant A's (where the due closure is filed) and tenant B's, whose tenant B group
    /// also holds Revoked tombstones of earlier admissions, written by the superuser with the guards paused.
    /// </summary>
    private static async Task<(Guid Person, Guid Closure)> PersonWithTombstonesAsync(AccountClosureBeginKit kit, int tombstones)
    {
        var person = await kit.PersonAsync();
        await kit.MemberAsync(TenantA, null, person, BuiltInRoleKey.Learner);
        await kit.MemberAsync(TenantB, null, person, BuiltInRoleKey.Learner);
        var closure = await kit.ClosureAsync(TenantA, null, person);
        await kit.SqlAsync($"""
            SET session_replication_role = replica;
            INSERT INTO public.memberships(id,tenant_id,organization_id,user_id,status,created_at,created_by,deleted_at,deleted_by)
            SELECT uuidv7(), '{TenantB}', NULL, '{person}', 'Revoked', clock_timestamp(), '{UserId.SystemActor.Value}',
                clock_timestamp(), '{UserId.SystemActor.Value}'
            FROM generate_series(1, {tombstones});
            """);
        return (person, closure);
    }
}
