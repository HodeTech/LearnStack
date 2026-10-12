using FluentAssertions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>ADR-0046: the dispatcher owns the restricted terminal-transition capability.</summary>
public sealed class OutboxDispatchConventionTests
{
    private const string KernelAssembly = "LearnStack.SharedKernel";
    private const string InfrastructureAssembly = "LearnStack.Infrastructure";
    private const string AuditAssembly = "LearnStack.Infrastructure.Audit";
    private const string Witness = "LearnStack.SharedKernel.Audit.EventTerminalTransition";
    private const string MintingSource = Witness + "/MintingSource";
    private const string Store = "LearnStack.Infrastructure.Messaging.OutboxDispatchStore";
    private const string AuditPort = "LearnStack.SharedKernel.Audit.IAuditStore";
    private const string AuditStore = "LearnStack.Infrastructure.Audit.PostgresAuditStore";
    private const string Mint = "FromProducerUpdate";
    private const string Terminalize = "TryTerminalizeAsync";
    private const string TerminalWrite = "WriteEventTerminalAsync";
    private static readonly string[] MintParameters =
    [
        "System.Data.Common.DbConnection", "System.Data.Common.DbTransaction",
        "System.Data.Common.DbDataReader", "System.String",
    ];
    private static readonly string[] TerminalWriteParameters =
    [
        "LearnStack.SharedKernel.Audit.AuditEntryDraft", Witness,
        "System.Data.Common.DbConnection", "System.Data.Common.DbTransaction",
        "System.Threading.CancellationToken",
    ];
    private static readonly string[] ResolutionRoots =
    [
        "LearnStack.Infrastructure.Messaging.OutboxEventRegistry",
        "LearnStack.Infrastructure.Messaging.OutboxEnvelopeReader",
        Store,
    ];

    /// <summary>
    /// ADR-0046, Atomic terminal audit and privilege boundary: restricted minting and
    /// fifth-write callers are closed over every production assembly. PostgreSQL tests
    /// separately prove that the live UPDATE result and draft identity agree.
    /// </summary>
    [Fact]
    public void Outbox_Terminal_Audit_Identity_Is_Row_Bound()
    {
        using var census = ProductionCensus.Read();
        TerminalOffenders(census).Should().BeEmpty(
            "Fix: keep the immutable private witness, its sole dispatcher minting subtype, "
            + "and the terminal writer/call sites closed; keep row/transaction identity checks in the real audit seam");
    }

    [Fact]
    public void Terminal_census_detects_extra_derivers_constructors_and_mint_callers()
    {
        using var census = ProductionCensus.Read();
        var source = census.Types[(KernelAssembly, MintingSource)];
        var factory = source.Methods.Single(method => method.Name == Mint);
        var constructor = census.Types[(KernelAssembly, Witness)].Methods.Single(method => method.IsConstructor);
        var application = census.Modules.Single(module => module.Assembly.Name.Name == "LearnStack.Application");

        AssertTerminalMutation(census, "a second minting subtype in another production assembly", () =>
        {
            var type = AddProbe(application, "AdditionalMintingSource");
            type.BaseType = application.ImportReference(source);
            return () => application.Types.Remove(type);
        });
        AssertTerminalMutation(census, "an inherited minting subtype", () =>
        {
            var parent = AddProbe(application, "AbstractMintingSource");
            parent.Attributes |= TypeAttributes.Abstract;
            parent.BaseType = application.ImportReference(source);
            var child = AddProbe(application, "InheritedMintingSource");
            child.BaseType = parent;
            return () => { application.Types.Remove(child); application.Types.Remove(parent); };
        });
        foreach (var opcode in new[] { OpCodes.Call, OpCodes.Ldftn })
        {
            AssertTerminalMutation(census, "a cross-assembly mint helper using " + opcode.Name, () =>
                AddCallProbe(application, "AdditionalMintCaller", factory, opcode));
        }
        AssertTerminalMutation(census, "a direct private witness constructor call", () =>
            AddCallProbe(application, "AdditionalWitnessConstructor", constructor, OpCodes.Newobj));
        AssertTerminalMutation(census, "an async mint helper with an identified unauthorized owner", () =>
            AddAsyncCallProbe(application, "AsyncMintCaller", factory));
        AssertTerminalMutation(census, "a helper on the sanctioned dispatcher type", () =>
        {
            var type = census.Types[(InfrastructureAssembly, Store)];
            var method = AddCall(type, "UnrelatedHelper", factory, OpCodes.Call);
            return () => type.Methods.Remove(method);
        });
        AssertTerminalMutation(census, "a generated name that has no real async owner", () =>
        {
            var type = census.Types[(InfrastructureAssembly, Store)];
            var generated = new TypeDefinition(string.Empty, "<TryTerminalizeAsync>d__999", TypeAttributes.NestedPrivate);
            generated.BaseType = type.Module.TypeSystem.Object;
            type.NestedTypes.Add(generated);
            AddCall(generated, "MoveNext", factory, OpCodes.Call);
            return () => type.NestedTypes.Remove(generated);
        });
    }

    [Fact]
    public void Terminal_census_detects_interface_concrete_delegate_async_and_extra_writer_paths()
    {
        using var census = ProductionCensus.Read();
        var port = census.Types[(KernelAssembly, AuditPort)];
        var writer = census.Types[(AuditAssembly, AuditStore)];
        var interfaceWrite = port.Methods.Single(method => method.Name == TerminalWrite);
        var concreteWrite = writer.Methods.Single(method => method.Name == TerminalWrite);
        var application = census.Modules.Single(module => module.Assembly.Name.Name == "LearnStack.Application");

        foreach (var target in new[] { interfaceWrite, concreteWrite })
            foreach (var opcode in new[] { OpCodes.Call, OpCodes.Callvirt, OpCodes.Ldftn, OpCodes.Ldvirtftn })
            {
                AssertTerminalMutation(census, "an extra " + target.DeclaringType.Name + " caller using " + opcode.Name, () =>
                    AddCallProbe(application, "AdditionalTerminalWriter", target, opcode));
            }
        AssertTerminalMutation(census, "an async helper with an identified unauthorized owner", () =>
            AddAsyncCallProbe(application, "AsyncTerminalWriter", interfaceWrite));
        AssertTerminalMutation(census, "a second IAuditStore implementation", () =>
        {
            var type = AddProbe(application, "AdditionalAuditStore");
            type.Interfaces.Add(new InterfaceImplementation(application.ImportReference(port)));
            var method = new MethodDefinition(TerminalWrite, MethodAttributes.Public,
                application.ImportReference(interfaceWrite.ReturnType));
            foreach (var parameter in interfaceWrite.Parameters)
                method.Parameters.Add(new ParameterDefinition(application.ImportReference(parameter.ParameterType)));
            type.Methods.Add(method);
            return () => application.Types.Remove(type);
        });
        AssertTerminalMutation(census, "an inherited concrete IAuditStore implementation", () =>
        {
            var type = AddProbe(application, "InheritedAuditStore");
            type.BaseType = application.ImportReference(writer);
            return () => application.Types.Remove(type);
        });
    }

    [Fact]
    public void Terminal_census_detects_mutability_accessibility_missing_subjects_and_friend_access()
    {
        using var census = ProductionCensus.Read();
        var witness = census.Types[(KernelAssembly, Witness)];
        var source = census.Types[(KernelAssembly, MintingSource)];
        var factory = source.Methods.Single(method => method.Name == Mint);
        var constructor = witness.Methods.Single(method => method.IsConstructor);
        AssertTerminalMutation(census, "an unsealed witness", () =>
            ChangeAttributes(() => witness.Attributes, value => witness.Attributes = value,
                witness.Attributes & ~TypeAttributes.Sealed));
        AssertTerminalMutation(census, "a public witness constructor", () =>
            ChangeAttributes(() => constructor.Attributes, value => constructor.Attributes = value,
                (constructor.Attributes & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Public));
        AssertTerminalMutation(census, "a mutable witness field", () =>
        {
            var field = witness.Fields.First(field => !field.IsStatic);
            return ChangeAttributes(() => field.Attributes, value => field.Attributes = value,
                field.Attributes & ~FieldAttributes.InitOnly);
        });
        AssertTerminalMutation(census, "a witness setter", () =>
        {
            var property = witness.Properties.First();
            var setter = new MethodDefinition("set_" + property.Name, MethodAttributes.Public, witness.Module.TypeSystem.Void);
            var original = property.SetMethod;
            property.SetMethod = setter;
            return () => property.SetMethod = original;
        });
        AssertTerminalMutation(census, "a public minting seam", () =>
            ChangeAttributes(() => factory.Attributes, value => factory.Attributes = value,
                (factory.Attributes & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Public));
        AssertTerminalMutation(census, "a caller-identity mint signature", () =>
        {
            var parameter = factory.Parameters[2];
            var original = parameter.ParameterType;
            parameter.ParameterType = witness.Module.TypeSystem.String;
            return () => parameter.ParameterType = original;
        });
        AssertTerminalMutation(census, "a caller-identity witness constructor", () =>
        {
            var parameter = constructor.Parameters[2];
            var original = parameter.ParameterType;
            parameter.ParameterType = witness.Module.TypeSystem.String;
            return () => parameter.ParameterType = original;
        });
        foreach (var name in new[] { Mint, TerminalWrite })
        {
            AssertTerminalMutation(census, "an empty " + name + " call-site census", () =>
            {
                var owner = census.Types.Values.SelectMany(type => type.Methods).Single(method => method.HasBody
                    && method.Body.Instructions.Any(instruction => instruction.Operand is MethodReference target
                        && target.Name == name));
                var instruction = owner.Body.Instructions.Single(instruction => instruction.Operand is MethodReference target
                    && target.Name == name);
                var index = owner.Body.Instructions.IndexOf(instruction);
                owner.Body.Instructions.RemoveAt(index);
                return () => owner.Body.Instructions.Insert(index, instruction);
            });
        }
        AssertTerminalMutation(census, "a missing dispatcher", () =>
        {
            var type = census.Types[(InfrastructureAssembly, Store)];
            var module = type.Module;
            module.Types.Remove(type);
            return () => module.Types.Add(type);
        });
        AssertTerminalMutation(census, "a SharedKernel friend assembly", () =>
        {
            var module = witness.Module;
            var attribute = new CustomAttribute(module.ImportReference(
                typeof(System.Runtime.CompilerServices.InternalsVisibleToAttribute).GetConstructor([typeof(string)])!));
            attribute.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String, "ReviewProbe"));
            module.Assembly.CustomAttributes.Add(attribute);
            return () => module.Assembly.CustomAttributes.Remove(attribute);
        });
        AssertTerminalMutation(census, "an audit-store constructor without the required catalogue", () =>
        {
            var type = census.Types[(AuditAssembly, AuditStore)];
            var method = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.SpecialName
                | MethodAttributes.RTSpecialName, type.Module.TypeSystem.Void);
            type.Methods.Add(method);
            return () => type.Methods.Remove(method);
        });
    }

    /// <summary>
    /// ADR-0046: persisted type text selects a declared registry entry. This bounded
    /// production helper walk bans named dynamic-loading/construction APIs; it does
    /// not claim arbitrary reflection or string-dataflow analysis.
    /// </summary>
    [Fact]
    public void Outbox_Type_Resolution_Is_Declared_And_Bounded()
    {
        using var census = ProductionCensus.Read();
        ResolutionOffenders(census).Should().BeEmpty(
            "Fix: resolve stored identities only through the declared registry and deserialize its EventType; "
            + "remove dynamic type loading or Activator construction from the resolver/validator/store helper graph");
    }

    [Fact]
    public void Resolution_walk_detects_loading_construction_async_and_polymorphic_helpers()
    {
        using var census = ProductionCensus.Read();
        var application = census.Modules.Single(module => module.Assembly.Name.Name == "LearnStack.Application");
        var root = census.Types[(InfrastructureAssembly, ResolutionRoots[0])];
        var targets = new[]
        {
            typeof(Type).GetMethod(nameof(Type.GetType), [typeof(string)])!,
            typeof(System.Reflection.Assembly).GetMethod(nameof(System.Reflection.Assembly.Load), [typeof(string)])!,
            typeof(System.Reflection.Assembly).GetMethod(nameof(System.Reflection.Assembly.LoadFrom), [typeof(string)])!,
            typeof(System.Reflection.Assembly).GetMethod(nameof(System.Reflection.Assembly.GetType), [typeof(string)])!,
            typeof(Activator).GetMethod(nameof(Activator.CreateInstance), [typeof(Type)])!,
        };
        foreach (var target in targets)
            foreach (var opcode in new[] { OpCodes.Call, OpCodes.Ldftn })
            {
                AssertResolutionMutation(census, target.DeclaringType!.Name + "." + target.Name + " using " + opcode.Name, () =>
                {
                    var helper = AddProbe(application, "DynamicResolutionHelper");
                    AddCall(helper, "Resolve", application.ImportReference(target), opcode);
                    var field = new FieldDefinition("ReviewHelper", FieldAttributes.Private, root.Module.ImportReference(helper));
                    root.Fields.Add(field);
                    return () => { root.Fields.Remove(field); application.Types.Remove(helper); };
                });
            }
        AssertResolutionMutation(census, "an async cross-assembly resolver helper", () =>
        {
            var helper = AddProbe(application, "AsyncResolutionHelper");
            var undoAsync = AddAsyncCall(helper, "Resolve", application.ImportReference(targets[0]));
            var field = new FieldDefinition("ReviewHelper", FieldAttributes.Private, root.Module.ImportReference(helper));
            root.Fields.Add(field);
            return () => { root.Fields.Remove(field); undoAsync(); application.Types.Remove(helper); };
        });
        foreach (var isInterface in new[] { true, false })
        {
            AssertResolutionMutation(census, "a " + (isInterface ? "interface" : "virtual") + " resolver helper", () =>
            {
                var contract = AddProbe(application, "ResolutionContract");
                if (isInterface) contract.Attributes |= TypeAttributes.Interface | TypeAttributes.Abstract;
                var helper = AddProbe(application, "ResolutionImplementation");
                if (isInterface) helper.Interfaces.Add(new InterfaceImplementation(contract));
                else helper.BaseType = contract;
                AddCall(helper, "Resolve", application.ImportReference(targets[0]), OpCodes.Call);
                var field = new FieldDefinition("ReviewHelper", FieldAttributes.Private, root.Module.ImportReference(contract));
                root.Fields.Add(field);
                return () => { root.Fields.Remove(field); application.Types.Remove(helper); application.Types.Remove(contract); };
            });
        }
    }

    [Fact]
    public void Resolution_walk_preserves_declared_type_metadata_and_clean_helpers()
    {
        using var census = ProductionCensus.Read();
        var application = census.Modules.Single(module => module.Assembly.Name.Name == "LearnStack.Application");
        var root = census.Types[(InfrastructureAssembly, ResolutionRoots[0])];
        var helper = AddProbe(application, "DeclaredTypeHelper");
        AddCall(helper, "ReadKnownAssembly", application.ImportReference(
            typeof(Type).GetProperty(nameof(Type.Assembly))!.GetMethod!), OpCodes.Callvirt);
        AddCall(helper, "ReadRuntimeType", application.ImportReference(
            typeof(object).GetMethod(nameof(GetType))!), OpCodes.Callvirt);
        var field = new FieldDefinition("ReviewHelper", FieldAttributes.Private, root.Module.ImportReference(helper));
        root.Fields.Add(field);
        try
        {
            ResolutionOffenders(census).Should().BeEmpty(
                "Fix: preserve metadata reads of already declared types and clean cross-assembly helpers");
        }
        finally
        {
            root.Fields.Remove(field);
            application.Types.Remove(helper);
        }
    }

    private static SortedSet<string> TerminalOffenders(ProductionCensus census)
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        var types = census.Types;
        CheckSet(offenders, "production assemblies", census.Modules.Select(module => module.Assembly.Name.Name), ProductionAssemblies.Names());
        foreach (var key in new[] { (KernelAssembly, Witness), (KernelAssembly, MintingSource),
                     (InfrastructureAssembly, Store), (KernelAssembly, AuditPort), (AuditAssembly, AuditStore) })
        {
            if (!types.ContainsKey(key)) offenders.Add("Missing production subject: " + key.Item2);
        }
        if (offenders.Count != 0) return offenders;

        var witness = types[(KernelAssembly, Witness)];
        var source = types[(KernelAssembly, MintingSource)];
        var store = types[(InfrastructureAssembly, Store)];
        var port = types[(KernelAssembly, AuditPort)];
        var auditStore = types[(AuditAssembly, AuditStore)];
        var constructors = witness.Methods.Where(method => method.IsConstructor && !method.IsStatic).ToArray();
        if (!witness.IsSealed || witness.BaseType?.FullName != "System.Object") offenders.Add(Witness + ": must be sealed with no custom base");
        if (constructors.Length != 1 || constructors.Any(method => !method.IsPrivate || !ParametersMatch(method, MintParameters)))
            offenders.Add(Witness + ": requires exactly one private live-reader constructor");
        if (witness.Fields.All(field => field.IsStatic) || witness.Fields.Any(field => !field.IsStatic && !field.IsInitOnly))
            offenders.Add(Witness + ": instance fields must be nonempty and readonly");
        if (witness.Properties.Count == 0 || witness.Properties.Any(property => property.SetMethod is not null))
            offenders.Add(Witness + ": properties must be nonempty and getter-only");
        if (!source.IsNestedPublic || !source.IsAbstract || source.IsSealed)
            offenders.Add(MintingSource + ": requires the public abstract nested minting capability");
        if (source.Methods.Where(method => method.IsConstructor && !method.IsStatic).Any(method => !method.IsFamily))
            offenders.Add(MintingSource + ": constructors must be protected");
        if (witness.Module.Assembly.CustomAttributes.Any(attribute =>
                attribute.AttributeType.FullName == "System.Runtime.CompilerServices.InternalsVisibleToAttribute"))
            offenders.Add(KernelAssembly + ": friend assemblies reopen restricted authority");

        var factories = source.Methods.Where(method => method.Name == Mint).ToArray();
        if (factories.Length != 1 || factories.Any(method => !method.IsFamily || !method.IsStatic
                || method.ReturnType.FullName != Witness || !ParametersMatch(method, MintParameters)))
            offenders.Add(MintingSource + "." + Mint + ": requires the protected static live-reader mint signature");
        var terminalizers = store.Methods.Where(method => method.Name == Terminalize).ToArray();
        if (terminalizers.Length != 1) offenders.Add(Store + "." + Terminalize + ": requires one concrete terminalization owner");
        CheckSet(offenders, "minting subtypes", types.Values.Where(type => Derives(type, source, types)).Select(TypeId), [TypeId(store)]);
        CheckSet(offenders, "witness-producing methods", types.Values.SelectMany(type => type.Methods)
            .Where(method => method.ReturnType.FullName == Witness).Select(MethodId), factories.Select(MethodId));

        var writers = types.Values.Where(type => !type.IsInterface && !type.IsAbstract && Derives(type, port, types)).ToArray();
        CheckSet(offenders, "audit store implementations", writers.Select(TypeId), [TypeId(auditStore)]);
        var declarations = types.Values.Where(type => SameType(type, port) || Derives(type, port, types))
            .SelectMany(type => type.Methods).Where(IsTerminalWriteName).ToArray();
        CheckSet(offenders, "terminal audit declarations", declarations.Select(method => TypeId(method.DeclaringType) + "." + method.Name),
            [TypeId(port) + "." + TerminalWrite, TypeId(auditStore) + "." + TerminalWrite]);
        foreach (var method in declarations.Where(method => method.ReturnType.FullName != "System.Threading.Tasks.Task"
                     || !ParametersMatch(method, TerminalWriteParameters)))
            offenders.Add(MethodId(method) + ": wrong fifth-write signature");
        var auditConstructors = auditStore.Methods.Where(method => method.IsConstructor && !method.IsStatic).ToArray();
        if (auditConstructors.Length == 0 || auditConstructors.Any(method => method.Parameters.All(parameter =>
                parameter.ParameterType.FullName != "LearnStack.SharedKernel.Audit.IAuditCatalog")))
            offenders.Add(AuditStore + ": every constructor must require IAuditCatalog");

        var sites = CallSites(census).ToArray();
        CheckSet(offenders, "witness constructor call sites", sites.Where(site => site.Target.Name == ".ctor"
                && SameType(site.Target.DeclaringType, witness)).Select(site => site.Owner), factories.Select(MethodId));
        CheckSet(offenders, "mint call sites", sites.Where(site => site.Target.Name == Mint
                && (SameType(site.Target.DeclaringType, source) || Derives(site.Target.DeclaringType, source, types)))
            .Select(site => site.Owner), terminalizers.Select(MethodId));
        CheckSet(offenders, "terminal audit call sites", sites.Where(site => IsTerminalWriteName(site.Target)
                && (SameType(site.Target.DeclaringType, port) || Derives(site.Target.DeclaringType, port, types)))
            .Select(site => site.Owner), terminalizers.Select(MethodId));
        return offenders;
    }

    private static SortedSet<string> ResolutionOffenders(ProductionCensus census)
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        var types = census.Types;
        CheckSet(offenders, "production assemblies", census.Modules.Select(module => module.Assembly.Name.Name), ProductionAssemblies.Names());
        foreach (var name in ResolutionRoots)
            if (!types.ContainsKey((InfrastructureAssembly, name))) offenders.Add("Missing production resolution subject: " + name);
        if (offenders.Count != 0) return offenders;

        var pending = new Queue<TypeDefinition>(ResolutionRoots.Select(name => types[(InfrastructureAssembly, name)]));
        var visited = new HashSet<(string Assembly, string Type)>();
        while (pending.TryDequeue(out var type))
        {
            if (!visited.Add(Key(type))) continue;
            foreach (var method in Il.Methods(type).Select(item => item.Definition).Where(method => method.HasBody))
                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.Operand is MethodReference target && DynamicResolution(target))
                        offenders.Add(MethodId(method) + " -> " + target.FullName);
                }
            foreach (var reference in Il.ReferencedTypeReferences(type))
            {
                if (!types.TryGetValue(Key(reference), out var helper)) continue;
                pending.Enqueue(helper);
                if (helper.IsSealed) continue;
                foreach (var implementation in types.Values.Where(candidate => !candidate.IsInterface
                             && Derives(candidate, helper, types)))
                    pending.Enqueue(implementation);
            }
        }
        return offenders;
    }

    private static bool DynamicResolution(MethodReference target) => target.DeclaringType.FullName switch
    {
        "System.Type" => target.Name == "GetType",
        "System.Reflection.Assembly" => target.Name is "GetType" or "Load" or "LoadFrom" or "LoadFile" or "UnsafeLoadFrom",
        "System.Activator" => target.Name is "CreateInstance" or "CreateInstanceFrom",
        _ => false,
    };

    private static IEnumerable<(string Owner, MethodReference Target)> CallSites(ProductionCensus census)
    {
        // Attribute mapping is deliberate: a nested type merely named
        // <TryTerminalizeAsync>d__123 must not inherit that owner's authority.
        var asyncOwners = new Dictionary<(string Assembly, string Type), string>();
        foreach (var method in census.Types.Values.SelectMany(type => type.Methods))
            foreach (var attribute in method.CustomAttributes.Where(attribute =>
                         attribute.AttributeType.FullName is "System.Runtime.CompilerServices.AsyncStateMachineAttribute"
                             or "System.Runtime.CompilerServices.IteratorStateMachineAttribute"
                             or "System.Runtime.CompilerServices.AsyncIteratorStateMachineAttribute"))
            {
                if (attribute.ConstructorArguments.FirstOrDefault().Value is TypeReference generated)
                    asyncOwners[Key(generated)] = MethodId(method);
            }
        foreach (var method in census.Types.Values.SelectMany(type => type.Methods).Where(method => method.HasBody))
        {
            var owner = method.Name == "MoveNext" && asyncOwners.TryGetValue(Key(method.DeclaringType), out var original)
                ? original : MethodId(method);
            foreach (var instruction in method.Body.Instructions)
            {
                // Includes call/callvirt, newobj, ldftn and ldvirtftn. A delegate
                // cannot hide a capability just because its invocation occurs later.
                if (instruction.Operand is MethodReference target) yield return (owner, target);
            }
        }
    }

    private static bool Derives(TypeReference candidate, TypeDefinition contract,
        Dictionary<(string Assembly, string Type), TypeDefinition> types)
    {
        if (!types.TryGetValue(Key(candidate), out var definition)) return false;
        var pending = new Queue<TypeReference>(definition.Interfaces.Select(item => item.InterfaceType));
        if (definition.BaseType is { } parent) pending.Enqueue(parent);
        var visited = new HashSet<(string Assembly, string Type)>();
        while (pending.TryDequeue(out var reference))
        {
            if (!visited.Add(Key(reference))) continue;
            if (SameType(reference, contract)) return true;
            if (!types.TryGetValue(Key(reference), out var inherited)) continue;
            foreach (var item in inherited.Interfaces) pending.Enqueue(item.InterfaceType);
            if (inherited.BaseType is { } baseType) pending.Enqueue(baseType);
        }
        return false;
    }

    private static bool IsTerminalWriteName(MethodReference method) =>
        method.Name == TerminalWrite || method.Name.EndsWith("." + TerminalWrite, StringComparison.Ordinal);

    private static bool ParametersMatch(MethodReference method, string[] names) =>
        method.Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(names, StringComparer.Ordinal);

    private static bool SameType(TypeReference left, TypeReference right) => Key(left) == Key(right);

    private static (string Assembly, string Type) Key(TypeReference reference)
    {
        if (reference is TypeSpecification specification) return Key(specification.ElementType);
        return (reference.Scope is AssemblyNameReference assembly ? assembly.Name
            : reference.Scope is ModuleDefinition module ? module.Assembly.Name.Name : reference.Scope.Name,
            reference.FullName);
    }

    private static string TypeId(TypeReference type) => Key(type).Assembly + ":" + type.FullName;
    private static string MethodId(MethodReference method) => Key(method.DeclaringType).Assembly + ":" + method.FullName;

    private static void CheckSet(SortedSet<string> offenders, string label, IEnumerable<string> actual, IEnumerable<string> expected)
    {
        var found = actual.Order(StringComparer.Ordinal).ToArray();
        var required = expected.Order(StringComparer.Ordinal).ToArray();
        if (!found.SequenceEqual(required, StringComparer.Ordinal))
            offenders.Add(label + ": expected [" + string.Join(", ", required) + "]; actual [" + string.Join(", ", found) + "]");
    }

    private static void AssertTerminalMutation(ProductionCensus census, string description, Func<Action> mutate) =>
        AssertMutation(census, description, mutate, TerminalOffenders);

    private static void AssertResolutionMutation(ProductionCensus census, string description, Func<Action> mutate) =>
        AssertMutation(census, description, mutate, ResolutionOffenders);

    private static void AssertMutation(ProductionCensus census, string description, Func<Action> mutate,
        Func<ProductionCensus, SortedSet<string>> detector)
    {
        detector(census).Should().BeEmpty("Fix: the planted control requires a clean nonempty production baseline");
        var undo = mutate();
        try
        {
            detector(census).Should().NotBeEmpty("Fix: the same production detector must reject " + description);
        }
        finally
        {
            undo();
        }
        detector(census).Should().BeEmpty("Fix: removing the planted offender must restore a clean production census");
    }

    private static Action ChangeAttributes<T>(Func<T> read, Action<T> write, T replacement)
    {
        var original = read();
        write(replacement);
        return () => write(original);
    }

    private static TypeDefinition AddProbe(ModuleDefinition module, string name)
    {
        var type = new TypeDefinition("ReviewProbe", name, TypeAttributes.Public | TypeAttributes.Class,
            module.TypeSystem.Object);
        module.Types.Add(type);
        return type;
    }

    private static MethodDefinition AddCall(TypeDefinition type, string name, MethodReference target, OpCode opcode)
    {
        var method = new MethodDefinition(name, MethodAttributes.Public | MethodAttributes.Static, type.Module.TypeSystem.Void);
        method.Body.Instructions.Add(Instruction.Create(opcode, type.Module.ImportReference(target)));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        type.Methods.Add(method);
        return method;
    }

    private static Action AddCallProbe(ModuleDefinition module, string name, MethodReference target, OpCode opcode)
    {
        var type = AddProbe(module, name);
        AddCall(type, "Run", target, opcode);
        return () => module.Types.Remove(type);
    }

    private static Action AddAsyncCallProbe(ModuleDefinition module, string name, MethodReference target)
    {
        var type = AddProbe(module, name);
        var undo = AddAsyncCall(type, "Run", target);
        return () => { undo(); module.Types.Remove(type); };
    }

    private static Action AddAsyncCall(TypeDefinition type, string name, MethodReference target)
    {
        var module = type.Module;
        var owner = new MethodDefinition(name, MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
        type.Methods.Add(owner);
        var generated = new TypeDefinition(string.Empty, "<" + name + ">d__999", TypeAttributes.NestedPrivate,
            module.TypeSystem.Object);
        type.NestedTypes.Add(generated);
        AddCall(generated, "MoveNext", target, OpCodes.Call);
        var attribute = new CustomAttribute(module.ImportReference(
            typeof(System.Runtime.CompilerServices.AsyncStateMachineAttribute).GetConstructor([typeof(Type)])!));
        attribute.ConstructorArguments.Add(new CustomAttributeArgument(module.ImportReference(typeof(Type)), generated));
        owner.CustomAttributes.Add(attribute);
        return () => { type.NestedTypes.Remove(generated); type.Methods.Remove(owner); };
    }

    private sealed class ProductionCensus : IDisposable
    {
        private ProductionCensus(List<ModuleDefinition> modules) => Modules = modules;
        public List<ModuleDefinition> Modules { get; }
        // Rebuild after each planted mutation so additions/removals are part of the
        // same census rather than hidden behind the original type-map snapshot.
        public Dictionary<(string Assembly, string Type), TypeDefinition> Types =>
            Modules.SelectMany(module => module.GetTypes()).ToDictionary(Key);

        public static ProductionCensus Read()
        {
            var modules = new List<ModuleDefinition>();
            try
            {
                foreach (var assembly in ProductionAssemblies.All()) modules.Add(ModuleDefinition.ReadModule(assembly.Location));
                return new ProductionCensus(modules);
            }
            catch
            {
                foreach (var module in modules) module.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            foreach (var module in Modules) module.Dispose();
        }
    }
}
