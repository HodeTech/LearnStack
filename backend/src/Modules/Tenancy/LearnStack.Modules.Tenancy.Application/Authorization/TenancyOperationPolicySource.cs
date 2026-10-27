using LearnStack.Modules.Tenancy.Application.Contracts.Account;
using LearnStack.Modules.Tenancy.Application.Contracts.Branding;
using LearnStack.Modules.Tenancy.Application.Contracts.Locales;
using LearnStack.Modules.Tenancy.Application.Contracts.PublicReads;
using LearnStack.Modules.Tenancy.Application.Contracts.Seeding;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Permissions;

namespace LearnStack.Modules.Tenancy.Application.Authorization;

public sealed class TenancyOperationPolicySource : IOperationPolicySource
{
    public static PermissionDefinition PublicSite { get; } = new(PermissionScope.Tenant, "tenancy.public_site.read");
    public IEnumerable<PermissionRegistration> Definitions => [new(PublicSite, Assignable: false)];
    public IEnumerable<OperationPolicy> Policies =>
    [
        OperationPolicy.ForPublic<GetPublicSiteQuery>("tenancy.public_site.read", PublicSite),
        OperationPolicy.ForAccount<GetAccountSiteQuery>("tenancy.account_site.read"),
        OperationPolicy.ForTrusted<GetTenantSeedStateQuery>("tenancy.seed.read_tenant", AuthorizationSourceKind.Seed),
        OperationPolicy.ForTrusted<GetOrganizationSeedStateQuery>("tenancy.seed.read_organization", AuthorizationSourceKind.Seed),
        OperationPolicy.ForTrusted<GetHostMappingSeedStateQuery>("tenancy.seed.read_host", AuthorizationSourceKind.Seed),
        OperationPolicy.ForTrusted<GetSettingSeedStateQuery>("tenancy.seed.read_setting", AuthorizationSourceKind.Seed),
        OperationPolicy.ForTrusted<ProvisionTenantCommand>("tenancy.seed.provision_tenant", AuthorizationSourceKind.Seed),
        OperationPolicy.ForTrusted<CreateOrganizationCommand>("tenancy.seed.create_organization", AuthorizationSourceKind.Seed),
        OperationPolicy.ForTrusted<MapHostToTenantCommand>("tenancy.seed.map_host", AuthorizationSourceKind.Seed),
        OperationPolicy.ForTrusted<AddTenantLocaleCommand>("tenancy.seed.add_locale", AuthorizationSourceKind.Seed),
        OperationPolicy.ForTrusted<SetDefaultTenantLocaleCommand>("tenancy.seed.set_locale", AuthorizationSourceKind.Seed),
        OperationPolicy.ForTrusted<SetTenantBrandingCommand>("tenancy.seed.set_branding", AuthorizationSourceKind.Seed),
    ];
}
