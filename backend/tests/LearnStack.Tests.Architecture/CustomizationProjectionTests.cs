using FluentAssertions;
using LearnStack.Modules.Customization.Application.Contracts.Definitions;
using LearnStack.Modules.Customization.Infrastructure.Projections;
using LearnStack.SharedKernel.Validation;
using Mono.Cecil;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed class CustomizationProjectionTests
{
    /// <summary>
    /// <see href="../../../docs/decisions/0043-customization-payload-validation.md">ADR-0043</see>
    /// and <see href="../../../docs/standards/20-infrastructure-stack.md#icacheservice-state">Standards 20</see>.
    /// </summary>
    [Fact]
    public void Customization_Projection_Does_Not_Validate_On_Read()
    {
        using var infrastructure = ModuleDefinition.ReadModule(typeof(CustomizationDefinitionProjectionReader).Assembly.Location);
        using var application = ModuleDefinition.ReadModule(typeof(LearnStack.Modules.Customization.Application.Customization.TextCardPresentation).Assembly.Location);
        var types = infrastructure.GetTypes().Concat(application.GetTypes())
            .Where(type => type.FullName.StartsWith("LearnStack.Modules.Customization.", StringComparison.Ordinal))
            .ToDictionary(type => type.FullName);
        var roots = infrastructure.GetTypes().Where(type => type.Interfaces.Any(contract =>
            contract.InterfaceType.FullName == typeof(ICustomizationDefinitionProjectionReader).FullName)).ToArray();
        roots.Should().NotBeEmpty("the registered display reader must be classified by its contract");
        Offenders(roots, types).Should().BeEmpty(
            "Fix: admit schemas on the write path; neither the projection nor its module helpers may reach schema validation");
    }

    [Fact]
    public void Projection_validator_guard_detects_direct_and_helper_dependencies()
    {
        using var module = ModuleDefinition.ReadModule(typeof(CustomizationProjectionTests).Assembly.Location);
        var types = module.GetTypes().ToDictionary(type => type.FullName);
        var direct = module.GetType(typeof(ValidatorProbe).FullName!.Replace('+', '/'));
        var indirect = module.GetType(typeof(HelperProbe).FullName!.Replace('+', '/'));
        var clean = module.GetType(typeof(CleanProbe).FullName!.Replace('+', '/'));
        Offenders([direct], types).Should().ContainSingle().Which.Should().Be(direct.FullName);
        Offenders([indirect], types).Should().ContainSingle().Which.Should().Be(direct.FullName);
        Offenders([clean], types).Should().BeEmpty();
    }

    private static HashSet<string> Offenders(IEnumerable<TypeDefinition> roots, Dictionary<string, TypeDefinition> types)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var offenders = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<TypeDefinition>(roots);
        while (queue.TryDequeue(out var type))
        {
            if (!visited.Add(type.FullName)) continue;
            foreach (var name in Il.ReferencedTypeNames(type))
            {
                if (name == typeof(IJsonSchemaValidator).FullName || name.StartsWith("Json.Schema.", StringComparison.Ordinal))
                    offenders.Add(type.FullName);
                if (types.TryGetValue(name, out var helper)) queue.Enqueue(helper);
            }
        }
        return offenders;
    }

    private sealed class ValidatorProbe(IJsonSchemaValidator validator)
    {
        public IJsonSchemaValidator Value => validator;
    }
    private sealed class HelperProbe
    {
        public static ValidatorProbe? Value => null;
    }
    private sealed class CleanProbe
    {
        public static string Read() => "display";
    }
}
