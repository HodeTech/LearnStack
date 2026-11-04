using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Identity.Application.Contracts.Bindings;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.SharedKernel.Identity;
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
    public async Task Competing_fixed_first_admin_commands_recheck_the_prepared_target_after_waiting_and_commit_one_graph()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(fixture.Postgres);
        await using (var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString))
            await IdentityAuthorityDeploymentInstaller.InstallAsync(owner, Authority);
        var declaration = SeedData.IdentityMemberships.First(member => member.IsFirstAdmin);
        var tenant = declaration.Identity.Tenant.TenantId;
        await using var source = ApplicationDataSource.Build(database.AppConnectionString);
        var content = new SeedRunner(seedContext => SeedComposition.Build(source, seedContext, NullLoggerFactory.Instance),
            NullLogger<SeedRunner>.Instance);
        (await content.RunAsync(CancellationToken.None, [declaration.Identity.Tenant])).Should().Be(0);

        var manifest = IdentityFoundationManifest.Version1;
        var roles = SeedData.IdentityRoles.Where(role => role.Tenant.TenantId == tenant).ToArray();
        roles.Should().HaveCount(8);
        var first = new CreateFirstSeedAdminMembershipCommand(declaration.MembershipId, declaration.Identity.UserId,
            declaration.Role.RoleId, manifest.Version, manifest.MembershipFingerprint(tenant, null,
                declaration.MembershipId, declaration.Identity.UserId, declaration.Role.RoleId));
        var second = first with { };
        var witnessJson = JsonSerializer.Serialize(new
        {
            version = 1,
            issuer = Authority.Issuer,
            users = SeedData.IdentityUsers.Select((person, index) => new
            { actorId = person.UserId.Value, opaqueSub = $"first-admin-race-subject-{index}" }),
        });
        await using var witnessInput = new MemoryStream(Encoding.UTF8.GetBytes(witnessJson));
        var witness = await SeedProviderWitness.ReadAsync(witnessInput, Authority);
        var admission = new FixedSeedIdentityFoundationAdmission(witness);
        const string bindingPrefixComplete = "First-admin fixture reached its real adopted global-binding prefix.";
        var bindings = new SeedIdentityFoundationRunner(seedContext =>
        {
            // The runner records all four real adoptions before its first tenant
            // lookup. Stop exactly there, before any Role or Membership dispatch.
            if (seedContext is not null) throw new InvalidOperationException(bindingPrefixComplete);
            return SeedComposition.Build(source, null, NullLoggerFactory.Instance,
                identityAuthority: Authority, seedAdmission: admission);
        }, admission);
        var prepareBindings = () => bindings.RunAsync();
        (await prepareBindings.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be(bindingPrefixComplete);
        await using (var bindingProvider = SeedComposition.Build(source, null, NullLoggerFactory.Instance,
            identityAuthority: Authority, seedAdmission: admission))
        {
            await bindingProvider.GetRequiredService<IdentityAuthorityQualification>().QualifyAsync();
            await using var bindingScope = bindingProvider.CreateAsyncScope();
            var binding = await bindingScope.ServiceProvider.GetRequiredService<IIdentityBindingReader>()
                .ResolveExistingAsync(witness.Subjects[declaration.Identity.UserId]);
            binding.IsBound.Should().BeTrue();
            binding.Actor.Should().Be(declaration.Identity.UserId);
        }
        var context = new SeedIdentityTenantContext(tenant, null);
        // Use the actual fixed prefix and verifier. Random fixture tuples are no
        // longer authorized seed continuations under ADR-0080.
        foreach (var role in roles)
        {
            await using var provider = SeedComposition.Build(source, context, NullLoggerFactory.Instance,
                identityAuthority: Authority, seedAdmission: admission);
            await using var scope = provider.CreateAsyncScope();
            var initialized = await scope.ServiceProvider.GetRequiredService<ISender>().Send(
                new InitializeSeedRoleCommand(role.RoleId, role.BuiltInKey, role.Scope, manifest.Version,
                    manifest.RoleFingerprint(tenant, role.RoleId, role.BuiltInKey, role.Scope)));
            initialized.IsSuccess.Should().BeTrue("the fixed {0}/{1} Role should initialize; refusal {2}",
                role.Scope, role.BuiltInKey, initialized.Error?.Code);
        }

        await using var blocker = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var held = await blocker.BeginTransactionAsync();
        await ScopeAsync(blocker, held, tenant.Value);
        await LockAsync(blocker, held, tenant.Value, null, true);
        (await ScalarAsync<long>(blocker, held, "SELECT count(*) FROM public.memberships")).Should().Be(0);
        (await ScalarAsync<long>(blocker, held, "SELECT count(*) FROM public.roles")).Should().Be(8);
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
            const string waitingSql = """
                SELECT count(*) FROM pg_catalog.pg_locks
                WHERE locktype='advisory' AND NOT granted AND objsubid=1
                  AND database=(SELECT oid FROM pg_catalog.pg_database WHERE datname=current_database())
                  AND ((classid::bigint << 32) | objid::bigint)=@key
                """;
            var tenantWaiters = await ScalarAsync<long>(observer, null, waitingSql,
                ("key", IdentityAdvisoryLockKey.ForTenant(tenant)));
            var userWaiters = await ScalarAsync<long>(observer, null, waitingSql,
                ("key", IdentityAdvisoryLockKey.ForUser(first.UserId)));
            if (tenantWaiters == 1 && userWaiters == 1)
            {
                waiting = true;
                break;
            }
            if (leftTask.IsCompleted || rightTask.IsCompleted) break;
            await Task.Delay(10);
        }
        // The fixed command names one User/target: one request waits for Tenant,
        // the other for User. Release before assertions to avoid stranded owners.
        await held.CommitAsync();
        var results = await Task.WhenAll(leftTask, rightTask);
        waiting.Should().BeTrue("both commands pass their pre-lock absence read, then the same fixed User serializes before Tenant");
        var winner = results.Should().ContainSingle(result => result.IsSuccess).Which.Value;
        winner.Should().Be(declaration.MembershipId);
        results.Should().ContainSingle(result => result.IsFailure).Which.Error!.Code.Should().Be("identity_foundation_refused");
        await using var read = await blocker.BeginTransactionAsync();
        await ScopeAsync(blocker, read, tenant.Value);
        foreach (var table in new[] { "memberships", "membership_profiles", "membership_role_assignments" })
            (await ScalarAsync<long>(blocker, read, $"SELECT count(*) FROM public.{table}")).Should().Be(1);
        (await ScalarAsync<Guid>(blocker, read, "SELECT id FROM public.memberships")).Should().Be(winner);
        (await ScalarAsync<long>(blocker, read, """
            SELECT count(*) FROM public.memberships member
            JOIN public.membership_profiles profile ON profile.tenant_id=member.tenant_id AND profile.membership_id=member.id
            JOIN public.membership_role_assignments assignment ON assignment.tenant_id=member.tenant_id AND assignment.membership_id=member.id
            WHERE member.id=@member AND member.user_id=@user AND member.organization_id IS NULL
              AND member.status='Active' AND member.row_version=0 AND assignment.role_id=@role
              AND profile.display_name IS NULL AND profile.preferred_locale IS NULL
            """, ("member", winner), ("user", declaration.Identity.UserId.Value), ("role", declaration.Role.RoleId))).Should().Be(1);
        (await ScalarAsync<long>(blocker, read, """
            SELECT count(*) FROM public.audit_log
            WHERE operation='identity.membership.create_first_seed_admin' AND outcome='success' AND entity_id=@member
            """, ("member", winner.ToString("D")))).Should().Be(1,
                "the winning real command commits its central MUST row with the sole initial Membership graph");
    }
}
