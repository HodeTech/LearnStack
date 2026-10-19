using System.Text.Json;
using FluentAssertions;
using LearnStack.Modules.Tenancy.Application.Contracts.Account;
using LearnStack.Modules.Tenancy.Application.Contracts.PublicReads;
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

/// <summary>The actual unrouted account request on the production API service graph and application role.</summary>
[Collection(PublicReadTestGroup.Name)]
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed partial class AccountSiteQueryTests(PublicReadFixture fixture)
{
    private static readonly UserId Actor = UserId.From(Guid.Parse("8c9fac35-0386-4a1c-9086-38b0d9d34139"));

    [Fact]
    public async Task Actual_account_query_reads_contrasting_tenants_and_mapped_organization_without_query_audits()
    {
        var beforeEnglish = await fixture.AuditCountAsync(SeedData.English);
        var beforeYoga = await fixture.AuditCountAsync(SeedData.Yoga);
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
        (await fixture.AuditCountAsync(SeedData.English)).Should().Be(beforeEnglish);
        (await fixture.AuditCountAsync(SeedData.Yoga)).Should().Be(beforeYoga);
        typeof(GetAccountSiteQuery).IsDefined(typeof(PublicSurfaceAttribute), false).Should().BeFalse();
    }

    [Theory]
    [InlineData(false, "Suspended")]
    [InlineData(false, "Archived")]
    [InlineData(false, "deleted")]
    [InlineData(true, "Suspended")]
    [InlineData(true, "Archived")]
    [InlineData(true, "deleted")]
    public async Task Actual_account_query_rechecks_live_tenant_and_mapped_organization(bool organization, string state)
    {
        var tenant = organization ? SeedData.Yoga : SeedData.English;
        var context = Context(tenant, organization);
        await using var host = CreateHost(context);
        (await Send(host)).IsSuccess.Should().BeTrue("the same stored scope is visible before its lifecycle changes");
        var before = await fixture.AuditCountAsync(tenant);
        var table = organization ? "organizations" : "tenants";
        var predicate = organization ? "tenant_id=@tenant AND id=@organization" : "id=@tenant";
        await fixture.ExecuteAsync(tenant, state == "deleted"
            ? $"UPDATE {table} SET deleted_at=now(),deleted_by=@actor WHERE {predicate}"
            : $"UPDATE {table} SET status='{state}' WHERE {predicate}");
        try
        {
            Hidden(await Send(host));
            (await fixture.AuditCountAsync(tenant)).Should().Be(before);
        }
        finally
        {
            await fixture.ExecuteAsync(tenant,
                $"UPDATE {table} SET status='{(organization ? "Active" : "Trial")}',deleted_at=NULL,deleted_by=NULL WHERE {predicate}");
        }
        (await Send(host)).IsSuccess.Should().BeTrue("restoring the exact scope restores the live read");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_account_query_hides_valid_empty_or_all_disabled_locale_configuration(bool delete)
    {
        var tenant = SeedData.English;
        await using var host = CreateHost(Context(tenant, false));
        (await Send(host)).IsSuccess.Should().BeTrue();
        var before = await fixture.AuditCountAsync(tenant);
        await fixture.ExecuteAsync(tenant, delete
            ? "DELETE FROM tenant_locales WHERE tenant_id=@tenant"
            : "UPDATE tenant_locales SET is_default=false,is_enabled=false WHERE tenant_id=@tenant");
        try
        {
            Hidden(await Send(host));
            (await fixture.AuditCountAsync(tenant)).Should().Be(before);
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
        var before = await fixture.AuditCountAsync(tenant);
        Hidden(await Send(host));
        reads.Reads.Should().Be(0, "the independent query guard must precede configuration access");
        (await fixture.AuditCountAsync(tenant)).Should().Be(before);
    }

    private async Task<AccountSite> ReadSite(SeedTenant tenant, bool mappedOrganization)
    {
        await using var host = CreateHost(Context(tenant, mappedOrganization));
        var result = await Send(host);
        result.IsSuccess.Should().BeTrue();
        return result.Value!;
    }

    private WebApplicationFactory<Program> CreateHost(ITenantContext context, Action<IServiceCollection>? configure = null) =>
        fixture.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
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

    private sealed class RefusedReadObservation
    {
        public int Reads { get; set; }
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
