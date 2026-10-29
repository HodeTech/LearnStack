using System.Text.RegularExpressions;
using FluentAssertions;
using LearnStack.Application.Authorization;
using LearnStack.Modules.Customization.Application.Authorization;
using LearnStack.Modules.Education.Application.Authorization;
using LearnStack.Modules.Identity.Application;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Application.Contracts.Roles;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Tenancy.Application.Authorization;
using LearnStack.Modules.Tenancy.Application.Contracts.Branding;
using LearnStack.Modules.Tenancy.Application.Contracts.Locales;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Permissions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>Actual module declarations are complete and finite; their registration grants no authority.</summary>
public sealed partial class OperationPermissionConventionTests
{
    [Fact]
    public void Permission_Definitions_DeclareScope()
    {
        var catalog = Catalog();
        catalog.Definitions.Should().HaveCount(11);
        catalog.Definitions.Should().OnlyContain(registration => Enum.IsDefined(registration.Definition.Scope)
            && registration.Definition.Scope != PermissionScope.Platform);
        catalog.Definitions.Where(registration => registration.Assignable).Select(registration => registration.Definition)
            .Should().BeEquivalentTo(IdentityRoleRelease.Version2.Definitions);
        catalog.Definitions.Where(registration => !registration.Assignable).Select(registration => registration.Definition.Key)
            .Should().BeEquivalentTo(["tenancy.public_site.read", "education.public_course.read", "education.public_lesson.read"]);
        WithProduction(modules =>
        {
            RequestOffenders(modules, catalog).Should().BeEmpty("Fix: every actual production request needs one exact policy (ADR-0076)");
            var module = modules.Single(item => item.Assembly.Name.Name == "LearnStack.Modules.Identity.Application.Contracts");
            var actual = module.GetType(typeof(ListMembershipsQuery).FullName);
            var contract = actual.Interfaces.Single(item => item.InterfaceType is GenericInstanceType generic
                && generic.ElementType.FullName == "MediatR.IRequest`1");
            var planted = new TypeDefinition("PermissionControl", "UnclassifiedQuery", TypeAttributes.NotPublic | TypeAttributes.Sealed, module.TypeSystem.Object);
            planted.Interfaces.Add(new InterfaceImplementation(contract.InterfaceType));
            module.Types.Add(planted);
            try { RequestOffenders(modules, catalog).Should().Contain(planted.FullName); }
            finally { module.Types.Remove(planted); }
            actual.Interfaces.Remove(contract);
            try { RequestOffenders(modules, catalog).Should().Contain(actual.FullName, "a declaration whose actual request disappears must fail too"); }
            finally { actual.Interfaces.Add(contract); }
            var unexpectedSource = new TypeDefinition("PermissionControl", "UnregisteredSource", TypeAttributes.NotPublic | TypeAttributes.Sealed, module.TypeSystem.Object);
            unexpectedSource.Interfaces.Add(new InterfaceImplementation(module.ImportReference(typeof(IOperationPolicySource))));
            module.Types.Add(unexpectedSource);
            try { RequestOffenders(modules, catalog).Should().Contain(unexpectedSource.FullName); }
            finally { module.Types.Remove(unexpectedSource); }
            var alternateCatalog = new TypeDefinition("PermissionControl", "AlternateCatalog", TypeAttributes.NotPublic | TypeAttributes.Sealed, module.TypeSystem.Object);
            alternateCatalog.Interfaces.Add(new InterfaceImplementation(module.ImportReference(typeof(IOperationPolicyCatalog))));
            module.Types.Add(alternateCatalog);
            try { RequestOffenders(modules, catalog).Should().Contain(alternateCatalog.FullName); }
            finally { module.Types.Remove(alternateCatalog); }
            RequestOffenders(modules, catalog).Should().BeEmpty("both compiled subjects are restored");
            AssertOwnMembershipReadFactory(modules);
        });
    }

    [Fact]
    public void Permission_Keys_Match_Convention()
    {
        var keys = Catalog().Definitions.Select(registration => registration.Definition.Key).Distinct(StringComparer.Ordinal).ToArray();
        keys.Should().NotBeEmpty();
        foreach (var key in keys) ValidKey(key).Should().BeTrue("Fix: scoped definitions use the closed three-segment convention: " + key);
        foreach (var dirty in new[] { "identity.membership.*", "identity.membership.publish", "Identity.membership.read", "identity..read", "identity.membership.read.extra" })
            ValidKey(dirty).Should().BeFalse();
    }

    [Fact]
    public void Permission_Scope_Matches_Resource_Scope()
    {
        var catalog = Catalog();
        var membershipAdmin = new PermissionDefinition(PermissionScope.Tenant, IdentityFoundationManifest.MembershipAdmin);
        var organizationAdmin = new PermissionDefinition(PermissionScope.Organization, IdentityFoundationManifest.MembershipAdmin);
        var profileWrite = new PermissionDefinition(PermissionScope.Tenant, IdentityFoundationManifest.ProfileWrite);
        var organizationProfileWrite = new PermissionDefinition(PermissionScope.Organization, IdentityFoundationManifest.ProfileWrite);
        var roleAdmin = new PermissionDefinition(PermissionScope.Tenant, IdentityFoundationManifest.RoleAdmin);
        (Type Request, bool Orgless, OperationPolicyClause[] Clauses)[] expected =
        [
            (typeof(ListMembershipsQuery), false, [new([membershipAdmin], OperationReadScope.Tenant)]),
            (typeof(ListOrganizationMembershipsQuery), false, [new([membershipAdmin]), new([organizationAdmin])]),
            (typeof(ListRolesQuery), false, [new([roleAdmin])]),
            (typeof(GetRoleDetailQuery), false, [new([roleAdmin])]),
            (typeof(GetPermissionRegistryQuery), false, [new([roleAdmin])]),
            (typeof(ListOrganizationsQuery), false, [new([BoundedTenancyAdministrationPermissions.OrganizationAdministration])]),
            (typeof(GetOrganizationDetailQuery), false, [new([BoundedTenancyAdministrationPermissions.OrganizationAdministration])]),
            (typeof(ReadOrganizationReceiptQuery), false, [new([BoundedTenancyAdministrationPermissions.OrganizationAdministration])]),
            (typeof(ReadDefaultOrganizationReceiptQuery), false, [new([BoundedTenancyAdministrationPermissions.OrganizationAdministration])]),
            (typeof(CreateManagedOrganizationCommand), true, [new([BoundedTenancyAdministrationPermissions.OrganizationAdministration])]),
            (typeof(RenameOrganizationCommand), true, [new([BoundedTenancyAdministrationPermissions.OrganizationAdministration])]),
            (typeof(ArchiveOrganizationCommand), true, [new([BoundedTenancyAdministrationPermissions.OrganizationAdministration])]),
            (typeof(SelectDefaultOrganizationCommand), true, [new([BoundedTenancyAdministrationPermissions.OrganizationAdministration])]),
            (typeof(AddManagedTenantLocaleCommand), true, [new([BoundedTenancyAdministrationPermissions.LocaleWrite])]),
            (typeof(SetManagedDefaultTenantLocaleCommand), true, [new([BoundedTenancyAdministrationPermissions.LocaleWrite])]),
            (typeof(SetTenantLocaleEnabledCommand), true, [new([BoundedTenancyAdministrationPermissions.LocaleWrite])]),
            (typeof(RemoveTenantLocaleCommand), true, [new([BoundedTenancyAdministrationPermissions.LocaleWrite])]),
            (typeof(SetManagedTenantBrandingCommand), true, [new([BoundedTenancyAdministrationPermissions.SettingWrite])]),
            (typeof(ChangeRoleGrantsCommand), true, [new([roleAdmin])]),
            (typeof(AdoptRoleGrantManifestCommand), true, [new([roleAdmin])]),
            (typeof(ReadRoleReceiptQuery), false, [new([roleAdmin])]),
            (typeof(ReadMembershipReceiptQuery), false, [new([membershipAdmin]), new([organizationAdmin])]),
            (typeof(ReadMemberProfileReceiptQuery), false, [new([membershipAdmin]), new([organizationAdmin])]),
            (typeof(GetMembershipDetailQuery), false,
                [new([membershipAdmin], OperationReadScope.Tenant), new([organizationAdmin])]),
            (typeof(SetMembershipProfileCommand), false,
                [new([membershipAdmin, profileWrite]), new([membershipAdmin, organizationProfileWrite]),
                    new([organizationAdmin, profileWrite]), new([organizationAdmin, organizationProfileWrite])]),
            (typeof(SetOwnMembershipProfileCommand), false, [new([profileWrite]), new([organizationProfileWrite])]),
            (typeof(ChangeMembershipStatusCommand), false, [new([membershipAdmin]), new([organizationAdmin])]),
            (typeof(ReplaceMembershipRoleAssignmentsCommand), false, [new([membershipAdmin]), new([organizationAdmin])]),
        ];
        catalog.All.Where(policy => policy.Kind == OperationPolicyKind.Protected).Select(policy => policy.RequestType)
            .Should().BeEquivalentTo(expected.Select(item => item.Request));
        catalog.All.Where(policy => policy.Kind == OperationPolicyKind.OwnMembershipRead).Select(policy => policy.RequestType)
            .Should().BeEquivalentTo([typeof(GetOwnMembershipProfileQuery), typeof(ReadOwnMembershipProfileReceiptQuery),
                typeof(GetOwnMembershipContextsQuery), typeof(GetOwnCapabilitiesQuery)],
                "exact own reads share coverage-only admission without sharing target lookup or audit mode");
        foreach (var policy in catalog.All)
        {
            if (policy.Kind == OperationPolicyKind.Protected)
            {
                var declaration = expected.Single(item => item.Request == policy.RequestType);
                policy.RequiresOrglessWrite.Should().Be(declaration.Orgless);
                policy.RequiresOrganization.Should().Be(declaration.Request == typeof(ListOrganizationMembershipsQuery));
                policy.AllowedSources.Should().BeEmpty();
                policy.TrustedEventHandlerType.Should().BeNull();
                policy.Alternatives.Should().HaveCount(declaration.Clauses.Length);
                for (var index = 0; index < declaration.Clauses.Length; index++)
                {
                    policy.Alternatives[index].RequiredPermissions.Should().BeEquivalentTo(declaration.Clauses[index].RequiredPermissions);
                    policy.Alternatives[index].ReadScope.Should().Be(declaration.Clauses[index].ReadScope,
                        "only the directory and member detail's exact Tenant clause mint tenant-wide read authority");
                }
                policy.ReadScope.Should().Be(declaration.Clauses.Length == 1
                    ? declaration.Clauses[0].ReadScope : OperationReadScope.Scoped);
                policy.RequiredPermission.Should().Be(declaration.Clauses is [{ RequiredPermissions.Length: 1 }]
                    ? declaration.Clauses[0].RequiredPermissions[0] : null);
            }
            else
            {
                policy.ReadScope.Should().Be(OperationReadScope.Scoped, "public, AccountSite, own Membership and finite owners cannot mint tenant-read authority");
                if (policy.Kind == OperationPolicyKind.PublicRead)
                {
                    policy.RequiredPermission!.Scope.Should().Be(PermissionScope.Tenant);
                    catalog.TryGetDefinition(policy.RequiredPermission.Scope, policy.RequiredPermission.Key, out var registration).Should().BeTrue();
                    registration.Assignable.Should().BeFalse();
                }
                else policy.RequiredPermission.Should().BeNull("finite source authority, AccountSite and own Membership coverage are not stored grants");
                if (policy.Kind == OperationPolicyKind.OwnMembershipRead)
                {
                    (Type Request, string Id)[] ownReads =
                    [
                        (typeof(GetOwnMembershipProfileQuery), "identity.membership_profile.read_own"),
                        (typeof(ReadOwnMembershipProfileReceiptQuery), "identity.membership_profile.read_own_receipt"),
                        (typeof(GetOwnMembershipContextsQuery), "identity.membership.list_own_contexts"),
                        (typeof(GetOwnCapabilitiesQuery), "identity.membership.read_own_capabilities"),
                    ];
                    policy.Id.Should().Be(ownReads.Single(read => read.Request == policy.RequestType).Id);
                    policy.Alternatives.Should().BeEmpty();
                    policy.AllowedSources.Should().BeEmpty();
                    policy.RequiresOrglessWrite.Should().BeFalse();
                    policy.RequiresOrganization.Should().BeFalse();
                    policy.TrustedEventHandlerType.Should().BeNull();
                }
            }
        }
        Action dirty = () => OperationPolicy.ForProtected<ListMembershipsQuery>("identity.control.read",
            new(PermissionScope.Organization, "identity.membership.admin"), OperationReadScope.Tenant);
        dirty.Should().Throw<ArgumentException>("an Organization pair must never mint the Tenant read exception");
    }

    private static void AssertOwnMembershipReadFactory(ModuleDefinition[] modules)
    {
        OwnMembershipReadFactoryOffenders(modules).Should().BeEmpty();
        var owner = modules.SelectMany(module => module.Types.SelectMany(Flatten))
            .Single(type => type.FullName == typeof(IdentityOperationPolicySource).FullName)
            .Methods.Single(method => method.Name == "get_Policies");
        var factories = owner.Body.Instructions.Where(instruction => instruction.Operand is MethodReference target
            && target.DeclaringType.FullName == typeof(OperationPolicy).FullName && target.Name == "ForOwnMembershipRead").ToArray();
        factories.Should().HaveCount(4);
        foreach (var factory in factories)
        {
            var factoryTarget = (GenericInstanceMethod)factory.Operand;
            var original = factoryTarget.GenericArguments[0];
            foreach (var replacement in new[] { typeof(ListMembershipsQuery), typeof(GetOwnMembershipProfileQuery), typeof(ReadOwnMembershipProfileReceiptQuery),
                         typeof(GetOwnMembershipContextsQuery), typeof(GetOwnCapabilitiesQuery) }
                         .Where(type => type.FullName != original.FullName))
            {
                factoryTarget.GenericArguments[0] = owner.Module.ImportReference(replacement);
                try { OwnMembershipReadFactoryOffenders(modules).Should().NotBeEmpty("a replacement request or duplicate exact type cannot inherit coverage"); }
                finally { factoryTarget.GenericArguments[0] = original; }
            }
            var index = owner.Body.Instructions.IndexOf(factory);
            owner.Body.Instructions.Remove(factory);
            try { OwnMembershipReadFactoryOffenders(modules).Should().NotBeEmpty("all exact own-read producers are required"); }
            finally { owner.Body.Instructions.Insert(index, factory); }
            foreach (var opcode in new[] { OpCodes.Call, OpCodes.Ldftn })
            {
                var duplicate = Instruction.Create(opcode, factoryTarget);
                owner.Body.Instructions.Add(duplicate);
                try { OwnMembershipReadFactoryOffenders(modules).Should().NotBeEmpty("duplicate factory calls and executable aliases do not broaden coverage"); }
                finally { owner.Body.Instructions.Remove(duplicate); }
            }
            var outsider = new MethodDefinition("OwnReadControl", MethodAttributes.Private | MethodAttributes.Static, owner.Module.TypeSystem.Void);
            owner.DeclaringType.Methods.Add(outsider);
            outsider.Body.Instructions.Add(Instruction.Create(OpCodes.Call, factoryTarget));
            owner.Body.Instructions.Remove(factory);
            try { OwnMembershipReadFactoryOffenders(modules).Should().NotBeEmpty("only the exact policy producer can call the factory, even within the owning type"); }
            finally
            {
                owner.Body.Instructions.Insert(index, factory);
                owner.DeclaringType.Methods.Remove(outsider);
            }
        }
        OwnMembershipReadFactoryOffenders(modules).Should().BeEmpty("restoring the exact producer restores the clean verdict");
    }

    private static SortedSet<string> OwnMembershipReadFactoryOffenders(ModuleDefinition[] modules)
    {
        var sites = modules.SelectMany(module => module.Types.SelectMany(Flatten)).SelectMany(type => type.Methods)
            .Where(method => method.HasBody).SelectMany(method => method.Body.Instructions.Select(instruction => (Method: method, Instruction: instruction)))
            .Where(site => site.Instruction.Operand is MethodReference target
                && target.DeclaringType.FullName == typeof(OperationPolicy).FullName && target.Name == "ForOwnMembershipRead").ToArray();
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        var expected = new[] { typeof(GetOwnMembershipProfileQuery).FullName!, typeof(ReadOwnMembershipProfileReceiptQuery).FullName!,
            typeof(GetOwnMembershipContextsQuery).FullName!, typeof(GetOwnCapabilitiesQuery).FullName! };
        var actual = sites.Select(site => site.Instruction.Operand is GenericInstanceMethod factory && factory.GenericArguments.Count == 1
            ? factory.GenericArguments[0].FullName : string.Empty).Order(StringComparer.Ordinal);
        if (!actual.SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            offenders.Add("Exactly the four distinct own Membership read factory calls are admitted");
        foreach (var site in sites)
            if (site.Method.DeclaringType.FullName != typeof(IdentityOperationPolicySource).FullName || site.Method.Name != "get_Policies"
                || site.Instruction.OpCode != OpCodes.Call || site.Instruction.Operand is not GenericInstanceMethod factory
                || factory.DeclaringType.Scope.Name != "LearnStack.SharedKernel" || factory.GenericArguments.Count != 1
                || !expected.Contains(factory.GenericArguments[0].FullName, StringComparer.Ordinal)
                || factory.GenericArguments[0].Scope.Name != "LearnStack.Modules.Identity.Application.Contracts")
                offenders.Add("Unexpected own Membership read factory: " + site.Method.FullName);
        return offenders;
    }

    private static bool ValidKey(string value) => Regex.IsMatch(value, @"\A[a-z][a-z0-9_]*\.[a-z][a-z0-9_]*\.(read|write|delete|admin)\z", RegexOptions.CultureInvariant);

    private static SortedSet<string> RequestOffenders(ModuleDefinition[] modules, OperationPolicyCatalog catalog)
    {
        var types = modules.SelectMany(module => module.Types.SelectMany(Flatten)).ToArray();
        var actual = types.Where(type => !type.IsAbstract
            && type.Interfaces.Any(contract => contract.InterfaceType is GenericInstanceType generic
                && generic.ElementType.FullName == "MediatR.IRequest`1"))
            .Select(type => type.FullName).ToHashSet(StringComparer.Ordinal);
        actual.SymmetricExceptWith(catalog.All.Select(policy => policy.RequestType.FullName!));
        var sources = types.Where(type => type.Interfaces.Any(contract => contract.InterfaceType.FullName == typeof(IOperationPolicySource).FullName))
            .Select(type => type.FullName).ToHashSet(StringComparer.Ordinal);
        sources.SymmetricExceptWith(new[] { typeof(TenancyOperationPolicySource), typeof(CustomizationOperationPolicySource),
            typeof(EducationOperationPolicySource), typeof(IdentityOperationPolicySource), typeof(BackgroundJobOperationPolicySource) }.Select(type => type.FullName!));
        actual.UnionWith(sources);
        var catalogs = types.Where(type => type.Interfaces.Any(contract => contract.InterfaceType.FullName == typeof(IOperationPolicyCatalog).FullName))
            .Select(type => type.FullName).ToHashSet(StringComparer.Ordinal);
        catalogs.SymmetricExceptWith([typeof(OperationPolicyCatalog).FullName!]);
        actual.UnionWith(catalogs);
        return new(actual, StringComparer.Ordinal);
    }

    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition type) => new[] { type }.Concat(type.NestedTypes.SelectMany(Flatten));
    private static OperationPolicyCatalog Catalog() => new([
        new TenancyOperationPolicySource(), new CustomizationOperationPolicySource(), new EducationOperationPolicySource(),
        new IdentityOperationPolicySource(), new BackgroundJobOperationPolicySource()]);
    private static void WithProduction(Action<ModuleDefinition[]> action)
    {
        var modules = ProductionAssemblies.All().Select(assembly => ModuleDefinition.ReadModule(assembly.Location)).ToArray();
        try { action(modules); }
        finally { foreach (var module in modules) module.Dispose(); }
    }
}
