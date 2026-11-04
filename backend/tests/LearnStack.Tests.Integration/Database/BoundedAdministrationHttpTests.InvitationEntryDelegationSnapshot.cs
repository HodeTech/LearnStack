using System.Data.Common;
using System.Net;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.Permissions;
using LearnStack.Tools.Seeder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task Actual_invitation_entry_delegation_uses_one_committed_graph_during_assignment_removal_and_grant_restore(bool race)
    {
        await using var proof = await CreatePreparedAsync();
        var branch = Tenant.SecondOrganization.OrganizationId;
        var adminRole = SeedData.IdentityRoles.Single(row => row.Tenant == Tenant && row.BuiltInKey == BuiltInRoleKey.OrgAdmin).RoleId;
        var instructorRole = SeedData.IdentityRoles.Single(row => row.Tenant == Tenant && row.BuiltInKey == BuiltInRoleKey.OrgInstructor).RoleId;
        var inviterMember = await proof.Database.AddMembershipAsync(Tenant, YogaAdmin, branch, BuiltInRoleKey.OrgAdmin);
        await using (var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.AppConnectionString))
        {
            await SchemaQueries.AssertApplicationRoleAsync(app);
            await using var tx = await app.BeginTransactionAsync();
            await ScopeAsync(app, tx, Tenant.TenantId.Value, branch.Value);
            await LockAsync(app, tx, Tenant.TenantId.Value, YogaAdmin.Value, false);
            await ExecuteAsync(app, tx, """
                INSERT INTO membership_role_assignments(tenant_id,membership_id,role_id,organization_id)
                VALUES(@tenant,@member,@role,@org);
                UPDATE memberships SET row_version=row_version+1,updated_at=clock_timestamp(),updated_by=@actor WHERE id=@member;
                """, ("tenant", Tenant.TenantId.Value), ("member", inviterMember), ("role", instructorRole), ("org", branch.Value), ("actor", EnglishAdmin.Value));
            await tx.CommitAsync();
        }
        var offer = await SeedEntryOfferAsync(proof, branch, inviter: YogaAdmin);
        if (race)
        {
            await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.AppConnectionString);
            await using var tx = await app.BeginTransactionAsync();
            await ScopeAsync(app, tx, Tenant.TenantId.Value);
            await LockAsync(app, tx, Tenant.TenantId.Value, null, false);
            Assert.Equal(1L, await ScalarAsync<long>(app, tx,
                "WITH changed AS (DELETE FROM role_permission_grants WHERE role_id=@role AND permission_key=@key RETURNING 1) SELECT count(*) FROM changed",
                ("role", adminRole), ("key", "identity.membership_profile.write")));
            await ExecuteAsync(app, tx, "UPDATE roles SET row_version=row_version+1,updated_at=clock_timestamp(),updated_by=@actor WHERE id=@role", ("role", adminRole), ("actor", EnglishAdmin.Value));
            await tx.CommitAsync();
        }
        SetEntryRecipient(proof, EnglishLearner);
        var pause = new InvitationDelegationQueryPause(YogaAdmin.Value, race);
        using var client = EntryClient(proof, configure: services => services.AddSingleton<ISaveChangesInterceptor>(pause));
        var pending = EntrySendAsync(client, EntryToken(proof, EnglishLearner), "acceptances", offer, "\"0\"");
        try
        {
            if (race)
            {
                await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.False(pending.IsCompleted);
                // Both permitted changes retain Shared Tenant, without taking the
                // inviter User fence held by acceptance. No guard is disabled.
                await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.AppConnectionString);
                await SchemaQueries.AssertApplicationRoleAsync(app);
                await using (var removal = await app.BeginTransactionAsync())
                {
                    await ScopeAsync(app, removal, Tenant.TenantId.Value, branch.Value);
                    await LockAsync(app, removal, Tenant.TenantId.Value, null, false);
                    Assert.Equal(1L, await ScalarAsync<long>(app, removal,
                        "WITH changed AS (DELETE FROM membership_role_assignments WHERE membership_id=@member AND role_id=@role RETURNING 1) SELECT count(*) FROM changed", ("member", inviterMember), ("role", adminRole)));
                    await ExecuteAsync(app, removal, "UPDATE memberships SET row_version=row_version+1,updated_at=clock_timestamp(),updated_by=@actor WHERE id=@member", ("member", inviterMember), ("actor", EnglishAdmin.Value));
                    await ExecuteAsync(app, removal, "SET CONSTRAINTS ALL IMMEDIATE");
                    await removal.CommitAsync();
                }
                await using (var restoration = await app.BeginTransactionAsync())
                {
                    await ScopeAsync(app, restoration, Tenant.TenantId.Value);
                    await LockAsync(app, restoration, Tenant.TenantId.Value, null, false);
                    Assert.Equal(1L, await ScalarAsync<long>(app, restoration, """
                        WITH changed AS (INSERT INTO role_permission_grants(tenant_id,role_id,permission_scope,permission_key)
                        VALUES(@tenant,@role,'Organization','identity.membership_profile.write') RETURNING 1) SELECT count(*) FROM changed
                        """, ("tenant", Tenant.TenantId.Value), ("role", adminRole)));
                    await ExecuteAsync(app, restoration, "UPDATE roles SET row_version=row_version+1,updated_at=clock_timestamp(),updated_by=@actor WHERE id=@role", ("role", adminRole), ("actor", EnglishAdmin.Value));
                    await ExecuteAsync(app, restoration, "SET CONSTRAINTS ALL IMMEDIATE");
                    await restoration.CommitAsync();
                }
            }
        }
        catch
        {
            pause.Release.TrySetResult();
            using var joined = await pending.WaitAsync(TimeSpan.FromSeconds(15));
            throw;
        }
        finally { pause.Release.TrySetResult(); }
        using var response = await pending.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(1, pause.Queries);
        Assert.True(pause.JoinedGrants);
        if (race)
        {
            await ProblemAsync(response, HttpStatusCode.Forbidden, "forbidden");
            Assert.All(await proof.AuditAsync(response, Tenant, branch), row => Assert.NotEqual("success", row.Outcome));
        }
        else Assert.Equal("created", (await JsonAsync(response)).GetProperty("disposition").GetString());
        await using var verify = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.AppConnectionString);
        await using var read = await verify.BeginTransactionAsync();
        await ScopeAsync(verify, read, Tenant.TenantId.Value, branch.Value);
        Assert.Equal(race ? "Pending" : "Accepted", await ScalarAsync<string>(verify, read, "SELECT status FROM invitations WHERE id=@id", ("id", offer.Id)));
        Assert.Equal(race ? 0L : 1L, await ScalarAsync<long>(verify, read,
            "SELECT count(*) FROM memberships WHERE user_id=@actor AND organization_id=@org", ("actor", EnglishLearner.Value), ("org", branch.Value)));
        Assert.Equal(1L, await ScalarAsync<long>(verify, read,
            "SELECT count(*) FROM membership_role_assignments WHERE membership_id=@member AND role_id=@role", ("member", inviterMember), ("role", instructorRole)));
    }

    private sealed class InvitationDelegationQueryPause(Guid inviter, bool pause) : DbCommandInterceptor, ISaveChangesInterceptor
    {
        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Queries { get; private set; }
        internal bool JoinedGrants { get; private set; }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM memberships", StringComparison.Ordinal)
                && command.CommandText.Contains("membership_role_assignments", StringComparison.Ordinal)
                && command.Parameters.Cast<DbParameter>().Any(parameter => parameter.Value is Guid value && value == inviter))
            {
                Queries++;
                JoinedGrants = command.CommandText.Contains("role_permission_grants", StringComparison.Ordinal)
                    && command.CommandText.Contains("JOIN", StringComparison.Ordinal);
                Reached.TrySetResult();
                if (pause) await Release.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }
            return result;
        }
    }
}
