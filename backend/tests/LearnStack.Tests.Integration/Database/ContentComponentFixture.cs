using LearnStack.SharedKernel.Identifiers;
using LearnStack.Modules.Customization.Application.Contracts.Customization;
using LearnStack.Modules.Customization.Application.Contracts.Seeding;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Incidental app-role setup for content component tests; Tenancy writers are not their subject.</summary>
internal static class ContentComponentFixture
{
    internal static ServiceProvider CreateCustomizationProvider(NpgsqlDataSource source, ITenantContext context)
    {
        var services = ComponentOperationRegistration.CreateDatabaseServices(source, context);
        ComponentOperationRegistration.AddExactComponentAuthority(services,
            OperationPolicy.ForTrusted<InitializeTenantContentTypeDefaultCommand>("tests.component.content_fixture.initialize_content", AuthorizationSourceKind.Seed),
            OperationPolicy.ForTrusted<InitializeTenantLevelTaxonomyDefaultCommand>("tests.component.content_fixture.initialize_taxonomy", AuthorizationSourceKind.Seed),
            OperationPolicy.ForTrusted<RegisterTenantContentTypeCommand>("tests.component.content_fixture.register_content", AuthorizationSourceKind.Seed),
            OperationPolicy.ForTrusted<PublishTenantContentTypeCommand>("tests.component.content_fixture.publish_content", AuthorizationSourceKind.Seed),
            OperationPolicy.ForTrusted<RegisterTenantLevelTaxonomyCommand>("tests.component.content_fixture.register_taxonomy", AuthorizationSourceKind.Seed),
            OperationPolicy.ForTrusted<PublishTenantLevelTaxonomyCommand>("tests.component.content_fixture.publish_taxonomy", AuthorizationSourceKind.Seed),
            OperationPolicy.ForTrusted<GetContentTypeSeedStateQuery>("tests.component.content_fixture.read_content", AuthorizationSourceKind.Seed),
            OperationPolicy.ForTrusted<GetTaxonomySeedStateQuery>("tests.component.content_fixture.read_taxonomy", AuthorizationSourceKind.Seed));
        return services.BuildServiceProvider(validateScopes: true);
    }

    internal static async Task CreateTenantAsync(NpgsqlDataSource source, TenantId tenant,
        OrganizationId? defaultOrganization = null, OrganizationId? secondOrganization = null, bool englishLocale = false)
    {
        var first = defaultOrganization ?? OrganizationId.From(Guid.CreateVersion7());
        await using var app = await source.OpenConnectionAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
        await IdentityMembershipFixture.ScopeAsync(app, transaction, tenant.Value);
        await IdentityMembershipFixture.LockAsync(app, transaction, tenant.Value, null, true);
        await IdentityMembershipFixture.ExecuteAsync(app, transaction, """
            INSERT INTO public.tenants(id,slug,display_name,status,created_at,created_by,row_version)
                VALUES(@tenant,@slug,'Content component fixture','Trial',clock_timestamp(),@actor,0);
            INSERT INTO public.organizations(id,tenant_id,slug,display_name,status,created_at,created_by,row_version)
                VALUES(@organization,@tenant,'main','Main','Active',clock_timestamp(),@actor,0);
            UPDATE public.tenants SET default_organization_id=@organization WHERE id=@tenant;
            """, ("tenant", tenant.Value), ("slug", "content-fixture-" + tenant.Value.ToString("N")),
            ("organization", first.Value), ("actor", UserId.SystemActor.Value));
        if (secondOrganization is { } second)
            await IdentityMembershipFixture.ExecuteAsync(app, transaction, """
                INSERT INTO public.organizations(id,tenant_id,slug,display_name,status,created_at,created_by,row_version)
                    VALUES(@organization,@tenant,'second','Second','Active',clock_timestamp(),@actor,0)
                """, ("tenant", tenant.Value), ("organization", second.Value), ("actor", UserId.SystemActor.Value));
        if (englishLocale)
            await IdentityMembershipFixture.ExecuteAsync(app, transaction, """
                INSERT INTO public.tenant_locales(tenant_id,locale,is_default,is_enabled,sort)
                    VALUES(@tenant,'en',true,true,0)
                """, ("tenant", tenant.Value));
        await transaction.CommitAsync();
    }
}
