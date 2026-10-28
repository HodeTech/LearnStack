using FluentAssertions;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace LearnStack.Tests.Architecture;

public sealed partial class AuthenticatedHttpSurfaceTests
{
    private const string HttpClassifier = "LearnStack.Api.Authentication.OperationHttpClassifier";
    private const string ControllerPair = "System.ValueTuple`2<System.Type,System.String>";
    private const string OperationTriple = "System.ValueTuple`3<System.Type,System.String,System.String>";
    private const string OptionalOperationTriple = "System.Nullable`1<" + OperationTriple + ">";

    // Only the literal controller operands in the fixed data table are
    // descriptive. Controller fields, constructor arguments, locals, method
    // targets and every other typeof occurrence retain their ordinary edges.
    private static bool IsControllerTypeMetadata(TypeDefinition owner, MethodDefinition method, Instruction instruction) =>
        owner.Module.Assembly.Name.Name == "LearnStack.Api" && owner.FullName == HttpClassifier && method.Name == "Expected"
        && instruction.OpCode == OpCodes.Ldtoken && instruction.Operand is TypeReference controller
        && Endpoints.Any(endpoint => Key(controller) == "LearnStack.Api:" + endpoint.Controller)
        && DataOnlyHttpTable(method);

    private static bool DataOnlyHttpTable(MethodDefinition method)
    {
        if (!method.IsPrivate || !method.IsStatic || method.Parameters.Count != 2
            || method.Parameters[0].ParameterType.FullName != "System.Type" || method.Parameters[1].ParameterType.FullName != "System.String"
            || method.ReturnType.FullName != OptionalOperationTriple || !method.HasBody
            || method.Body.Instructions.Any(instruction => instruction.OpCode == OpCodes.Calli)
            || method.Body.Variables.Any(variable => variable.VariableType.FullName is not (ControllerPair or OptionalOperationTriple or "System.Type"))) return false;
        var tokens = method.Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Ldtoken)
            .Select(instruction => instruction.Operand).OfType<TypeReference>().ToArray();
        var expectedTokens = Endpoints.SelectMany(endpoint => new[] { "LearnStack.Api:" + endpoint.Controller,
            endpoint.Request.Assembly.GetName().Name + ":" + endpoint.Request.FullName }).ToArray();
        if (!tokens.Select(Key).SequenceEqual(expectedTokens, StringComparer.Ordinal)) return false;
        var actual = method.Body.Instructions.Where(instruction => instruction.Operand is MethodReference)
            .Select(instruction => instruction.OpCode.Name + ":" + ((MethodReference)instruction.Operand).FullName).Order(StringComparer.Ordinal);
        var expected = Enumerable.Repeat("call:System.Type System.Type::GetTypeFromHandle(System.RuntimeTypeHandle)", Endpoints.Length * 2)
            .Concat(Enumerable.Repeat("call:System.Boolean System.Type::op_Equality(System.Type,System.Type)", Endpoints.Length))
            .Concat(Enumerable.Repeat("call:System.Boolean System.String::op_Equality(System.String,System.String)", Endpoints.Length))
            .Append("call:System.Void " + ControllerPair + "::.ctor(!0,!1)")
            .Concat(Enumerable.Repeat("newobj:System.Void " + OperationTriple + "::.ctor(!0,!1,!2)", Endpoints.Length))
            .Concat(Enumerable.Repeat("newobj:System.Void " + OptionalOperationTriple + "::.ctor(!0)", Endpoints.Length)).Order(StringComparer.Ordinal);
        return actual.SequenceEqual(expected, StringComparer.Ordinal)
            && method.Body.Instructions.Where(instruction => instruction.Operand is FieldReference)
                .All(instruction => instruction.OpCode == OpCodes.Ldfld && instruction.Operand is FieldReference field
                    && field.DeclaringType.FullName == ControllerPair && field.Name is "Item1" or "Item2");
    }

    private static void AssertControllerMetadataControls(ModuleDefinition[] modules, CensusFacts facts)
    {
        var api = modules.Single(module => module.Assembly.Name.Name == "LearnStack.Api");
        var classifier = api.GetType(HttpClassifier);
        var expected = classifier.Methods.Single(method => method.Name == "Expected");
        var account = api.GetType(Account);
        void Dirty() => Violations(modules, facts).Should().Contain(value => value.StartsWith("Nested HTTP dispatch/helper handler:", StringComparison.Ordinal));
        var field = new FieldDefinition("ActualController", FieldAttributes.Private, account);
        classifier.Fields.Add(field);
        try { Dirty(); } finally { classifier.Fields.Remove(field); }
        var constructor = classifier.Methods.Single(method => method.IsConstructor);
        var argument = new ParameterDefinition("actualController", ParameterAttributes.None, account);
        constructor.Parameters.Add(argument);
        try { Dirty(); } finally { constructor.Parameters.Remove(argument); }
        var local = new VariableDefinition(account);
        expected.Body.Variables.Add(local);
        try { Dirty(); } finally { expected.Body.Variables.Remove(local); }
        var allocation = Instruction.Create(OpCodes.Newobj, account.Methods.Single(method => method.IsConstructor));
        expected.Body.Instructions.Add(allocation);
        try { Dirty(); } finally { expected.Body.Instructions.Remove(allocation); }
        foreach (var opcode in new[] { OpCodes.Callvirt, OpCodes.Ldftn })
        {
            var call = Instruction.Create(opcode, account.Methods.Single(method => method.Name == "GetSite"));
            expected.Body.Instructions.Add(call);
            try { Dirty(); } finally { expected.Body.Instructions.Remove(call); }
        }
        var factory = new MethodDefinition("AcquireController", MethodAttributes.Private | MethodAttributes.Static, api.TypeSystem.Object);
        factory.Body.Instructions.Add(Instruction.Create(OpCodes.Newobj, account.Methods.Single(method => method.IsConstructor)));
        factory.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        classifier.Methods.Add(factory);
        var delegated = Instruction.Create(OpCodes.Call, factory);
        expected.Body.Instructions.Add(delegated);
        try { Dirty(); }
        finally { expected.Body.Instructions.Remove(delegated); classifier.Methods.Remove(factory); }
        Violations(modules, facts).Should().BeEmpty("only the original fixed typeof table remains after real controller capabilities are removed");
    }
}
