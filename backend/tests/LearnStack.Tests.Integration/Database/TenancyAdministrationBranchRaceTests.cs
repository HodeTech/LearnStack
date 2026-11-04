using System.Text.Json;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class TenancyAdministrationBranchRaceTests(TenancyAdministrationFixture fixture) : IClassFixture<TenancyAdministrationFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_branch_profile_and_archive_share_the_retained_tenant_fence_in_both_orders(bool archiveWins)
    {
        var created = await fixture.SendAsync(new CreateManagedOrganizationCommand("branch-race-" + Guid.NewGuid().ToString("N"), "Branch race"), "tenancy-branch-race-create");
        Assert.True(created.IsSuccess);
        var branch = created.Value!.OrganizationId;
        var member = await fixture.Membership.AddMembershipAsync(PersonA, branch.Value, BuiltInRoleKey.Learner);
        var before = await fixture.Membership.ReadAsync(member, branch.Value);
        var pause = new PauseBeforeSave(db => archiveWins
            ? db is TenancyDbContext && db.ChangeTracker.Entries<Organization>().Any(entry => entry.Entity.Id == branch
                && entry.State == EntityState.Modified && entry.Entity.Status == OrganizationStatus.Archived)
            : db is IdentityDbContext && db.ChangeTracker.Entries<Membership>().Any(entry => entry.Entity.Id.Value == member
                && entry.State == EntityState.Modified));
        await using var profile = await fixture.CreateAsync("tenancy-branch-race-profile-" + archiveWins,
            organization: branch.Value, configure: services => { if (!archiveWins) services.AddSingleton<ISaveChangesInterceptor>(pause); });
        await using var archive = await fixture.CreateAsync("tenancy-branch-race-archive-" + archiveWins,
            configure: services => { if (archiveWins) services.AddSingleton<ISaveChangesInterceptor>(pause); });
        Task? first = null;
        Task? second = null;
        try
        {
            if (archiveWins)
            {
                var archival = archive.Sender.Send(new ArchiveOrganizationCommand(branch, created.Value.Version));
                first = archival;
                await pause.Entered.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.False(archival.IsCompleted);
                var write = profile.Sender.Send(new SetMembershipProfileCommand(member, before.Version, "Must not commit after archive", "en"));
                second = write;
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await fixture.Membership.ObserveWaitAsync(profile.ApplicationName,
                    ((NpgsqlConnection)archive.Unit.Connection).ProcessID, write, deadline.Token);
                Assert.False(write.IsCompleted);
                pause.Release();
                Assert.True((await archival).IsSuccess);
                Assert.Equal("not_found", (await write).Error!.Code);
                var after = await fixture.Membership.ReadAsync(member, branch.Value);
                Assert.Equal(before.Version, after.Version);
                Assert.Equal(before.DisplayName, after.DisplayName);
                Assert.Equal(before.PreferredLocale, after.PreferredLocale);
                Assert.Equal(before.RootStamp, after.RootStamp);
                Assert.Equal(before.Assignments.OrderBy(row => row.Key), after.Assignments.OrderBy(row => row.Key));
            }
            else
            {
                var write = profile.Sender.Send(new SetMembershipProfileCommand(member, before.Version, "Committed before archive", "en"));
                first = write;
                await pause.Entered.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.False(write.IsCompleted);
                var archival = archive.Sender.Send(new ArchiveOrganizationCommand(branch, created.Value.Version));
                second = archival;
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await fixture.Membership.ObserveWaitAsync(archive.ApplicationName,
                    ((NpgsqlConnection)profile.Unit.Connection).ProcessID, archival, deadline.Token);
                Assert.False(archival.IsCompleted);
                pause.Release();
                Assert.True((await write).IsSuccess);
                Assert.True((await archival).IsSuccess);
                var after = await fixture.Membership.ReadAsync(member, branch.Value);
                Assert.Equal(before.Version + 1, after.Version);
                Assert.Equal("Committed before archive", after.DisplayName);
            }
            var detail = await fixture.SendAsync(new GetOrganizationDetailQuery(branch), "tenancy-branch-race-final");
            Assert.True(detail.IsSuccess);
            Assert.Equal(ManagedOrganizationStatus.Archived, detail.Value!.Status);
            Assert.False(detail.Value.IsDefault);
            var rows = await fixture.Membership.AuditAsync("tenancy-branch-race-profile-" + archiveWins,
                "identity.membership_profile.write", branch.Value);
            foreach (var row in rows)
            {
                using var audit = JsonDocument.Parse(row);
                if (archiveWins) Assert.NotEqual("success", audit.RootElement.GetProperty("outcome").GetString());
            }
            if (!archiveWins)
                Assert.Contains(rows, row => JsonDocument.Parse(row).RootElement.GetProperty("outcome").GetString() == "success");
        }
        finally
        {
            pause.Release();
            // Ensure every actual competing request finishes before its owning harness is disposed.
            if (first is not null) try { await first.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
            if (second is not null) try { await second.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
        }
    }

    private sealed class PauseBeforeSave(Func<DbContext, bool> matches) : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _paused;
        public Task Entered => _entered.Task;
        public void Release() => _release.TrySetResult();
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context is { } db && matches(db) && Interlocked.Exchange(ref _paused, 1) == 0)
            {
                _entered.TrySetResult();
                await _release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }
            return result;
        }
    }
}
