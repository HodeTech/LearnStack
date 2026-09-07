using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using NetArchTest.Rules;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// Mechanical dependency-direction checks. Standards 01 § Dependency Direction and
/// Module Boundaries forbid Domain → Application/Infrastructure and cross-module
/// Domain references. These tests are the enforceable backstop.
///
/// Phase-01 scope: Domain layer only. The Application + Infrastructure layer rules
/// (Standards 01 § Architecture Tests "Module dependency direction" full matrix)
/// land in Phase 02a once those layers carry real types.
/// TODO(2026-05-19, @platform): extend coverage to Application + Infrastructure
/// layers — Application.X must not depend on Module.Y.Domain / Module.Y.Infrastructure
/// for X ≠ Y; Infrastructure.X must not depend on any other Module.Y.* layer;
/// core Application must not depend on Infrastructure.
/// </summary>
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
    /// Amendment 4</see> settles which rule yields, and this is what holds it.
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
        var domainAssembly = LoadModuleAssembly(moduleName, layer: "Domain");

        // Prefix matches — `LearnStack.Application` also catches
        // `LearnStack.Application.Contracts`. That is intentional: Domain may not
        // reference either, so the broader match is correct.
        var forbiddenPrefixes = new[]
        {
            "LearnStack.Application",
            "LearnStack.Infrastructure",
            "LearnStack.Modules.{0}.Application",
            "LearnStack.Modules.{0}.Infrastructure",
        };

        foreach (var prefixTemplate in forbiddenPrefixes)
        {
            var prefix = string.Format(CultureInfo.InvariantCulture, prefixTemplate, moduleName);

            var result = Types.InAssembly(domainAssembly)
                .Should()
                .NotHaveDependencyOn(prefix)
                .GetResult();

            result.IsSuccessful.Should().BeTrue(
                $"Module {moduleName}.Domain references {prefix}. " +
                "Domain may only reference SharedKernel (Standards 01 § Dependency Direction).");
        }
    }

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

    private static Assembly LoadModuleAssembly(string moduleName, string layer)
    {
        var assemblyName = $"LearnStack.Modules.{moduleName}.{layer}";

        try
        {
            return Assembly.Load(assemblyName);
        }
        catch (FileNotFoundException ex)
        {
            throw new InvalidOperationException(
                $"Could not load assembly {assemblyName}. " +
                "Confirm the module project is referenced by LearnStack.Tests.Architecture.csproj.",
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
