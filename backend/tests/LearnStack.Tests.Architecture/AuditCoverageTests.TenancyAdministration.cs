using FluentAssertions;
using LearnStack.Modules.Tenancy.Application.Contracts.Branding;
using LearnStack.Modules.Tenancy.Application.Contracts.Locales;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Audit;

namespace LearnStack.Tests.Architecture;

public sealed partial class AuditCoverageTests
{
    private static void AssertTenancyAdministrationBindings()
    {
        (Type Request, string Operation, OperationType Kind, OperationClass Class, Type Root)[] expected =
        [
            (typeof(ListOrganizationsQuery), "tenancy.organization.list", OperationType.ReadSensitive, OperationClass.Must, typeof(Organization)),
            (typeof(GetOrganizationDetailQuery), "tenancy.organization.read", OperationType.ReadSensitive, OperationClass.Must, typeof(Organization)),
            (typeof(CreateManagedOrganizationCommand), "tenancy.organization.create", OperationType.Create, OperationClass.Must, typeof(Organization)),
            (typeof(RenameOrganizationCommand), "tenancy.organization.rename", OperationType.Update, OperationClass.Should, typeof(Organization)),
            (typeof(ArchiveOrganizationCommand), "tenancy.organization.archive", OperationType.Update, OperationClass.Must, typeof(Organization)),
            (typeof(SelectDefaultOrganizationCommand), "tenancy.organization.select_default", OperationType.Update, OperationClass.Must, typeof(Tenant)),
            (typeof(AddManagedTenantLocaleCommand), "tenancy.locale.write", OperationType.Update, OperationClass.Should, typeof(Tenant)),
            (typeof(SetManagedDefaultTenantLocaleCommand), "tenancy.locale.write", OperationType.Update, OperationClass.Should, typeof(Tenant)),
            (typeof(SetTenantLocaleEnabledCommand), "tenancy.locale.write", OperationType.Update, OperationClass.Should, typeof(Tenant)),
            (typeof(RemoveTenantLocaleCommand), "tenancy.locale.write", OperationType.Update, OperationClass.Should, typeof(Tenant)),
            (typeof(SetManagedTenantBrandingCommand), "tenancy.setting.write", OperationType.Update, OperationClass.Must, typeof(TenantSetting)),
        ];
        var catalog = Catalogue();
        foreach (var declaration in expected)
        {
            catalog.TryGet(declaration.Request, out var registration).Should().BeTrue();
            var entry = new AuditCatalogEntry("tenancy", declaration.Operation, declaration.Kind, declaration.Class, declaration.Root);
            BindingMatches(registration, entry).Should().BeTrue("each separate human request retains its exact event, tier and aggregate subject");
            BindingMatches(registration with { WritesNoRow = true }, entry).Should().BeFalse();
            BindingMatches(registration with { Entries = [] }, entry).Should().BeFalse();
            BindingMatches(registration with { Entries = [entry, entry] }, entry).Should().BeFalse();
            foreach (var dirty in new[]
            {
                entry with { ModuleName = "identity" }, entry with { Operation = "tenancy.control.write" },
                entry with { OperationType = OperationType.SecurityEvent },
                entry with { OperationClass = declaration.Class == OperationClass.Must ? OperationClass.Should : OperationClass.Must },
                entry with { EntityType = declaration.Root == typeof(Tenant) ? typeof(Organization) : typeof(Tenant) },
            }) BindingMatches(registration with { Entries = [dirty] }, entry).Should().BeFalse();
        }
    }

    private static bool BindingMatches(AuditRegistration registration, AuditCatalogEntry expected) =>
        !registration.WritesNoRow && registration.Entries.Count == 1 && registration.Entries[0] == expected;
}
