using System.Diagnostics;
using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.Tools.Seeder;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class IdentityMembershipTests
{
    [Fact]
    public async Task Competing_actual_first_admin_commands_recheck_history_after_the_tenant_wait_and_commit_exactly_one_graph()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(fixture.Postgres);
        await SeedSchemaAsync(database.MigrationConnectionString, database.AppConnectionString);
        var tenant = TenantId.From(Guid.CreateVersion7());
        await SeedFoundationTenantAsync(database.AppConnectionString, tenant);
        var role = Guid.CreateVersion7();
        var manifest = IdentityFoundationManifest.Version1;
        var roleFingerprint = manifest.RoleFingerprint(tenant, role, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant);
        var firstId = Guid.CreateVersion7();
        var secondId = Guid.CreateVersion7();
        var first = new CreateFirstSeedAdminMembershipCommand(firstId, UserId.From(PersonA), role, 1,
            manifest.MembershipFingerprint(tenant, null, firstId, UserId.From(PersonA), role));
        var second = new CreateFirstSeedAdminMembershipCommand(secondId, UserId.From(PersonB), role, 1,
            manifest.MembershipFingerprint(tenant, null, secondId, UserId.From(PersonB), role));
        var admission = new FoundationProofAdmission(
            new(SeedIdentityFoundationOperation.InitializeRole, tenant, null, UserId.SystemActor, role,
                null, null, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant, 1, roleFingerprint),
            new(SeedIdentityFoundationOperation.CreateFirstAdmin, tenant, null, UserId.SystemActor, firstId,
                first.UserId, role, null, PermissionScope.Tenant, 1, first.ExpectedFingerprint),
            new(SeedIdentityFoundationOperation.CreateFirstAdmin, tenant, null, UserId.SystemActor, secondId,
                second.UserId, role, null, PermissionScope.Tenant, 1, second.ExpectedFingerprint));
        var context = new FoundationProofContext(tenant);
        await using var source = ApplicationDataSource.Build(database.AppConnectionString);
        await using (var provider = SeedComposition.Build(source, context, NullLoggerFactory.Instance,
            identityAuthority: Authority, seedAdmission: admission))
        {
            await using var scope = provider.CreateAsyncScope();
            var initialized = await scope.ServiceProvider.GetRequiredService<ISender>().Send(
                new InitializeSeedRoleCommand(role, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant, 1, roleFingerprint));
            initialized.IsSuccess.Should().BeTrue();
        }

        await using var blocker = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var held = await blocker.BeginTransactionAsync();
        await ScopeAsync(blocker, held, tenant.Value);
        await LockAsync(blocker, held, tenant.Value, null, true);
        (await ScalarAsync<long>(blocker, held, "SELECT count(*) FROM public.memberships")).Should().Be(0);
        await using var leftProvider = SeedComposition.Build(source, context, NullLoggerFactory.Instance,
            identityAuthority: Authority, seedAdmission: admission);
        await using var rightProvider = SeedComposition.Build(source, context, NullLoggerFactory.Instance,
            identityAuthority: Authority, seedAdmission: admission);
        await using var left = leftProvider.CreateAsyncScope();
        await using var right = rightProvider.CreateAsyncScope();
        var leftTask = left.ServiceProvider.GetRequiredService<ISender>().Send(first);
        var rightTask = right.ServiceProvider.GetRequiredService<ISender>().Send(second);
        await using var observer = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        var budget = Stopwatch.StartNew();
        var waiting = false;
        while (budget.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (await ScalarAsync<long>(observer, null, """
                SELECT count(*) FROM pg_catalog.pg_locks
                WHERE locktype='advisory' AND NOT granted AND objsubid=1
                  AND database=(SELECT oid FROM pg_catalog.pg_database WHERE datname=current_database())
                  AND ((classid::bigint << 32) | objid::bigint)=@key
                """, ("key", IdentityAdvisoryLockKey.ForTenant(tenant))) == 2)
            {
                waiting = true;
                break;
            }
            if (leftTask.IsCompleted || rightTask.IsCompleted) break;
            await Task.Delay(10);
        }
        // Release before assertions so a failed observation cannot leave either
        // actual command blocked while its scope is being disposed.
        await held.CommitAsync();
        var results = await Task.WhenAll(leftTask, rightTask);
        waiting.Should().BeTrue("both commands must pass their pre-lock absence read before either can commit");
        var winner = results.Should().ContainSingle(result => result.IsSuccess).Which.Value;
        results.Should().ContainSingle(result => result.IsFailure).Which.Error!.Code.Should().Be("identity_foundation_refused");
        await using var read = await blocker.BeginTransactionAsync();
        await ScopeAsync(blocker, read, tenant.Value);
        foreach (var table in new[] { "memberships", "membership_profiles", "membership_role_assignments" })
            (await ScalarAsync<long>(blocker, read, $"SELECT count(*) FROM public.{table}")).Should().Be(1);
        (await ScalarAsync<Guid>(blocker, read, "SELECT id FROM public.memberships")).Should().Be(winner);
        (await ScalarAsync<long>(blocker, read, """
            SELECT count(*) FROM public.audit_log
            WHERE operation='identity.membership.create_first_seed_admin' AND outcome='success' AND entity_id=@member
            """, ("member", winner.ToString("D")))).Should().Be(1,
                "the winning real command commits its central MUST row with the sole initial Membership graph");
    }
}
