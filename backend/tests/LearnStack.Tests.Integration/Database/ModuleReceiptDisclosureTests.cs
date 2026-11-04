using System.Text.Json;
using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Application.Contracts.Roles;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class ModuleReceiptDisclosureTests(TenancyAdministrationFixture fixture) : IClassFixture<TenancyAdministrationFixture>
{
    [Fact]
    public async Task Own_receipt_keeps_the_original_terminal_membership_and_requires_current_coverage()
    {
        var original = await fixture.Membership.AddMemberAsync(SchemaFixture.OrgA1);
        Assert.True((await fixture.SendAsync(new SetOwnMembershipProfileCommand(0, "original-receipt-private-name", "tr"),
            "receipt-own-original-write", original.Actor, SchemaFixture.OrgA1)).IsSuccess);
        Assert.True((await fixture.SendAsync(new ReadOwnMembershipProfileReceiptQuery(original.Membership),
            "receipt-own-original-live", original.Actor, SchemaFixture.OrgA1)).IsSuccess);
        Assert.True((await fixture.SendAsync(new ChangeMembershipStatusCommand(original.Membership, 1, ManagedMembershipStatus.Revoked),
            "receipt-own-revoke", organization: SchemaFixture.OrgA1)).IsSuccess);
        var terminal = await fixture.Membership.ReadAsync(original.Membership, SchemaFixture.OrgA1);
        Assert.Equal("not_found", (await fixture.SendAsync(new ReadOwnMembershipProfileReceiptQuery(original.Membership),
            "receipt-own-no-current-coverage", original.Actor, SchemaFixture.OrgA1)).Error!.Code);

        var successor = await fixture.Membership.AddMembershipAsync(original.Actor, SchemaFixture.OrgA1);
        Assert.True((await fixture.SendAsync(new ReadOwnMembershipProfileReceiptQuery(original.Membership),
            "receipt-own-successor-context", original.Actor, SchemaFixture.OrgA1)).IsSuccess);
        Assert.True((await fixture.SendAsync(new ReadMembershipReceiptQuery(original.Membership),
            "receipt-admin-terminal", organization: SchemaFixture.OrgA1)).IsSuccess);
        Assert.True((await fixture.SendAsync(new ReadMemberProfileReceiptQuery(original.Membership),
            "receipt-admin-profile-terminal", organization: SchemaFixture.OrgA1)).IsSuccess);
        Assert.Equal("not_found", (await fixture.SendAsync(new GetMembershipDetailQuery(original.Membership),
            "receipt-ordinary-detail-still-current", organization: SchemaFixture.OrgA1)).Error!.Code);
        var currentProfile = await fixture.SendAsync(new GetOwnMembershipProfileQuery(),
            "receipt-own-current-profile", original.Actor, SchemaFixture.OrgA1);
        Assert.True(currentProfile.IsSuccess);
        Assert.Equal("profile-canary", currentProfile.Value.DisplayName);
        Assert.Equal(0, currentProfile.Value.Version);
        Assert.Equal(0, (await fixture.Membership.ReadAsync(successor, SchemaFixture.OrgA1)).Version);
        Assert.Equal(terminal.RootStamp, (await fixture.Membership.ReadAsync(original.Membership, SchemaFixture.OrgA1)).RootStamp);
        await AssertReceiptAuditAsync("receipt-own-successor-context", "identity.membership_profile.read_receipt",
            "Membership", original.Membership, SchemaFixture.OrgA1);
        await AssertReceiptAuditAsync("receipt-admin-terminal", "identity.membership.read_receipt",
            "Membership", original.Membership, SchemaFixture.OrgA1);
        await AssertReceiptAuditAsync("receipt-admin-profile-terminal", "identity.membership_profile.read_receipt",
            "Membership", original.Membership, SchemaFixture.OrgA1);
        Assert.Empty(await fixture.Membership.AuditAsync("receipt-own-successor-context", "identity.membership_profile.write", SchemaFixture.OrgA1));
    }

    [Fact]
    public async Task Admin_profile_receipt_uses_current_membership_disclosure_without_profile_write()
    {
        var administrator = await fixture.Membership.AddMemberAsync(SchemaFixture.OrgA1, BuiltInRoleKey.OrgInstructor);
        var target = await fixture.Membership.AddMemberAsync(SchemaFixture.OrgA1);
        try
        {
            await fixture.Membership.SetRoleGrantsAsync(BuiltInRoleKey.OrgInstructor, PermissionScope.Organization,
                new PermissionDefinition(PermissionScope.Organization, IdentityFoundationManifest.MembershipAdmin));
            Assert.Equal("forbidden", (await fixture.SendAsync(new SetMembershipProfileCommand(target.Membership, 0, "must-not-write", null),
                "receipt-profile-original-write-refused", administrator.Actor, SchemaFixture.OrgA1)).Error!.Code);
            Assert.True((await fixture.SendAsync(new ReadMemberProfileReceiptQuery(target.Membership),
                "receipt-profile-read-permission", administrator.Actor, SchemaFixture.OrgA1)).IsSuccess);
            Assert.True((await fixture.SendAsync(new ReadMembershipReceiptQuery(target.Membership),
                "receipt-membership-read-permission", administrator.Actor, SchemaFixture.OrgA1)).IsSuccess);
            Assert.Equal(0, (await fixture.Membership.ReadAsync(target.Membership, SchemaFixture.OrgA1)).Version);
            await AssertReceiptAuditAsync("receipt-profile-read-permission", "identity.membership_profile.read_receipt",
                "Membership", target.Membership, SchemaFixture.OrgA1);
            await fixture.Membership.SetRoleGrantsAsync(BuiltInRoleKey.OrgInstructor, PermissionScope.Organization);
            Assert.Equal("forbidden", (await fixture.SendAsync(new ReadMemberProfileReceiptQuery(target.Membership),
                "receipt-profile-current-capability-removed", administrator.Actor, SchemaFixture.OrgA1)).Error!.Code);
        }
        finally { await fixture.Membership.SetRoleGrantsAsync(BuiltInRoleKey.OrgInstructor, PermissionScope.Organization); }
    }

    [Fact]
    public async Task Own_receipt_requires_current_coverage_but_not_the_old_profile_write_permission()
    {
        var person = await fixture.Membership.AddMemberAsync(SchemaFixture.OrgA1);
        try
        {
            await fixture.Membership.SetRoleGrantsAsync(BuiltInRoleKey.Learner, PermissionScope.Organization);
            Assert.Equal("forbidden", (await fixture.SendAsync(new SetOwnMembershipProfileCommand(0, "must-not-write", null),
                "receipt-own-original-permission-removed", person.Actor, SchemaFixture.OrgA1)).Error!.Code);
            Assert.True((await fixture.SendAsync(new ReadOwnMembershipProfileReceiptQuery(person.Membership),
                "receipt-own-current-coverage-only", person.Actor, SchemaFixture.OrgA1)).IsSuccess);
            await AssertReceiptAuditAsync("receipt-own-current-coverage-only", "identity.membership_profile.read_receipt",
                "Membership", person.Membership, SchemaFixture.OrgA1);
            Assert.Equal(0, (await fixture.Membership.ReadAsync(person.Membership, SchemaFixture.OrgA1)).Version);
        }
        finally
        {
            await fixture.Membership.SetRoleGrantsAsync(BuiltInRoleKey.Learner, PermissionScope.Organization,
                new PermissionDefinition(PermissionScope.Organization, IdentityFoundationManifest.ProfileWrite));
        }
    }

    [Fact]
    public async Task Organization_receipts_observe_removed_current_capability_while_the_caller_still_has_coverage()
    {
        var current = IdentityFoundationManifest.Version1.FindTemplate(BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant)!.Grants
            .Union(IdentityRoleRelease.Version2.TenantAdminDelta).ToArray();
        try
        {
            await fixture.Membership.SetRoleGrantsAsync(BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant,
                current.Where(grant => grant.Key != "tenancy.organization.admin").ToArray());
            Assert.Equal("forbidden", (await fixture.SendAsync(new ReadOrganizationReceiptQuery(OrganizationId.From(SchemaFixture.OrgA1)),
                "receipt-org-current-capability-removed", organization: SchemaFixture.OrgA1)).Error!.Code);
            Assert.Equal("forbidden", (await fixture.SendAsync(new ReadDefaultOrganizationReceiptQuery(TenantId.From(SchemaFixture.TenantA)),
                "receipt-default-current-capability-removed", organization: SchemaFixture.OrgA1)).Error!.Code);
            Assert.True((await fixture.SendAsync(new ReadRoleReceiptQuery(RoleId(SchemaFixture.TenantA, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant)),
                "receipt-capability-coverage-control", organization: SchemaFixture.OrgA1)).IsSuccess);
        }
        finally { await fixture.Membership.SetRoleGrantsAsync(BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant, current); }
    }

    [Fact]
    public async Task Role_receipt_in_a_live_branch_ignores_original_adoption_eligibility_and_allows_a_retired_subject()
    {
        var adminRole = RoleId(SchemaFixture.TenantA, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant);
        var release = IdentityRoleRelease.Version2;
        long roleVersion;
        await using (var app = (Npgsql.NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.AppConnectionString))
        {
            await SchemaQueries.AssertApplicationRoleAsync(app);
            await using var transaction = await app.BeginTransactionAsync();
            await ScopeAsync(app, transaction, SchemaFixture.TenantA);
            roleVersion = await ScalarAsync<long>(app, transaction, "SELECT row_version FROM roles WHERE id=@role", ("role", adminRole));
        }
        Assert.True((await fixture.SendAsync(new ReadRoleReceiptQuery(adminRole), "receipt-role-branch", organization: SchemaFixture.OrgA1)).IsSuccess);
        Assert.Equal("forbidden", (await fixture.SendAsync(new AdoptRoleGrantManifestCommand(adminRole, roleVersion, release.Version, release.Fingerprint),
            "receipt-role-original-branch-write", organization: SchemaFixture.OrgA1)).Error!.Code);
        Assert.Equal("forbidden", (await fixture.SendAsync(new AdoptRoleGrantManifestCommand(adminRole, roleVersion, release.Version, release.Fingerprint),
            "receipt-role-original-adoption-repeat")).Error!.Code);
        var retired = RoleId(SchemaFixture.TenantA, BuiltInRoleKey.Editor, PermissionScope.Tenant);
        await fixture.Membership.MutateAsync(SchemaFixture.TenantA, null, null, true, async (app, transaction) =>
        {
            await ExecuteAsync(app, transaction, "SELECT id FROM roles WHERE id=@role FOR UPDATE", ("role", retired));
            await ExecuteAsync(app, transaction, "UPDATE roles SET deleted_at=clock_timestamp(),deleted_by=@actor,row_version=row_version+1 WHERE id=@role",
                ("actor", AdminA), ("role", retired));
        });
        Assert.True((await fixture.SendAsync(new ReadRoleReceiptQuery(retired), "receipt-role-retired", organization: SchemaFixture.OrgA1)).IsSuccess);
        await AssertReceiptAuditAsync("receipt-role-retired", "identity.role.read_receipt", "Role", retired, SchemaFixture.OrgA1);
        Assert.Empty(await fixture.Membership.AuditAsync("receipt-role-retired", "identity.role.adopt_manifest", SchemaFixture.OrgA1));
        Assert.Empty(await fixture.Membership.AuditAsync("receipt-role-retired", "identity.role.change_grants", SchemaFixture.OrgA1));
    }

    [Fact]
    public async Task Organization_and_default_receipts_retain_the_original_root_after_default_moves_and_organization_archives()
    {
        var created = await fixture.SendAsync(new CreateManagedOrganizationCommand("receipt-" + Guid.NewGuid().ToString("N"), "receipt-original-private-branch"),
            "receipt-org-create");
        Assert.True(created.IsSuccess);
        var organization = created.Value.OrganizationId;
        var selected = await fixture.SendAsync(new SelectDefaultOrganizationCommand(organization, await fixture.TenantVersionAsync()), "receipt-default-original");
        Assert.True(selected.IsSuccess);
        Assert.True((await fixture.SendAsync(new SelectDefaultOrganizationCommand(OrganizationId.From(SchemaFixture.OrgA1), selected.Value),
            "receipt-default-moved")).IsSuccess);
        Assert.True((await fixture.SendAsync(new ArchiveOrganizationCommand(organization, created.Value.Version), "receipt-org-archived")).IsSuccess);
        var tenantVersion = await fixture.TenantVersionAsync();
        Assert.Equal("not_found", (await fixture.SendAsync(new ReadOrganizationReceiptQuery(organization),
            "receipt-terminal-context-is-not-live-authority", organization: organization.Value)).Error!.Code);
        Assert.True((await fixture.SendAsync(new ReadOrganizationReceiptQuery(organization), "receipt-org-retained",
            organization: SchemaFixture.OrgA1)).IsSuccess);
        Assert.True((await fixture.SendAsync(new ReadDefaultOrganizationReceiptQuery(TenantId.From(SchemaFixture.TenantA)),
            "receipt-default-original-tenant", organization: SchemaFixture.OrgA1)).IsSuccess);
        Assert.Equal(tenantVersion, await fixture.TenantVersionAsync());
        await AssertReceiptAuditAsync("receipt-org-retained", "tenancy.organization.read_receipt", "Organization", organization.Value, SchemaFixture.OrgA1);
        await AssertReceiptAuditAsync("receipt-default-original-tenant", "tenancy.organization.read_receipt", "Tenant", SchemaFixture.TenantA, SchemaFixture.OrgA1);
        Assert.Empty(await fixture.Membership.AuditAsync("receipt-default-original-tenant", "tenancy.organization.select_default", SchemaFixture.OrgA1));
        Assert.Empty(await fixture.Membership.AuditAsync("receipt-org-retained", "tenancy.organization.archive", SchemaFixture.OrgA1));
    }

    [Theory]
    [InlineData("membership")]
    [InlineData("profile")]
    [InlineData("own")]
    [InlineData("role")]
    [InlineData("organization")]
    [InlineData("default")]
    public async Task A_previously_authorized_but_now_revoked_caller_cannot_disclose_a_receipt(string kind)
    {
        var caller = await fixture.Membership.AddMemberAsync(role: BuiltInRoleKey.TenantAdmin);
        var query = Query(kind, caller.Membership);
        Assert.True((await fixture.SendAsync(query, "receipt-before-revoke-" + kind, caller.Actor)).IsSuccess);
        Assert.True((await fixture.SendAsync(new ChangeMembershipStatusCommand(caller.Membership, 0, ManagedMembershipStatus.Revoked),
            "receipt-caller-revoke-" + kind)).IsSuccess);
        Assert.Equal("not_found", (await fixture.SendAsync(query, "receipt-after-revoke-" + kind, caller.Actor)).Error!.Code);
        AssertNoSuccess(await fixture.AuditAsync("receipt-after-revoke-" + kind, Operation(kind)));
    }

    [Theory]
    [InlineData("membership")]
    [InlineData("profile")]
    [InlineData("own")]
    [InlineData("role")]
    [InlineData("organization")]
    [InlineData("default")]
    public async Task An_original_target_from_another_tenant_is_never_substituted(string kind)
    {
        IRequest<Result<None>> query = kind switch
        {
            "membership" => new ReadMembershipReceiptQuery(BranchMembershipB),
            "profile" => new ReadMemberProfileReceiptQuery(BranchMembershipB),
            "own" => new ReadOwnMembershipProfileReceiptQuery(BranchMembershipB),
            "role" => new ReadRoleReceiptQuery(RoleId(SchemaFixture.TenantB, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant)),
            "organization" => new ReadOrganizationReceiptQuery(OrganizationId.From(SchemaFixture.OrgB1)),
            "default" => new ReadDefaultOrganizationReceiptQuery(TenantId.From(SchemaFixture.TenantB)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        Assert.Equal("not_found", (await fixture.SendAsync(query, "receipt-foreign-" + kind)).Error!.Code);
    }

    [Theory]
    [InlineData("membership")]
    [InlineData("profile")]
    [InlineData("own")]
    [InlineData("role")]
    [InlineData("organization")]
    [InlineData("default")]
    public async Task A_missing_original_target_does_not_fall_back_to_a_current_resource(string kind)
    {
        var missing = Guid.CreateVersion7();
        IRequest<Result<None>> query = kind switch
        {
            "membership" => new ReadMembershipReceiptQuery(missing),
            "profile" => new ReadMemberProfileReceiptQuery(missing),
            "own" => new ReadOwnMembershipProfileReceiptQuery(missing),
            "role" => new ReadRoleReceiptQuery(missing),
            "organization" => new ReadOrganizationReceiptQuery(OrganizationId.From(missing)),
            "default" => new ReadDefaultOrganizationReceiptQuery(TenantId.From(missing)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        Assert.Equal("not_found", (await fixture.SendAsync(query, "receipt-missing-" + kind)).Error!.Code);
    }

    [Fact]
    public async Task Own_and_admin_membership_receipts_keep_the_exact_nullable_organization_and_original_person()
    {
        var person = await fixture.Membership.AddMemberAsync(SchemaFixture.OrgA1);
        var orgless = await fixture.Membership.AddMembershipAsync(person.Actor, null);
        Assert.True((await fixture.SendAsync(new ReadOwnMembershipProfileReceiptQuery(person.Membership),
            "receipt-scope-positive", person.Actor, SchemaFixture.OrgA1)).IsSuccess);
        Assert.Equal("not_found", (await fixture.SendAsync(new ReadOwnMembershipProfileReceiptQuery(orgless),
            "receipt-own-no-null-fallback", person.Actor, SchemaFixture.OrgA1)).Error!.Code);
        Assert.Equal("not_found", (await fixture.SendAsync(new ReadOwnMembershipProfileReceiptQuery(person.Membership),
            "receipt-own-wrong-person", AdminA, SchemaFixture.OrgA1)).Error!.Code);
        Assert.Equal("not_found", (await fixture.SendAsync(new ReadMembershipReceiptQuery(BranchMembershipA2),
            "receipt-admin-no-wide-hatch", organization: SchemaFixture.OrgA1)).Error!.Code);
        Assert.Equal("not_found", (await fixture.SendAsync(new ReadMemberProfileReceiptQuery(orgless),
            "receipt-admin-no-null-fallback", organization: SchemaFixture.OrgA1)).Error!.Code);
    }

    [Fact]
    public async Task Revocation_committed_after_the_target_read_is_seen_by_the_final_live_authority_check()
    {
        var caller = await fixture.Membership.AddMemberAsync(SchemaFixture.OrgA1, BuiltInRoleKey.OrgAdmin);
        var target = await fixture.Membership.AddMemberAsync(SchemaFixture.OrgA1);
        var reached = false;
        await using var harness = await fixture.CreateAsync("receipt-post-read-revoke", caller.Actor, SchemaFixture.OrgA1,
            services => services.AddScoped<IIdentityReceiptReader>(provider => new AfterMembershipRead(
                new IdentityReceiptReader(provider.GetRequiredService<IdentityDbContext>(), provider.GetRequiredService<IUnitOfWork>(),
                    provider.GetRequiredService<ITenantContext>()), async () =>
                {
                    reached = true;
                    Assert.True((await fixture.SendAsync(new ChangeMembershipStatusCommand(caller.Membership, 0, ManagedMembershipStatus.Revoked),
                        "receipt-post-read-revoke-writer", organization: SchemaFixture.OrgA1)).IsSuccess);
                })));
        Assert.Equal("not_found", (await harness.Sender.Send(new ReadMembershipReceiptQuery(target.Membership))).Error!.Code);
        Assert.True(reached);
        AssertNoSuccess(await fixture.Membership.AuditAsync("receipt-post-read-revoke", "identity.membership.read_receipt", SchemaFixture.OrgA1));
    }

    [Fact]
    public async Task Dedicated_receipt_reader_materializes_no_profile_assignments_or_grants_and_keeps_the_tracker_clean()
    {
        await using var harness = await fixture.CreateAsync("receipt-minimal-root-reader");
        await using var frame = await harness.Unit.BeginTransactionAsync();
        await harness.Unit.SetTenantContextAsync(harness.Services.GetRequiredService<ITenantContext>());
        var reader = harness.Services.GetRequiredService<IIdentityReceiptReader>();
        var member = await reader.ReadMembershipAsync(MembershipId.From(LearnerMembershipA));
        Assert.NotNull(member);
        Assert.Null(member.Profile);
        Assert.Empty(member.RoleAssignments);
        var role = await reader.ReadRoleAsync(LearnStack.Modules.Identity.Domain.RoleId.From(
            RoleId(SchemaFixture.TenantA, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant)));
        Assert.NotNull(role);
        Assert.Empty(role.Grants);
        Assert.Empty(harness.Services.GetRequiredService<IdentityDbContext>().ChangeTracker.Entries());
        await frame.FailAsync();
    }

    internal static IRequest<Result<None>> Query(string kind, Guid membership) => kind switch
    {
        "membership" => new ReadMembershipReceiptQuery(membership),
        "profile" => new ReadMemberProfileReceiptQuery(membership),
        "own" => new ReadOwnMembershipProfileReceiptQuery(membership),
        "role" => new ReadRoleReceiptQuery(RoleId(SchemaFixture.TenantA, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant)),
        "organization" => new ReadOrganizationReceiptQuery(OrganizationId.From(SchemaFixture.OrgA1)),
        "default" => new ReadDefaultOrganizationReceiptQuery(TenantId.From(SchemaFixture.TenantA)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    internal static string Operation(string kind) => kind switch
    {
        "membership" => "identity.membership.read_receipt",
        "profile" or "own" => "identity.membership_profile.read_receipt",
        "role" => "identity.role.read_receipt",
        "organization" or "default" => "tenancy.organization.read_receipt",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    internal static void AssertNoSuccess(IEnumerable<string> rows)
    {
        foreach (var row in rows)
        {
            using var audit = JsonDocument.Parse(row);
            Assert.NotEqual("success", audit.RootElement.GetProperty("outcome").GetString());
        }
    }

    private async Task AssertReceiptAuditAsync(string correlation, string operation, string entity, Guid id, Guid? organization = null)
    {
        var row = Assert.Single(await fixture.Membership.AuditAsync(correlation, operation, organization));
        using var audit = JsonDocument.Parse(row);
        Assert.Equal("success", audit.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("Must", audit.RootElement.GetProperty("class").GetString());
        Assert.Equal("ReadSensitive", audit.RootElement.GetProperty("type").GetString());
        Assert.Equal(entity, audit.RootElement.GetProperty("entity").GetString());
        Assert.Equal(id.ToString("D"), audit.RootElement.GetProperty("id").GetString());
        Assert.DoesNotContain("original-receipt-private-name", row, StringComparison.Ordinal);
        Assert.DoesNotContain("receipt-original-private-branch", row, StringComparison.Ordinal);
        Assert.DoesNotContain("profile-canary", row, StringComparison.Ordinal);
    }

    private sealed class AfterMembershipRead(IIdentityReceiptReader inner, Func<Task> after) : IIdentityReceiptReader
    {
        public async Task<Membership?> ReadMembershipAsync(MembershipId id, CancellationToken cancellationToken = default)
        {
            var result = await inner.ReadMembershipAsync(id, cancellationToken);
            await after();
            return result;
        }
        public Task<Role?> ReadRoleAsync(LearnStack.Modules.Identity.Domain.RoleId id, CancellationToken cancellationToken = default) =>
            inner.ReadRoleAsync(id, cancellationToken);
    }
}
