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
            var expectedPort = census.Types.Single(type => EntrySymbol(type) == EntryPortSymbol(call.Port));
            var expectedMember = expectedPort.Methods.Single(method => method.Name == call.Method);
            var implementations = census.Types.Where(type => type.Interfaces.Any(contract => EntrySymbol(contract.InterfaceType) == EntrySymbol(expectedPort))).ToArray();
            if (!implementations.SequenceEqual([census.Type(call.Implementation)]))
                yield return "Global-self direct port implementations changed: " + call.Port;
            var names = implementations.Select(type => type.FullName).Append(call.Port).ToHashSet(StringComparer.Ordinal);
            var actual = sites.Where(site => names.Contains(site.Target.DeclaringType.FullName)
                && (site.Target.Name == call.Method || site.Target.Name.EndsWith("." + call.Method, StringComparison.Ordinal))).ToArray();
            if (actual.Length != 1 || actual[0].Owner != Entry || actual[0].Name != call.Owner
                || !ExactEntryPortMember(actual[0].Target, expectedMember)
                || actual[0].Instruction.OpCode.Code is not (Code.Call or Code.Callvirt))
                yield return "Global-self direct port callers changed: " + call.Port + "." + call.Method;
        }
    }

    // Follow executed members, including async bodies and delegates. A readonly
    // Current getter does not acquire its owner's unrelated SQL startup methods.
    // Only the four exact entry calls terminate at a persistence port.
    private static IEnumerable<string> EntryHelperOffenders(Census census)
    {
        var map = census.Types.ToDictionary(EntrySymbol, StringComparer.Ordinal);
        var ports = EntryCalls.Select(call => map[EntryPortSymbol(call.Port)].Methods.Single(method => method.Name == call.Method)).ToArray();
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
                if (instruction.OpCode.Code is Code.Call or Code.Callvirt && ports.Any(port => ExactEntryPortMember(target, port))) continue;
                if (!map.TryGetValue(EntrySymbol(target.DeclaringType), out var owner)) continue;
                var dynamicDispatch = instruction.OpCode.Code is Code.Callvirt or Code.Ldvirtftn;
                if (!owner.IsInterface || !dynamicDispatch)
                {
                    if (!TryEntryClassTargets(owner, target, dynamicDispatch, map, out var targets))
                        yield return "Global-self helper unresolved member: " + EntrySymbol(owner) + " -> " + target.FullName;
                    else
                        foreach (var candidate in targets)
                            foreach (var body in Il.Methods(candidate.DeclaringType).Where(pair => pair.Definition == candidate || pair.DeclaredAs == candidate.Name))
                                pending.Enqueue(body.Definition);
                    continue;
                }
                var owners = map.Values.Where(candidate => !candidate.IsInterface && !candidate.IsAbstract
                    && PublicSurfaceTests.Implements(candidate, EntrySymbol(owner), map, [])).Append(owner).Distinct().ToArray();
                foreach (var implementation in owners)
                {
                    var candidates = EntryAncestors(implementation, map).SelectMany(type => type.Methods)
                        .Where(candidate => EntryCallable(candidate, target)).Distinct().ToArray();
                    // An abstract declaration has no body of its own. Every
                    // concrete implementer, however, must resolve its actual
                    // member; unsupported generic substitution cannot erase it.
                    if (candidates.Length == 0 && !implementation.IsInterface && !implementation.IsAbstract)
                        yield return "Global-self helper unresolved member: " + EntrySymbol(implementation) + " -> " + target.FullName;
                    foreach (var candidate in candidates)
                        foreach (var body in Il.Methods(candidate.DeclaringType).Where(pair => pair.Definition == candidate || pair.DeclaredAs == candidate.Name))
                            pending.Enqueue(body.Definition);
                }
            }
        }
    }

    // A class MemberRef names a declaration; callvirt/ldvirtftn additionally
    // select its CLR slot on the receiver. Same-named new slots are unrelated.
    private static bool TryEntryClassTargets(TypeDefinition owner, MethodReference target, bool dynamicDispatch,
        Dictionary<string, TypeDefinition> map, out MethodDefinition[] targets)
    {
        targets = [];
        MethodDefinition? declaration = null;
        foreach (var ancestor in EntryAncestors(owner, map))
        {
            var matches = ancestor.Methods.Where(candidate => EntryMemberShape(candidate, target)).ToArray();
            if (matches.Length > 1) return false;
            if (matches.Length == 1) { declaration = matches[0]; break; }
            if (ancestor.BaseType is TypeSpecification) return false;
        }
        if (declaration is null) return false;
        if (!dynamicDispatch || !declaration.IsVirtual || declaration.IsFinal || owner.IsSealed)
        {
            if (!declaration.HasBody) return false;
            targets = [declaration];
            return true;
        }
        var receivers = map.Values.Where(candidate => !candidate.IsInterface && !candidate.IsAbstract
            && (candidate == owner || PublicSurfaceTests.Implements(candidate, EntrySymbol(owner), map, []))).ToArray();
        if (receivers.Length == 0) return false;
        var resolved = new HashSet<MethodDefinition>();
        foreach (var receiver in receivers)
        {
            if (!TryEntryClassSlot(declaration, receiver, map, out var implementation)) return false;
            resolved.Add(implementation);
        }
        targets = resolved.ToArray();
        return true;
    }

    private static bool TryEntryClassSlot(MethodDefinition declaration, TypeDefinition receiver,
        Dictionary<string, TypeDefinition> map, out MethodDefinition implementation)
    {
        implementation = declaration;
        var ancestors = EntryAncestors(receiver, map).Reverse().ToArray();
        var start = Array.IndexOf(ancestors, declaration.DeclaringType);
        if (start < 0 || declaration.HasGenericParameters || declaration.DeclaringType.HasGenericParameters
            || declaration.Overrides.Any(overridden =>
                !map.TryGetValue(EntrySymbol(overridden.DeclaringType), out var contract) || !contract.IsInterface)) return false;
        var nearest = declaration;
        var sameSlot = true;
        foreach (var type in ancestors.Skip(start + 1))
        {
            // Substituted generic bases and explicit class MethodImpl mappings
            // require another resolver. Never approximate them by name/arity.
            if (type.HasGenericParameters || type.BaseType is TypeSpecification
                || type.Methods.Any(method => method.Overrides.Any(overridden =>
                    !map.TryGetValue(EntrySymbol(overridden.DeclaringType), out var contract) || !contract.IsInterface))
                || type.Methods.Any(method => method.Name == declaration.Name && method.HasGenericParameters)) return false;
            var matches = type.Methods.Where(method => EntryMemberShape(method, declaration)).ToArray();
            if (matches.Length > 1) return false;
            if (matches.Length == 0) continue;
            var candidate = matches[0];
            if (!candidate.IsVirtual || candidate.IsNewSlot)
            {
                sameSlot = false;
                nearest = candidate;
                continue;
            }
            if (!nearest.IsVirtual || nearest.IsFinal) return false;
            if (sameSlot) implementation = candidate;
            nearest = candidate;
        }
        return implementation.HasBody;
    }

    private static string EntrySymbol(TypeReference reference) =>
        reference.GetElementType().Scope.Name.Replace(".dll", "", StringComparison.Ordinal) + ":" + reference.GetElementType().FullName;

    private static IEnumerable<TypeDefinition> EntryAncestors(TypeDefinition type, Dictionary<string, TypeDefinition> map)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var current = type; visited.Add(EntrySymbol(current));)
        {
            yield return current;
            if (current.BaseType is not { } parent || !map.TryGetValue(EntrySymbol(parent), out var next)) yield break;
            current = next;
        }
    }

    private static bool EntryCallable(MethodDefinition candidate, MethodReference target) =>
        EntryMemberShape(candidate, target)
        || candidate.Overrides.Any(overridden => EntrySymbol(overridden.DeclaringType) == EntrySymbol(target.DeclaringType)
            && EntryMemberShape(overridden, target));

    // Explicit implementations name their interface in the MethodDef name; their
    // MethodImpl override names the actual interface member. Inherited ordinary
    // implementations are found on the base, even when only the leaf declares
    // the interface. Do not approximate either relation by an unqualified name.
    private static bool EntryMemberShape(MethodReference candidate, MethodReference target)
    {
        if (candidate.Name != target.Name || candidate.HasThis != target.HasThis || candidate.ExplicitThis != target.ExplicitThis
            || candidate.CallingConvention != target.CallingConvention
            || candidate.GenericParameters.Count != (target is GenericInstanceMethod instance ? instance.ElementMethod.GenericParameters.Count : target.GenericParameters.Count)
            || candidate.Parameters.Count != target.Parameters.Count) return false;
        var left = candidate.Parameters.Select(parameter => EntryTypeShape(parameter.ParameterType)).Append(EntryTypeShape(candidate.ReturnType)).ToArray();
        var right = target.Parameters.Select(parameter => EntryTypeShape(parameter.ParameterType)).Append(EntryTypeShape(target.ReturnType)).ToArray();
        return left.All(shape => shape is not null) && right.All(shape => shape is not null) && left.SequenceEqual(right, StringComparer.Ordinal);
    }

    private static string? EntryTypeShape(TypeReference reference) => reference switch
    {
        GenericParameter { Owner: TypeReference owner } parameter => EntrySymbol(owner) + "!" + parameter.Position,
        GenericParameter { Owner: MethodReference owner } parameter => EntrySymbol(owner.DeclaringType) + "::" + owner.Name + "!!" + parameter.Position,
        GenericParameter => null,
        GenericInstanceType generic => EntryComposedShape(EntrySymbol(generic.ElementType), generic.GenericArguments),
        ArrayType array => EntryComposedShape("array:" + array.Rank + ":" + array.IsVector + ":"
            + string.Join(",", array.Dimensions.Select(dimension => dimension.LowerBound + ":" + dimension.UpperBound)), [array.ElementType]),
        OptionalModifierType modifier => EntryComposedShape("optional", [modifier.ModifierType, modifier.ElementType]),
        RequiredModifierType modifier => EntryComposedShape("required", [modifier.ModifierType, modifier.ElementType]),
        ByReferenceType byReference => EntryComposedShape("byref", [byReference.ElementType]),
        PointerType pointer => EntryComposedShape("pointer", [pointer.ElementType]),
        PinnedType pinned => EntryComposedShape("pinned", [pinned.ElementType]),
        SentinelType sentinel => EntryComposedShape("sentinel", [sentinel.ElementType]),
        TypeSpecification => null,
        _ => EntrySymbol(reference),
    };

    private static string? EntryComposedShape(string owner, IEnumerable<TypeReference> arguments)
    {
        var shapes = arguments.Select(EntryTypeShape).ToArray();
        return shapes.Any(shape => shape is null) ? null : owner + "<" + string.Join(",", shapes) + ">";
    }

    private static string EntryPortSymbol(string port) => HttpEntrySubjects.Single(subject => subject.Name == port).Assembly + ":" + port;

    private static bool ExactEntryPortMember(MethodReference target, MethodDefinition expected) =>
        EntrySymbol(target.DeclaringType) == EntrySymbol(expected.DeclaringType) && EntryMemberShape(target, expected);

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

    private static void AssertEntryClassDispatchControls(ModuleDefinition[] modules)
    {
        var census = Census.Borrow(modules);
        var caller = census.Body(Entry, "TryPage");
        var module = caller.Module;
        foreach (var scenario in new[] { "callvirt", "ldvirtftn", "call", "ldftn", "newslot", "intervening-newslot", "nonvirtual-target", "final-target", "final-override" })
        {
            var root = new TypeDefinition("NativeControl", "EntryRoot", TypeAttributes.NotPublic, module.TypeSystem.Object);
            var parent = new TypeDefinition("NativeControl", "EntryBase", TypeAttributes.NotPublic, root);
            var middle = new TypeDefinition("NativeControl", "EntryMiddle", TypeAttributes.NotPublic, parent);
            var child = new TypeDefinition("NativeControl", "EntryDerived", TypeAttributes.NotPublic | TypeAttributes.Sealed,
                scenario == "intervening-newslot" ? middle : parent);
            Read(root, MethodAttributes.Virtual | MethodAttributes.NewSlot);
            var member = Read(parent, scenario switch
            {
                "nonvirtual-target" => 0,
                "final-target" => MethodAttributes.Virtual | MethodAttributes.Final,
                _ => MethodAttributes.Virtual,
            });
            Read(middle, MethodAttributes.Virtual | MethodAttributes.NewSlot);
            var execute = Read(child, MethodAttributes.Virtual
                | (scenario is "newslot" or "nonvirtual-target" or "final-target" ? MethodAttributes.NewSlot : 0)
                | (scenario == "final-override" ? MethodAttributes.Final : 0));
            var rootConstructor = Constructor(root, module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!));
            var parentConstructor = Constructor(parent, rootConstructor);
            var middleConstructor = Constructor(middle, parentConstructor);
            var constructor = Constructor(child, scenario == "intervening-newslot" ? middleConstructor : parentConstructor);
            var subjects = new[] { root, parent, middle, child };
            foreach (var subject in subjects) module.Types.Add(subject);
            var instructions = new List<Instruction> { Instruction.Create(OpCodes.Newobj, constructor) };
            if (scenario is "ldftn" or "ldvirtftn")
            {
                if (scenario == "ldvirtftn") instructions.Add(Instruction.Create(OpCodes.Dup));
                instructions.Add(Instruction.Create(scenario == "ldvirtftn" ? OpCodes.Ldvirtftn : OpCodes.Ldftn, member));
                instructions.Add(Instruction.Create(OpCodes.Newobj,
                    module.ImportReference(typeof(Action).GetConstructor([typeof(object), typeof(nint)])!)));
                instructions.Add(Instruction.Create(OpCodes.Callvirt, module.ImportReference(typeof(Action).GetMethod(nameof(Action.Invoke))!)));
            }
            else instructions.Add(Instruction.Create(scenario == "call" ? OpCodes.Call : OpCodes.Callvirt, member));
            // Preserve TryPage's existing bool on the stack: the injected object
            // construction and void call consume only their own operands.
            var anchor = caller.Body.Instructions.First(instruction => instruction.OpCode == OpCodes.Ret);
            var processor = caller.Body.GetILProcessor();
            var maxStack = caller.Body.MaxStackSize;
            caller.Body.MaxStackSize += 3;
            foreach (var instruction in instructions) processor.InsertBefore(anchor, instruction);
            try
            {
                HttpEntryOffenders(modules).Should().BeEmpty("the actual TryPage " + scenario + " path begins data-only");
                DirtyBody(member, true, "the declared base body proves this same detector remains active");
                DirtyBody(execute, scenario is "callvirt" or "ldvirtftn" or "final-override",
                    "only genuine dynamic dispatch to this inherited slot reaches the derived body: " + scenario);
                HttpEntryOffenders(modules).Should().BeEmpty("restoring both bodies restores this exact dispatch graph");
            }
            finally
            {
                foreach (var instruction in instructions) caller.Body.Instructions.Remove(instruction);
                caller.Body.MaxStackSize = maxStack;
                foreach (var subject in subjects) module.Types.Remove(subject);
            }
            HttpEntryOffenders(modules).Should().BeEmpty("the actual TryPage body and type census were restored");

            void DirtyBody(MethodDefinition method, bool expected, string because)
            {
                var persistence = new VariableDefinition(module.ImportReference(typeof(Npgsql.NpgsqlDataSource)));
                method.Body.Variables.Add(persistence);
                try
                {
                    var errors = HttpEntryOffenders(modules).ToArray();
                    if (expected) errors.Should().Contain(error => error.StartsWith("Global-self helper persistence/dispatch bypass:", StringComparison.Ordinal)
                        && error.Contains(method.FullName, StringComparison.Ordinal), because);
                    else errors.Should().BeEmpty(because);
                }
                finally { method.Body.Variables.Remove(persistence); }
                HttpEntryOffenders(modules).Should().BeEmpty("the dirty local was removed from " + method.FullName);
            }

            MethodDefinition Read(TypeDefinition type, MethodAttributes attributes)
            {
                var method = new MethodDefinition("Read", MethodAttributes.Public | MethodAttributes.HideBySig | attributes, module.TypeSystem.Void);
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                type.Methods.Add(method);
                return method;
            }

            MethodDefinition Constructor(TypeDefinition type, MethodReference baseConstructor)
            {
                var method = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.HideBySig
                    | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, module.TypeSystem.Void);
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, baseConstructor));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                type.Methods.Add(method);
                return method;
            }
        }
    }

    private static void AssertEntryInterfaceControls(ModuleDefinition[] modules)
    {
        var census = Census.Borrow(modules);
        var caller = census.Body(Entry, "TryPage");
        var module = caller.Module;
        foreach (var scenario in new[] { "explicit", "inherited-interface", "inherited-method" })
        {
            var contract = new TypeDefinition("NativeControl", "IEntryHelper", TypeAttributes.NotPublic | TypeAttributes.Interface | TypeAttributes.Abstract);
            var member = new MethodDefinition("Read", MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual | MethodAttributes.NewSlot, module.TypeSystem.Void);
            contract.Methods.Add(member);
            var alias = new TypeDefinition("NativeControl", "IEntryHelperAlias", TypeAttributes.NotPublic | TypeAttributes.Interface | TypeAttributes.Abstract);
            alias.Interfaces.Add(new InterfaceImplementation(contract));
            var parent = new TypeDefinition("NativeControl", "EntryHelperBase", TypeAttributes.NotPublic, module.TypeSystem.Object);
            var implementation = new TypeDefinition("NativeControl", "EntryHelper", TypeAttributes.NotPublic | TypeAttributes.Sealed,
                scenario == "inherited-method" ? parent : module.TypeSystem.Object);
            implementation.Interfaces.Add(new InterfaceImplementation(scenario == "inherited-interface" ? alias : contract));
            var execute = new MethodDefinition(scenario == "inherited-method" ? "Read" : contract.FullName + ".Read",
                scenario == "inherited-method" ? MethodAttributes.Public : MethodAttributes.Private | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot,
                module.TypeSystem.Void);
            if (scenario != "inherited-method") execute.Overrides.Add(member);
            execute.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            (scenario == "inherited-method" ? parent : implementation).Methods.Add(execute);
            foreach (var subject in new[] { contract, alias, parent, implementation }) module.Types.Add(subject);
            var invocation = Instruction.Create(OpCodes.Callvirt, member);
            caller.Body.Instructions.Add(invocation);
            try
            {
                HttpEntryOffenders(modules).Should().BeEmpty("the executed " + scenario + " implementation is initially data-only");
                var persistence = new VariableDefinition(module.ImportReference(typeof(Npgsql.NpgsqlDataSource)));
                execute.Body.Variables.Add(persistence);
                try
                {
                    HttpEntryOffenders(modules).Should().Contain(error => error.StartsWith("Global-self helper persistence/dispatch bypass:", StringComparison.Ordinal)
                        && error.Contains(execute.FullName, StringComparison.Ordinal), "the same live detector must reach " + scenario + " implementation bodies");
                }
                finally { execute.Body.Variables.Remove(persistence); }
                HttpEntryOffenders(modules).Should().BeEmpty("restoring the actual implementation restores the clean member graph");
            }
            finally
            {
                caller.Body.Instructions.Remove(invocation);
                foreach (var subject in new[] { contract, alias, parent, implementation }) module.Types.Remove(subject);
            }
        }
    }

    private static void AssertEntryPortIdentityControls(ModuleDefinition[] modules)
    {
        var census = Census.Borrow(modules);
        foreach (var call in EntryCalls)
        {
            var body = census.Body(Entry, call.Owner);
            var instruction = body.Body.Instructions.Single(item => item.Operand is MethodReference target
                && target.DeclaringType.FullName == call.Port && target.Name == call.Method);
            var original = (MethodReference)instruction.Operand;
            var foreign = new TypeReference(original.DeclaringType.Namespace, original.DeclaringType.Name, body.Module,
                new AssemblyNameReference("NativeControl.ForeignPort", new Version(1, 0)));
            var replacement = new MethodReference(original.Name, original.ReturnType, foreign)
            { HasThis = original.HasThis, ExplicitThis = original.ExplicitThis, CallingConvention = original.CallingConvention };
            foreach (var parameter in original.Parameters) replacement.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
            instruction.Operand = replacement;
            try
            {
                HttpEntryOffenders(modules).Should().Contain("Global-self direct port callers changed: " + call.Port + "." + call.Method,
                "a familiar port name in another assembly is not the admitted persistence boundary");
            }
            finally { instruction.Operand = original; }
            HttpEntryOffenders(modules).Should().BeEmpty("restoring the actual port operand restores the same live detector");
        }
    }

    internal static void AssertHttpEntryControls(ModuleDefinition[] modules)
    {
        var census = Census.Borrow(modules);
        HttpEntryOffenders(modules).Should().BeEmpty();
        AssertHttpEntryPopulationControls(modules);
        AssertEntryInterfaceControls(modules);
        AssertEntryPortIdentityControls(modules);
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
