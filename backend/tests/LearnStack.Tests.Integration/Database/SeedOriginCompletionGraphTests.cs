using System.Diagnostics;
using System.Text;
using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Application.Contracts.Roles;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.Modules.Tenancy.Application.Contracts.Seeding;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Results;
using LearnStack.Tools.Seeder;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Actual interrupted graphs, retained mutable state and competing Tenant-fence orders.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class SeedOriginCompletionGraphTests(SchemaFixture schema) : IAsyncLifetime
{
    private const string Adoption = "tenancy.tenant.adopt_seed_origin";
    private const string HostWrite = "tenancy.hostmapping.write";
    private const string ConflictingPin = "competing-origin-proof-pin";
    private DisposableSchemaDatabase _database = null!;

    public async Task InitializeAsync()
    {
        _database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.MigrationConnectionString);
        await IdentityAuthorityDeploymentInstaller.InstallAsync(owner, SeedOriginCompletedFoundationFixture.Registration);
    }

    public Task DisposeAsync() => _database.DisposeAsync().AsTask();

    [Theory]
    [InlineData("customization")]
    [InlineData("education")]
    [InlineData("identity")]
    public async Task A_real_interrupted_prerequisite_act_cannot_be_promoted_to_origin_completion(string stage)
    {
        await using var source = ApplicationDataSource.Build(_database.AppConnectionString);
        var interruption = InterruptionFor(stage);
        // Deployment-only fault: reject one real MUST audit, so its business act
        // rolls back while earlier fixture acts remain independently committed.
        await DeploymentAsync($$"""
            CREATE FUNCTION public.proof_interrupt_completion_graph() RETURNS trigger LANGUAGE plpgsql AS $proof$
            BEGIN
                IF NEW.operation='{{interruption.Operation}}' AND NEW.entity_id='{{interruption.Target:D}}'
                    AND NEW.outcome='success' THEN
                    RAISE EXCEPTION 'completion graph prerequisite interruption' USING ERRCODE='23514';
                END IF;
                RETURN NEW;
            END $proof$;
            CREATE TRIGGER proof_interrupt_completion_graph BEFORE INSERT ON public.audit_log
                FOR EACH ROW EXECUTE FUNCTION public.proof_interrupt_completion_graph();
            """);
        try
        {
            var failure = await FluentActions.Awaiting(() => SeedOriginCompletedFoundationFixture.InstallAsync(source))
                .Should().ThrowAsync<Exception>();
            failure.Which.ToString().Should().Contain("completion graph prerequisite interruption");
        }
        finally
        {
            await DeploymentAsync("""
                DROP TRIGGER proof_interrupt_completion_graph ON public.audit_log;
                DROP FUNCTION public.proof_interrupt_completion_graph();
                """);
        }

        await using (var app = await OpenAppAsync())
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await IdentityMembershipFixture.ScopeAsync(app, transaction, SeedData.English.TenantId.Value,
                interruption.Organization);
            (await IdentityMembershipFixture.ScalarAsync<bool>(app, transaction, interruption.RetainedStateSql,
                ("target", interruption.Target), ("predecessor", interruption.Predecessor))).Should().BeTrue(
                    "the real earlier act committed, while the selected prerequisite never completed");
            (await IdentityMembershipFixture.ScalarAsync<long>(app, transaction,
                "SELECT count(*) FROM public.audit_log WHERE operation=@operation AND entity_id=@entity AND outcome='success'",
                ("operation", interruption.Operation), ("entity", interruption.Target.ToString("D")))).Should().Be(0);
        }

        var before = await OriginAsync(SeedData.English);
        before.Marker.Should().Be(0);
        before.MappingRevision.Should().BeNull();
        (await AdoptAsync(source, SeedData.English, before.Version)).IsFailure.Should().BeTrue();
        (await OriginAsync(SeedData.English)).Should().Be(before);
        await AssertOriginAuditsAsync(SeedData.English, success: 0, refused: 2);
    }

    [Fact]
    public async Task Completed_current_profile_grants_assignments_locales_theme_and_organization_are_preserved()
    {
        await using var source = ApplicationDataSource.Build(_database.AppConnectionString);
        await SeedOriginCompletedFoundationFixture.InstallAsync(source);
        var initialGraph = await MutableGraphAsync();
        var learner = SeedData.IdentityMemberships[1];
        await using (var roles = await RoleGrantPipelineHarness.CreateAsync(_database.AppConnectionString,
            SeedOriginCompletedFoundationFixture.Registration, SeedData.English.TenantId,
            SeedData.IdentityUsers[0].UserId, "origin-retained-grants"))
        {
            // A real protected command removes the Learner's nonkernel profile
            // grant. The original TenantAdmin and both kernel pairs remain live.
            (await roles.Sender.Send(new ChangeRoleGrantsCommand(learner.Role.RoleId, 0, [])))
                .IsSuccess.Should().BeTrue();
        }
        await EditRetainedGraphAsync();
        var retained = await MutableGraphAsync();
        retained.Should().NotBe(initialGraph);
        var before = await OriginAsync(SeedData.English);

        (await AdoptAsync(source, SeedData.English, before.Version)).IsSuccess.Should().BeTrue();

        var after = await OriginAsync(SeedData.English);
        after.Marker.Should().Be(2);
        after.Version.Should().Be(before.Version + 1);
        after.ExactMapping.Should().BeTrue();
        (await MutableGraphAsync()).Should().Be(retained,
            "completion preserves every retained graph row, attribution stamp and row revision");
        await AssertOriginAuditsAsync(SeedData.English, success: 2, refused: 0);
    }

    [Fact]
    public async Task A_branch_competitor_committing_first_is_freshly_observed_after_the_Tenant_fence()
    {
        await using var setup = ApplicationDataSource.Build(_database.AppConnectionString);
        await SeedOriginCompletedFoundationFixture.InstallAsync(setup);
        var tenant = SeedData.Yoga;
        var course = BranchCourse();
        var before = await OriginAsync(tenant);
        var application = "origin-competing-first-" + Guid.NewGuid().ToString("N");
        await using var source = NamedSource(application);
        await using var competitor = await OpenAppAsync();
        await using var transaction = await competitor.BeginTransactionAsync();
        await PrepareBranchAsync(competitor, transaction, course);
        await IdentityMembershipFixture.LockAsync(competitor, transaction, tenant.TenantId.Value, null, false);
        await ChangeBranchPinAsync(competitor, transaction, course);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<Result<long>>? adoption = null;
        var committed = false;
        try
        {
            adoption = AdoptAsync(source, tenant, before.Version, deadline.Token);
            _ = await WaitForBlockedWriterAsync(application, competitor.ProcessID, adoption);
            await transaction.CommitAsync(deadline.Token);
            committed = true;
            (await adoption.WaitAsync(TimeSpan.FromSeconds(12))).IsFailure.Should().BeTrue();
        }
        finally
        {
            if (!committed) await transaction.RollbackAsync();
            await deadline.CancelAsync();
            await DrainAsync(adoption);
        }

        (await ReadBranchPinAsync()).Should().Be(ConflictingPin);
        (await OriginAsync(tenant)).Should().Be(before,
            "a newly committed conflicting branch pin cannot create a marker or mapping");
        await AssertOriginAuditsAsync(tenant, success: 0, refused: 2);
    }

    [Fact]
    public async Task Adoption_holding_the_Tenant_fence_completes_before_a_waiting_branch_competitor()
    {
        await using var setup = ApplicationDataSource.Build(_database.AppConnectionString);
        await SeedOriginCompletedFoundationFixture.InstallAsync(setup);
        var tenant = SeedData.Yoga;
        var before = await OriginAsync(tenant);
        var application = "origin-adoption-first-" + Guid.NewGuid().ToString("N");
        var competingApplication = "origin-branch-waiter-" + Guid.NewGuid().ToString("N");
        await using var source = NamedSource(application);
        await using var competingSource = NamedSource(competingApplication);
        await using var rootHolder = await OpenAppAsync();
        await using var heldRoot = await rootHolder.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(rootHolder, heldRoot, tenant.TenantId.Value);
        // A read lock gates target preparation after the actual handler acquires
        // its Tenant fence. No verifier, reader or production registration is replaced.
        await IdentityMembershipFixture.ExecuteAsync(rootHolder, heldRoot,
            "SELECT id FROM public.tenants WHERE id=@tenant FOR UPDATE", ("tenant", tenant.TenantId.Value));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<Result<long>>? adoption = null;
        Task? competitor = null;
        var released = false;
        try
        {
            adoption = AdoptAsync(source, tenant, before.Version, deadline.Token);
            var adoptionPid = await WaitForBlockedWriterAsync(application, rootHolder.ProcessID, adoption);
            competitor = CommitBranchPinAsync(competingSource);
            _ = await WaitForBlockedWriterAsync(competingApplication, adoptionPid, competitor);
            await heldRoot.CommitAsync(deadline.Token);
            released = true;
            (await adoption.WaitAsync(TimeSpan.FromSeconds(12))).IsSuccess.Should().BeTrue();
            await competitor.WaitAsync(TimeSpan.FromSeconds(12));
        }
        finally
        {
            if (!released) await heldRoot.RollbackAsync();
            await deadline.CancelAsync();
            await Task.WhenAll(DrainAsync(adoption), DrainAsync(competitor));
        }

        var completed = await OriginAsync(tenant);
        completed.Marker.Should().Be(2);
        completed.Version.Should().Be(before.Version + 1);
        completed.ExactMapping.Should().BeTrue();
        (await ReadBranchPinAsync()).Should().Be(ConflictingPin,
            "the competing branch transaction proceeds only after origin completion commits");
        await AssertOriginAuditsAsync(tenant, success: 2, refused: 0);
    }

    [Fact]
    public async Task Adoption_locks_the_exact_existing_management_mapping_and_preserves_its_row()
    {
        await using var setup = ApplicationDataSource.Build(_database.AppConnectionString);
        await SeedOriginCompletedFoundationFixture.InstallAsync(setup);
        var tenant = SeedData.English;
        var host = SeedAdministrationOriginManifest.Find(tenant.TenantId)!.Host;
        await using (var app = await OpenAppAsync())
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await IdentityMembershipFixture.ScopeAsync(app, transaction, tenant.TenantId.Value);
            await IdentityMembershipFixture.LockAsync(app, transaction, tenant.TenantId.Value, null, true);
            await IdentityMembershipFixture.ExecuteAsync(app, transaction, """
                INSERT INTO public.platform_host_to_tenant(host,tenant_id,organization_id,is_active,is_publicly_live)
                    VALUES(@host,@tenant,NULL,true,true)
                """, ("host", host), ("tenant", tenant.TenantId.Value));
            await transaction.CommitAsync();
        }
        var before = await OriginAsync(tenant);
        before.Marker.Should().Be(0);
        before.ExactMapping.Should().BeTrue();
        before.MappingRevision.Should().NotBeNull();
        var application = "origin-mapping-lock-" + Guid.NewGuid().ToString("N");
        await using var source = NamedSource(application);
        await using var holder = await OpenAppAsync();
        await using var heldMapping = await holder.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(holder, heldMapping, tenant.TenantId.Value);
        (await IdentityMembershipFixture.ScalarAsync<string>(holder, heldMapping, """
            SELECT host FROM public.platform_host_to_tenant WHERE host=@host AND tenant_id=@tenant
                AND organization_id IS NULL AND is_active AND is_publicly_live FOR UPDATE
            """, ("host", host), ("tenant", tenant.TenantId.Value))).Should().Be(host);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<Result<long>>? adoption = null;
        var released = false;
        try
        {
            adoption = AdoptAsync(source, tenant, before.Version, deadline.Token);
            _ = await WaitForBlockedWriterAsync(application, holder.ProcessID, adoption);
            await heldMapping.CommitAsync(deadline.Token);
            released = true;
            (await adoption.WaitAsync(TimeSpan.FromSeconds(12))).IsSuccess.Should().BeTrue();
        }
        finally
        {
            if (!released) await heldMapping.RollbackAsync();
            await deadline.CancelAsync();
            await DrainAsync(adoption);
        }

        var completed = await OriginAsync(tenant);
        completed.Marker.Should().Be(2);
        completed.Version.Should().Be(before.Version + 1);
        completed.ExactMapping.Should().BeTrue();
        completed.MappingRevision.Should().Be(before.MappingRevision,
            "locking and verifying the exact existing projection must not UPDATE or reinsert it");
        await AssertOriginAuditsAsync(tenant, success: 2, refused: 0);
        await using var observer = await OpenAppAsync();
        await using var observation = await observer.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(observer, observation, tenant.TenantId.Value);
        (await IdentityMembershipFixture.ScalarAsync<long>(observer, observation, """
            SELECT count(*) FROM public.audit_log WHERE operation=@operation AND entity_id=@host
                AND outcome='success' AND before_state IS NULL AND after_state IS NULL
            """, ("operation", HostWrite), ("host", host))).Should().Be(1,
                "the successful mapping intent cannot fabricate a change to the retained row");
    }

    private async Task EditRetainedGraphAsync()
    {
        var tenant = SeedData.English;
        var learner = SeedData.IdentityMemberships[1];
        var actor = SeedData.IdentityUsers[0].UserId.Value;
        await using var app = await OpenAppAsync();
        // Current Active profile edit; no future inactive-member policy is assumed.
        await using (var profile = await app.BeginTransactionAsync())
        {
            await IdentityMembershipFixture.ScopeAsync(app, profile, tenant.TenantId.Value);
            await IdentityMembershipFixture.LockAsync(app, profile, tenant.TenantId.Value, null, false);
            await IdentityMembershipFixture.ExecuteAsync(app, profile, """
                SELECT id FROM public.memberships WHERE id=@member FOR UPDATE;
                UPDATE public.membership_profiles SET display_name='Retained learner profile',preferred_locale='fr'
                    WHERE membership_id=@member;
                UPDATE public.memberships SET row_version=row_version+1,updated_at=clock_timestamp(),updated_by=@actor
                    WHERE id=@member;
                SET CONSTRAINTS ALL IMMEDIATE;
                """, ("member", learner.MembershipId), ("actor", actor));
            await profile.CommitAsync();
        }
        // An added nonadmin assignment needs the target User fence before Tenant;
        // lock retained Role roots before changing the owning Membership graph.
        await using (var assignment = await app.BeginTransactionAsync())
        {
            await IdentityMembershipFixture.ScopeAsync(app, assignment, tenant.TenantId.Value);
            await IdentityMembershipFixture.LockAsync(app, assignment, tenant.TenantId.Value, learner.Identity.UserId.Value, false);
            await IdentityMembershipFixture.ExecuteAsync(app, assignment, """
                SELECT id FROM public.roles WHERE id IN (@existing,@role) ORDER BY id FOR KEY SHARE;
                SELECT id FROM public.memberships WHERE id=@member FOR UPDATE;
                INSERT INTO public.membership_role_assignments(tenant_id,membership_id,role_id,organization_id)
                    VALUES(@tenant,@member,@role,NULL);
                UPDATE public.memberships SET row_version=row_version+1,updated_at=clock_timestamp(),updated_by=@actor
                    WHERE id=@member;
                SET CONSTRAINTS ALL IMMEDIATE;
                """, ("existing", learner.Role.RoleId), ("role", SeedData.IdentityRoles[1].RoleId),
                ("member", learner.MembershipId), ("tenant", tenant.TenantId.Value), ("actor", actor));
            await assignment.CommitAsync();
        }
        await using (var locales = await app.BeginTransactionAsync())
        {
            await IdentityMembershipFixture.ScopeAsync(app, locales, tenant.TenantId.Value);
            await IdentityMembershipFixture.LockAsync(app, locales, tenant.TenantId.Value, null, true);
            await IdentityMembershipFixture.ExecuteAsync(app, locales, """
                SELECT id FROM public.tenants WHERE id=@tenant FOR UPDATE;
                INSERT INTO public.tenant_locales(tenant_id,locale,is_default,is_enabled,sort)
                    VALUES(@tenant,'fr',false,true,1);
                UPDATE public.tenants SET row_version=row_version+1,updated_at=clock_timestamp(),updated_by=@actor
                    WHERE id=@tenant;
                """, ("tenant", tenant.TenantId.Value), ("actor", actor));
            await locales.CommitAsync();
        }
        await using (var theme = await app.BeginTransactionAsync())
        {
            await IdentityMembershipFixture.ScopeAsync(app, theme, tenant.TenantId.Value);
            await IdentityMembershipFixture.LockAsync(app, theme, tenant.TenantId.Value, null, false);
            await IdentityMembershipFixture.ExecuteAsync(app, theme, """
                UPDATE public.tenant_settings SET value=jsonb_set(value,'{primary}','"#3730a3"'),
                    row_version=row_version+1,updated_at=clock_timestamp(),updated_by=@actor WHERE id=@theme;
                """, ("theme", tenant.Curriculum!.Theme.Id), ("actor", actor));
            await theme.CommitAsync();
        }
        await using (var organization = await app.BeginTransactionAsync())
        {
            await IdentityMembershipFixture.ScopeAsync(app, organization, tenant.TenantId.Value);
            await IdentityMembershipFixture.LockAsync(app, organization, tenant.TenantId.Value, null, true);
            await IdentityMembershipFixture.ExecuteAsync(app, organization, """
                UPDATE public.organizations SET display_name='Retained branch name',status='Suspended',
                    row_version=row_version+1,updated_at=clock_timestamp(),updated_by=@actor WHERE id=@organization;
                """, ("organization", tenant.SecondOrganization.OrganizationId.Value), ("actor", actor));
            await organization.CommitAsync();
        }
        await using var read = await app.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(app, read, tenant.TenantId.Value);
        (await IdentityMembershipFixture.ScalarAsync<bool>(app, read, """
            SELECT EXISTS(SELECT 1 FROM public.membership_profiles WHERE membership_id=@member
                    AND display_name='Retained learner profile' AND preferred_locale='fr')
                AND (SELECT count(*) FROM public.membership_role_assignments WHERE membership_id=@member)=2
                AND (SELECT row_version FROM public.memberships WHERE id=@member)=3
                AND NOT EXISTS(SELECT 1 FROM public.role_permission_grants WHERE role_id=@role)
                AND (SELECT row_version FROM public.roles WHERE id=@role)=1
                AND EXISTS(SELECT 1 FROM public.tenant_locales WHERE locale='fr' AND is_enabled AND NOT is_default)
                AND EXISTS(SELECT 1 FROM public.tenant_settings WHERE id=@theme AND value->>'primary'='#3730a3')
                AND EXISTS(SELECT 1 FROM public.organizations WHERE id=@organization
                    AND display_name='Retained branch name' AND status='Suspended')
            """, ("member", learner.MembershipId), ("role", learner.Role.RoleId),
            ("theme", tenant.Curriculum!.Theme.Id), ("organization", tenant.SecondOrganization.OrganizationId.Value)))
            .Should().BeTrue("each admitted mutable dimension differs from the fixed demo declaration before adoption");
    }

    private async Task<string> MutableGraphAsync()
    {
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(app, transaction, SeedData.English.TenantId.Value);
        var snapshot = new StringBuilder();
        // The edited Membership and theme are orgless; Organizations, Roles and
        // locales are Tenant-owned. No wide scope is used by this observer.
        foreach (var table in new[] { "memberships", "membership_profiles", "membership_role_assignments",
            "roles", "role_permission_grants", "tenant_locales", "tenant_settings", "organizations" })
        {
            var rows = await IdentityMembershipFixture.ScalarAsync<string>(app, transaction, $$"""
                SELECT coalesce(jsonb_agg(item ORDER BY item::text),'[]'::jsonb)::text FROM
                    (SELECT to_jsonb(retained)||jsonb_build_object('revision',retained.xmin::text) AS item
                        FROM public.{{table}} AS retained) AS snapshot
                """);
            snapshot.Append(table).Append(':').AppendLine(rows);
        }
        return snapshot.ToString();
    }

    private static Interruption InterruptionFor(string stage)
    {
        var tenant = SeedData.English;
        var branch = tenant.Curriculum!.Courses.Single(course => course.OrganizationId is not null);
        return stage switch
        {
            "customization" => new("customization.level_taxonomy.publish", tenant.Curriculum.Taxonomy.Id, null,
                tenant.Curriculum.ContentType.Id, """
                SELECT EXISTS(SELECT 1 FROM public.tenant_level_taxonomies WHERE id=@target AND status='Draft')
                    AND EXISTS(SELECT 1 FROM public.tenant_content_types WHERE id=@predecessor AND status='Active')
                """),
            "education" => new("education.lesson.publish", branch.Lessons[0].Id, branch.OrganizationId!.Value.Value,
                branch.Id, """
                SELECT EXISTS(SELECT 1 FROM public.lessons WHERE id=@target AND status='draft' AND row_version=1)
                    AND EXISTS(SELECT 1 FROM public.lesson_translations WHERE lesson_id=@target)
                    AND EXISTS(SELECT 1 FROM public.courses WHERE id=@predecessor AND status='draft')
                """),
            "identity" => new("identity.membership.create_seed", SeedData.IdentityMemberships[1].MembershipId, null,
                SeedData.IdentityMemberships[0].MembershipId, """
                SELECT NOT EXISTS(SELECT 1 FROM public.memberships WHERE id=@target)
                    AND EXISTS(SELECT 1 FROM public.memberships WHERE id=@predecessor AND status='Active' AND row_version=0)
                    AND (SELECT count(*) FROM public.roles)=8
                """),
            _ => throw new ArgumentOutOfRangeException(nameof(stage)),
        };
    }

    private static SeedCourse BranchCourse() => SeedData.Yoga.Curriculum!.Courses.Single(
        course => course.OrganizationId == SeedData.Yoga.SecondOrganization.OrganizationId);

    private static async Task PrepareBranchAsync(NpgsqlConnection app, NpgsqlTransaction transaction, SeedCourse course)
    {
        await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
        await IdentityMembershipFixture.ScopeAsync(app, transaction, SeedData.Yoga.TenantId.Value, course.OrganizationId!.Value.Value);
        await IdentityMembershipFixture.ExecuteAsync(app, transaction, "SET LOCAL lock_timeout='10s'; SET LOCAL statement_timeout='12s'");
    }

    private static Task ChangeBranchPinAsync(NpgsqlConnection app, NpgsqlTransaction transaction, SeedCourse course) =>
        // Deliberately contradictory fixture state; this is not an admitted
        // mutable content writer or a claim about Phase 06 administration.
        IdentityMembershipFixture.ExecuteAsync(app, transaction, """
            UPDATE public.courses SET slug_key=@pin,row_version=row_version+1,
                updated_at=clock_timestamp(),updated_by=@actor WHERE id=@course
            """, ("pin", ConflictingPin), ("actor", UserId.SystemActor.Value), ("course", course.Id));

    private static async Task CommitBranchPinAsync(NpgsqlDataSource source)
    {
        await using var app = await source.OpenConnectionAsync();
        await using var transaction = await app.BeginTransactionAsync();
        var course = BranchCourse();
        await PrepareBranchAsync(app, transaction, course);
        await IdentityMembershipFixture.LockAsync(app, transaction, SeedData.Yoga.TenantId.Value, null, false);
        await ChangeBranchPinAsync(app, transaction, course);
        await transaction.CommitAsync();
    }

    private async Task<string> ReadBranchPinAsync()
    {
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        var course = BranchCourse();
        await IdentityMembershipFixture.ScopeAsync(app, transaction, SeedData.Yoga.TenantId.Value, course.OrganizationId!.Value.Value);
        return await IdentityMembershipFixture.ScalarAsync<string>(app, transaction,
            "SELECT slug_key FROM public.courses WHERE id=@course", ("course", course.Id));
    }

    private static async Task<Result<long>> AdoptAsync(NpgsqlDataSource source, SeedTenant tenant, long version,
        CancellationToken cancellationToken = default)
    {
        await using var provider = SeedComposition.Build(source, new SeedTenantContext(tenant.TenantId, null),
            NullLoggerFactory.Instance, seedOriginAdmission: new FixedSeedAdministrationOriginAdmission());
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send(new AdoptSeedAdministrationOriginCommand(version), cancellationToken);
    }

    private async Task<Origin> OriginAsync(SeedTenant tenant)
    {
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(app, transaction, tenant.TenantId.Value);
        await using var command = new NpgsqlCommand("""
            SELECT row_version,adopted_seed_origin_version,xmin::text,
                (SELECT mapping.xmin::text FROM public.platform_host_to_tenant AS mapping WHERE host=@host),
                EXISTS(SELECT 1 FROM public.platform_host_to_tenant WHERE host=@host AND tenant_id=@tenant
                    AND organization_id IS NULL AND is_active AND is_publicly_live)
            FROM public.tenants WHERE id=@tenant
            """, app, transaction);
        command.Parameters.AddWithValue("tenant", tenant.TenantId.Value);
        command.Parameters.AddWithValue("host", SeedAdministrationOriginManifest.Find(tenant.TenantId)!.Host);
        await using var row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        return new(row.GetInt64(0), row.GetInt32(1), row.GetString(2), row.IsDBNull(3) ? null : row.GetString(3), row.GetBoolean(4));
    }

    private async Task AssertOriginAuditsAsync(SeedTenant tenant, long success, long refused)
    {
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(app, transaction, tenant.TenantId.Value);
        // Count successes independently from refusal evidence; a failed command
        // must not appear write-free merely because its successful rows vanished.
        (await IdentityMembershipFixture.ScalarAsync<long>(app, transaction, """
            SELECT count(*) FROM public.audit_log WHERE outcome='success'
                AND (operation=@adoption OR operation=@host AND entity_id=@origin)
            """, ("adoption", Adoption), ("host", HostWrite),
            ("origin", SeedAdministrationOriginManifest.Find(tenant.TenantId)!.Host))).Should().Be(success);
        (await IdentityMembershipFixture.ScalarAsync<long>(app, transaction, """
            SELECT count(*) FROM public.audit_log WHERE operation_class='Must'
                AND outcome IN ('failed','denied') AND organization_id IS NULL
                AND (operation=@adoption OR operation=@host AND (entity_id=@origin OR entity_id IS NULL))
            """, ("adoption", Adoption), ("host", HostWrite),
            ("origin", SeedAdministrationOriginManifest.Find(tenant.TenantId)!.Host))).Should().Be(refused);
        if (success > 0)
            (await IdentityMembershipFixture.ScalarAsync<long>(app, transaction, """
                SELECT count(*) FROM public.audit_log WHERE operation_class='Must' AND outcome='success'
                    AND organization_id IS NULL AND actor_user_id IS NULL
                    AND ((operation=@adoption AND entity_type='Tenant' AND entity_id=@tenant)
                        OR (operation=@host AND entity_type='PlatformHostMapping' AND entity_id=@origin))
                """, ("adoption", Adoption), ("host", HostWrite), ("tenant", tenant.TenantId.Value.ToString("D")),
                ("origin", SeedAdministrationOriginManifest.Find(tenant.TenantId)!.Host))).Should().Be(2);
    }

    private async Task<int> WaitForBlockedWriterAsync(string application, int blocker, Task writer)
    {
        await using var app = await OpenAppAsync();
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(4) && !writer.IsCompleted)
        {
            var pid = await IdentityMembershipFixture.ScalarAsync<int>(app, null, """
                SELECT coalesce((SELECT pid FROM pg_catalog.pg_stat_activity WHERE application_name=@application
                    AND @blocker=ANY(pg_catalog.pg_blocking_pids(pid)) LIMIT 1),0)
                """, ("application", application), ("blocker", blocker));
            if (pid != 0) return pid;
            await Task.Delay(10);
        }
        throw new InvalidOperationException("The actual completion graph writer did not reach the expected held lock.");
    }

    private static async Task DrainAsync(Task? task)
    {
        if (task is null) return;
        try { await task.WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (Exception) when (task.IsCompleted) { /* Preserve the assertion/act failure; observe the released task. */ }
    }

    private NpgsqlDataSource NamedSource(string application) => ApplicationDataSource.Build(
        new NpgsqlConnectionStringBuilder(_database.AppConnectionString) { ApplicationName = application }.ConnectionString);

    private async Task<NpgsqlConnection> OpenAppAsync()
    {
        var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        return app;
    }

    private async Task DeploymentAsync(string sql)
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.MigrationConnectionString);
        await IdentityMembershipFixture.ExecuteAsync(owner, null, sql);
    }

    private sealed record Interruption(string Operation, Guid Target, Guid? Organization, Guid Predecessor, string RetainedStateSql);
    private sealed record Origin(long Version, int Marker, string TenantRevision, string? MappingRevision, bool ExactMapping);
}
