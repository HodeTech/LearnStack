using System.Text.Json;
using FluentAssertions;
using LearnStack.Modules.Education.Application.Contracts.PublicReads;
using LearnStack.Modules.Tenancy.Application.Contracts.Account;
using LearnStack.Modules.Tenancy.Application.Account;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Permissions;
using Npgsql;
using NpgsqlTypes;
using LearnStack.Modules.Tenancy.Application.Contracts.PublicReads;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.Tools.Seeder;
using MediatR;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Unrouted account-handler and physical transaction component proofs on real app-role data.
/// Exact fixture admission is separate from the live HTTP binding/capability proofs.</summary>
[Collection(PublicReadTestGroup.Name)]
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed partial class AccountSiteQueryTests(PublicReadFixture fixture) : IAsyncLifetime
{
    private ComponentAuthorizationEvidence _evidence = null!;
    public async Task InitializeAsync() => _evidence = await ComponentAuthorizationEvidence.CreateAsync(
        fixture.AppConnectionString, fixture.MigrationConnectionString);
    public Task DisposeAsync() => _evidence.DisposeAsync().AsTask();

    private static readonly UserId Actor = UserId.From(Guid.Parse("8c9fac35-0386-4a1c-9086-38b0d9d34139"));

    [Fact]
    public async Task Actual_account_query_reads_contrasting_tenants_and_mapped_organization_without_query_audits()
    {
        var englishContext = Context(SeedData.English, mappedOrganization: false);
        var yogaContext = Context(SeedData.Yoga, mappedOrganization: false);
        var yogaMappedContext = Context(SeedData.Yoga, mappedOrganization: true);
        var beforeEnglish = await AuditCountsAsync(englishContext);
        var beforeYoga = await AuditCountsAsync(yogaContext);
        var beforeYogaMapped = await AuditCountsAsync(yogaMappedContext);
        var readsBefore = fixture.Observation.Reads;
        var english = await ReadSite(SeedData.English, mappedOrganization: false);
        var yoga = await ReadSite(SeedData.Yoga, mappedOrganization: true);
        var yogaTenant = await ReadSite(SeedData.Yoga, mappedOrganization: false);

        english.ActorId.Should().Be(Actor);
        english.TenantId.Should().Be(SeedData.English.TenantId);
        english.OrganizationId.Should().BeNull();
        english.Site.DisplayName.Should().Be(SeedData.English.DisplayName);
        english.Site.EnabledLocales.Should().Equal("en");
        english.Site.DefaultLocale.Should().Be("en");
        yoga.ActorId.Should().Be(Actor);
        yoga.TenantId.Should().Be(SeedData.Yoga.TenantId);
        yoga.OrganizationId.Should().Be(SeedData.Yoga.DefaultOrganization.OrganizationId);
        yoga.Site.DisplayName.Should().Be(SeedData.Yoga.DisplayName);
        yoga.Site.EnabledLocales.Should().Equal("tr-TR", "en");
        yoga.Site.DefaultLocale.Should().Be("tr-TR");
        yoga.Site.Theme.Should().NotBeEquivalentTo(english.Site.Theme,
            "the two real stored themes must distinguish a widened tenant query");
        yogaTenant.OrganizationId.Should().BeNull();
        yogaTenant.Site.Should().BeEquivalentTo(yoga.Site,
            "branding.theme is deliberately tenant-wide, not an organization override");
        foreach (var (site, tenant) in new[] { (english.Site, SeedData.English), (yoga.Site, SeedData.Yoga) })
        {
            using var theme = JsonDocument.Parse(tenant.Curriculum!.Theme.Value);
            site.Theme.Should().NotBeNull();
            site.Theme!.Primary.Should().Be(theme.RootElement.GetProperty("primary").GetString());
            site.ShowPlatformAttribution.Should().BeFalse();
        }
        fixture.Observation.Reads.Should().Be(readsBefore + 3,
            "the production reader verifies the actual enlisted ReadOnly transaction and app role");
        (await AuditCountsAsync(englishContext)).Should().Be(beforeEnglish);
        (await AuditCountsAsync(yogaContext)).Should().Be(beforeYoga);
        (await AuditCountsAsync(yogaMappedContext)).Should().Be(beforeYogaMapped);
        typeof(GetAccountSiteQuery).IsDefined(typeof(PublicSurfaceAttribute), false).Should().BeFalse();
    }

    [Theory]
    [InlineData(false, "Suspended", false)]
    [InlineData(false, "Archived", false)]
    [InlineData(false, "deleted", false)]
    [InlineData(true, "Suspended", false)]
    [InlineData(true, "Archived", false)]
    [InlineData(true, "deleted", false)]
    [InlineData(true, "Suspended", true)]
    [InlineData(true, "Archived", true)]
    [InlineData(true, "deleted", true)]
    public async Task Actual_account_query_rechecks_live_tenant_mapped_and_claim_selected_organization(bool organization, string state, bool claimSelected)
    {
        // An archived Organization is terminal. Its history belongs to this
        // disposable database; cleanup must not resurrect a shared seed branch.
        await using var isolated = organization ? await PublicReadFixture.CreateIsolatedAsync(fixture) : null;
        var source = isolated ?? fixture;
        await using var isolatedEvidence = organization ? await ComponentAuthorizationEvidence.CreateAsync(
            source.AppConnectionString, source.MigrationConnectionString) : null;
        var tenant = organization ? SeedData.Yoga : SeedData.English;
        var context = claimSelected ? TenantContextFactory.Create(new TenantResolutionAttempt
        {
            HostTenantId = tenant.TenantId,
            ClaimTenantId = tenant.TenantId,
            ClaimOrganizationId = tenant.DefaultOrganization.OrganizationId,
            UserId = Actor,
            HasValidatedPrincipal = true,
            MembershipCovers = true,
            ClaimedOrganizationBelongsToTenant = true,
        }).Value! : Context(tenant, organization);
        await using var host = CreateHost(source, isolatedEvidence ?? _evidence, context);
        (await Send(host)).IsSuccess.Should().BeTrue("the same stored scope is visible before its lifecycle changes");
        if (claimSelected) await AssertPublicConfigurationLiveness(host, visible: true);
        var before = await AuditCountsAsync(source, context);
        var table = organization ? "organizations" : "tenants";
        var predicate = organization ? "tenant_id=@tenant AND id=@organization" : "id=@tenant";
        var withdraw = state == "deleted"
            ? $"UPDATE {table} SET deleted_at=now(),deleted_by=@actor WHERE {predicate}"
            : $"UPDATE {table} SET status='{state}' WHERE {predicate}";
        if (organization)
        {
            Func<Task> unfenced = () => source.ExecuteAsync(tenant, withdraw);
            (await unfenced.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("P0311");
            Func<Task> currentDefault = () => source.ExecuteLifecycleAsync(tenant, withdraw);
            var refusedDefault = (await currentDefault.Should().ThrowAsync<PostgresException>()).Which;
            refusedDefault.SqlState.Should().Be("P0312");
            refusedDefault.MessageText.Should().Be("tenancy default graph is invalid");

            // ADR-0079: explicitly select another Active default in its own
            // committed transaction, then withdraw the originally resolved branch.
            await source.ExecuteLifecycleAsync(tenant, $"""
                UPDATE tenants SET default_organization_id='{tenant.SecondOrganization.OrganizationId.Value:D}',
                    row_version=row_version+1,updated_at=clock_timestamp(),updated_by=@actor WHERE id=@tenant
                """);
            var afterSelection = await Send(host);
            afterSelection.IsSuccess.Should().BeTrue("changing the default does not rewrite the mapped or claimed branch");
            afterSelection.Value!.OrganizationId.Should().Be(tenant.DefaultOrganization.OrganizationId);
            (await AuditCountsAsync(source, context)).Should().Be(before);
            await source.ExecuteLifecycleAsync(tenant, withdraw);
        }
        else await source.ExecuteAsync(tenant, withdraw);
        try
        {
            Hidden(await Send(host));
            if (claimSelected) await AssertPublicConfigurationLiveness(host, visible: false);
            (await AuditCountsAsync(source, context)).Should().Be((before.Business, before.Denials + 1));
            if (organization && state == "Archived")
            {
                Func<Task> revive = () => source.ExecuteLifecycleAsync(tenant,
                    "UPDATE organizations SET status='Active' WHERE tenant_id=@tenant AND id=@organization");
                var terminal = (await revive.Should().ThrowAsync<PostgresException>()).Which;
                terminal.SqlState.Should().Be("P0312");
                terminal.MessageText.Should().Be("tenancy lifecycle transition is invalid");
                Hidden(await Send(host));
                if (claimSelected) await AssertPublicConfigurationLiveness(host, visible: false);
                (await AuditCountsAsync(source, context)).Should().Be((before.Business, before.Denials + 2));
                return;
            }
        }
        finally
        {
            if (organization && state != "Archived")
                await source.ExecuteLifecycleAsync(tenant, """
                    UPDATE organizations SET status='Active',deleted_at=NULL,deleted_by=NULL
                        WHERE tenant_id=@tenant AND id=@organization;
                    UPDATE tenants SET default_organization_id=@organization,row_version=row_version+1,
                        updated_at=clock_timestamp(),updated_by=@actor WHERE id=@tenant;
                    """);
            else if (!organization)
                await source.ExecuteAsync(tenant,
                    "UPDATE tenants SET status='Trial',deleted_at=NULL,deleted_by=NULL WHERE id=@tenant");
        }
        (await Send(host)).IsSuccess.Should().BeTrue("restoring the exact nonterminal scope restores the live read");
        if (claimSelected) await AssertPublicConfigurationLiveness(host, visible: true);
        (await AuditCountsAsync(source, context)).Should().Be((before.Business, before.Denials + 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_account_query_hides_valid_empty_or_all_disabled_locale_configuration(bool delete)
    {
        var tenant = SeedData.English;
        var context = Context(tenant, false);
        await using var host = CreateHost(context);
        (await Send(host)).IsSuccess.Should().BeTrue();
        var before = await AuditCountsAsync(context);
        await fixture.ExecuteAsync(tenant, delete
            ? "DELETE FROM tenant_locales WHERE tenant_id=@tenant"
            : "UPDATE tenant_locales SET is_default=false,is_enabled=false WHERE tenant_id=@tenant");
        try
        {
            Hidden(await Send(host));
            (await AuditCountsAsync(context)).Should().Be((before.Business, before.Denials + 1));
        }
        finally
        {
            await fixture.ExecuteAsync(tenant, "DELETE FROM tenant_locales WHERE tenant_id=@tenant; "
                + "INSERT INTO tenant_locales(tenant_id,locale,is_default,is_enabled,sort) VALUES(@tenant,'en',true,true,0)");
        }
        (await Send(host)).IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_account_query_refuses_foreign_tenant_or_sibling_organization_provenance_before_reading(bool organization)
    {
        var tenant = organization ? SeedData.Yoga : SeedData.English;
        var actual = Context(tenant, organization);
        var other = organization
            ? TenantContextFactory.Create(new TenantResolutionAttempt
            {
                HostTenantId = tenant.TenantId,
                HostOrganizationId = tenant.SecondOrganization.OrganizationId,
                ClaimTenantId = tenant.TenantId,
                ClaimOrganizationId = tenant.SecondOrganization.OrganizationId,
                UserId = Actor,
                HasValidatedPrincipal = true,
            }).Value!
            : Context(SeedData.Yoga, true);
        var reads = new RefusedReadObservation();
        await using var host = CreateHost(new ChangedHostContext(actual, other.HostScope!), services =>
        {
            services.AddSingleton(reads);
            services.AddScoped<IPublicTenantConfigurationReader, ForbiddenConfigurationReader>();
        });
        var before = await AuditCountsAsync(actual);
        Hidden(await Send(host));
        reads.HandlerEntries.Should().Be(1, "the actual query must reach its independent provenance guard");
        reads.Reads.Should().Be(0, "the independent query guard must precede configuration access");
        (await AuditCountsAsync(actual)).Should().Be((before.Business, before.Denials + 1));
    }

    private static async Task AssertPublicConfigurationLiveness(WebApplicationFactory<Program> host, bool visible)
    {
        Result<PublicSite> site;
        await using (var scope = host.Services.CreateAsyncScope())
            site = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new GetPublicSiteQuery());
        Result<PublicCourseCatalog> courses;
        await using (var scope = host.Services.CreateAsyncScope())
            courses = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new GetPublicCoursesQuery(
                new PublicReadInput("en", null, null, "claim-selected-organization-liveness", [])));
        if (visible)
        {
            site.IsSuccess.Should().BeTrue();
            site.Value!.DisplayName.Should().Be(SeedData.Yoga.DisplayName);
            courses.IsSuccess.Should().BeTrue();
            courses.Value!.Locale.Should().Be("en");
        }
        else
        {
            site.IsFailure.Should().BeTrue();
            site.Error!.Code.Should().Be("not_found");
            courses.IsFailure.Should().BeTrue();
            courses.Error!.Code.Should().Be("not_found");
        }
    }

    private async Task<AccountSite> ReadSite(SeedTenant tenant, bool mappedOrganization)
    {
        await using var host = CreateHost(Context(tenant, mappedOrganization));
        var catalog = host.Services.GetRequiredService<IAuditCatalog>();
        catalog.TryGet(typeof(GetAccountSiteQuery), out var registration).Should().BeTrue();
        registration.WritesNoRow.Should().BeTrue("the actual API composition must register this exact read Off");
        registration.Entries.Should().BeEmpty();
        var result = await Send(host);
        result.IsSuccess.Should().BeTrue();
        return result.Value!;
    }

    private WebApplicationFactory<Program> CreateHost(ITenantContext context, Action<IServiceCollection>? configure = null) =>
        CreateHost(fixture, _evidence, context, configure);

    private static WebApplicationFactory<Program> CreateHost(PublicReadFixture source,
        ComponentAuthorizationEvidence evidence, ITenantContext context, Action<IServiceCollection>? configure = null) =>
        source.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            // Admit only this unrouted component request; keep the canonical slot 5
            // and the actual ExplicitReadOnlyRequest/handler/EF/app-role adapters.
            ComponentOperationRegistration.AddExactComponentAuthority(services,
                OperationPolicy.ForTrusted<GetAccountSiteQuery>("tests.component.account.read", AuthorizationSourceKind.Seed));
            evidence.AddTo(services);
            services.AddSingleton<RefusedReadObservation>();
            services.AddScoped<GetAccountSiteQueryHandler>();
            services.AddScoped<IRequestHandler<GetAccountSiteQuery, Result<AccountSite>>, ObservedAccountHandler>();
            services.AddSingleton<ITenantContext>(context);
            services.AddSingleton<ITenantContextAccessor>(new StaticTenantContextAccessor(context));
            configure?.Invoke(services);
        }));

    private static async Task<Result<AccountSite>> Send(WebApplicationFactory<Program> host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(new GetAccountSiteQuery());
    }

    private static TenantContext Context(SeedTenant tenant, bool mappedOrganization) =>
        TenantContextFactory.Create(new TenantResolutionAttempt
        {
            HostTenantId = tenant.TenantId,
            HostOrganizationId = mappedOrganization ? tenant.DefaultOrganization.OrganizationId : null,
            ClaimTenantId = tenant.TenantId,
            ClaimOrganizationId = mappedOrganization ? tenant.DefaultOrganization.OrganizationId : null,
            UserId = Actor,
            HasValidatedPrincipal = true,
        }).Value!;

    private static void Hidden(Result<AccountSite> result)
    {
        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("not_found");
    }

    private Task<(long Business, long Denials)> AuditCountsAsync(TenantContext context) =>
        AuditCountsAsync(fixture, context);

    private static async Task<(long Business, long Denials)> AuditCountsAsync(PublicReadFixture source, TenantContext context)
    {
        await using var connection = new NpgsqlConnection(source.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(connection, transaction, context.TenantId.Value);
        await SchemaQueries.SetSettingAsync(connection, transaction, "app.organization_id",
            context.OrganizationId?.Value.ToString("D") ?? "");
        await SchemaQueries.AssertApplicationRoleAsync(connection, transaction);
        await using var command = new NpgsqlCommand("""
            SELECT count(*) FILTER (WHERE operation <> 'identity.authorization.deny'),
                   count(*) FILTER (WHERE operation = 'identity.authorization.deny'
                     AND operation_class = 'Must' AND outcome = 'denied'
                     AND metadata->'authorization'->>'policy' = 'tests.component.account.read'
                     AND metadata->'authorization'->>'source' = 'seed')
            FROM audit_log WHERE tenant_id = @tenant
              AND organization_id IS NOT DISTINCT FROM @organization
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", context.TenantId.Value);
        command.Parameters.Add(new NpgsqlParameter("organization", NpgsqlDbType.Uuid)
        { Value = context.OrganizationId is { } organization ? organization.Value : DBNull.Value });
        await using var row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        return (row.GetInt64(0), row.GetInt64(1));
    }

    private sealed class ObservedAccountHandler(GetAccountSiteQueryHandler inner,
        IUnitOfWork unit, RefusedReadObservation observation)
        : IRequestHandler<GetAccountSiteQuery, Result<AccountSite>>
    {
        public async Task<Result<AccountSite>> Handle(GetAccountSiteQuery request, CancellationToken cancellationToken)
        {
            observation.HandlerEntries++;
            unit.Mode.Should().Be(TransactionMode.ReadOnly);
            unit.GetTenantContextAnnouncement(unit.Transaction).Should().NotBeNull();
            await SchemaQueries.AssertApplicationRoleAsync(unit.Connection, unit.Transaction);
            await using var physical = new NpgsqlCommand("SHOW transaction_read_only",
                (NpgsqlConnection)unit.Connection, (NpgsqlTransaction?)unit.Transaction);
            (await physical.ExecuteScalarAsync(cancellationToken)).Should().Be("on");
            return await inner.Handle(request, cancellationToken);
        }
    }

    private sealed class RefusedReadObservation
    {
        public int Reads { get; set; }
        public int HandlerEntries { get; set; }
    }

    private sealed class ForbiddenConfigurationReader(RefusedReadObservation observation) : IPublicTenantConfigurationReader
    {
        public Task<Result<PublicTenantConfiguration>> ReadAsync(CancellationToken cancellationToken)
        {
            observation.Reads++;
            throw new InvalidOperationException("A refused account query reached configuration.");
        }
    }

    private sealed class ChangedHostContext(ITenantContext actual, HostScope host) : ITenantContext
    {
        public bool IsResolved => actual.IsResolved;
        public TenantId TenantId => actual.TenantId;
        public OrganizationId? OrganizationId => actual.OrganizationId;
        public UserId? UserId => actual.UserId;
        public TenantContextOrigin? Origin => actual.Origin;
        public HostScope HostScope => host;
        public string? CorrelationId => actual.CorrelationId;
        public string? ModuleName => actual.ModuleName;
    }
}
