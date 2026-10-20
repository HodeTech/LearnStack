using FluentAssertions;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace LearnStack.Tests.Architecture;

public sealed partial class TenancyConventionTests
{
    private static void WithRenamedAssertionVocabulary(TypeDefinition owner, string previous, string replacement, Action assertion)
    {
        var types = AssertionTypes(owner).ToArray();
        var fields = types.SelectMany(type => type.Fields)
            .Where(field => field.HasConstant && field.Constant is string literal && literal == previous).ToArray();
        var loads = types.SelectMany(type => type.Methods).Where(method => method.HasBody)
            .SelectMany(method => method.Body.Instructions)
            .Where(instruction => instruction.OpCode == OpCodes.Ldstr && Equals(instruction.Operand, previous)).ToArray();
        fields.Should().NotBeEmpty("the rename control must alter a real production declaration");
        loads.Should().NotBeEmpty("the rename control must also alter the compiled uses of the declaration");
        foreach (var field in fields) field.Constant = replacement;
        foreach (var instruction in loads) instruction.Operand = replacement;
        try { assertion(); }
        finally
        {
            foreach (var field in fields) field.Constant = previous;
            foreach (var instruction in loads) instruction.Operand = previous;
        }
    }

    private static void AssertAssertionMetadataControls(ModuleDefinition module, string[] headers)
    {
        var type = new TypeDefinition("", "AssertionMetadataControl", TypeAttributes.NotPublic, module.TypeSystem.Object);
        type.GenericParameters.Add(new GenericParameter("T", type));
        type.Fields.Add(new FieldDefinition("Bound", FieldAttributes.Private, module.TypeSystem.String));
        type.Properties.Add(new PropertyDefinition("Bound", PropertyAttributes.None, module.TypeSystem.String));
        type.Events.Add(new EventDefinition("Bound", EventAttributes.None, module.TypeSystem.Object));
        var method = new MethodDefinition("Bind", MethodAttributes.Private | MethodAttributes.Static, module.TypeSystem.Void);
        method.Parameters.Add(new ParameterDefinition("bound", ParameterAttributes.None, module.TypeSystem.String));
        method.GenericParameters.Add(new GenericParameter("T", method));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        type.Methods.Add(method);
        var nested = new TypeDefinition("", "Nested", TypeAttributes.NestedPrivate, module.TypeSystem.Object);
        type.NestedTypes.Add(nested);
        var constructor = module.ImportReference(typeof(AssertionMetadataArgumentsAttribute).GetConstructor([typeof(object[])])!);
        string[] argumentSites = ["constructor", "field", "property"];
        AssertionLiterals(type).Should().NotContain(headers, "an unrelated declaration is a clean control");
        foreach (var provider in AssertionAttributeProviders(type).Append(nested))
        {
            var attribute = new CustomAttribute(constructor);
            // Constructor, named field and named property values all permit boxed
            // arguments/arrays in metadata. A string-only top-level scan misses them.
            var literal = new CustomAttributeArgument(module.TypeSystem.String, headers[0]);
            var boxed = new CustomAttributeArgument(module.TypeSystem.Object, literal);
            var array = new CustomAttributeArgument(new ArrayType(module.TypeSystem.Object), new[] { boxed });
            var nestedArray = new CustomAttributeArgument(new ArrayType(module.TypeSystem.Object),
                new[] { new CustomAttributeArgument(module.TypeSystem.Object, array) });
            foreach (var site in argumentSites)
            {
                attribute.ConstructorArguments.Clear();
                attribute.Fields.Clear();
                attribute.Properties.Clear();
                attribute.ConstructorArguments.Add(site == "constructor" ? nestedArray
                    : new CustomAttributeArgument(new ArrayType(module.TypeSystem.Object), Array.Empty<CustomAttributeArgument>()));
                if (site == "field") attribute.Fields.Add(new CustomAttributeNamedArgument(nameof(AssertionMetadataArgumentsAttribute.FieldNames), nestedArray));
                if (site == "property") attribute.Properties.Add(new CustomAttributeNamedArgument(nameof(AssertionMetadataArgumentsAttribute.PropertyNames), nestedArray));
                provider.CustomAttributes.Add(attribute);
                try
                {
                    AssertionLiterals(type).Should().Contain(headers[0],
                        "each metadata provider and argument shape must participate in the same literal discovery");
                }
                finally { provider.CustomAttributes.Remove(attribute); }
            }
        }
        AssertionLiterals(type).Should().NotContain(headers, "removing metadata restores the clean control");
    }

    private static void AssertNestedAssertionCounterControls(ModuleDefinition module, TypeDefinition owner)
    {
        const string Declared = "nested_assertion_counter_total";
        const string Created = "generated_assertion_counter_total";
        var nested = new TypeDefinition("", "<>c__AssertionCounterProbe", TypeAttributes.NestedPrivate, module.TypeSystem.Object);
        nested.Fields.Add(new FieldDefinition("NestedCounterName", FieldAttributes.Static | FieldAttributes.Literal,
            module.TypeSystem.String)
        { Constant = Declared });
        var target = owner.Methods.Where(method => method.HasBody).SelectMany(method => method.Body.Instructions)
            .Select(instruction => instruction.Operand).OfType<MethodReference>()
            .First(reference => reference.Name == "CreateCounter" && reference.DeclaringType.FullName == "System.Diagnostics.Metrics.Meter");
        var factory = new MethodDefinition("Create", MethodAttributes.Private | MethodAttributes.Static, module.TypeSystem.Void);
        factory.Parameters.Add(new ParameterDefinition("dynamicName", ParameterAttributes.None, module.TypeSystem.String));
        factory.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
        var name = Instruction.Create(OpCodes.Ldstr, Created);
        factory.Body.Instructions.Add(name);
        for (var index = 1; index < target.Parameters.Count; index++)
            factory.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
        factory.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, target));
        factory.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
        factory.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        nested.Methods.Add(factory);
        owner.NestedTypes.Add(nested);
        try
        {
            var expanded = AssertionCounterNames(owner);
            expanded.Should().Contain([Declared, Created], "nested declarations and generated factory bodies belong to the recorder inventory");
            foreach (var literal in new[] { Declared, Created })
                WithAssertionLiteral(module, literal, () => AssertionNameOffenders([module], expanded, [AssertionMetricOwner])
                    .Should().Contain("AssertionOwnershipProbe: " + literal));
            name.OpCode = OpCodes.Ldarg_0;
            name.Operand = null;
            ((Action)(() => AssertionCounterNames(owner))).Should().Throw<InvalidOperationException>()
                .WithMessage("Assertion counter creation must retain a verifiable constant name.");
        }
        finally { owner.NestedTypes.Remove(nested); }
        AssertAssertionCounterNames(owner);
    }

    [AttributeUsage(AttributeTargets.All)]
    private sealed class AssertionMetadataArgumentsAttribute(object[] names) : Attribute
    {
        public object[] Names { get; } = names;
        public object[] FieldNames = [];
        public object[] PropertyNames { get; set; } = [];
    }
}
