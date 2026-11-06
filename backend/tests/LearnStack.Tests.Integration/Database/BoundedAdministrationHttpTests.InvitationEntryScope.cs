using System.Net;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Permissions;
using LearnStack.Tools.Seeder;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityAuthorizationHttpProof;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BoundedAdministrationHttpTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_invitation_entry_result_ignores_pending_role_pin_inviter_delegation_and_email_but_requires_live_lifecycle_and_frozen_mapping(bool mappingLost)
    {
        await using var proof = await CreatePreparedAsync();
        var offer = await SeedEntryOfferAsync(proof);
        SetEntryRecipient(proof, YogaAdmin);
        using var client = EntryClient(proof);
        var token = EntryToken(proof, YogaAdmin);
        using var accepted = await EntrySendAsync(client, token, "acceptances", offer, "\"0\"");
        var expected = await JsonAsync(accepted);
        await using (var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.AppConnectionString))
        await using (var tx = await app.BeginTransactionAsync())
        {
            await ScopeAsync(app, tx, Tenant.TenantId.Value);
            await LockAsync(app, tx, Tenant.TenantId.Value, null, true);
            var role = SeedData.IdentityRoles.Single(value => value.Tenant == Tenant && value.BuiltInKey == BuiltInRoleKey.Learner && value.Scope == PermissionScope.Tenant);
            await ExecuteAsync(app, tx, "UPDATE roles SET row_version=row_version+1, updated_at=clock_timestamp(), updated_by=@actor WHERE id=@role",
                ("actor", EnglishAdmin.Value), ("role", role.RoleId));
            var inviterRole = SeedData.IdentityRoles.Single(value => value.Tenant == Tenant && value.BuiltInKey == BuiltInRoleKey.TenantAdmin && value.Scope == PermissionScope.Tenant);
            await ExecuteAsync(app, tx, "DELETE FROM role_permission_grants WHERE role_id=@role AND permission_key=@key",
                ("role", inviterRole.RoleId), ("key", IdentityFoundationManifest.ProfileWrite));
            await tx.CommitAsync();
        }
        SetEntryRecipient(proof, YogaAdmin, "changed@example.test");
        proof.Issuer.UserInfoStatusCode = 503;
        using (var read = await EntrySendAsync(client, token, "results", offer))
            Assert.Equal(expected.GetRawText(), (await JsonAsync(read)).GetRawText());
        Assert.Equal(1, proof.Issuer.UserInfoRequests);
        if (mappingLost)
            await proof.Database.ExecuteAsync(Tenant,
                "UPDATE platform_host_to_tenant SET is_active=false,is_publicly_live=false WHERE host=@host", new Dictionary<string, object> { ["host"] = Tenant.Host });
        else await proof.Database.ExecuteLifecycleAsync(Tenant, "UPDATE tenants SET status='Suspended' WHERE id=@tenant");
        using var refused = await EntrySendAsync(client, token, "results", offer);
        await ProblemAsync(refused, HttpStatusCode.NotFound, "not_found");
        Assert.Equal(1, proof.Issuer.UserInfoRequests);
    }

    [Fact]
    public async Task Actual_invitation_entry_branch_offer_creates_exact_branch_member_despite_active_tenant_member()
    {
        await using var proof = await CreatePreparedAsync();
        var branch = Tenant.SecondOrganization.OrganizationId;
        var offer = await SeedEntryOfferAsync(proof, branch);
        SetEntryRecipient(proof, EnglishLearner);
        using var client = EntryClient(proof);
        using var response = await EntrySendAsync(client, EntryToken(proof, EnglishLearner), "acceptances", offer, "\"0\"");
        var result = await JsonAsync(response);
        Assert.Equal("created", result.GetProperty("disposition").GetString());
        Assert.Equal(branch.Value.ToString("D"), result.GetProperty("organizationId").GetString());
        Assert.NotEqual(SeedData.IdentityMemberships[1].MembershipId, Guid.Parse(result.GetProperty("membershipId").GetString()!));
        Assert.Equal(2, (await proof.AuditAsync(response, Tenant, branch)).Count);
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.AppConnectionString);
        await using var tx = await app.BeginTransactionAsync();
        await ScopeAsync(app, tx, Tenant.TenantId.Value, branch.Value);
        Assert.Equal(1L, await ScalarAsync<long>(app, tx,
            "SELECT count(*) FROM memberships WHERE user_id=@actor AND organization_id=@org AND status='Active'", ("actor", EnglishLearner.Value), ("org", branch.Value)));
        Assert.Equal(1L, await ScalarAsync<long>(app, tx,
            "SELECT count(*) FROM memberships WHERE user_id=@actor AND organization_id IS NULL AND status='Active'", ("actor", EnglishLearner.Value)));
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Actual_invitation_entry_uses_strongest_actual_admin_effect_and_keeps_complete_user_fences(bool existing, bool offerAdmin, bool samePerson)
    {
        await using var proof = await CreatePreparedAsync();
        var actor = samePerson ? EnglishAdmin : existing ? EnglishLearner : YogaAdmin;
        var existingMember = SeedData.IdentityMemberships[samePerson ? 0 : 1].MembershipId;
        var offer = await SeedEntryOfferAsync(proof, role: offerAdmin ? BuiltInRoleKey.TenantAdmin : BuiltInRoleKey.Learner);
        SetEntryRecipient(proof, actor);
        var exclusive = !existing && offerAdmin;
        var originalGraph = existing ? await EntryMemberGraphAsync(proof, existingMember) : null;
        await proof.OwnerAsync($$"""
            CREATE FUNCTION public.fn_invitation_entry_lock_proof() RETURNS trigger LANGUAGE plpgsql AS $proof$
            BEGIN IF NEW.operation='identity.invitation.accept' THEN
                IF public.fn_identity_lock_held('user','{{actor.Value:D}}',true) IS NOT TRUE
                    OR public.fn_identity_lock_held('user','{{EnglishAdmin.Value:D}}',true) IS NOT TRUE
                    OR public.fn_identity_lock_held('tenant',NEW.tenant_id,true) IS DISTINCT FROM {{(exclusive ? "true" : "false")}}
                    OR public.fn_identity_lock_held('tenant',NEW.tenant_id,false) IS NOT TRUE
                THEN RAISE EXCEPTION 'Invitation lock proof refused' USING ERRCODE='P0399'; END IF;
            END IF; RETURN NEW; END $proof$;
            CREATE TRIGGER tg_invitation_entry_lock_proof BEFORE INSERT ON audit_log FOR EACH ROW EXECUTE FUNCTION public.fn_invitation_entry_lock_proof();
            """);
        try
        {
            using var client = EntryClient(proof);
            using var response = await EntrySendAsync(client, EntryToken(proof, actor), "acceptances", offer, "\"0\"");
            var result = await JsonAsync(response);
            Assert.Equal(existing ? "already_member" : "created", result.GetProperty("disposition").GetString());
            if (existing)
            {
                await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.AppConnectionString);
                await using var tx = await app.BeginTransactionAsync();
                await ScopeAsync(app, tx, Tenant.TenantId.Value);
                var member = existingMember;
                Assert.Equal(member.ToString("D"), result.GetProperty("membershipId").GetString());
                Assert.Equal(originalGraph, await EntryMemberGraphAsync(proof, member));
                Assert.True(await ScalarAsync<bool>(app, tx, """
                    SELECT before_state IS NULL AND after_state IS NULL AND changes IS NULL AND entity_id=@member
                    FROM audit_log WHERE correlation_id=@correlation AND operation='identity.membership.admit_invitation'
                    """, ("member", member.ToString("D")), ("correlation", TenantAuthenticationReconciliationTests.Correlation(response))));
            }
        }
        finally { await proof.OwnerAsync("DROP TRIGGER tg_invitation_entry_lock_proof ON audit_log; DROP FUNCTION public.fn_invitation_entry_lock_proof();"); }
    }
    private static async Task<string> EntryMemberGraphAsync(IdentityAuthorizationHttpProof proof, Guid member)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.AppConnectionString);
        await using var tx = await app.BeginTransactionAsync();
        await ScopeAsync(app, tx, Tenant.TenantId.Value);
        return await ScalarAsync<string>(app, tx, """
            SELECT jsonb_build_object('member',to_jsonb(m),'profile',(SELECT to_jsonb(p) FROM membership_profiles p WHERE p.membership_id=m.id),
                'roles',(SELECT jsonb_agg(to_jsonb(a) ORDER BY a.role_id) FROM membership_role_assignments a WHERE a.membership_id=m.id))::text
            FROM memberships m WHERE id=@member
            """, ("member", member));
    }

}
