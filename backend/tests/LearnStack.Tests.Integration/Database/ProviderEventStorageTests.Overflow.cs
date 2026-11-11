using FluentAssertions;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class ProviderEventStorageTests
{
    [Fact]
    public async Task Persistent_257_recipient_problem_does_not_accept_partial_targets_or_block_later_history_after_restart()
    {
        // Arrange complete Identity graphs through the actual app-role constraints,
        // not disabled triggers or forged owner visibility. The person already has tenant A.
        var template = IdentityFoundationManifest.Version1.Templates.Single(item =>
            item.BuiltInKey == BuiltInRoleKey.TenantAdmin && item.Scope == PermissionScope.Tenant);
        await using (var app = await OpenAsync(fixture.Postgres.AppConnectionString))
        {
            for (var index = 0; index < 256; index++)
            {
                var tenant = Guid.CreateVersion7();
                var role = Guid.CreateVersion7();
                await using var transaction = await app.BeginTransactionAsync();
                await IdentityMembershipFixture.ScopeAsync(app, transaction, tenant);
                await IdentityMembershipFixture.LockAsync(app, transaction, tenant, IdentityMembershipFixture.AdminA, exclusive: true);
                await IdentityMembershipFixture.ExecuteAsync(app, transaction, """
                    INSERT INTO public.roles(id,tenant_id,built_in_key,permission_scope,created_at,created_by)
                    VALUES(@role,@tenant,'TenantAdmin','Tenant',clock_timestamp(),@actor)
                    """, ("role", role), ("tenant", tenant), ("actor", UserId.SystemActor.Value));
                foreach (var grant in template.Grants)
                    await IdentityMembershipFixture.ExecuteAsync(app, transaction, """
                        INSERT INTO public.role_permission_grants(tenant_id,role_id,permission_scope,permission_key)
                        VALUES(@tenant,@role,@scope,@key)
                        """, ("tenant", tenant), ("role", role), ("scope", grant.Scope.ToString()), ("key", grant.Key));
                await IdentityMembershipFixture.InsertMemberAsync(app, transaction, tenant, null,
                    Guid.CreateVersion7(), IdentityMembershipFixture.AdminA, role, displayName: null);
                await transaction.CommitAsync();
            }
        }

        var problemId = Guid.CreateVersion7();
        var at = DateTimeOffset.UtcNow.AddMinutes(-30);
        var key = IdentitySubjectKey.FromBytes(SubjectKey(IdentityMembershipFixture.AdminA));
        Guid receipt;
        await using (var source = await QualifiedSourceAsync())
        {
            var rejected = await source.AcceptAsync(problemId, at, "LOGIN", key);
            rejected.Accepted.Should().BeFalse();
            rejected.Disposition.Should().Be("fanout_exceeded");
            rejected.TargetCount.Should().Be(0);
            receipt = rejected.ReceiptId;
            (await source.AcceptAsync(Guid.CreateVersion7(), at.AddMinutes(1), "LOGOUT",
                IdentitySubjectKey.FromBytes(SubjectKey(IdentityMembershipFixture.PersonA)))).Accepted.Should().BeTrue();
        }
        await using (var restarted = await QualifiedSourceAsync())
        {
            var stillRejected = await restarted.AcceptAsync(problemId, at, "LOGIN", key);
            stillRejected.ReceiptId.Should().Be(receipt);
            stillRejected.Accepted.Should().BeFalse();
            (await restarted.AcceptAsync(Guid.CreateVersion7(), at.AddMinutes(2), "LOGIN_ERROR",
                IdentitySubjectKey.FromBytes(SubjectKey(IdentityMembershipFixture.PersonB)))).Accepted.Should().BeTrue();
        }
        await using var machine = await MachineAsync();
        (await IdentityMembershipFixture.ScalarAsync<long>(machine, null, """
            SELECT count(*) FROM public.identity_provider_event_problems
            WHERE provider_event_id=@event AND reason='fanout_exceeded' AND resolved_at IS NULL
            """, ("event", problemId))).Should().Be(1);
        // Inspect each tenant only through an announced app-role scope; no targets exist even in the original tenant.
        await using var observer = await OpenAsync(fixture.Postgres.AppConnectionString);
        await using var read = await observer.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(observer, read, SchemaFixture.TenantA);
        (await IdentityMembershipFixture.ScalarAsync<long>(observer, read,
            "SELECT count(*) FROM public.identity_provider_event_targets WHERE provider_event_id=@event", ("event", problemId))).Should().Be(0);
    }
}
