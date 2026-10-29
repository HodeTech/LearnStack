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
    public const string AccountSiteOperation = "tenancy.account_site.read";
    public static PermissionDefinition PublicSite { get; } = new(PermissionScope.Tenant, "tenancy.public_site.read");
    public IEnumerable<PermissionRegistration> Definitions =>
        BoundedTenancyAdministrationPermissions.Definitions.Select(definition => new PermissionRegistration(definition, Assignable: true))
            .Append(new(PublicSite, Assignable: false));
    public IEnumerable<OperationPolicy> Policies =>
    [
        OperationPolicy.ForPublic<GetPublicSiteQuery>("tenancy.public_site.read", PublicSite),
        OperationPolicy.ForAccount<GetAccountSiteQuery>(AccountSiteOperation),
        OperationPolicy.ForProtected<ListOrganizationsQuery>("tenancy.organization.list", BoundedTenancyAdministrationPermissions.OrganizationAdministration),
        OperationPolicy.ForProtected<GetOrganizationDetailQuery>("tenancy.organization.read", BoundedTenancyAdministrationPermissions.OrganizationAdministration),
        OperationPolicy.ForProtected<ReadOrganizationReceiptQuery>("tenancy.organization.read_receipt", BoundedTenancyAdministrationPermissions.OrganizationAdministration),
        OperationPolicy.ForProtected<ReadDefaultOrganizationReceiptQuery>("tenancy.organization.read_default_receipt", BoundedTenancyAdministrationPermissions.OrganizationAdministration),
        OperationPolicy.ForProtected<CreateManagedOrganizationCommand>("tenancy.organization.create", BoundedTenancyAdministrationPermissions.OrganizationAdministration, requiresOrglessWrite: true),
        OperationPolicy.ForProtected<RenameOrganizationCommand>("tenancy.organization.rename", BoundedTenancyAdministrationPermissions.OrganizationAdministration, requiresOrglessWrite: true),
        OperationPolicy.ForProtected<ArchiveOrganizationCommand>("tenancy.organization.archive", BoundedTenancyAdministrationPermissions.OrganizationAdministration, requiresOrglessWrite: true),
        OperationPolicy.ForProtected<SelectDefaultOrganizationCommand>("tenancy.organization.select_default", BoundedTenancyAdministrationPermissions.OrganizationAdministration, requiresOrglessWrite: true),
        OperationPolicy.ForProtected<AddManagedTenantLocaleCommand>("tenancy.locale.add", BoundedTenancyAdministrationPermissions.LocaleWrite, requiresOrglessWrite: true),
        OperationPolicy.ForProtected<SetManagedDefaultTenantLocaleCommand>("tenancy.locale.set_default", BoundedTenancyAdministrationPermissions.LocaleWrite, requiresOrglessWrite: true),
        OperationPolicy.ForProtected<SetTenantLocaleEnabledCommand>("tenancy.locale.set_enabled", BoundedTenancyAdministrationPermissions.LocaleWrite, requiresOrglessWrite: true),
        OperationPolicy.ForProtected<RemoveTenantLocaleCommand>("tenancy.locale.remove", BoundedTenancyAdministrationPermissions.LocaleWrite, requiresOrglessWrite: true),
        OperationPolicy.ForProtected<SetManagedTenantBrandingCommand>("tenancy.setting.write", BoundedTenancyAdministrationPermissions.SettingWrite, requiresOrglessWrite: true),
        OperationPolicy.ForTrusted<AdoptSeedAdministrationOriginCommand>("tenancy.seed.adopt_admin_origin", AuthorizationSourceKind.Seed),
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
