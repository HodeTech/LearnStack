using System.Text.Json;
using LearnStack.Modules.Identity.Application;
using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Application.Contracts.Roles;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class IdentityAdministrationReadTests(TenancyAdministrationFixture fixture) : IClassFixture<TenancyAdministrationFixture>
{
    [Fact]
    public async Task Branch_directory_accepts_Tenant_or_exact_Organization_capability_without_a_read_hatch()
    {
        var orgAdmin = await fixture.Membership.AddMemberAsync(SchemaFixture.OrgA1, BuiltInRoleKey.OrgAdmin);
        foreach (var actor in new[] { AdminA, orgAdmin.Actor })
        {
            var correlation = "branch-directory-" + actor.ToString("N");
            await using var harness = await fixture.CreateAsync(correlation, actor, SchemaFixture.OrgA1);
            var result = await harness.Sender.Send(new ListOrganizationMembershipsQuery());
            Assert.True(result.IsSuccess);
            Assert.NotEmpty(result.Value.Items);
            Assert.All(result.Value.Items, item => Assert.Equal(OrganizationId.From(SchemaFixture.OrgA1), item.OrganizationId));
            Assert.DoesNotContain(result.Value.Items, item => item.MembershipId == AdminMembershipA || item.MembershipId == BranchMembershipA2);
            Assert.False(((IOperationReadScopeView)harness.Unit).IsTenantWideRead);
            using var row = JsonDocument.Parse(Assert.Single(await fixture.Membership.AuditAsync(correlation,
                IdentityOperationPolicySource.OrganizationMembershipDirectoryOperation, SchemaFixture.OrgA1)));
            Assert.Equal("success", row.RootElement.GetProperty("outcome").GetString());
            Assert.Equal("Must", row.RootElement.GetProperty("class").GetString());
        }
        Assert.Equal("forbidden", (await fixture.SendAsync(new ListOrganizationMembershipsQuery(), "branch-directory-orgless")).Error!.Code);
        Assert.Equal("forbidden", (await fixture.SendAsync(new ListOrganizationMembershipsQuery(), "branch-directory-learner", PersonA,
            SchemaFixture.OrgA1)).Error!.Code);
        Assert.Equal("not_found", (await fixture.SendAsync(new ListOrganizationMembershipsQuery(), "branch-directory-other-org",
            orgAdmin.Actor, SchemaFixture.OrgA2)).Error!.Code);
    }

    [Fact]
    public async Task Branch_directory_continuation_is_actor_scope_and_operation_bound()
    {
        await fixture.Membership.AddMemberAsync(SchemaFixture.OrgA1);
        var first = await fixture.SendAsync(new ListOrganizationMembershipsQuery(Limit: 1), "branch-cursor-first", organization: SchemaFixture.OrgA1);
        Assert.True(first.IsSuccess);
        Assert.True(first.Value.PageInfo.HasNext);
        var next = await fixture.SendAsync(new ListOrganizationMembershipsQuery(first.Value.PageInfo.NextCursor, 1),
            "branch-cursor-next", organization: SchemaFixture.OrgA1);
        Assert.True(next.IsSuccess);
        Assert.NotEqual(first.Value.Items[0].MembershipId, next.Value.Items[0].MembershipId);
        Assert.Equal("validation_failed", (await fixture.SendAsync(new ListOrganizationMembershipsQuery(first.Value.PageInfo.NextCursor, 1),
            "branch-cursor-other-org", organization: SchemaFixture.OrgA2)).Error!.Code);
        Assert.Equal("validation_failed", (await fixture.SendAsync(new ListMembershipsQuery(first.Value.PageInfo.NextCursor, 1),
            "branch-cursor-not-tenant-directory", organization: SchemaFixture.OrgA1)).Error!.Code);
    }

    [Fact]
    public async Task Role_reads_are_finite_and_current_and_branch_reading_does_not_require_mutation_eligibility()
    {
        var roles = await fixture.SendAsync(new ListRolesQuery(), "role-read-list", organization: SchemaFixture.OrgA1);
        Assert.True(roles.IsSuccess);
        Assert.Equal(8, roles.Value.Items.Length);
        Assert.Equal(8, roles.Value.Items.Select(item => (item.BuiltInKey, item.CapabilityScope)).Distinct().Count());
        var id = RoleId(SchemaFixture.TenantA, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant);
        var detail = await fixture.SendAsync(new GetRoleDetailQuery(id), "role-read-detail", organization: SchemaFixture.OrgA1);
        Assert.True(detail.IsSuccess);
        Assert.Equal(id, detail.Value.RoleId);
        Assert.Equal(IdentityRoleRelease.Version2.Version, detail.Value.AdoptedReleaseVersion);
        Assert.Equal(IdentityRoleRelease.Version2.Fingerprint, detail.Value.AdoptedReleaseFingerprint);
        Assert.Equal("forbidden", (await fixture.SendAsync(new ChangeRoleGrantsCommand(id, detail.Value.Version, detail.Value.Grants),
            "role-read-not-edit", organization: SchemaFixture.OrgA1)).Error!.Code);
        Assert.Equal("not_found", (await fixture.SendAsync(new GetRoleDetailQuery(
            RoleId(SchemaFixture.TenantB, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant)), "role-read-foreign")).Error!.Code);
        Assert.Equal("not_found", (await fixture.SendAsync(new GetRoleDetailQuery(Guid.CreateVersion7()), "role-read-missing")).Error!.Code);
        Assert.Equal("forbidden", (await fixture.SendAsync(new ListRolesQuery(), "role-read-learner", PersonA)).Error!.Code);
        using var audit = JsonDocument.Parse(Assert.Single(await fixture.Membership.AuditAsync("role-read-detail",
            IdentityOperationPolicySource.RoleDetailOperation, SchemaFixture.OrgA1)));
        Assert.Equal(id.ToString("D"), audit.RootElement.GetProperty("id").GetString());
        Assert.Equal("Must", audit.RootElement.GetProperty("class").GetString());
    }

    [Fact]
    public async Task Registry_has_exactly_eight_pairs_and_bounded_release_with_current_addition_hints()
    {
        var registry = await fixture.SendAsync(new GetPermissionRegistryQuery(), "registry-orgless");
        Assert.True(registry.IsSuccess);
        Assert.Equal(8, registry.Value.Items.Length);
        Assert.Equal(IdentityRoleRelease.Version2.Definitions.Select(pair => (pair.Scope, pair.Key)),
            registry.Value.Items.Select(pair => (pair.Scope, pair.Key)));
        Assert.All(registry.Value.Items, item => { Assert.True(item.CanGrant); Assert.StartsWith("lockey_permission_", item.LabelKey); });
        Assert.Equal(IdentityRoleRelease.Version2.Fingerprint, registry.Value.Release.Fingerprint);
        Assert.Equal(3, registry.Value.Release.TenantAdminDelta.Length);
        var branch = await fixture.SendAsync(new GetPermissionRegistryQuery(), "registry-branch", organization: SchemaFixture.OrgA1);
        Assert.True(branch.IsSuccess);
        Assert.All(branch.Value.Items, item => Assert.False(item.CanGrant));
        Assert.Equal("forbidden", (await fixture.SendAsync(new GetPermissionRegistryQuery(), "registry-learner", PersonA)).Error!.Code);
    }

    [Fact]
    public async Task Nine_own_flags_are_advisory_and_false_previews_emit_no_denial()
    {
        Assert.Equal(9, typeof(OwnCapabilitiesDto).GetProperties().Length);
        var admin = await fixture.SendAsync(new GetOwnCapabilitiesQuery(), "capabilities-admin");
        Assert.True(admin.IsSuccess);
        Assert.True(admin.Value.CanReadTenantMembers);
        Assert.False(admin.Value.CanReadOrganizationMembers);
        Assert.True(admin.Value.CanReadRoles);
        Assert.True(admin.Value.CanChangeRoleGrants);
        Assert.True(admin.Value.CanAdoptRoleRelease);
        Assert.True(admin.Value.CanManageOrganizations);
        var branch = await fixture.SendAsync(new GetOwnCapabilitiesQuery(), "capabilities-admin-branch", organization: SchemaFixture.OrgA1);
        Assert.True(branch.IsSuccess);
        Assert.True(branch.Value.CanReadOrganizationMembers);
        Assert.True(branch.Value.CanReadRoles);
        Assert.False(branch.Value.CanChangeRoleGrants);
        Assert.False(branch.Value.CanAdoptRoleRelease);
        Assert.False(branch.Value.CanManageOrganizations);
        Assert.False(branch.Value.CanWriteOwnProfile); // A NULL-org Membership is not the current branch's profile.
        var learner = await fixture.SendAsync(new GetOwnCapabilitiesQuery(), "capabilities-learner", PersonA, SchemaFixture.OrgA1);
        Assert.True(learner.IsSuccess);
        Assert.Equal(new OwnCapabilitiesDto(false, false, false, false, false, false, false, true, false), learner.Value);
        Assert.Empty(await fixture.Membership.AuditAsync("capabilities-learner", "identity.authorization.deny", SchemaFixture.OrgA1));
        Assert.Empty(await fixture.Membership.AuditAsync("capabilities-learner", "identity.membership_profile.read", SchemaFixture.OrgA1));
    }

    [Fact]
    public async Task Removed_profile_grant_changes_preview_without_removing_live_own_coverage()
    {
        try
        {
            await fixture.Membership.SetRoleGrantsAsync(BuiltInRoleKey.Learner, PermissionScope.Organization);
            var result = await fixture.SendAsync(new GetOwnCapabilitiesQuery(), "capabilities-removed-profile", PersonA, SchemaFixture.OrgA1);
            Assert.True(result.IsSuccess);
            // PersonA also has a tenant-wide Learner grant; remove it independently to prove no stale capability cache.
            await fixture.Membership.SetRoleGrantsAsync(BuiltInRoleKey.Learner, PermissionScope.Tenant);
            result = await fixture.SendAsync(new GetOwnCapabilitiesQuery(), "capabilities-all-profile-removed", PersonA, SchemaFixture.OrgA1);
            Assert.True(result.IsSuccess);
            Assert.False(result.Value.CanWriteOwnProfile);
            Assert.Empty(await fixture.Membership.AuditAsync("capabilities-all-profile-removed", "identity.authorization.deny", SchemaFixture.OrgA1));
        }
        finally
        {
            await fixture.Membership.SetRoleGrantsAsync(BuiltInRoleKey.Learner, PermissionScope.Organization,
                new PermissionDefinition(PermissionScope.Organization, IdentityFoundationManifest.ProfileWrite));
            await fixture.Membership.SetRoleGrantsAsync(BuiltInRoleKey.Learner, PermissionScope.Tenant,
                new PermissionDefinition(PermissionScope.Tenant, IdentityFoundationManifest.ProfileWrite));
        }
    }

    [Fact]
    public async Task Revocation_after_branch_target_read_is_seen_before_directory_disclosure()
    {
        var caller = await fixture.Membership.AddMemberAsync(SchemaFixture.OrgA1, BuiltInRoleKey.OrgAdmin);
        await using var harness = await fixture.CreateAsync("branch-directory-final-recheck", caller.Actor, SchemaFixture.OrgA1,
            services => services.AddScoped<IIdentityAdministrationReadStore>(provider => new AfterBranchRead(
                new IdentityAdministrationReadStore(provider.GetRequiredService<IdentityDbContext>(), provider.GetRequiredService<IUnitOfWork>(),
                    provider.GetRequiredService<ITenantContext>()), async () =>
                {
                    Assert.True((await fixture.SendAsync(new ChangeMembershipStatusCommand(caller.Membership, 0, ManagedMembershipStatus.Revoked),
                        "branch-directory-revoke", organization: SchemaFixture.OrgA1)).IsSuccess);
                })));
        Assert.Equal("not_found", (await harness.Sender.Send(new ListOrganizationMembershipsQuery())).Error!.Code);
        ModuleReceiptDisclosureTests.AssertNoSuccess(await fixture.Membership.AuditAsync("branch-directory-final-recheck",
            IdentityOperationPolicySource.OrganizationMembershipDirectoryOperation, SchemaFixture.OrgA1));
    }

    [Theory]
    [InlineData("branch", false)]
    [InlineData("branch", true)]
    [InlineData("roles", false)]
    [InlineData("roles", true)]
    [InlineData("detail", false)]
    [InlineData("detail", true)]
    [InlineData("registry", false)]
    [InlineData("registry", true)]
    public async Task Every_new_sensitive_read_withholds_success_on_MUST_INSERT_or_COMMIT_failure(string kind, bool deferred)
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.OwnerConnectionString);
        await ExecuteAsync(owner, null, """
            CREATE FUNCTION administration_read_audit_proof() RETURNS trigger LANGUAGE plpgsql SECURITY INVOKER SET search_path=pg_catalog AS $$
            BEGIN
                IF NEW.correlation_id LIKE 'administration-read-refusal-%' AND NEW.outcome='success' THEN
                    RAISE EXCEPTION 'fixed administration read audit refusal' USING ERRCODE='23514';
                END IF;
                RETURN NEW;
            END $$;
            """);
        await ExecuteAsync(owner, null, deferred ? """
            CREATE CONSTRAINT TRIGGER tg_administration_read_audit_proof AFTER INSERT ON public.audit_log
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION administration_read_audit_proof();
            """ : """
            CREATE TRIGGER tg_administration_read_audit_proof BEFORE INSERT ON public.audit_log
                FOR EACH ROW EXECUTE FUNCTION administration_read_audit_proof();
            """);
        try
        {
            await using var harness = await fixture.CreateAsync("administration-read-refusal-" + kind + deferred, organization: SchemaFixture.OrgA1);
            await Assert.ThrowsAsync<AuditWriteFailedException>(async () =>
            {
                switch (kind)
                {
                    case "branch": await harness.Sender.Send(new ListOrganizationMembershipsQuery()); break;
                    case "roles": await harness.Sender.Send(new ListRolesQuery()); break;
                    case "detail": await harness.Sender.Send(new GetRoleDetailQuery(RoleId(SchemaFixture.TenantA, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant))); break;
                    case "registry": await harness.Sender.Send(new GetPermissionRegistryQuery()); break;
                    default: throw new ArgumentOutOfRangeException(nameof(kind));
                }
            });
            Assert.False(harness.Unit.HasActiveTransaction);
        }
        finally
        {
            await ExecuteAsync(owner, null, "DROP TRIGGER tg_administration_read_audit_proof ON public.audit_log; DROP FUNCTION administration_read_audit_proof()");
        }
    }

    private sealed class AfterBranchRead(IIdentityAdministrationReadStore inner, Func<Task> continuation) : IIdentityAdministrationReadStore
    {
        public async Task<IReadOnlyList<MembershipDirectoryItem>> ReadOrganizationMembersAsync(MembershipId? after, int take, CancellationToken cancellationToken = default)
        {
            var result = await inner.ReadOrganizationMembersAsync(after, take, cancellationToken);
            await continuation();
            return result;
        }
        public Task<IReadOnlyList<Role>> ReadRolesAsync(CancellationToken cancellationToken = default) => inner.ReadRolesAsync(cancellationToken);
        public Task<Role?> ReadRoleAsync(LearnStack.Modules.Identity.Domain.RoleId roleId, CancellationToken cancellationToken = default) => inner.ReadRoleAsync(roleId, cancellationToken);
    }
}
