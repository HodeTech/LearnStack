using System.Reflection;
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
    private static readonly string[] ModuleNames =
    [
        "Tenancy",
        "Identity",
        "Customization",
        "Audit",
        "Content",
        "Media",
        "Education",
    ];

    [Theory]
    [MemberData(nameof(EveryModule))]
    public void ModuleDomain_DoesNotDependOn_OtherModuleDomain(string moduleName)
    {
        var domainAssembly = LoadModuleAssembly(moduleName, layer: "Domain");

        foreach (var other in ModuleNames)
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
            var prefix = string.Format(prefixTemplate, moduleName);

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
    /// Positive control: the test assembly itself has a `ProjectReference` to
    /// `LearnStack.Domain` (see the .csproj). NetArchTest MUST detect that
    /// dependency — if it cannot, every other architecture test in this project
    /// is vacuously green and the suite is meaningless.
    ///
    /// While the module Domain projects are empty placeholders (Phase 01
    /// scaffolding), this meta-test is the only thing standing between a green
    /// CI signal and silent rule erosion. Keep it in perpetuity.
    /// </summary>
    [Fact(DisplayName = "(meta) NetArchTest detects a planted forbidden dependency")]
    public void Meta_NetArchTest_DetectsAPlantedViolation()
    {
        var testAssembly = typeof(ModuleDependencyTests).Assembly;

        var result = Types.InAssembly(testAssembly)
            .Should()
            .NotHaveDependencyOn("LearnStack.Domain")
            .GetResult();

        result.IsSuccessful.Should().BeFalse(
            "NetArchTest must detect the test assembly's intentional dependency on LearnStack.Domain " +
            "(via ProjectReference in LearnStack.Tests.Architecture.csproj). A green result here means " +
            "every other architecture test in this project is vacuous and CI cannot be trusted.");
    }

    public static IEnumerable<object[]> EveryModule() =>
        ModuleNames.Select(m => new object[] { m });

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
}
