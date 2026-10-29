using System.Diagnostics;
using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.Tools.Seeder;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class IdentitySeederTests
{
    [Theory]
    [InlineData("organization", false)]
    [InlineData("host", false)]
    [InlineData("locale", false)]
    [InlineData("theme", false)]
    [InlineData("role", false)]
    [InlineData("first-admin", false)]
    [InlineData("member", false)]
    [InlineData("profile", false)]
    [InlineData("organization", true)]
    [InlineData("host", true)]
    [InlineData("locale", true)]
    [InlineData("theme", true)]
    [InlineData("role", true)]
    [InlineData("first-admin", true)]
    [InlineData("member", true)]
    [InlineData("profile", true)]
    public async Task Every_temporary_read_consumer_serializes_its_real_write_with_a_Tenant_editor(
        string kind, bool seedFirst)
    {
        const long pauseKey = -9223372036854770000L;
        var application = "seed-all-fences-" + Guid.NewGuid().ToString("N");
        await using var source = ApplicationDataSource.Build(new NpgsqlConnectionStringBuilder(_database.AppConnectionString)
        { ApplicationName = application }.ConnectionString);
        var act = await PrepareFenceActAsync(source, kind);
        var before = await ReadFenceTargetAsync(act);
        var successes = await FenceSuccessCountAsync(act);
        await using var editor = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.AppConnectionString);
        await using var edit = await editor.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(editor, edit, SeedData.English.TenantId.Value);

        Task seeding;
        if (!seedFirst)
        {
            await IdentityMembershipFixture.LockAsync(editor, edit, SeedData.English.TenantId.Value, null, false);
            seeding = StartFenceSeedAsync(source, act);
            await WaitForSeedFenceAsync(application, editor.ProcessID, seeding);
            await EditFenceTenantAsync(editor, edit);
            await edit.CommitAsync();
            await FluentActions.Awaiting(() => seeding).Should().ThrowAsync<InvalidOperationException>();
            (await ReadFenceTargetAsync(act)).Should().Be(before);
            (await FenceSuccessCountAsync(act)).Should().Be(successes);
        }
        else
        {
            // Deployment-only pause in the actual DML. The production caller still
            // authenticates as learnstack_app and retains its real Tenant fence.
            await using var pause = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.AppConnectionString);
            await using var paused = await pause.BeginTransactionAsync();
            await IdentityMembershipFixture.ExecuteAsync(pause, paused,
                "SELECT pg_catalog.pg_advisory_xact_lock(@key)", ("key", pauseKey));
            await DeploymentAsync($$"""
                CREATE FUNCTION public.proof_pause_seed_target() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF {{act.MutationPredicate}} THEN
                        IF COALESCE(current_setting('app.scope',true),'') <> '' THEN
                            RAISE EXCEPTION 'seed write retained verification scope' USING ERRCODE='23514';
                        END IF;
                        PERFORM pg_catalog.pg_advisory_xact_lock({{pauseKey}});
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER proof_pause_seed_target BEFORE INSERT OR UPDATE ON public.{{act.Table}}
                    FOR EACH ROW EXECUTE FUNCTION public.proof_pause_seed_target();
                """);
            seeding = StartFenceSeedAsync(source, act);
            try
            {
                await WaitForSeedFenceAsync(application, pause.ProcessID, seeding);
                var producer = await FindFenceWaiterAsync(application, pause.ProcessID);
                var admission = IdentityMembershipFixture.LockAsync(editor, edit,
                    SeedData.English.TenantId.Value, null, false);
                await WaitForFenceEdgeAsync(editor.ProcessID, producer, admission);
                admission.IsCompleted.Should().BeFalse("the editor must wait for the real seed business transaction");
                await paused.CommitAsync();
                await admission.WaitAsync(TimeSpan.FromSeconds(5));
                // Obtaining the conflicting fence proves the seed transaction ended.
                // Its target must have committed before this edit can run.
                var installed = await ReadFenceTargetAsync(act);
                installed.Count.Should().Be(1);
                installed.Version.Should().BeGreaterThan(before.Version);
                (await FenceSuccessCountAsync(act)).Should().Be(successes + 1);
                await EditFenceTenantAsync(editor, edit);
                await edit.CommitAsync();
                await FluentActions.Awaiting(() => seeding).Should().ThrowAsync<InvalidOperationException>();
                (await ReadFenceTargetAsync(act)).Should().Be(installed);
            }
            finally
            {
                await paused.DisposeAsync();
                await edit.DisposeAsync();
                try { await seeding.WaitAsync(TimeSpan.FromSeconds(15)); }
                catch (InvalidOperationException) { /* The expected later incompatible-prefix refusal. */ }
                await DeploymentAsync($"DROP TRIGGER proof_pause_seed_target ON public.{act.Table}; DROP FUNCTION public.proof_pause_seed_target();");
            }
        }
        await using var check = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.AppConnectionString);
        await using var checkScope = await check.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(check, checkScope, SeedData.English.TenantId.Value);
        (await IdentityMembershipFixture.ScalarAsync<string>(check, checkScope,
            "SELECT display_name FROM public.tenants WHERE id=@tenant", ("tenant", SeedData.English.TenantId.Value)))
            .Should().Be("Preserved competing institution");
    }

    private async Task<SeedFenceAct> PrepareFenceActAsync(NpgsqlDataSource source, string kind)
    {
        var tenant = SeedData.English;
        var identity = kind is "role" or "first-admin" or "member" or "profile";
        var operation = kind switch
        {
            "organization" => "tenancy.organization.create",
            "host" => "tenancy.hostmapping.write",
            "locale" => "tenancy.locale.write",
            "theme" => "tenancy.setting.write",
            "role" => "identity.role.initialize",
            "first-admin" => "identity.membership.create_first_seed_admin",
            "member" => "identity.membership.create_seed",
            "profile" => "identity.membership_profile.set_seed_profile",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        object target = kind switch
        {
            "organization" => tenant.SecondOrganization.OrganizationId.Value,
            "host" => tenant.Host,
            "locale" => tenant.TenantId.Value,
            "theme" => tenant.Curriculum!.Theme.Id,
            "role" => SeedData.IdentityRoles[0].RoleId,
            "first-admin" or "profile" => SeedData.IdentityMemberships[0].MembershipId,
            "member" => SeedData.IdentityMemberships[1].MembershipId,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var (table, predicate, snapshot) = kind switch
        {
            "organization" => ("organizations", $"NEW.id='{target:D}'::uuid", "SELECT count(*),coalesce(max(row_version),-1) FROM public.organizations WHERE id=@target"),
            "host" => ("platform_host_to_tenant", $"NEW.host='{tenant.Host}'", "SELECT count(*),coalesce(max(xmin::text::bigint),-1) FROM public.platform_host_to_tenant WHERE host=@target"),
            "locale" => ("tenant_locales", $"NEW.tenant_id='{tenant.TenantId.Value:D}'::uuid AND NEW.locale='en'", "SELECT count(*),coalesce(max(xmin::text::bigint),-1) FROM public.tenant_locales WHERE tenant_id=@target AND locale='en'"),
            "theme" => ("tenant_settings", $"NEW.id='{target:D}'::uuid", "SELECT count(*),coalesce(max(row_version),-1) FROM public.tenant_settings WHERE id=@target"),
            "role" => ("roles", $"NEW.id='{target:D}'::uuid", "SELECT count(*),coalesce(max(row_version),-1) FROM public.roles WHERE id=@target"),
            "first-admin" or "member" => ("memberships", $"NEW.id='{target:D}'::uuid", "SELECT count(*),coalesce(max(row_version),-1) FROM public.memberships WHERE id=@target"),
            "profile" => ("membership_profiles", $"NEW.membership_id='{target:D}'::uuid", "SELECT count(*),coalesce(max(row_version),-1) FROM public.memberships WHERE id=@target"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var act = new SeedFenceAct(operation, table, predicate, snapshot, target, identity);
        if (identity)
        {
            await SeedContentAsync(source);
            await InterruptFenceActAsync(act, () => RunAsync(source));
        }
        else if (kind == "theme")
            await InterruptFenceActAsync(act, async () => { await SeedContentAsync(source); }, businessFault: true);
        else
        {
            await SendFencePreparationAsync(source, null, new ProvisionTenantCommand(tenant.TenantId, tenant.Slug, tenant.DisplayName,
                tenant.DefaultOrganization.OrganizationId, tenant.DefaultOrganization.Slug, tenant.DefaultOrganization.DisplayName));
            var context = new SeedTenantContext(tenant.TenantId, null);
            if (kind is "host" or "locale") await SendFencePreparationAsync(source, context,
                new CreateOrganizationCommand(tenant.SecondOrganization.OrganizationId, tenant.SecondOrganization.Slug, tenant.SecondOrganization.DisplayName));
            if (kind == "locale") await SendFencePreparationAsync(source, context,
                new MapHostToTenantCommand(tenant.Host, null, true, true));
        }
        return act;
    }

    private async Task InterruptFenceActAsync(SeedFenceAct act, Func<Task> seed, bool businessFault = false)
    {
        var table = businessFault ? act.Table : "audit_log";
        var predicate = businessFault ? act.MutationPredicate
            : $"NEW.operation='{act.Operation}' AND NEW.entity_id='{act.Target}' AND NEW.outcome='success'";
        await DeploymentAsync($$"""
            CREATE FUNCTION public.proof_stop_seed_fence_act() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF {{predicate}} THEN RAISE EXCEPTION 'fixed seed interruption' USING ERRCODE='23514'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER proof_stop_seed_fence_act BEFORE INSERT ON public.{{table}}
                FOR EACH ROW EXECUTE FUNCTION public.proof_stop_seed_fence_act();
            """);
        try { await FluentActions.Awaiting(seed).Should().ThrowAsync<Exception>(); }
        finally { await DeploymentAsync($"DROP TRIGGER proof_stop_seed_fence_act ON public.{table}; DROP FUNCTION public.proof_stop_seed_fence_act();"); }
        (await FenceSuccessCountAsync(act)).Should().Be(0);
    }

    private static Task StartFenceSeedAsync(NpgsqlDataSource source, SeedFenceAct act) => act.Identity
        ? RunAsync(source) : SeedContentAsync(source);

    private static async Task SendFencePreparationAsync<T>(NpgsqlDataSource source, ITenantContext? context, IRequest<Result<T>> command)
    {
        await using var provider = SeedComposition.Build(source, context, NullLoggerFactory.Instance);
        await using var scope = provider.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<ISender>().Send(command)).IsSuccess.Should().BeTrue();
    }

    private static Task EditFenceTenantAsync(NpgsqlConnection app, NpgsqlTransaction transaction) =>
        IdentityMembershipFixture.ExecuteAsync(app, transaction, """
            UPDATE public.tenants SET display_name='Preserved competing institution',
                row_version=row_version+1,updated_at=clock_timestamp(),updated_by=@actor WHERE id=@tenant
            """, ("actor", SeedData.IdentityUsers[0].UserId.Value), ("tenant", SeedData.English.TenantId.Value));

    private async Task<SeedFenceSnapshot> ReadFenceTargetAsync(SeedFenceAct act)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await IdentityMembershipFixture.ScopeAsync(app, transaction, SeedData.English.TenantId.Value);
        await using var command = new NpgsqlCommand(act.SnapshotSql, app, transaction);
        command.Parameters.AddWithValue("target", act.Target);
        await using var rows = await command.ExecuteReaderAsync();
        (await rows.ReadAsync()).Should().BeTrue();
        return new(rows.GetInt64(0), rows.GetInt64(1));
    }

    private async Task<long> FenceSuccessCountAsync(SeedFenceAct act)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(app, transaction, SeedData.English.TenantId.Value);
        return await IdentityMembershipFixture.ScalarAsync<long>(app, transaction,
            "SELECT count(*) FROM public.audit_log WHERE operation=@operation AND entity_id=@target AND outcome='success'",
            ("operation", act.Operation), ("target", act.Target.ToString()));
    }

    private async Task<int> FindFenceWaiterAsync(string application, int blocker)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.AppConnectionString);
        return await IdentityMembershipFixture.ScalarAsync<int>(app, null, """
            SELECT pid FROM pg_catalog.pg_stat_activity WHERE application_name=@application
                AND @blocker=ANY(pg_catalog.pg_blocking_pids(pid))
            """, ("application", application), ("blocker", blocker));
    }

    private async Task WaitForFenceEdgeAsync(int waiter, int blocker, Task waiting)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.AppConnectionString);
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(1.5) && !waiting.IsCompleted)
        {
            if (await IdentityMembershipFixture.ScalarAsync<bool>(app, null,
                "SELECT @blocker=ANY(pg_catalog.pg_blocking_pids(@waiter))", ("blocker", blocker), ("waiter", waiter))) return;
            await Task.Delay(10);
        }
        throw new InvalidOperationException("The real competing owner did not wait for the seed transaction.");
    }

    private sealed record SeedFenceAct(string Operation, string Table, string MutationPredicate, string SnapshotSql, object Target, bool Identity);
    private sealed record SeedFenceSnapshot(long Count, long Version);
}
