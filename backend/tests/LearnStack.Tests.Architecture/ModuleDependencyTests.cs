using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using NetArchTest.Rules;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// Mechanical dependency-direction checks: the matrix
/// <see href="../../../docs/standards/01-architecture-standards.md">Standards 01 § Dependency
/// Direction</see> draws, one rule per layer, catalogued in
/// <see href="../../../docs/standards/21-architecture-tests-catalogue.md">Standards 21
/// § Repository layout and module boundaries</see>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Allow-lists, not deny-lists.</b> Each layer names the LearnStack assemblies it may
/// reference and fails on any other, so a module added next year is covered the day it
/// exists — a list of forbidden edges would have to be edited to include it, and the
/// edit is the step that gets forgotten.
/// </para>
/// <para>
/// <b>Two legs per rule.</b> The IL leg reads the assembly's references, which are the
/// assemblies it actually uses. The project-file leg reads its <c>ProjectReference</c>s,
/// which are what it could start using with no further edit — an unused reference
/// compiles to nothing, so only the project file shows it.
/// </para>
/// </remarks>
public sealed class ModuleDependencyTests
{
    [Theory]
    [MemberData(nameof(EveryModule))]
    public void ModuleDomain_DoesNotDependOn_OtherModuleDomain(string moduleName)
    {
        var domainAssembly = LoadModuleAssembly(moduleName, layer: "Domain");

        foreach (var other in Modules.Names)
        {
            if (other == moduleName)
            {
                continue;
            }

            var result = Types.InAssembly(domainAssembly)
                .Should()
                .NotHaveDependencyOn($"LearnStack.Modules.{other}.Domain")
                .GetResult();

            result.IsSuccessful.Should().BeTrue(
                $"Module {moduleName}.Domain references {other}.Domain. " +
                "Cross-module Domain references are forbidden — talk through Application.Contracts or integration events.");
        }
    }

    /// <summary>
    /// A command contract names no module's <c>Domain</c> — not another module's,
    /// and not its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the rule that makes a <c>Guid</c> in a contract correct rather than
    /// sloppy. <c>Backend Coding Standards § Naming</c> says never to expose a raw
    /// <c>Guid</c> on a public surface, and a contract that named
    /// <c>TenantContentTypeId</c> would obey it by putting
    /// <c>Customization.Domain</c> into the IL of every module that sends the
    /// command — the forbidden <c>Module A → Module B.Domain</c> edge, reached
    /// through the one assembly whose whole purpose is to be referenced widely.
    /// <see href="../../../docs/decisions/0023-strongly-typed-id-source-generator.md">ADR-0023
    /// Amendment 8</see> settles which rule yields, and this is what holds it.
    /// </para>
    /// <para>
    /// A <c>SharedKernel</c> identifier — <c>TenantId</c>, <c>OrganizationId</c>,
    /// <c>UserId</c> — stays typed in a contract, because naming it creates no such
    /// edge. The rule is about the assembly the type lives in, which is exactly
    /// what this measures.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(EveryModule))]
    public void ModuleContracts_DoNotDependOn_AnyModuleDomain(string moduleName)
    {
        var contractsAssembly = LoadModuleAssembly(moduleName, layer: "Application.Contracts");

        foreach (var other in Modules.Names)
        {
            var result = Types.InAssembly(contractsAssembly)
                .Should()
                .NotHaveDependencyOn($"LearnStack.Modules.{other}.Domain")
                .GetResult();

            result.IsSuccessful.Should().BeTrue(
                $"Module {moduleName}.Application.Contracts references {other}.Domain. "
                + "A contract is the cross-module surface, so a Domain type named here "
                + "reaches every sender: "
                + string.Join(", ", result.FailingTypeNames ?? []));
        }

        // The second leg, and it is not redundant: NetArchTest (Mono.Cecil) walks IL
        // TypeRefs, so it sees a Domain type USED and not a project reference that
        // merely exists — Meta_NetArchTest_DetectsAPlantedViolation says as much in
        // as many words. An unused reference is one edit away from the first use, and
        // it exports the assembly to every consumer of the contracts besides. This is
        // the leg ADR-0023 Amendment 8 rests on.
        ProjectReferencesOf(moduleName, "Application.Contracts")
            .Where(reference => reference.EndsWith(".Domain.csproj", StringComparison.Ordinal))
            .Should().BeEmpty(
                $"{moduleName}.Application.Contracts must not reference any Domain project — "
                + "a module-local identifier crosses a contract as Guid precisely so that "
                + "reference never has to exist (ADR-0023 Amendment 8)");
    }

    /// <summary>Every <c>ProjectReference</c> path one module project declares.</summary>
    private static List<string> ProjectReferencesOf(string moduleName, string layer)
    {
        var path = Path.Combine(
            RepositoryPaths.BackendSrc(),
            "Modules",
            moduleName,
            $"LearnStack.Modules.{moduleName}.{layer}",
            $"LearnStack.Modules.{moduleName}.{layer}.csproj");

        File.Exists(path).Should().BeTrue(
            $"{Path.GetFileName(path)} is where the module's references are declared — "
            + "a renamed or moved project silently empties this leg");

        return Regex.Matches(File.ReadAllText(path), @"ProjectReference\s+Include=""(?<path>[^""]+)""")
            .Select(match => match.Groups["path"].Value.Replace('\\', '/'))
            .ToList();
    }

    [Theory]
    [MemberData(nameof(EveryModule))]
    public void ModuleDomain_DoesNotDependOn_AnyApplicationOrInfrastructure(string moduleName)
    {
        // Its own module's Application and Infrastructure were the only ones checked until
        // Packet 10: another module's reached no rule at all. The allow-list closes both at
        // once, because a Domain references SharedKernel and nothing else.
        AssertReferencesOnly(moduleName, "Domain");
    }

    [Theory]
    [MemberData(nameof(EveryModule))]
    public void ModuleApplication_References_Only_Its_Own_Layers_And_Other_Contracts(string moduleName)
    {
        // Another module's Domain or Application is a cross-module call none of ADR-0010's
        // four mechanisms sanctions; its own Infrastructure is the composition root's to
        // wire in.
        AssertReferencesOnly(moduleName, "Application");
    }

    [Theory]
    [MemberData(nameof(EveryModule))]
    public void ModuleInfrastructure_References_Only_Its_Own_Layers_And_Core_Infrastructure(string moduleName)
    {
        // Core LearnStack.Infrastructure is the shared persistence seam every tenant-owned
        // module derives from (TenantScopedDbContext); any other module, and any other core
        // assembly, is a shortcut the module could not be extracted without undoing.
        AssertReferencesOnly(moduleName, "Infrastructure");
    }

    [Theory]
    [MemberData(nameof(EveryModule))]
    public void ModuleContracts_Reference_Only_SharedKernel(string moduleName)
    {
        // Wider than ModuleContracts_DoNotDependOn_AnyModuleDomain, which bans a Domain only.
        // A contract is referenced by every sender: its own Application would export the
        // handler assembly to all of them, and another module's contracts would chain two
        // modules' surfaces together.
        AssertReferencesOnly(moduleName, "Application.Contracts");
    }

    [Fact]
    public void CoreApplication_DoesNotDependOn_Any_Infrastructure_Or_Module()
    {
        // The pipeline behaviors live here. An edge to an Infrastructure would make the
        // kernel know an implementation; an edge to a module, the kernel know a module —
        // TransactionBehavior_Does_Not_Reference_A_Module_Assembly holds one behavior to
        // that, and this holds the assembly every behavior lives in.
        const string Core = "LearnStack.Application";

        LearnStackReferencesOf(LoadAssembly(Core)).Where(CoreApplicationMayNotReference).Should().BeEmpty(
            $"{Core} uses no Infrastructure and no module (Standards 01 § Dependency Direction)");

        ProjectReferences(Path.Combine(RepositoryPaths.BackendSrc(), Core, $"{Core}.csproj"))
            .Where(CoreApplicationMayNotReference).Should().BeEmpty(
                $"{Core}'s project file declares no Infrastructure and no module either");
    }

    /// <summary>
    /// The two families the core <c>Application</c> may not reference: any
    /// <c>LearnStack.Infrastructure*</c> and any module.
    /// </summary>
    private static bool CoreApplicationMayNotReference(string reference) =>
        reference.StartsWith("LearnStack.Infrastructure", StringComparison.Ordinal)
        || reference.StartsWith("LearnStack.Modules.", StringComparison.Ordinal);

    [Fact]
    public void The_Dependency_Matrix_Can_Actually_Fail()
    {
        // Every real module passes, so the rules above pass whether the allow-lists work or
        // not. Each layer is fed one reference it may have and one it may not, and must
        // report exactly the second — the edges Standards 01 names as forbidden.
        Disallowed("Tenancy", "Domain", ["LearnStack.SharedKernel", "LearnStack.Modules.Audit.Application"])
            .Should().Equal("LearnStack.Modules.Audit.Application");
        Disallowed("Tenancy", "Application", ["LearnStack.Modules.Audit.Application.Contracts", "LearnStack.Modules.Audit.Domain"])
            .Should().Equal("LearnStack.Modules.Audit.Domain");
        Disallowed("Tenancy", "Application", ["LearnStack.Modules.Tenancy.Domain", "LearnStack.Modules.Tenancy.Infrastructure"])
            .Should().Equal("LearnStack.Modules.Tenancy.Infrastructure");
        Disallowed("Tenancy", "Infrastructure", ["LearnStack.Infrastructure", "LearnStack.Modules.Audit.Application.Contracts"])
            .Should().Equal("LearnStack.Modules.Audit.Application.Contracts");
        Disallowed("Tenancy", "Infrastructure", ["LearnStack.Modules.Tenancy.Application", "LearnStack.Infrastructure.Audit"])
            .Should().Equal("LearnStack.Infrastructure.Audit");
        Disallowed("Tenancy", "Application.Contracts", ["LearnStack.SharedKernel", "LearnStack.Modules.Tenancy.Application"])
            .Should().Equal("LearnStack.Modules.Tenancy.Application");

        // The core Application's rule names what it forbids rather than what it allows, so it
        // is fed one of each family and the kernel it may reference.
        string[] coreReferences =
            ["LearnStack.SharedKernel", "LearnStack.Infrastructure.Audit", "LearnStack.Modules.Tenancy.Application.Contracts"];
        coreReferences.Where(CoreApplicationMayNotReference)
            .Should().Equal("LearnStack.Infrastructure.Audit", "LearnStack.Modules.Tenancy.Application.Contracts");

        // And the project-file parser reads both separators, or the Windows-style paths
        // every project here uses would yield no names and the leg would pass on nothing.
        ReferenceNames([@"..\..\LearnStack.SharedKernel\LearnStack.SharedKernel.csproj", "../Other/LearnStack.Modules.Audit.Domain.csproj"])
            .Should().Equal("LearnStack.SharedKernel", "LearnStack.Modules.Audit.Domain");
    }

    [Fact]
    public void Domain_Does_Not_Depend_On_Microsoft_EntityFrameworkCore_Except_Vogen_Emitted_Converters()
    {
        // Architecture Standards § Build-time-only exceptions sanctions the EF Core reference
        // for the converters Vogen emits beside each strongly-typed id, and for nothing else.
        // The reference is already there, so without this the first
        // `using Microsoft.EntityFrameworkCore;` in an aggregate compiles.
        var dependents = DomainAssemblies()
            .SelectMany(assembly => Types.InAssembly(assembly)
                .That().HaveDependencyOn("Microsoft.EntityFrameworkCore")
                .GetTypes())
            .ToList();

        dependents.Should().Contain(type => IsVogenEmittedConverter(type),
            "the exception is exercised — every strongly-typed id carries an emitted converter, "
            + "and a rule whose exception matched nothing could not tell one from a leak");

        dependents.Where(type => !IsVogenEmittedConverter(type))
            .Select(type => type.FullName)
            .Should().BeEmpty(
                "only a converter Vogen emits may name EF Core in SharedKernel or a module Domain "
                + "(Architecture Standards § Build-time-only exceptions)");
    }

    [Fact]
    public void The_Domain_EF_Core_Rule_Can_Actually_Fail()
    {
        // No Domain type names EF Core by hand, so the rule above passes whether its
        // predicate works or not. This assembly does — its model probes derive DbContext —
        // and they must be reported, while a real emitted converter must not.
        var handWritten = Types.InAssembly(typeof(ModuleDependencyTests).Assembly)
            .That().HaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetTypes()
            .ToList();

        handWritten.Should().NotBeEmpty().And.NotContain(type => IsVogenEmittedConverter(type));

        DomainAssemblies()
            .SelectMany(assembly => assembly.GetTypes())
            .Where(IsVogenEmittedConverter)
            .Should().NotBeEmpty("the exception recognises what Vogen actually emits");

        // A name is not enough: both shapes borrowed by a type that is not a value object.
        IsVogenEmittedConverter(typeof(Probes.NotAnId.EfCoreValueConverter)).Should().BeFalse(
            "a converter nested in a type that is not a value object is hand-written");
        IsVogenEmittedConverter(typeof(Probes.__NotAnIdEfCoreExtensions)).Should().BeFalse(
            "an extensions class named for a type that is not a value object is hand-written");
    }

    /// <summary>SharedKernel and every module's Domain — the assemblies the exception covers.</summary>
    private static IEnumerable<Assembly> DomainAssemblies() =>
        Modules.Names.Select(module => LoadModuleAssembly(module, "Domain"))
            .Prepend(LoadAssembly("LearnStack.SharedKernel"));

    /// <summary>
    /// A type Vogen emitted for EF Core: the converter or comparer nested in a
    /// <c>[ValueObject]</c> type, or the <c>__&lt;Id&gt;EfCoreExtensions</c> class it emits
    /// beside that type.
    /// </summary>
    /// <remarks>
    /// Both shapes are pinned to a real value object — the owner, or the type the extension
    /// class is named for, in the same namespace — so a hand-written class cannot pass by
    /// borrowing the name alone.
    /// </remarks>
    private static bool IsVogenEmittedConverter(Type type)
    {
        if (type is { IsNested: true, DeclaringType: { } owner })
        {
            return type.Name.StartsWith("EfCoreValue", StringComparison.Ordinal) && IsValueObject(owner);
        }

        var extensions = Regex.Match(type.Name, "^__(?<id>[A-Za-z0-9]+)EfCoreExtensions$");

        return extensions.Success
            && type is { IsAbstract: true, IsSealed: true }
            && type.Assembly.GetType($"{type.Namespace}.{extensions.Groups["id"].Value}") is { } id
            && IsValueObject(id);
    }

    private static bool IsValueObject(Type type) =>
        type.GetCustomAttributes(inherit: false)
            .Any(attribute => attribute.GetType().Namespace == "Vogen"
                && attribute.GetType().Name.StartsWith("ValueObjectAttribute", StringComparison.Ordinal));

    /// <summary>
    /// The LearnStack assemblies a module layer may reference, per Standards 01 § Dependency
    /// Direction.
    /// </summary>
    private static HashSet<string> AllowedReferences(string moduleName, string layer)
    {
        string Own(string ownLayer) => $"LearnStack.Modules.{moduleName}.{ownLayer}";

        var allowed = new HashSet<string>(StringComparer.Ordinal) { "LearnStack.SharedKernel" };

        switch (layer)
        {
            case "Domain":
            case "Application.Contracts":
                break;
            case "Application":
                allowed.Add(Own("Domain"));
                allowed.Add(Own("Application.Contracts"));
                allowed.UnionWith(Modules.Names
                    .Where(other => other != moduleName)
                    .Select(other => $"LearnStack.Modules.{other}.Application.Contracts"));
                break;
            case "Infrastructure":
                allowed.Add("LearnStack.Infrastructure");
                allowed.Add(Own("Domain"));
                allowed.Add(Own("Application"));
                allowed.Add(Own("Application.Contracts"));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(layer), layer, "not a module layer");
        }

        return allowed;
    }

    /// <summary>The references a layer holds that its allow-list does not name.</summary>
    private static List<string> Disallowed(string moduleName, string layer, IEnumerable<string> references)
    {
        var allowed = AllowedReferences(moduleName, layer);

        return [.. references
            .Where(reference => reference.StartsWith("LearnStack.", StringComparison.Ordinal))
            .Where(reference => !allowed.Contains(reference))
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Asserts both legs for one module layer: the assemblies its IL references, and the
    /// projects its file declares — the analyzer project aside, which is not a runtime
    /// reference.
    /// </summary>
    private static void AssertReferencesOnly(string moduleName, string layer)
    {
        var assembly = LoadModuleAssembly(moduleName, layer);

        Disallowed(moduleName, layer, LearnStackReferencesOf(assembly)).Should().BeEmpty(
            $"LearnStack.Modules.{moduleName}.{layer} references only what Standards 01 "
            + "§ Dependency Direction allows it");

        Disallowed(moduleName, layer, ReferenceNames(ProjectReferencesOf(moduleName, layer))
                .Where(name => name != "LearnStack.Analyzers"))
            .Should().BeEmpty(
                $"LearnStack.Modules.{moduleName}.{layer}.csproj declares only what it may use — "
                + "an unused reference compiles to nothing and is one edit from the first use");
    }

    /// <summary>The LearnStack assemblies an assembly's IL references.</summary>
    private static IEnumerable<string> LearnStackReferencesOf(Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name!)
            .Where(name => name.StartsWith("LearnStack.", StringComparison.Ordinal));

    /// <summary>The project names a project file references.</summary>
    private static List<string> ProjectReferences(string projectPath)
    {
        File.Exists(projectPath).Should().BeTrue(
            $"{Path.GetFileName(projectPath)} is where the references are declared — a renamed "
            + "or moved project silently empties this leg");

        return ReferenceNames(Regex
            .Matches(File.ReadAllText(projectPath), @"ProjectReference\s+Include=""(?<path>[^""]+)""")
            .Select(match => match.Groups["path"].Value));
    }

    /// <summary>Project names from <c>ProjectReference</c> paths, whichever separator they use.</summary>
    private static List<string> ReferenceNames(IEnumerable<string> paths) =>
        [.. paths.Select(path => Path.GetFileNameWithoutExtension(path.Replace('\\', '/').Split('/')[^1]))];

    /// <summary>
    /// Positive control: this test class plants an explicit IL-level dependency
    /// on `LearnStack.Domain.AssemblyMarker` via the `_plantedDependency` field.
    /// NetArchTest (Mono.Cecil) walks type/member/method-body TypeRefs, NOT
    /// csproj `ProjectReference` entries — an unused project reference produces
    /// no IL TypeRef, so the planted field is the actual hook.
    ///
    /// If this meta-test ever passes (i.e. NetArchTest reports the dependency
    /// as absent), every other architecture test in this project is vacuously
    /// green and the suite is meaningless. Keep this meta-test in perpetuity.
    /// </summary>
    [Fact(DisplayName = "(meta) NetArchTest detects a planted forbidden dependency")]
    public void Meta_NetArchTest_DetectsAPlantedViolation()
    {
        // Reference the planted type at runtime too, so a build-time dead-code
        // elimination pass (should one ever apply) cannot strip the IL TypeRef.
        _ = _plantedDependency;

        var testAssembly = typeof(ModuleDependencyTests).Assembly;

        var result = Types.InAssembly(testAssembly)
            .Should()
            .NotHaveDependencyOn("LearnStack.Domain")
            .GetResult();

        result.IsSuccessful.Should().BeFalse(
            "NetArchTest must detect the planted LearnStack.Domain.AssemblyMarker IL TypeRef " +
            "(see `_plantedDependency` on this class). A green result here means every other " +
            "architecture test in this project is vacuous and CI cannot be trusted.");
    }

    // Planted IL-level dependency for Meta_NetArchTest_DetectsAPlantedViolation.
    // DO NOT remove or replace with a string mention — only a real Type
    // reference produces the IL TypeRef NetArchTest scans.
    private static readonly Type _plantedDependency = typeof(LearnStack.Domain.AssemblyMarker);

    public static IEnumerable<object[]> EveryModule() =>
        Modules.Names.Select(m => new object[] { m });

    private static Assembly LoadModuleAssembly(string moduleName, string layer) =>
        LoadAssembly($"LearnStack.Modules.{moduleName}.{layer}");

    /// <summary>
    /// Loads an assembly by name, loudly: a layer this project cannot load is a layer no
    /// rule here checks, which must fail rather than pass on nothing.
    /// </summary>
    private static Assembly LoadAssembly(string assemblyName)
    {
        try
        {
            return Assembly.Load(assemblyName);
        }
        catch (FileNotFoundException ex)
        {
            throw new InvalidOperationException(
                $"Could not load assembly {assemblyName}. " +
                "Confirm the project is referenced by LearnStack.Tests.Architecture.csproj.",
                ex);
        }
    }

    [Fact]
    public void CoreInfrastructure_DoesNotDependOn_AnyModule()
    {
        // The one-way half of the edge Packet 7 step 3 introduced. A module's
        // Infrastructure may reference core Infrastructure — that is where
        // TenantScopedDbContext lives, and every tenant-owned module derives from
        // it. The reverse would close the loop: core Infrastructure is referenced
        // by every module, so a single edge back into one makes the whole graph
        // cyclic and makes that module impossible to extract.
        var result = Types
            .InAssembly(typeof(LearnStack.Infrastructure.Persistence.TenantQueryFilters).Assembly)
            .Should()
            .NotHaveDependencyOn("LearnStack.Modules")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "core Infrastructure must reference no module: "
            + string.Join(", ", result.FailingTypeNames ?? []));
    }
}
