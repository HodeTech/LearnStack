using FluentAssertions;
using LearnStack.Infrastructure.Validation;
using LearnStack.Modules.Customization.Application.Contracts.Definitions;
using LearnStack.Modules.Customization.Infrastructure.Projections;
using LearnStack.SharedKernel.Validation;
using Mono.Cecil;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed class CustomizationProjectionTests
{
    private static readonly HashSet<string> ValidatorTypes = typeof(JsonSchemaNetValidator).Assembly.GetTypes()
        .Where(type => typeof(IJsonSchemaValidator).IsAssignableFrom(type)).Select(type => type.FullName!)
        .Append(typeof(IJsonSchemaValidator).FullName!).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// <see href="../../../docs/decisions/0043-customization-payload-validation.md">ADR-0043</see>
    /// and <see href="../../../docs/standards/20-infrastructure-stack.md#icacheservice-state">Standards 20</see>.
    /// </summary>
    [Fact]
    public void Customization_Projection_Does_Not_Validate_On_Read()
    {
        var modules = ReadProductionModules();
        try
        {
            modules.Where(module => module.Name.StartsWith("LearnStack.Modules.Customization.", StringComparison.Ordinal))
                .Select(module => Path.GetFileNameWithoutExtension(module.Name)).Should().BeEquivalentTo(
                    ["LearnStack.Modules.Customization.Domain", "LearnStack.Modules.Customization.Application.Contracts",
                     "LearnStack.Modules.Customization.Application", "LearnStack.Modules.Customization.Infrastructure"],
                    "Fix: scan every Customization layer, including Domain and Contracts helpers");
            var types = TypeMap(modules);
            var roots = Roots(types);
            roots.Should().NotBeEmpty("the registered display reader must be classified by its contract");
            Offenders(roots, types).Should().BeEmpty(
                "Fix: admit schemas on the write path; neither the projection nor its production helpers may reach schema validation");
        }
        finally
        {
            foreach (var module in modules) module.Dispose();
        }
    }

    [Fact]
    public void Projection_validator_guard_detects_direct_and_helper_dependencies()
    {
        using var module = ModuleDefinition.ReadModule(typeof(CustomizationProjectionTests).Assembly.Location);
        var types = TypeMap([module]);
        var direct = module.GetType(typeof(ValidatorProbe).FullName!.Replace('+', '/'));
        var indirect = module.GetType(typeof(HelperProbe).FullName!.Replace('+', '/'));
        var concrete = module.GetType(typeof(ConcreteProbe).FullName!.Replace('+', '/'));
        var clean = module.GetType(typeof(CleanProbe).FullName!.Replace('+', '/'));
        Offenders([direct], types).Should().ContainSingle().Which.Should().Be(direct.FullName);
        Offenders([indirect], types).Should().ContainSingle().Which.Should().Be(direct.FullName);
        Offenders([concrete], types).Should().ContainSingle().Which.Should().Be(concrete.FullName);
        Offenders([clean], types).Should().BeEmpty();

        foreach (var probe in new[] { typeof(ArrayProbe), typeof(ConstraintProbe<>), typeof(ParameterAttributeProbe),
                     typeof(ReturnAttributeProbe), typeof(GenericAttributeProbe<>), typeof(EventAttributeProbe),
                     typeof(LambdaProbe), typeof(AsyncProbe), typeof(CatchProbe) })
        {
            var planted = module.GetType(probe.FullName!.Replace('+', '/'));
            Offenders([planted], types).Should().Contain(planted.FullName,
                $"Fix: the validator guard must see the dependency carried by {probe.Name}");
            Il.NamesNamespace(planted, probe == typeof(CatchProbe) ? "Json.Schema" : "LearnStack.SharedKernel.Validation")
                .Should().BeTrue("the shared namespace guards must see the same metadata references");
        }

        var callback = new FunctionPointerType { ReturnType = module.TypeSystem.Void };
        callback.Parameters.Add(new ParameterDefinition(module.ImportReference(typeof(IJsonSchemaValidator))));
        var pointerProbe = new TypeDefinition("ReviewProbe", "Callback", TypeAttributes.Class);
        pointerProbe.Fields.Add(new FieldDefinition("Callback", FieldAttributes.Public, callback));
        module.Types.Add(pointerProbe);
        Offenders([pointerProbe], TypeMap([module])).Should().ContainSingle().Which.Should().Be(pointerProbe.FullName);
        Il.NamesNamespace(pointerProbe, "LearnStack.SharedKernel.Validation").Should().BeTrue();
    }

    [Fact]
    public void Projection_validator_guard_follows_domain_contracts_and_external_production_helpers()
    {
        var modules = ReadProductionModules();
        try
        {
            var infrastructure = modules.Single(module => module.Name == "LearnStack.Modules.Customization.Infrastructure.dll");
            var reader = infrastructure.GetType(typeof(CustomizationDefinitionProjectionReader).FullName!);
            // Mutate only the in-memory Cecil models. The real production census and
            // root predicate must reach each planted helper; no source file is changed.
            foreach (var name in new[] { "LearnStack.Modules.Customization.Domain.dll",
                         "LearnStack.Modules.Customization.Application.Contracts.dll", "LearnStack.Infrastructure.dll" })
            {
                var owner = modules.Single(module => module.Name == name);
                var helper = new TypeDefinition("ReviewProbe", "Helper", TypeAttributes.Class | TypeAttributes.Public);
                owner.Types.Add(helper);
                helper.Fields.Add(new FieldDefinition("Validator", FieldAttributes.Public,
                    owner.ImportReference(typeof(IJsonSchemaValidator))));
                var dependency = new FieldDefinition("ReviewDependency", FieldAttributes.Private, infrastructure.ImportReference(helper));
                reader.Fields.Add(dependency);
                var types = TypeMap(modules);
                Offenders(Roots(types), types).Should().Contain(helper.FullName,
                    $"Fix: a helper in {name} must not hide projection validation");
                reader.Fields.Remove(dependency);
                owner.Types.Remove(helper);
            }
        }
        finally
        {
            foreach (var module in modules) module.Dispose();
        }
    }

    private static List<ModuleDefinition> ReadProductionModules()
    {
        var modules = new List<ModuleDefinition>();
        try
        {
            foreach (var assembly in ProductionAssemblies.All()) modules.Add(ModuleDefinition.ReadModule(assembly.Location));
            return modules;
        }
        catch
        {
            foreach (var module in modules) module.Dispose();
            throw;
        }
    }

    private static Dictionary<(string Assembly, string Type), TypeDefinition> TypeMap(IEnumerable<ModuleDefinition> modules) =>
        modules.SelectMany(module => module.GetTypes()).ToDictionary(type => (type.Module.Assembly.Name.Name, type.FullName));

    private static TypeDefinition[] Roots(Dictionary<(string Assembly, string Type), TypeDefinition> types) =>
        types.Values.Where(type => type.Interfaces.Any(contract =>
            contract.InterfaceType.FullName == typeof(ICustomizationDefinitionProjectionReader).FullName)).ToArray();

    private static HashSet<string> Offenders(IEnumerable<TypeDefinition> roots, Dictionary<(string Assembly, string Type), TypeDefinition> types)
    {
        var visited = new HashSet<(string Assembly, string Type)>();
        var offenders = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<TypeDefinition>(roots);
        while (queue.TryDequeue(out var type))
        {
            if (!visited.Add((type.Module.Assembly.FullName, type.FullName))) continue;
            foreach (var reference in Il.ReferencedTypeReferences(type))
            {
                var name = reference.FullName;
                if (ValidatorTypes.Contains(name) || name.StartsWith("Json.Schema.", StringComparison.Ordinal))
                    offenders.Add(type.FullName);
                var assembly = reference.Scope is AssemblyNameReference scope ? scope.Name
                    : reference.Scope is ModuleDefinition module ? module.Assembly.Name.Name : reference.Scope.Name;
                if (types.TryGetValue((assembly, name), out var helper)) queue.Enqueue(helper);
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
    private sealed class ConcreteProbe
    {
        public static JsonSchemaNetValidator? Value => null;
    }
    private sealed class CleanProbe
    {
        public static string Read() => "display";
    }
    private sealed class ArrayProbe
    {
        public Dictionary<string, IJsonSchemaValidator>[]? Value { get; }
    }
    private sealed class ConstraintProbe<T> where T : IJsonSchemaValidator;
    [AttributeUsage(AttributeTargets.All)]
    private sealed class TypeProbeAttribute(Type type) : Attribute
    {
        public Type Value => type;
    }
    private sealed class ParameterAttributeProbe
    {
        public static void Read([TypeProbe(typeof(IJsonSchemaValidator))] string value) { }
    }
    private sealed class ReturnAttributeProbe
    {
        [return: TypeProbe(typeof(IJsonSchemaValidator))]
        public static string Read() => "display";
    }
    private sealed class GenericAttributeProbe<[TypeProbe(typeof(IJsonSchemaValidator))] T>;
    private sealed class EventAttributeProbe
    {
        [TypeProbe(typeof(IJsonSchemaValidator))]
        public static event Action Changed { add { } remove { } }
    }
    private sealed class LambdaProbe
    {
        public static Func<object?> Read() => () => typeof(IJsonSchemaValidator);
    }
    private sealed class AsyncProbe
    {
        public static async Task<object> Read()
        {
            await Task.Yield();
            return typeof(IJsonSchemaValidator);
        }
    }
    private sealed class CatchProbe
    {
        public static void Read()
        {
            try { CleanProbe.Read(); }
            catch (Json.Schema.JsonSchemaException) { }
        }
    }
}
