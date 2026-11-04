using System.Text.Json;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class MembershipAdministrationAssignmentTests(MembershipAdministrationFixture fixture)
    : IClassFixture<MembershipAdministrationFixture>
{
    [Fact]
    public async Task Assignment_replacement_preserves_retained_rows_deletes_removed_children_and_noop_keeps_the_version()
    {
        var member = await fixture.AddMemberAsync();
        var learner = RoleId(SchemaFixture.TenantA, BuiltInRoleKey.Learner, PermissionScope.Tenant);
        var instructor = RoleId(SchemaFixture.TenantA, BuiltInRoleKey.Instructor, PermissionScope.Tenant);
        var initial = await fixture.ReadAsync(member.Membership);
        var addition = await fixture.SendAsync(new ReplaceMembershipRoleAssignmentsCommand(member.Membership, 0, [learner, instructor]),
            "membership-assignment-add");
        Assert.True(addition.IsSuccess);
        Assert.Equal(1, addition.Value.Version);
        var both = await fixture.ReadAsync(member.Membership);
        Assert.Equal(initial.Assignments[learner], both.Assignments[learner]);
        Assert.Equal(2, both.Assignments.Count);
        var removed = await fixture.SendAsync(new ReplaceMembershipRoleAssignmentsCommand(member.Membership, 1, [instructor]),
            "membership-assignment-remove");
        Assert.True(removed.IsSuccess);
        var after = await fixture.ReadAsync(member.Membership);
        Assert.Equal(2, after.Version);
        Assert.Equal(instructor, Assert.Single(after.Assignments).Key);
        Assert.Equal(both.Assignments[instructor], after.Assignments[instructor]);
        Assert.True((await fixture.SendAsync(new ReplaceMembershipRoleAssignmentsCommand(member.Membership, 2, [instructor]),
            "membership-assignment-noop")).IsSuccess);
        var unchanged = await fixture.ReadAsync(member.Membership);
        Assert.Equal(2, unchanged.Version);
        Assert.Equal(after.RootStamp, unchanged.RootStamp);
        Assert.Equal(after.Assignments[instructor], unchanged.Assignments[instructor]);
        var row = Assert.Single(await fixture.AuditAsync("membership-assignment-remove", "identity.membership.replace_role_assignments"));
        using var audit = JsonDocument.Parse(row);
        Assert.Equal(member.Membership.ToString("D"), audit.RootElement.GetProperty("id").GetString());
        Assert.Contains(learner.ToString("D"), row, StringComparison.Ordinal);
        Assert.Equal("SecurityEvent", audit.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Whole_current_Role_graph_limits_additions_but_never_blocks_retaining_or_removing_existing_assignments()
    {
        var member = await fixture.AddMemberAsync();
        var other = await fixture.AddMemberAsync();
        var instructor = RoleId(SchemaFixture.TenantA, BuiltInRoleKey.Instructor, PermissionScope.Tenant);
        Assert.True((await fixture.SendAsync(new ReplaceMembershipRoleAssignmentsCommand(member.Membership, 0, [instructor]),
            "membership-role-before-authority-change")).IsSuccess);
        try
        {
            await fixture.SetRoleGrantsAsync(BuiltInRoleKey.Instructor, PermissionScope.Tenant,
                new PermissionDefinition(PermissionScope.Tenant, "tenancy.locale.write"));
            Assert.Equal("forbidden", (await fixture.SendAsync(new ReplaceMembershipRoleAssignmentsCommand(other.Membership, 0, [instructor]),
                "membership-role-current-add-refusal")).Error!.Code);
            Assert.True((await fixture.SendAsync(new ReplaceMembershipRoleAssignmentsCommand(member.Membership, 1, [instructor]),
                "membership-role-current-retain")).IsSuccess);
            Assert.Equal(1, (await fixture.ReadAsync(member.Membership)).Version);
            Assert.True((await fixture.SendAsync(new ReplaceMembershipRoleAssignmentsCommand(member.Membership, 1, []),
                "membership-role-current-remove")).IsSuccess);
            Assert.Empty((await fixture.ReadAsync(member.Membership)).Assignments);
        }
        finally { await fixture.SetRoleGrantsAsync(BuiltInRoleKey.Instructor, PermissionScope.Tenant); }
    }

    [Fact]
    public async Task Assignment_inputs_cannot_cross_tenant_or_capability_scope_or_duplicate_references()
    {
        var member = await fixture.AddMemberAsync();
        var learner = RoleId(SchemaFixture.TenantA, BuiltInRoleKey.Learner, PermissionScope.Tenant);
        var branch = RoleId(SchemaFixture.TenantA, BuiltInRoleKey.Learner, PermissionScope.Organization);
        var foreign = RoleId(SchemaFixture.TenantB, BuiltInRoleKey.Learner, PermissionScope.Tenant);
        Assert.True((await fixture.SendAsync(new ReplaceMembershipRoleAssignmentsCommand(member.Membership, 0, [branch]),
            "membership-assignment-wrong-capability")).IsFailure);
        Assert.True((await fixture.SendAsync(new ReplaceMembershipRoleAssignmentsCommand(member.Membership, 0, [foreign]),
            "membership-assignment-foreign-role")).IsFailure);
        Assert.Equal("validation_failed", (await fixture.SendAsync(new ReplaceMembershipRoleAssignmentsCommand(member.Membership, 0, [learner, learner]),
            "membership-assignment-duplicate")).Error!.Code);
        Assert.Equal(0, (await fixture.ReadAsync(member.Membership)).Version);
    }

    [Fact]
    public async Task Status_transition_version_noop_and_terminal_revocation_are_actual_persisted_behaviors()
    {
        var member = await fixture.AddMemberAsync();
        Assert.Equal("concurrency_conflict", (await fixture.SendAsync(new ChangeMembershipStatusCommand(member.Membership, 1, ManagedMembershipStatus.Suspended),
            "membership-status-stale")).Error!.Code);
        var unchanged = await fixture.ReadAsync(member.Membership);
        var active = await fixture.SendAsync(new ChangeMembershipStatusCommand(member.Membership, 0, ManagedMembershipStatus.Active), "membership-status-noop");
        Assert.True(active.IsSuccess);
        Assert.Equal(unchanged.RootStamp, (await fixture.ReadAsync(member.Membership)).RootStamp);
        Assert.True((await fixture.SendAsync(new ChangeMembershipStatusCommand(member.Membership, 0, ManagedMembershipStatus.Suspended),
            "membership-status-suspend")).IsSuccess);
        var detail = await fixture.SendAsync(new GetMembershipDetailQuery(member.Membership), "membership-status-suspended-detail");
        Assert.True(detail.IsSuccess);
        Assert.Equal(ManagedMembershipStatus.Suspended, detail.Value.Status);
        Assert.True((await fixture.SendAsync(new ChangeMembershipStatusCommand(member.Membership, 1, ManagedMembershipStatus.Active),
            "membership-status-reactivate")).IsSuccess);
        Assert.True((await fixture.SendAsync(new ChangeMembershipStatusCommand(member.Membership, 2, ManagedMembershipStatus.Revoked),
            "membership-status-revoke")).IsSuccess);
        Assert.Equal("not_found", (await fixture.SendAsync(new GetMembershipDetailQuery(member.Membership), "membership-status-revoked-detail")).Error!.Code);
        Assert.True((await fixture.SendAsync(new ChangeMembershipStatusCommand(member.Membership, 3, ManagedMembershipStatus.Active),
            "membership-status-terminal")).IsFailure);
        var state = await fixture.ReadAsync(member.Membership);
        Assert.Equal("Revoked", state.Status);
        Assert.Equal(3, state.Version);
    }

    [Theory]
    [InlineData("suspend")]
    [InlineData("revoke")]
    [InlineData("assignments")]
    public async Task Every_last_administrator_removal_refuses_without_any_partial_committed_state(string operation)
    {
        var before = await fixture.ReadAsync(AdminMembershipA);
        var result = operation == "assignments"
            ? await fixture.SendAsync(new ReplaceMembershipRoleAssignmentsCommand(AdminMembershipA, before.Version, []), "membership-final-admin-assignments")
            : await fixture.SendAsync(new ChangeMembershipStatusCommand(AdminMembershipA, before.Version,
                operation == "suspend" ? ManagedMembershipStatus.Suspended : ManagedMembershipStatus.Revoked), "membership-final-admin-" + operation);
        Assert.True(result.IsFailure);
        var after = await fixture.ReadAsync(AdminMembershipA);
        Assert.Equal("Active", after.Status);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.Assignments.Keys, after.Assignments.Keys);
    }

    [Fact]
    public async Task Another_administrator_allows_retirement_without_weakening_the_total_admin_invariant()
    {
        var other = await fixture.AddMemberAsync(role: BuiltInRoleKey.TenantAdmin);
        var result = await fixture.SendAsync(new ChangeMembershipStatusCommand(other.Membership, 0, ManagedMembershipStatus.Revoked),
            "membership-second-admin-retire");
        Assert.True(result.IsSuccess);
        Assert.Equal("Revoked", (await fixture.ReadAsync(other.Membership)).Status);
        Assert.Equal("Active", (await fixture.ReadAsync(AdminMembershipA)).Status);
    }

    [Fact]
    public async Task Suspended_prepared_assignments_do_not_create_own_live_authority()
    {
        var member = await fixture.AddMemberAsync(SchemaFixture.OrgA1);
        var role = RoleId(SchemaFixture.TenantA, BuiltInRoleKey.OrgAdmin, PermissionScope.Organization);
        Assert.True((await fixture.SendAsync(new ChangeMembershipStatusCommand(member.Membership, 0, ManagedMembershipStatus.Suspended),
            "membership-prepare-suspend", organization: SchemaFixture.OrgA1)).IsSuccess);
        Assert.True((await fixture.SendAsync(new ReplaceMembershipRoleAssignmentsCommand(member.Membership, 1, [role]),
            "membership-prepare-assignment", organization: SchemaFixture.OrgA1)).IsSuccess);
        Assert.Equal("not_found", (await fixture.SendAsync(new GetOwnMembershipProfileQuery(), "membership-prepare-no-authority",
            member.Actor, SchemaFixture.OrgA1)).Error!.Code);
        Assert.Equal("Suspended", (await fixture.ReadAsync(member.Membership, SchemaFixture.OrgA1)).Status);
    }

    [Fact]
    public async Task Failed_MUST_assignment_audit_rolls_back_child_DELETE_INSERT_and_root_version_together()
    {
        var member = await fixture.AddMemberAsync();
        var before = await fixture.ReadAsync(member.Membership);
        var instructor = RoleId(SchemaFixture.TenantA, BuiltInRoleKey.Instructor, PermissionScope.Tenant);
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.OwnerConnectionString);
        await ExecuteAsync(owner, null, """
            CREATE FUNCTION membership_assignment_audit_proof() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.correlation_id='membership-assignment-audit-refusal' AND NEW.operation='identity.membership.replace_role_assignments' THEN
                    RAISE EXCEPTION 'fixed assignment audit refusal' USING ERRCODE='23514';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER tg_membership_assignment_audit_proof BEFORE INSERT ON audit_log
            FOR EACH ROW EXECUTE FUNCTION membership_assignment_audit_proof();
            """);
        try
        {
            await using var harness = await fixture.CreateAsync("membership-assignment-audit-refusal");
            Result<MembershipMutationResult>? published = null;
            await Assert.ThrowsAsync<AuditWriteFailedException>(async () => published = await harness.Sender.Send(
                new ReplaceMembershipRoleAssignmentsCommand(member.Membership, 0, [instructor])));
            Assert.Null(published);
            var after = await fixture.ReadAsync(member.Membership);
            Assert.Equal(before.Version, after.Version);
            Assert.Equal(before.RootStamp, after.RootStamp);
            Assert.Equal(before.Assignments.Keys, after.Assignments.Keys);
        }
        finally
        {
            await ExecuteAsync(owner, null, "DROP TRIGGER tg_membership_assignment_audit_proof ON audit_log; DROP FUNCTION membership_assignment_audit_proof()");
        }
    }
}
