using System.Text.RegularExpressions;
using FluentAssertions;
using LearnStack.Application.Authorization;
using LearnStack.Modules.Customization.Application.Authorization;
using LearnStack.Modules.Education.Application.Authorization;
using LearnStack.Modules.Identity.Application;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Tenancy.Application.Authorization;
using LearnStack.SharedKernel.Permissions;
using Mono.Cecil;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>Actual module declarations are complete and finite; their registration grants no authority.</summary>
public sealed class OperationPermissionConventionTests
{
    [Fact]
    public void Permission_Definitions_DeclareScope()
    {
        var catalog = Catalog();
        catalog.Definitions.Should().HaveCount(8);
        catalog.Definitions.Should().OnlyContain(registration => Enum.IsDefined(registration.Definition.Scope)
            && registration.Definition.Scope != PermissionScope.Platform);
        catalog.Definitions.Where(registration => registration.Assignable).Select(registration => registration.Definition)
            .Should().BeEquivalentTo(IdentityFoundationManifest.Version1.Definitions);
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
        foreach (var policy in catalog.All)
        {
            if (policy.Kind == OperationPolicyKind.Protected)
            {
                policy.RequestType.Should().Be<ListMembershipsQuery>("only the admitted directory is staged in Step 3");
                policy.RequiredPermission.Should().Be(IdentityOperationPolicySource.MembershipAdministration);
                policy.ReadScope.Should().Be(OperationReadScope.Tenant, "the tenant-wide Membership read is an explicit read exception, never org write scope");
                policy.RequiresOrglessWrite.Should().BeFalse();
            }
            else
            {
                policy.ReadScope.Should().Be(OperationReadScope.Scoped, "public, AccountSite and finite owners cannot mint tenant-read authority");
                if (policy.Kind == OperationPolicyKind.PublicRead)
                {
                    policy.RequiredPermission!.Scope.Should().Be(PermissionScope.Tenant);
                    catalog.TryGetDefinition(policy.RequiredPermission.Scope, policy.RequiredPermission.Key, out var registration).Should().BeTrue();
                    registration.Assignable.Should().BeFalse();
                }
                else policy.RequiredPermission.Should().BeNull("finite source authority and AccountSite coverage are not stored grants");
            }
        }
        Action dirty = () => OperationPolicy.ForProtected<ListMembershipsQuery>("identity.control.read",
            new(PermissionScope.Organization, "identity.membership.admin"), OperationReadScope.Tenant);
        dirty.Should().Throw<ArgumentException>("an Organization pair must never mint the Tenant read exception");
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
