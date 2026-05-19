using System.Reflection;
using FluentAssertions;
using NetArchTest.Rules;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// Mechanical dependency-direction checks. Standards 01 § Dependency Direction and
/// Module Boundaries forbid Domain → Application/Infrastructure and cross-module
/// Domain references. These tests are the enforceable backstop.
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
