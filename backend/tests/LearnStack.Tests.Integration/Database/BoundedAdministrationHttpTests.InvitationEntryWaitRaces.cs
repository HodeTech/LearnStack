using System.Net;
using System.Security.Cryptography;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.Tools.Seeder;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityAuthorizationHttpProof;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BoundedAdministrationHttpTests
{
    [Fact]
    public async Task Actual_invitation_entry_rechecks_role_pin_after_the_recipient_User_wait()
    {
        await using var proof = await CreatePreparedAsync();
        var offer = await SeedEntryOfferAsync(proof);
        SetEntryRecipient(proof, YogaAdmin);
        using var client = EntryClient(proof);
        using var response = await AcceptAfterEntryUserWaitAsync(proof, client, offer, (app, tx) =>
            ExecuteAsync(app, tx, "UPDATE roles SET row_version=row_version+1,updated_at=clock_timestamp(),updated_by=@actor WHERE id=@role",
                ("actor", EnglishAdmin.Value), ("role", EntryWaitRole(BuiltInRoleKey.Learner))));
        await ProblemAsync(response, HttpStatusCode.Conflict, "concurrency_conflict");
        Assert.All(await proof.AuditAsync(response, Tenant), row => Assert.NotEqual("success", row.Outcome));
        await AssertEntryStateAsync(proof, offer, "Pending", 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_invitation_entry_refuses_reissue_or_revoke_committed_during_the_recipient_User_wait(bool reissue)
    {
        await using var proof = await CreatePreparedAsync();
        var offer = await SeedEntryOfferAsync(proof);
        SetEntryRecipient(proof, YogaAdmin);
        using var client = EntryClient(proof);
        var replacementSecret = RandomNumberGenerator.GetBytes(32);
        var replacementDigest = InvitationVerifier.Compute(offer.Id, replacementSecret);
        CryptographicOperations.ZeroMemory(replacementSecret);
        try
        {
            using var response = await AcceptAfterEntryUserWaitAsync(proof, client, offer, (app, tx) => reissue
                ? ExecuteAsync(app, tx, """
                    UPDATE invitations SET generation=generation+1,generation_id=@generation,current_delivery_job_id=@job,
                        verifier_digest=@digest,generation_created_at=clock_timestamp(),expires_at=clock_timestamp()+interval '14 days',
                        submission_status='Queued',submission_receipt_id=NULL,row_version=row_version+1,
                        updated_at=clock_timestamp(),updated_by=@actor WHERE id=@id
                    """, ("generation", Guid.CreateVersion7()), ("job", Guid.CreateVersion7()), ("digest", replacementDigest),
                    ("actor", EnglishAdmin.Value), ("id", offer.Id))
                : ExecuteAsync(app, tx, """
                    UPDATE invitations SET status='Revoked',verifier_digest=NULL,row_version=row_version+1,
                        updated_at=clock_timestamp(),updated_by=@actor WHERE id=@id
                    """, ("actor", EnglishAdmin.Value), ("id", offer.Id)));
            await ProblemAsync(response, HttpStatusCode.Conflict, "concurrency_conflict");
            Assert.All(await proof.AuditAsync(response, Tenant), row => Assert.NotEqual("success", row.Outcome));
            await AssertEntryStateAsync(proof, offer, reissue ? "Pending" : "Revoked", 0);
            using var stale = await EntrySendAsync(client, EntryToken(proof, YogaAdmin), "inspections", offer);
            await ProblemAsync(stale, HttpStatusCode.NotFound, "not_found");
            Assert.Equal(1, proof.Issuer.UserInfoRequests);
        }
        finally { CryptographicOperations.ZeroMemory(replacementDigest); }
    }

    [Theory]
    [InlineData("Active")]
    [InlineData("Suspended")]
    public async Task Actual_invitation_entry_refuses_a_new_exact_member_committed_during_the_recipient_User_wait(string status)
    {
        await using var proof = await CreatePreparedAsync();
        // An absent recipient offered TenantAdmin initially requires an exclusive Tenant
        // fence; a concurrently created member also changes the actual effect plan.
        var offer = await SeedEntryOfferAsync(proof, role: BuiltInRoleKey.TenantAdmin);
        var member = Guid.CreateVersion7();
        SetEntryRecipient(proof, YogaAdmin);
        using var client = EntryClient(proof);
        using var response = await AcceptAfterEntryUserWaitAsync(proof, client, offer, async (app, tx) =>
        {
            await InsertMemberAsync(app, tx, Tenant.TenantId.Value, null, member, YogaAdmin.Value, EntryWaitRole(BuiltInRoleKey.Learner));
            if (status == "Suspended")
                await ExecuteAsync(app, tx, "UPDATE memberships SET status='Suspended',row_version=row_version+1,updated_at=clock_timestamp(),updated_by=@actor WHERE id=@member",
                    ("actor", EnglishAdmin.Value), ("member", member));
        });
        await ProblemAsync(response, HttpStatusCode.Conflict, "concurrency_conflict");
        Assert.All(await proof.AuditAsync(response, Tenant), row => Assert.NotEqual("success", row.Outcome));
        await AssertEntryStateAsync(proof, offer, "Pending", status == "Active" ? 1 : 0);
        await using var verify = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.AppConnectionString);
        await using var read = await verify.BeginTransactionAsync();
        await ScopeAsync(verify, read, Tenant.TenantId.Value);
        Assert.Equal(status, await ScalarAsync<string>(verify, read, "SELECT status FROM memberships WHERE id=@member", ("member", member)));
        Assert.Equal(1L, await ScalarAsync<long>(verify, read,
            "SELECT count(*) FROM membership_role_assignments WHERE membership_id=@member AND role_id=@role",
            ("member", member), ("role", EntryWaitRole(BuiltInRoleKey.Learner))));
    }

    [Fact]
    public async Task Actual_invitation_entry_rechecks_stored_inviter_delegation_after_the_recipient_User_wait()
    {
        await using var proof = await CreatePreparedAsync();
        // Learner already requires ProfileWrite. Removing that non-kernel grant
        // from the inviter's TenantAdmin leaves a valid graph but loses this ceiling.
        var offer = await SeedEntryOfferAsync(proof);
        SetEntryRecipient(proof, YogaAdmin);
        using var client = EntryClient(proof);
        using var response = await AcceptAfterEntryUserWaitAsync(proof, client, offer, (app, tx) => ExecuteAsync(app, tx, """
            DELETE FROM role_permission_grants WHERE role_id=@role AND permission_key=@key;
            UPDATE roles SET row_version=row_version+1 WHERE id=@role;
            """, ("role", EntryWaitRole(BuiltInRoleKey.TenantAdmin)), ("key", IdentityFoundationManifest.ProfileWrite)));
        await ProblemAsync(response, HttpStatusCode.Forbidden, "forbidden");
        Assert.All(await proof.AuditAsync(response, Tenant), row => Assert.NotEqual("success", row.Outcome));
        await AssertEntryStateAsync(proof, offer, "Pending", 0);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Actual_invitation_entry_defensively_rechecks_global_users_after_the_User_wait(bool closeInviter, bool existing)
    {
        await using var proof = await CreatePreparedAsync();
        Guid? member = existing ? await proof.Database.AddMembershipAsync(Tenant, YogaAdmin) : null;
        var offer = await SeedEntryOfferAsync(proof);
        var closing = closeInviter ? EnglishAdmin : YogaAdmin;
        SetEntryRecipient(proof, YogaAdmin);
        using var client = EntryClient(proof);
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.MigrationConnectionString);
        await using var monitor = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(monitor);
        var originalCheck = await ScalarAsync<string>(owner, null,
            "SELECT pg_get_expr(polwithcheck,polrelid) FROM pg_policy WHERE polrelid='public.users'::regclass AND polname='users_isolation'");
        Assert.True(await ScalarAsync<bool>(owner, null,
            "SELECT tgenabled='O' FROM pg_trigger WHERE tgrelid='public.users'::regclass AND tgname='tg_users_no_update'"));
        // The production schema has no User closure writer. Reuse ADR-0074's
        // isolated owner-only corruption setup to prove the actual defensive
        // reread, without claiming a production closure or P03-10 capability.
        try
        {
            await ExecuteAsync(owner, null, $"""
                ALTER TABLE public.users DISABLE TRIGGER tg_users_no_update;
                ALTER POLICY users_isolation ON public.users WITH CHECK (({originalCheck}) OR
                    (current_user='learnstack_migration' AND id='{closing.Value:D}'::uuid AND status='Closing'
                        AND deleted_at IS NULL AND deleted_by IS NULL));
                """);
            await using var held = await owner.BeginTransactionAsync();
            await ExecuteAsync(owner, held, "SELECT pg_advisory_xact_lock(@key)",
                ("key", IdentityAdvisoryLockKey.ForUser(closing)));
            var request = EntrySendAsync(client, EntryToken(proof, YogaAdmin), "acceptances", offer, "\"0\"");
            var committed = false;
            HttpResponseMessage? response = null;
            try
            {
                await ObserveEntryUserWaitAsync(monitor, owner.ProcessID, request);
                Assert.Equal(1, proof.Issuer.UserInfoRequests);
                // The retained fixture closure protocol takes the User fence
                // before the complete ordered Tenant family affected by closure.
                foreach (var key in new[] { Tenant.TenantId, SeedData.Yoga.TenantId }
                    .Select(IdentityAdvisoryLockKey.ForTenant).Distinct().Order())
                    await ExecuteAsync(owner, held, "SELECT pg_advisory_xact_lock(@key)", ("key", key));
                Assert.Equal(1L, await ScalarAsync<long>(owner, held, """
                    WITH changed AS (UPDATE users SET status='Closing' WHERE id=@actor RETURNING 1)
                    SELECT count(*) FROM changed
                    """, ("actor", closing.Value)));
                await held.CommitAsync();
                committed = true;
                response = await request.WaitAsync(TimeSpan.FromSeconds(15));
                using (response)
                {
                    // Actor absence and a present, globally closed inviter are
                    // distinct available facts; neither becomes Unavailable/503.
                    await ProblemAsync(response, closeInviter ? HttpStatusCode.NotFound : HttpStatusCode.Conflict,
                        closeInviter ? "not_found" : "concurrency_conflict");
                    Assert.All(await proof.AuditAsync(response, Tenant), row => Assert.NotEqual("success", row.Outcome));
                }
                await AssertEntryStateAsync(proof, offer, "Pending", existing ? 1 : 0);
                if (member is { } originalMember)
                {
                    await using var read = await monitor.BeginTransactionAsync();
                    await ScopeAsync(monitor, read, Tenant.TenantId.Value);
                    Assert.True(await ScalarAsync<bool>(monitor, read, """
                        SELECT m.status='Active' AND m.row_version=0 AND p.display_name='HTTP contextual profile'
                            AND (SELECT count(*) FROM membership_role_assignments a WHERE a.membership_id=m.id)=1
                            AND EXISTS(SELECT 1 FROM membership_role_assignments a WHERE a.membership_id=m.id AND a.role_id=@role)
                        FROM memberships m JOIN membership_profiles p ON p.membership_id=m.id AND p.tenant_id=m.tenant_id
                        WHERE m.id=@member
                        """, ("member", originalMember), ("role", EntryWaitRole(BuiltInRoleKey.Learner))));
                }
            }
            finally
            {
                if (!committed) await held.RollbackAsync();
                if (response is null)
                {
                    using var abandoned = await request.WaitAsync(TimeSpan.FromSeconds(15));
                }
            }
        }
        finally
        {
            await ExecuteAsync(owner, null, $"""
                ALTER POLICY users_isolation ON public.users WITH CHECK ({originalCheck});
                ALTER TABLE public.users ENABLE TRIGGER tg_users_no_update;
                """);
        }
        Assert.Equal(originalCheck, await ScalarAsync<string>(owner, null,
            "SELECT pg_get_expr(polwithcheck,polrelid) FROM pg_policy WHERE polrelid='public.users'::regclass AND polname='users_isolation'"));
        Assert.True(await ScalarAsync<bool>(owner, null,
            "SELECT tgenabled='O' FROM pg_trigger WHERE tgrelid='public.users'::regclass AND tgname='tg_users_no_update'"));
        var refused = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(owner, null,
            "UPDATE users SET status=status WHERE id=@actor", ("actor", closing.Value)));
        Assert.Equal("23514", refused.SqlState);
    }

    private static Guid EntryWaitRole(BuiltInRoleKey role) => SeedData.IdentityRoles.Single(value =>
        value.Tenant == Tenant && value.BuiltInKey == role && value.Scope == PermissionScope.Tenant).RoleId;

    private static async Task<HttpResponseMessage> AcceptAfterEntryUserWaitAsync(IdentityAuthorizationHttpProof proof,
        HttpClient client, EntryOffer offer, Func<NpgsqlConnection, NpgsqlTransaction, Task> competingMutation)
    {
        await using var blocker = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(blocker);
        await using var monitor = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(monitor);
        await using var tx = await blocker.BeginTransactionAsync();
        await ScopeAsync(blocker, tx, Tenant.TenantId.Value);
        await ExecuteAsync(blocker, tx, "SELECT pg_advisory_xact_lock(@key)", ("key", IdentityAdvisoryLockKey.ForUser(YogaAdmin)));
        var request = EntrySendAsync(client, EntryToken(proof, YogaAdmin), "acceptances", offer, "\"0\"");
        var committed = false;
        HttpResponseMessage? response = null;
        try
        {
            await ObserveEntryUserWaitAsync(monitor, blocker.ProcessID, request);
            Assert.Equal(1, proof.Issuer.UserInfoRequests);
            // The request has read its pre-wait member and Role, and cannot own its
            // Tenant fence yet. Commit promptly within the production 2s lock budget.
            await LockAsync(blocker, tx, Tenant.TenantId.Value, null, true);
            await competingMutation(blocker, tx);
            await ExecuteAsync(blocker, tx, "SET CONSTRAINTS ALL IMMEDIATE");
            await tx.CommitAsync();
            committed = true;
            response = await request.WaitAsync(TimeSpan.FromSeconds(15));
            return response;
        }
        finally
        {
            if (!committed) await tx.RollbackAsync();
            if (response is null)
            {
                using var abandoned = await request.WaitAsync(TimeSpan.FromSeconds(15));
            }
        }
    }

    private static async Task ObserveEntryUserWaitAsync(NpgsqlConnection monitor, int blocker, Task<HttpResponseMessage> request)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await ScalarAsync<bool>(monitor, null, """
            SELECT EXISTS(SELECT 1 FROM pg_stat_activity
                WHERE @blocker=ANY(pg_blocking_pids(pid)) AND state='active'
                    AND wait_event_type='Lock' AND wait_event='advisory')
            """, ("blocker", blocker)))
        {
            Assert.False(request.IsCompleted);
            await Task.Delay(10, deadline.Token);
        }
        Assert.False(request.IsCompleted);
    }
}
