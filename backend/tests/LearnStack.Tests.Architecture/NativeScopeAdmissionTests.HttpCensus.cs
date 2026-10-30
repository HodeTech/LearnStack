using FluentAssertions;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace LearnStack.Tests.Architecture;

public sealed partial class NativeScopeAdmissionTests
{
    private static readonly (string Port, string Method, string Owner, string Implementation)[] EntryCalls =
    [
        (LazyEntryPorts[0], "ReadSelfAsync", "InvokeAsync", BindingService),
        (LazyEntryPorts[1], "BootstrapAsync", "InvokeAsync", BindingService),
        (LazyEntryPorts[2], "ReadScopesAsync", "ContextsAsync", BindingService),
        (LazyEntryPorts[3], "ReadAsync", "ContextsAsync", Labels),
    ];

    // Borrow the same mutable production modules as the HTTP census; never dispose
    // them here or obtain a fresh assembly snapshot that would hide its mutants.
    internal static IEnumerable<string> HttpEntryOffenders(ModuleDefinition[] modules)
    {
        var census = Census.Borrow(modules);
        var population = HttpEntryPopulationOffenders(census);
        if (population.Length != 0) return population;
        return EntryOffenders(census).Concat(EntryCallerOffenders(census)).Concat(EntryHelperOffenders(census)).Concat(CompiledDescriptorOffenders(census)).Distinct(StringComparer.Ordinal).ToArray();
    }

    // Fail closed before extractors that require a unique subject. A missing
    // assembly/type/method is a census finding, never an empty successful scan.
    private static string[] HttpEntryPopulationOffenders(Census census)
    {
        var errors = new List<string>();
        var expectedAssemblies = ProductionAssemblies.All().Select(assembly => assembly.GetName().Name!).Order(StringComparer.Ordinal);
        if (!census.Modules.Select(module => module.Assembly.Name.Name).Order(StringComparer.Ordinal)
                .SequenceEqual(expectedAssemblies, StringComparer.Ordinal))
            errors.Add("Global-self production assembly census changed");
        var types = census.Types.ToArray();
        foreach (var (assembly, name) in HttpEntrySubjects)
        {
            var matching = types.Where(type => type.FullName == name).ToArray();
            if (matching.Length != 1 || matching[0].Module.Assembly.Name.Name != assembly)
                errors.Add("Global-self missing or ambiguous production subject: " + assembly + ":" + name);
        }
        foreach (var (owner, name) in new[] { (Entry, "InvokeAsync"), (Entry, "ContextsAsync"), (Auth + "GlobalSelfOperation", ".cctor") })
        {
            var matching = types.Where(type => type.FullName == owner && type.Module.Assembly.Name.Name == "LearnStack.Api")
                .SelectMany(type => type.Methods).Where(method => method.Name == name).ToArray();
            if (matching.Length != 1 || !matching[0].HasBody)
                errors.Add("Global-self missing or ambiguous production member: " + owner + "." + name);
        }
        return errors.ToArray();
    }

    private static readonly (string Assembly, string Name)[] HttpEntrySubjects =
    [
        ("LearnStack.Api", Entry), ("LearnStack.Api", Auth + "GlobalSelfOperation"),
        ("LearnStack.Modules.Identity.Application.Contracts", LazyEntryPorts[0]),
        ("LearnStack.Modules.Identity.Application.Contracts", LazyEntryPorts[1]),
        ("LearnStack.Modules.Identity.Application.Contracts", LazyEntryPorts[2]),
        ("LearnStack.Modules.Tenancy.Application.Contracts", LazyEntryPorts[3]),
        ("LearnStack.Modules.Identity.Infrastructure", BindingService),
        ("LearnStack.Modules.Tenancy.Infrastructure", Labels),
    ];

    private static IEnumerable<string> CompiledDescriptorOffenders(Census census)
    {
        var rows = new List<Descriptor>();
        var descriptor = census.Type(Auth + "GlobalSelfOperation");
        foreach (var body in census.Bodies)
            foreach (var instruction in body.Body.Body.Instructions)
                if (instruction.Operand is MethodReference target && target.DeclaringType.FullName == Auth + "GlobalSelfOperation")
                {
                    if (target.Name == "<Clone>$") { yield return "Global-self descriptor union changed"; continue; }
                    if (target.Name != ".ctor") continue;
                    // Records emit a dormant clone/copy pair. Validate that exact
                    // generated data copy, and reject every actual clone/copy caller.
                    if (GeneratedDescriptorCopy(descriptor, body.Body, instruction, target)) continue;
                    var kind = EndpointComposition.ArgumentProducer(body.Body, instruction, 0);
                    var path = EndpointComposition.ArgumentProducer(body.Body, instruction, 1);
                    var verb = EndpointComposition.ArgumentProducer(body.Body, instruction, 2);
                    if (!ExactDescriptorType(target.DeclaringType) || target.Parameters.Count != 3
                        || body.Owner != Auth + "GlobalSelfOperation" || body.Name != ".cctor" || instruction.OpCode != OpCodes.Newobj
                        || kind?.OpCode.Code is not (Code.Ldc_I4_1 or Code.Ldc_I4_2 or Code.Ldc_I4_3)
                        || path is not { OpCode.Code: Code.Ldstr, Operand: string literal }
                        || verb is not { OpCode.Code: Code.Ldsfld, Operand: FieldReference field }
                        || field.DeclaringType.FullName != "Microsoft.AspNetCore.Http.HttpMethods" || field.Name is not ("Get" or "Post"))
                    { yield return "Global-self descriptor union changed"; continue; }
                    rows.Add(new(kind.OpCode.Code == Code.Ldc_I4_1 ? 1 : kind.OpCode.Code == Code.Ldc_I4_2 ? 2 : 3,
                        literal, field.Name == "Get" ? "GET" : "POST"));
                }
        foreach (var error in DescriptorOffenders(rows)) yield return error;
    }

    private static bool ExactDescriptorType(TypeReference type) => type.FullName == Auth + "GlobalSelfOperation"
        && type.Scope.Name.Replace(".dll", "", StringComparison.Ordinal) == "LearnStack.Api";

    private static bool GeneratedDescriptorCopy(TypeDefinition descriptor, MethodDefinition owner, Instruction construction, MethodReference target)
    {
        if (!ExactDescriptorType(target.DeclaringType) || !target.HasThis || target.ExplicitThis || target.HasGenericParameters
            || target.ReturnType.FullName != "System.Void" || target.Parameters.Count != 1 || !ExactDescriptorType(target.Parameters[0].ParameterType)
            || construction.OpCode != OpCodes.Newobj || owner.DeclaringType != descriptor || owner.Name != "<Clone>$"
            || !owner.IsPublic || !owner.HasThis || owner.ExplicitThis || owner.HasGenericParameters || owner.Parameters.Count != 0 || !ExactDescriptorType(owner.ReturnType)
            || !owner.CustomAttributes.Any(attribute => attribute.AttributeType.FullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute")) return false;
        var clone = owner.Body.Instructions.Where(instruction => instruction.OpCode != OpCodes.Nop).ToArray();
        if (clone is not [{ OpCode.Code: Code.Ldarg_0 }, _, { OpCode.Code: Code.Ret }] || clone[1] != construction) return false;
        var copy = descriptor.Methods.SingleOrDefault(method => method.IsConstructor && !method.IsStatic && method.FullName == target.FullName);
        if (copy is not { IsPrivate: true, HasBody: true, HasThis: true, ExplicitThis: false, HasGenericParameters: false }
            || !copy.CustomAttributes.Any(attribute =>
                attribute.AttributeType.FullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute")) return false;
        var body = copy.Body.Instructions.Where(instruction => instruction.OpCode != OpCodes.Nop).ToArray();
        if (body.Length != 15 || body[0].OpCode != OpCodes.Ldarg_0 || body[1].OpCode != OpCodes.Call
            || body[1].Operand is not MethodReference
            {
                Name: ".ctor", DeclaringType.FullName: "System.Object", Parameters.Count: 0,
                HasThis: true, ExplicitThis: false, HasGenericParameters: false, ReturnType.FullName: "System.Void"
            } objectConstructor
            || objectConstructor.DeclaringType.Scope.Name != "System.Runtime"
            || body[^1].OpCode != OpCodes.Ret) return false;
        var fields = new[] { "<Kind>k__BackingField", "<Path>k__BackingField", "<Method>k__BackingField" };
        for (var index = 0; index < fields.Length; index++)
        {
            var start = 2 + index * 4;
            var declared = descriptor.Fields.SingleOrDefault(field => field.Name == fields[index]);
            if (body[start].OpCode != OpCodes.Ldarg_0 || body[start + 1].OpCode != OpCodes.Ldarg_1
                || body[start + 2] is not { OpCode.Code: Code.Ldfld, Operand: FieldReference read }
                || body[start + 3] is not { OpCode.Code: Code.Stfld, Operand: FieldReference write }
                || !ExactDescriptorType(read.DeclaringType) || !ExactDescriptorType(write.DeclaringType)
                || declared is null || declared.IsStatic || read.FullName != declared.FullName || read.FullName != write.FullName
                || read.FieldType.Scope.Name != declared.FieldType.Scope.Name || write.FieldType.Scope.Name != declared.FieldType.Scope.Name) return false;
        }
        return true;
    }

    private static IEnumerable<string> EntryCallerOffenders(Census census)
    {
        var sites = census.Bodies.SelectMany(body => body.Body.Body.Instructions
            .Where(instruction => instruction.Operand is MethodReference)
            .Select(instruction => (body.Owner, body.Name, Instruction: instruction, Target: (MethodReference)instruction.Operand))).ToArray();
        foreach (var call in EntryCalls)
        {
            var implementations = census.Types.Where(type => type.Interfaces.Any(contract => contract.InterfaceType.FullName == call.Port)).ToArray();
            if (!implementations.Select(type => type.FullName).SequenceEqual([call.Implementation], StringComparer.Ordinal))
                yield return "Global-self direct port implementations changed: " + call.Port;
            var names = implementations.Select(type => type.FullName).Append(call.Port).ToHashSet(StringComparer.Ordinal);
            var actual = sites.Where(site => names.Contains(site.Target.DeclaringType.FullName)
                && (site.Target.Name == call.Method || site.Target.Name.EndsWith("." + call.Method, StringComparison.Ordinal))).ToArray();
            if (actual.Length != 1 || actual[0].Owner != Entry || actual[0].Name != call.Owner
                || actual[0].Target.DeclaringType.FullName != call.Port
                || actual[0].Instruction.OpCode.Code is not (Code.Call or Code.Callvirt))
                yield return "Global-self direct port callers changed: " + call.Port + "." + call.Method;
        }
    }

    // Follow executed members, including async bodies and delegates. A readonly
    // Current getter does not acquire its owner's unrelated SQL startup methods.
    // Only the four exact entry calls terminate at a persistence port.
    private static IEnumerable<string> EntryHelperOffenders(Census census)
    {
        var map = census.Types.ToDictionary(type => (type.Module.Assembly.Name.Name, type.FullName));
        var pending = new Queue<MethodDefinition>(Il.Methods(census.Type(Entry)).Select(pair => pair.Definition));
        var visited = new HashSet<MethodDefinition>();
        while (pending.TryDequeue(out var method))
        {
            if (!visited.Add(method)) continue;
            var references = method.Parameters.Select(parameter => parameter.ParameterType).Append(method.ReturnType)
                .Concat(method.HasBody ? method.Body.Variables.Select(variable => variable.VariableType) : []);
            if (references.SelectMany(Il.ExpandedTypeReferences).Any(reference => ForbiddenEntry(reference.FullName)))
                yield return "Global-self helper persistence/dispatch bypass: " + method.FullName;
            if (!method.HasBody) continue;
            foreach (var instruction in method.Body.Instructions)
            {
                IEnumerable<TypeReference> types = instruction.Operand switch
                {
                    TypeReference reference => Il.ExpandedTypeReferences(reference),
                    FieldReference field => Il.ExpandedTypeReferences(field.FieldType),
                    MethodReference call => Il.ExpandedTypeReferences(call.DeclaringType).Concat(Il.ExpandedTypeReferences(call.ReturnType))
                        .Concat(call.Parameters.SelectMany(parameter => Il.ExpandedTypeReferences(parameter.ParameterType)))
                        .Concat(call is GenericInstanceMethod generic ? generic.GenericArguments.SelectMany(Il.ExpandedTypeReferences) : []),
                    _ => []
                };
                if (types.Any(reference => ForbiddenEntry(reference.FullName))
                    || instruction.Operand is MethodReference { Name: "get_RequestServices" })
                    yield return "Global-self helper persistence/dispatch bypass: " + method.FullName;
                if (instruction.Operand is not MethodReference target) continue;
                if (EntryCalls.Any(call => target.DeclaringType.FullName == call.Port && target.Name == call.Method)) continue;
                var referenceType = target.DeclaringType.GetElementType();
                var assembly = referenceType.Scope.Name.Replace(".dll", "", StringComparison.Ordinal);
                if (!map.TryGetValue((assembly, referenceType.FullName), out var owner)) continue;
                var owners = owner.IsInterface || owner.IsAbstract
                    ? map.Values.Where(candidate => candidate.Interfaces.Any(contract => contract.InterfaceType.GetElementType().FullName == owner.FullName)
                        || candidate.BaseType?.FullName == owner.FullName).Append(owner) : [owner];
                foreach (var implementation in owners)
                    foreach (var candidate in implementation.Methods.Where(candidate => candidate.Name == target.Name
                                 && candidate.Parameters.Count == target.Parameters.Count))
                        foreach (var body in Il.Methods(implementation).Where(pair => pair.Definition == candidate || pair.DeclaredAs == candidate.Name))
                            pending.Enqueue(body.Definition);
            }
        }
    }

    private static bool ForbiddenEntry(string name) => name.StartsWith("Npgsql", StringComparison.Ordinal)
        || name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
        || name.StartsWith("System.Data.", StringComparison.Ordinal)
        || name.StartsWith("MediatR.IRequestHandler", StringComparison.Ordinal)
        || name is "MediatR.ISender" or "MediatR.IMediator" or "MediatR.IPublisher" or "MediatR.Mediator" or "System.IServiceProvider"
        || name.StartsWith("LearnStack.SharedKernel.Persistence.IUnitOfWork", StringComparison.Ordinal)
        || name is "LearnStack.SharedKernel.Tenancy.ITenantContextAccessor" or "LearnStack.SharedKernel.Tenancy.TenantContextFactory";

    private static void AssertHttpEntryPopulationControls(ModuleDefinition[] modules)
    {
        HttpEntryOffenders([]).Should().Contain("Global-self production assembly census changed");
        foreach (var module in modules)
            HttpEntryOffenders(modules.Where(candidate => candidate != module).ToArray())
                .Should().Contain("Global-self production assembly census changed");
        foreach (var (assembly, name) in HttpEntrySubjects)
        {
            var module = modules.Single(candidate => candidate.Assembly.Name.Name == assembly);
            var subject = module.Types.Single(type => type.FullName == name);
            var index = module.Types.IndexOf(subject);
            module.Types.RemoveAt(index);
            try { HttpEntryOffenders(modules).Should().Contain("Global-self missing or ambiguous production subject: " + assembly + ":" + name); }
            finally { module.Types.Insert(index, subject); }
        }
        var api = modules.Single(module => module.Assembly.Name.Name == "LearnStack.Api");
        foreach (var (owner, name) in new[] { (Entry, "InvokeAsync"), (Entry, "ContextsAsync"), (Auth + "GlobalSelfOperation", ".cctor") })
        {
            var subject = api.GetType(owner);
            var method = subject.Methods.Single(candidate => candidate.Name == name);
            var index = subject.Methods.IndexOf(method);
            subject.Methods.RemoveAt(index);
            try { HttpEntryOffenders(modules).Should().Contain("Global-self missing or ambiguous production member: " + owner + "." + name); }
            finally { subject.Methods.Insert(index, method); }
        }
        HttpEntryOffenders(modules).Should().BeEmpty("restoring every required subject restores the same production detector");
    }

    internal static void AssertHttpEntryControls(ModuleDefinition[] modules)
    {
        var census = Census.Borrow(modules);
        HttpEntryOffenders(modules).Should().BeEmpty();
        AssertHttpEntryPopulationControls(modules);
        var foreign = census.Body(Entry, "TryPage");
        foreach (var call in EntryCalls)
        {
            var target = census.Type(call.Port).Methods.Single(method => method.Name == call.Method);
            foreach (var opcode in new[] { OpCodes.Callvirt, OpCodes.Ldftn })
            {
                var planted = Instruction.Create(opcode, target);
                foreign.Body.Instructions.Add(planted);
                try { HttpEntryOffenders(modules).Should().Contain("Global-self direct port callers changed: " + call.Port + "." + call.Method); }
                finally { foreign.Body.Instructions.Remove(planted); }
            }
        }
        var descriptors = census.Type(Auth + "GlobalSelfOperation").Methods.Single(method => method.Name == ".cctor");
        var literal = descriptors.Body.Instructions.First(instruction => instruction.OpCode == OpCodes.Ldstr);
        var path = literal.Operand;
        literal.Operand = "/api/v1/identity/self/extra";
        try { HttpEntryOffenders(modules).Should().Contain("Global-self descriptor union changed"); }
        finally { literal.Operand = path; }
        var construction = descriptors.Body.Instructions.First(instruction => instruction.Operand is MethodReference target
            && target.DeclaringType.FullName == Auth + "GlobalSelfOperation" && target.Name == ".ctor");
        var duplicate = Instruction.Create(OpCodes.Newobj, (MethodReference)construction.Operand);
        foreign.Body.Instructions.Add(duplicate);
        try { HttpEntryOffenders(modules).Should().Contain("Global-self descriptor union changed"); }
        finally { foreign.Body.Instructions.Remove(duplicate); }
        var descriptorType = census.Type(Auth + "GlobalSelfOperation");
        var copy = descriptorType.Methods.Single(method => method.IsConstructor && !method.IsStatic && method.Parameters.Count == 1);
        var clone = descriptorType.Methods.Single(method => method.Name == "<Clone>$");
        foreach (var invocation in new[]
                 {
                     Instruction.Create(OpCodes.Newobj, copy), Instruction.Create(OpCodes.Callvirt, clone), Instruction.Create(OpCodes.Ldftn, clone)
                 })
        {
            foreign.Body.Instructions.Add(invocation);
            try { HttpEntryOffenders(modules).Should().Contain("Global-self descriptor union changed", "the compiler's dormant copy pair is not an executable descriptor factory"); }
            finally { foreign.Body.Instructions.Remove(invocation); }
        }
        var copiedField = copy.Body.Instructions.First(instruction => instruction.OpCode == OpCodes.Ldfld);
        var originalField = copiedField.Operand;
        copiedField.Operand = descriptorType.Fields.Single(field => field.Name == "<Path>k__BackingField");
        try { HttpEntryOffenders(modules).Should().Contain("Global-self descriptor union changed", "only the exact generated data copy qualifies"); }
        finally { copiedField.Operand = originalField; }
        var helper = new TypeDefinition("NativeControl", "HttpHelper", TypeAttributes.NotPublic, foreign.Module.TypeSystem.Object);
        var body = new MethodDefinition("Read", MethodAttributes.Static | MethodAttributes.Assembly, foreign.Module.TypeSystem.Void);
        body.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        helper.Methods.Add(body);
        foreign.Module.Types.Add(helper);
        var invoke = Instruction.Create(OpCodes.Call, body);
        foreign.Body.Instructions.Add(invoke);
        try
        {
            HttpEntryOffenders(modules).Should().BeEmpty("an executed data-free helper is a clean member-graph control");
            var local = new VariableDefinition(foreign.Module.ImportReference(typeof(Npgsql.NpgsqlDataSource)));
            body.Body.Variables.Add(local);
            try { HttpEntryOffenders(modules).Should().Contain(error => error.StartsWith("Global-self helper persistence/dispatch bypass:", StringComparison.Ordinal)); }
            finally { body.Body.Variables.Remove(local); }
        }
        finally { foreign.Body.Instructions.Remove(invoke); foreign.Module.Types.Remove(helper); }
        HttpEntryOffenders(modules).Should().BeEmpty();
    }
}
