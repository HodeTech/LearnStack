using System.Diagnostics;
using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Application.Contracts.Roles;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.Tools.Seeder;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class IdentitySeederTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Completed_content_preserves_a_valid_whole_theme_and_refuses_malformed_theme(bool valid)
    {
        await using var source = ApplicationDataSource.Build(_database.AppConnectionString);
        await SeedContentAsync(source);
        var tenant = SeedData.English;
        var theme = valid ? """{"primary":"#112233","background":"#ffffff","foreground":"#111111","muted":"#333333"}""" : "{}";
        await using (var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await IdentityMembershipFixture.ScopeAsync(app, transaction, tenant.TenantId.Value);
            await IdentityMembershipFixture.LockAsync(app, transaction, tenant.TenantId.Value, null, false);
            await IdentityMembershipFixture.ExecuteAsync(app, transaction, """
                UPDATE public.tenant_settings SET value=@theme::jsonb,row_version=row_version+1,
                    updated_at=clock_timestamp(),updated_by=@actor WHERE id=@setting
                """, ("theme", theme), ("actor", UserId.SystemActor.Value), ("setting", tenant.Curriculum!.Theme.Id));
            await transaction.CommitAsync();
        }
        var before = await AuditCountAsync();
        if (valid) await SeedContentAsync(source);
        else await FluentActions.Awaiting(() => SeedContentAsync(source)).Should().ThrowAsync<InvalidOperationException>();
        (await AuditCountAsync()).Should().Be(before);
    }

    [Fact]
    public async Task Completed_seed_preserves_an_edited_empty_profile_and_removed_assignment_without_writes()
    {
        await using var source = ApplicationDataSource.Build(_database.AppConnectionString);
        await SeedContentAsync(source);
        await RunAsync(source);
        var member = SeedData.IdentityMemberships[1];
        await using (var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await IdentityMembershipFixture.ScopeAsync(app, transaction, member.Identity.Tenant.TenantId.Value);
            await IdentityMembershipFixture.LockAsync(app, transaction, member.Identity.Tenant.TenantId.Value, null, false);
            await IdentityMembershipFixture.ExecuteAsync(app, transaction, """
                UPDATE public.memberships SET row_version=row_version+1,updated_at=clock_timestamp(),updated_by=@actor WHERE id=@member;
                UPDATE public.membership_profiles SET display_name=NULL,preferred_locale=NULL WHERE membership_id=@member;
                DELETE FROM public.membership_role_assignments WHERE membership_id=@member;
                SET CONSTRAINTS ALL IMMEDIATE;
                """, ("member", member.MembershipId), ("actor", member.Identity.UserId.Value));
            await transaction.CommitAsync();
        }
        var before = await AuditCountAsync();
        await RunAsync(source);
        (await AuditCountAsync()).Should().Be(before);
        await using var read = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.AppConnectionString);
        await using var scope = await read.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(read, scope, member.Identity.Tenant.TenantId.Value);
        (await IdentityMembershipFixture.ScalarAsync<long>(read, scope, """
            SELECT count(*) FROM public.memberships m JOIN public.membership_profiles p
                ON p.tenant_id=m.tenant_id AND p.membership_id=m.id
            WHERE m.id=@member AND m.row_version=2 AND p.display_name IS NULL AND p.preferred_locale IS NULL
                AND NOT EXISTS (SELECT 1 FROM public.membership_role_assignments a WHERE a.membership_id=m.id)
            """, ("member", member.MembershipId))).Should().Be(1);
    }

    [Fact]
    public async Task Completed_content_preserves_changed_tenant_fields_and_removed_locale_without_writes()
    {
        await using var source = ApplicationDataSource.Build(_database.AppConnectionString);
        await SeedContentAsync(source);
        var tenant = SeedData.Yoga;
        var locale = tenant.Curriculum!.Locales.First(value => !value.IsDefault).Locale;
        await using (var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await IdentityMembershipFixture.ScopeAsync(app, transaction, tenant.TenantId.Value);
            await IdentityMembershipFixture.LockAsync(app, transaction, tenant.TenantId.Value, null, false);
            await IdentityMembershipFixture.ExecuteAsync(app, transaction, """
                UPDATE public.tenants SET display_name='Edited institution',status='Suspended',default_organization_id=@organization,
                    row_version=row_version+1,updated_at=clock_timestamp(),updated_by=@actor WHERE id=@tenant;
                DELETE FROM public.tenant_locales WHERE tenant_id=@tenant AND locale=@locale;
                """, ("tenant", tenant.TenantId.Value), ("organization", tenant.SecondOrganization.OrganizationId.Value),
                ("actor", UserId.SystemActor.Value), ("locale", locale));
            await transaction.CommitAsync();
        }
        var before = await AuditCountAsync();
        await SeedContentAsync(source);
        (await AuditCountAsync()).Should().Be(before);
        await using var read = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.AppConnectionString);
        await using var scope = await read.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(read, scope, tenant.TenantId.Value);
        (await IdentityMembershipFixture.ScalarAsync<long>(read, scope,
            "SELECT count(*) FROM public.tenant_locales WHERE tenant_id=@tenant AND locale=@locale",
            ("tenant", tenant.TenantId.Value), ("locale", locale))).Should().Be(0);
    }

    [Fact]
    public async Task Completed_seed_preserves_v2_adoption_after_an_ordinary_grant_removal()
    {
        await using var source = ApplicationDataSource.Build(_database.AppConnectionString);
        await SeedContentAsync(source);
        await RunAsync(source);
        var member = SeedData.IdentityMemberships[0];
        var release = IdentityRoleRelease.Version2;
        await using (var grants = await RoleGrantPipelineHarness.CreateAsync(_database.AppConnectionString, Registration,
            member.Identity.Tenant.TenantId, member.Identity.UserId, "warm-v2-adoption"))
        {
            var adopted = await grants.Sender.Send(new AdoptRoleGrantManifestCommand(member.Role.RoleId, 0, release.Version, release.Fingerprint));
            adopted.IsSuccess.Should().BeTrue();
        }
        await using (var grants = await RoleGrantPipelineHarness.CreateAsync(_database.AppConnectionString, Registration,
            member.Identity.Tenant.TenantId, member.Identity.UserId, "warm-v2-removal"))
        {
            var changed = await grants.Sender.Send(new ChangeRoleGrantsCommand(member.Role.RoleId, 1,
                IdentityFoundationManifest.Version1.TenantAdminKernel));
            changed.IsSuccess.Should().BeTrue();
        }
        var before = await AuditCountAsync();
        await RunAsync(source);
        (await AuditCountAsync()).Should().Be(before);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("grant")]
    [InlineData("locale")]
    public async Task A_committed_edit_while_incomplete_seed_waits_is_rechecked_before_any_remaining_profile_write(string fact)
    {
        var application = "warm-seed-wait-" + Guid.NewGuid().ToString("N");
        await using var source = ApplicationDataSource.Build(new NpgsqlConnectionStringBuilder(_database.AppConnectionString)
        { ApplicationName = application }.ConnectionString);
        await SeedContentAsync(source);
        var member = SeedData.IdentityMemberships[fact == "locale" ? 2 : 0];
        var tenant = member.Identity.Tenant;
        await StopBeforeFirstProfileAsync(source, fact == "locale" ? tenant : null);
        var successfulProfilesBefore = await CountSuccessfulProfilesAsync();
        await using var blocker = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.AppConnectionString);
        await using var transaction = await blocker.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(blocker, transaction, tenant.TenantId.Value);
        await IdentityMembershipFixture.LockAsync(blocker, transaction, tenant.TenantId.Value, null, fact == "grant");
        var before = await AuditCountAsync();
        var seeding = RunAsync(source);
        await WaitForSeedFenceAsync(application, blocker.ProcessID, seeding);
        var sql = fact switch
        {
            "profile" => """
                UPDATE public.memberships SET row_version=row_version+1,updated_at=clock_timestamp(),updated_by=@actor WHERE id=@member;
                UPDATE public.membership_profiles SET display_name='Preserved edit',preferred_locale='en' WHERE membership_id=@member;
                """,
            "grant" => """
                UPDATE public.roles SET row_version=row_version+1,updated_at=clock_timestamp(),updated_by=@actor WHERE id=@role;
                DELETE FROM public.role_permission_grants WHERE role_id=@role AND permission_key='identity.membership_profile.write';
                """,
            "locale" => """
                UPDATE public.tenants SET row_version=row_version+1,updated_at=clock_timestamp(),updated_by=@actor WHERE id=@tenant;
                DELETE FROM public.tenant_locales WHERE tenant_id=@tenant AND locale=@locale;
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(fact)),
        };
        await IdentityMembershipFixture.ExecuteAsync(blocker, transaction, sql,
            ("member", member.MembershipId), ("role", member.Role.RoleId), ("tenant", tenant.TenantId.Value),
            ("actor", member.Identity.UserId.Value),
            ("locale", fact == "locale" ? tenant.Curriculum!.Locales.First(value => !value.IsDefault).Locale : string.Empty));
        await IdentityMembershipFixture.ExecuteAsync(blocker, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
        await transaction.CommitAsync();
        await FluentActions.Awaiting(() => seeding).Should().ThrowAsync<InvalidOperationException>();
        // A denied act may have its ordinary failure row; no successful remaining seed act is permitted.
        (await CountSuccessfulProfilesAsync()).Should().Be(successfulProfilesBefore);
        (await AuditCountAsync()).Should().BeGreaterThanOrEqualTo(before);
    }

    private async Task StopBeforeFirstProfileAsync(NpgsqlDataSource source, SeedTenant? tenant = null)
    {
        var tenantPredicate = tenant is null ? string.Empty : $"AND NEW.tenant_id='{tenant.TenantId.Value:D}'::uuid";
        await DeploymentAsync($$"""
            CREATE FUNCTION public.proof_stop_warm_profile() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW.operation='identity.membership_profile.set_seed_profile' AND NEW.outcome='success'
                {{tenantPredicate}}
                THEN RAISE EXCEPTION 'fixed seed interruption' USING ERRCODE='23514'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER proof_stop_warm_profile BEFORE INSERT ON public.audit_log
                FOR EACH ROW EXECUTE FUNCTION public.proof_stop_warm_profile();
            """);
        try { await FluentActions.Awaiting(() => RunAsync(source)).Should().ThrowAsync<AuditWriteFailedException>(); }
        finally { await DeploymentAsync("DROP TRIGGER proof_stop_warm_profile ON public.audit_log; DROP FUNCTION public.proof_stop_warm_profile();"); }
        (await CountContextualAsync("memberships")).Should().Be(4);
        (await CountSuccessfulProfilesAsync()).Should().Be(tenant is null ? 0 : 2);
    }

    [Fact]
    public async Task A_branch_profile_edit_while_orgless_incomplete_seed_waits_refuses_the_next_seed_profile()
    {
        var application = "warm-seed-branch-wait-" + Guid.NewGuid().ToString("N");
        await using var source = ApplicationDataSource.Build(new NpgsqlConnectionStringBuilder(_database.AppConnectionString)
        { ApplicationName = application }.ConnectionString);
        await SeedContentAsync(source);
        var tenant = SeedData.Yoga;
        await DeploymentAsync($$"""
            CREATE FUNCTION public.proof_stop_yoga_profile() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW.operation='identity.membership_profile.set_seed_profile' AND NEW.outcome='success'
                AND NEW.tenant_id='{{tenant.TenantId.Value:D}}'::uuid
                THEN RAISE EXCEPTION 'fixed seed interruption' USING ERRCODE='23514'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER proof_stop_yoga_profile BEFORE INSERT ON public.audit_log
                FOR EACH ROW EXECUTE FUNCTION public.proof_stop_yoga_profile();
            """);
        try { await FluentActions.Awaiting(() => RunAsync(source)).Should().ThrowAsync<AuditWriteFailedException>(); }
        finally { await DeploymentAsync("DROP TRIGGER proof_stop_yoga_profile ON public.audit_log; DROP FUNCTION public.proof_stop_yoga_profile();"); }
        (await CountSuccessfulProfilesAsync()).Should().Be(2);
        var member = SeedData.IdentityMemberships[3];
        await using var blocker = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.AppConnectionString);
        await using var transaction = await blocker.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(blocker, transaction, tenant.TenantId.Value, member.OrganizationId!.Value.Value);
        await IdentityMembershipFixture.LockAsync(blocker, transaction, tenant.TenantId.Value, null, false);
        var seeding = RunAsync(source);
        await WaitForSeedFenceAsync(application, blocker.ProcessID, seeding);
        await IdentityMembershipFixture.ExecuteAsync(blocker, transaction, """
            UPDATE public.memberships SET row_version=row_version+1,updated_at=clock_timestamp(),updated_by=@actor WHERE id=@member;
            UPDATE public.membership_profiles SET display_name='Branch edit',preferred_locale='tr-TR' WHERE membership_id=@member;
            SET CONSTRAINTS ALL IMMEDIATE;
            """, ("member", member.MembershipId), ("actor", member.Identity.UserId.Value));
        await transaction.CommitAsync();
        // ADR-0079 requires an edited incomplete installation to refuse before the next write,
        // including an edit outside the next target's narrower ordinary SQL organization scope.
        await FluentActions.Awaiting(() => seeding).Should().ThrowAsync<InvalidOperationException>();
        (await CountSuccessfulProfilesAsync()).Should().Be(2);
    }

    private async Task<long> CountSuccessfulProfilesAsync()
    {
        await using var read = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.PlatformConnectionString);
        return await IdentityMembershipFixture.ScalarAsync<long>(read, null,
            "SELECT count(*) FROM public.audit_log WHERE operation='identity.membership_profile.set_seed_profile' AND outcome='success'");
    }

    private async Task WaitForSeedFenceAsync(string application, int blocker, Task seeding)
    {
        await using var observer = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.AppConnectionString);
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(10) && !seeding.IsCompleted)
        {
            if (await IdentityMembershipFixture.ScalarAsync<bool>(observer, null, """
                SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_stat_activity
                WHERE application_name=@application AND wait_event_type='Lock' AND wait_event='advisory'
                  AND @blocker=ANY(pg_catalog.pg_blocking_pids(pid)))
                """, ("application", application), ("blocker", blocker))) return;
            await Task.Delay(20);
        }
        throw new InvalidOperationException("Seed did not reach the held tenant fence.");
    }
}
