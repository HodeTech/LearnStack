using FluentAssertions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed partial class OutboxDispatchConventionTests
{
    private const string JobGeneration = "LearnStack.Infrastructure.Jobs.HangfireGeneration";
    private const string JobRuntimeGate = "LearnStack.Infrastructure.Jobs.BackgroundJobRuntimeGate";
    private const string JobFatalBoundary = JobRuntimeGate + "/PoolBoundary";
    private const string JobTerminalState = JobRuntimeGate + "/TerminalState";
    private const string JobFatalStage = "LearnStack.Infrastructure.Jobs.HangfireFatalStage";
    private const string JobPoolRegistration = "LearnStack.Infrastructure.Jobs.BackgroundJobWorkerPoolRegistration";
    private const string JobTopology = "LearnStack.Infrastructure.Jobs.BackgroundJobInfrastructureRegistry";
    private const string CoreProcessingServer = "Hangfire.Server.BackgroundProcessingServer";
    private static readonly string[] HangfireGenerationParameters =
    [
        "Hangfire.PostgreSql.PostgreSqlStorage", "LearnStack.Infrastructure.Jobs.HangfireComponentInventory",
        "LearnStack.Infrastructure.Jobs.HangfireRuntimeFilters", "Hangfire.JobActivator", JobPoolRegistration,
        JobRuntimeGate, "LearnStack.Infrastructure.Jobs.BackgroundJobRuntimeOptions", "System.TimeProvider",
    ];
    private static string HangfireGenerationConstructorId => InfrastructureAssembly + ":System.Void " + JobGeneration
        + "::.ctor(" + string.Join(",", HangfireGenerationParameters) + ")";

    /// <summary>ADR-0059/0060: exact fatal authority and public Core construction in the current owner slice.</summary>
    [Fact]
    public void Hangfire_Bootstrap_Uses_Pinned_Schema_And_Confined_Role()
    {
        // This is the nonempty structural portion of the Registered rule. It does
        // not establish runtime state dominance, prevalidation or child qualification.
        using var census = ProductionCensus.Read();
        HangfireRuntimeOffenders(census).Should().BeEmpty(
            "Fix: retain the closed frozen fatal capability, sole string sink and explicitly owned public Core constructor; "
            + "qualify runtime ordering separately under ADR-0059/0060");
    }

    private static SortedSet<string> HangfireRuntimeOffenders(ProductionCensus census)
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        // Per-call snapshot only: every dirty/clean control sees its current IL.
        var ownedBodies = OwnedBodies(census).ToArray();
        var types = census.Types;
        CheckSet(offenders, "Hangfire runtime production assemblies", census.Modules.Select(module => module.Assembly.Name.Name),
            ProductionAssemblies.Names());
        foreach (var name in new[] { JobRuntimeGate, JobFatalBoundary, JobTerminalState, JobGeneration, JobFatalStage })
            if (!types.ContainsKey((InfrastructureAssembly, name))) offenders.Add("Missing actual Hangfire runtime subject: " + name);
        if (offenders.Count != 0) return offenders;

        var gate = types[(InfrastructureAssembly, JobRuntimeGate)];
        var boundary = types[(InfrastructureAssembly, JobFatalBoundary)];
        var terminal = types[(InfrastructureAssembly, JobTerminalState)];
        var generation = types[(InfrastructureAssembly, JobGeneration)];
        if (!gate.IsPublic || !gate.IsSealed || !boundary.IsNestedAssembly || !boundary.IsSealed
            || !terminal.IsNestedPrivate || !terminal.IsSealed || !generation.IsNotPublic || !generation.IsSealed)
            offenders.Add("The runtime gate, frozen pool capability and generation must retain their exact sealed visibility");
        var stage = types[(InfrastructureAssembly, JobFatalStage)];
        if (!stage.IsEnum || !stage.IsNotPublic) offenders.Add("Fatal stages must remain an internal finite enum");
        CheckSet(offenders, "frozen fatal stages", stage.Fields.Where(field => field.IsLiteral)
            .Select(field => field.Name + ":" + field.Constant), ["Constructor:0", "StartupUnwind:1", "LifecycleUnwind:2"]);

        var gateConstructor = RequireBoundaryMethod(offenders, gate, ".ctor", "System.Void", MethodAttributes.Assembly, JobTopology);
        var boundaryConstructor = RequireBoundaryMethod(offenders, boundary, ".ctor", "System.Void", MethodAttributes.Assembly,
            JobRuntimeGate, JobPoolRegistration);
        var generationConstructor = RequireBoundaryMethod(offenders, generation, ".ctor", "System.Void", MethodAttributes.Assembly,
            HangfireGenerationParameters);
        var forPool = RequireBoundaryMethod(offenders, gate, "ForPool", JobFatalBoundary, MethodAttributes.Assembly, JobPoolRegistration);
        var fatal = RequireBoundaryMethod(offenders, gate, "Terminate", "System.Void", MethodAttributes.Private, JobTerminalState);
        var poolFatal = RequireBoundaryMethod(offenders, boundary, "Terminate", "System.Void", MethodAttributes.Assembly, JobFatalStage);
        var sink = RequireHangfireStaticMethod(offenders, gate, "FailFast", "System.Void", MethodAttributes.Private, "System.String");
        var construct = RequireBoundaryMethod(offenders, generation, "Construct", "System.Void", MethodAttributes.Private);
        var unwind = RequireBoundaryMethod(offenders, generation, "ObserveUnwind", "System.Void", MethodAttributes.Private);
        var registerExit = RequireBoundaryMethod(offenders, gate, "EnsureFinalExitRegistered", "System.Void", MethodAttributes.Assembly);
        var onExit = RequireBoundaryMethod(offenders, gate, "OnFinalExit", "System.Void", MethodAttributes.Private,
            "System.Object", "System.EventArgs");
        var beginExit = RequireBoundaryMethod(offenders, gate, "BeginFinalExit", "System.Void", MethodAttributes.Assembly);

        // The named registration alone creates the process gate; the closed pool
        // owner alone constructs generations after the facade preallocates owners.
        CheckHangfireCapabilitySites(offenders, ownedBodies, JobRuntimeGate, ".ctor", gateConstructor,
            [InfrastructureAssembly + ":" + JobRuntimeGate + " " + JobRuntimeRegistration + "::CreateGate(System.IServiceProvider):newobj"]);
        CheckHangfireCapabilitySites(offenders, ownedBodies, JobGeneration, ".ctor", generationConstructor,
            [InfrastructureAssembly + ":System.Threading.Tasks.Task LearnStack.Infrastructure.Jobs.HangfirePoolOwner::SuperviseAsync():newobj"]);
        CheckHangfireCapabilitySites(offenders, ownedBodies, JobRuntimeGate, "ForPool", forPool, [generationConstructor + ":callvirt"]);
        CheckHangfireCapabilitySites(offenders, ownedBodies, JobFatalBoundary, "Terminate", poolFatal,
            [construct + ":callvirt", unwind + ":callvirt"]);
        CheckHangfireCapabilitySites(offenders, ownedBodies, JobRuntimeGate, "Terminate", fatal, [poolFatal + ":callvirt"]);
        CheckHangfireCapabilitySites(offenders, ownedBodies, JobRuntimeGate, "FailFast", sink, [fatal + ":call", fatal + ":call"]);
        CheckHangfireCapabilitySites(offenders, ownedBodies, JobRuntimeGate, "EnsureFinalExitRegistered", registerExit,
            [generationConstructor + ":callvirt", InfrastructureAssembly + ":System.Void " + JobRuntime + "/BootstrapInvocation::Execute():callvirt"]);
        CheckHangfireCapabilitySites(offenders, ownedBodies, JobRuntimeGate, "OnFinalExit", onExit, [gateConstructor + ":ldftn"]);
        CheckHangfireCapabilitySites(offenders, ownedBodies, JobRuntimeGate, "BeginFinalExit", beginExit, [onExit + ":call"]);

        var poolFactories = gate.Methods.Where(method => method.IsPrivate && !method.IsStatic
            && !method.HasGenericParameters && method.ReturnType.FullName == JobFatalBoundary
            && ParametersMatch(method, [JobPoolRegistration])
            && method.CustomAttributes.Any(attribute => attribute.AttributeType.FullName
                == "System.Runtime.CompilerServices.CompilerGeneratedAttribute")).ToArray();
        if (poolFactories.Length != 1) offenders.Add("The preallocated pool boundary requires exactly its closed constructor callback");
        var poolFactory = poolFactories.Length == 1 ? MethodId(poolFactories[0]) : "missing:pool constructor callback";
        if (poolFactories.Length == 1)
            CheckHangfireCapabilitySites(offenders, ownedBodies, JobRuntimeGate, poolFactories[0].Name, poolFactory,
                [gateConstructor + ":ldftn"]);
        CheckHangfireCapabilitySites(offenders, ownedBodies, JobFatalBoundary, ".ctor", boundaryConstructor, [poolFactory + ":newobj"]);

        CheckSet(offenders, "closed fatal capability consumers", ownedBodies
            .Where(item => item.Owner.DeclaringType.FullName != JobFatalBoundary && ReferencesCapability(item.Body, JobFatalBoundary))
            .Select(item => MethodId(item.Owner)).Concat(types.Values.SelectMany(type => type.Methods)
                .Where(method => !method.HasBody && method.DeclaringType.FullName != JobFatalBoundary
                    && ReferencesCapability(method, JobFatalBoundary)).Select(MethodId)).Distinct(StringComparer.Ordinal),
            [gateConstructor, poolFactory, forPool, generationConstructor, construct, unwind]);
        CheckSet(offenders, "closed fatal capability holders", types.Values.SelectMany(type => type.Fields)
            .Where(field => ContainsBridgeCapability(field.FieldType, JobFatalBoundary))
            .Select(field => TypeId(field.DeclaringType) + ":" + field.Name),
            [TypeId(gate) + ":_pools", TypeId(generation) + ":_boundary"]);
        foreach (var (owner, name, capability, expected) in new[]
                 {
                     (generation, "_boundary", JobFatalBoundary, new[] { generationConstructor + ":stfld", construct + ":ldfld", unwind + ":ldfld" }),
                     (boundary, "_gate", JobRuntimeGate, new[] { boundaryConstructor + ":stfld", poolFatal + ":ldfld" }),
                     (boundary, "_fatal", JobTerminalState + "[]", new[] { boundaryConstructor + ":stfld", poolFatal + ":ldfld" }),
                 })
        {
            var fields = owner.Fields.Where(field => field.Name == name).ToArray();
            if (fields.Length != 1 || !fields[0].IsPrivate || fields[0].IsStatic || fields[0].FieldType.FullName != capability)
                offenders.Add(TypeId(owner) + ": requires its exact private instance capture " + name);
            CheckSet(offenders, TypeId(owner) + " captured capability field access", ownedBodies
                .SelectMany(item => item.Body.Body.Instructions.Where(instruction => instruction.Operand is FieldReference field
                        && SameType(field.DeclaringType, owner) && field.Name == name)
                    .Select(instruction => MethodId(item.Owner) + ":" + instruction.OpCode.Name)), expected);
        }
        foreach (var (owner, name) in new[] { (gate, "_pools"), (generation, "_boundary"), (boundary, "_gate"), (boundary, "_fatal") })
        {
            var fields = owner.Fields.Where(field => field.Name == name).ToArray();
            if (fields.Length != 1 || !fields[0].IsPrivate || !fields[0].IsInitOnly || fields[0].IsStatic)
                offenders.Add(TypeId(owner) + ": requires its private readonly instance capability " + name);
        }
        var frozenConstructors = boundary.Methods.Where(method => method.Name == ".ctor" && method.HasBody).ToArray();
        CheckSet(offenders, "frozen preallocated fatal diagnostic fragments", frozenConstructors.SelectMany(method => method.Body.Instructions)
            .Where(instruction => instruction.OpCode == OpCodes.Ldstr).Select(instruction => (string)instruction.Operand),
            ["LearnStack jobs fatal: ", ":constructor", "LearnStack jobs fatal: ", ":startup_unwind",
             "LearnStack jobs fatal: ", ":lifecycle_unwind"]);
        CheckSet(offenders, "preallocated fatal diagnostic state construction", frozenConstructors.SelectMany(method => method.Body.Instructions)
            .Where(instruction => instruction.Operand is MethodReference target && target.DeclaringType.FullName == JobTerminalState)
            .Select(instruction => instruction.OpCode.Name + ":" + ((MethodReference)instruction.Operand).FullName),
            Enumerable.Repeat("newobj:System.Void " + JobTerminalState + "::.ctor(LearnStack.Infrastructure.Jobs.BackgroundJobProcessState,System.String)", 3));

        var sites = ownedBodies.SelectMany(item => item.Body.Body.Instructions
            .Where(instruction => instruction.Operand is MethodReference)
            .Select(instruction => (Owner: MethodId(item.Owner), Instruction: instruction, Target: (MethodReference)instruction.Operand))).ToArray();
        // Include every operand form, including delegates, and every overload.
        CheckSet(offenders, "sole process termination sink", sites.Where(site => IsHangfireTerminationSink(site.Target))
            .Select(site => site.Owner + ":" + site.Instruction.OpCode.Name + ":" + site.Target.FullName),
            [sink + ":call:System.Void System.Environment::FailFast(System.String)"]);
        foreach (var site in sites.Where(site => IsHangfireTerminationSink(site.Target)))
            if (Key(site.Target.DeclaringType).Assembly is not ("System.Runtime" or "System.Private.CoreLib" or "System.Diagnostics.Process"))
                offenders.Add("The process termination sink must resolve to its framework identity: " + MethodId(site.Target));
        CheckSet(offenders, "public Core server construction", sites.Where(site =>
                (site.Target.DeclaringType.FullName is CoreProcessingServer or "Hangfire.BackgroundJobServer") && site.Target.Name == ".ctor"
                || site.Target.Name is "AddHangfireServer" or "UseHangfireServer")
            .Select(site => site.Owner + ":" + site.Instruction.OpCode.Name + ":" + MethodId(site.Target)),
            [construct + ":newobj:Hangfire.Core:System.Void " + CoreProcessingServer
                + "::.ctor(Hangfire.JobStorage,System.Collections.Generic.IEnumerable`1<Hangfire.Server.IBackgroundProcessDispatcherBuilder>,"
                + "System.Collections.Generic.IDictionary`2<System.String,System.Object>,Hangfire.Server.BackgroundProcessingServerOptions)"]);

        // The actual sink's complete call census admits no forwarding/callback.
        CheckSet(offenders, "closed fatal sink body", sites.Where(site => site.Owner == sink)
            .Select(site => site.Instruction.OpCode.Name + ":" + site.Target.FullName),
            ["call:System.Void System.Environment::FailFast(System.String)"]);
        CheckSet(offenders, "private final-exit callback body", sites.Where(site => site.Owner == onExit)
            .Select(site => site.Instruction.OpCode.Name + ":" + MethodId(site.Target)), ["call:" + beginExit]);
        CheckSet(offenders, "process-lifetime final-exit event registrations", sites.Where(site =>
                site.Target.DeclaringType.FullName == "System.AppDomain"
                && site.Target.Name is "add_ProcessExit" or "remove_ProcessExit" or "add_DomainUnload" or "remove_DomainUnload")
            .Select(site => site.Owner + ":" + site.Instruction.OpCode.Name + ":" + site.Target.FullName),
            [registerExit + ":callvirt:System.Void System.AppDomain::add_ProcessExit(System.EventHandler)",
             registerExit + ":callvirt:System.Void System.AppDomain::add_DomainUnload(System.EventHandler)"]);
        return offenders;
    }

    private static bool IsHangfireTerminationSink(MethodReference target) =>
        target.DeclaringType.FullName == "System.Environment" && target.Name is "FailFast" or "Exit"
        || target.DeclaringType.FullName == "System.Diagnostics.Process" && target.Name == "Kill";

    private static string RequireHangfireStaticMethod(SortedSet<string> offenders, TypeDefinition type, string name,
        string result, MethodAttributes visibility, params string[] parameters)
    {
        var expected = Key(type).Assembly + ":" + result + " " + type.FullName + "::" + name + "(" + string.Join(",", parameters) + ")";
        var methods = type.Methods.Where(method => method.Name == name).ToArray();
        if (methods.Length != 1 || !methods[0].IsStatic || methods[0].HasGenericParameters
            || (methods[0].Attributes & MethodAttributes.MemberAccessMask) != visibility
            || methods[0].ReturnType.FullName != result || !ParametersMatch(methods[0], parameters))
            offenders.Add(expected + ": requires exactly its closed static declaration");
        return expected;
    }

    private static void CheckHangfireCapabilitySites(SortedSet<string> offenders,
        IReadOnlyList<(MethodDefinition Body, MethodDefinition Owner)> ownedBodies,
        string type, string name, string target, string[] callers)
    {
        CheckSet(offenders, "exact Hangfire capability callers " + type + "." + name, ownedBodies
            .SelectMany(item => item.Body.Body.Instructions.Where(instruction => instruction.Operand is MethodReference method
                    && method.DeclaringType.FullName == type && method.Name == name)
                .Select(instruction => MethodId(item.Owner) + ":" + instruction.OpCode.Name + ":" + MethodId((MethodReference)instruction.Operand))),
            callers.Select(caller => caller + ":" + target));
    }

    public sealed partial class JobLifecycleMutationTests
    {

        /// <summary>ADR-0060: planted controls exercise the same production capability/sink census.</summary>
        [Fact]
        public void Hangfire_runtime_census_rejects_fatal_and_constructor_escapes()
        {
            using var census = ProductionCensus.Read();
            HangfireRuntimeOffenders(census).Should().BeEmpty("Fix: dirty controls require the actual nonempty clean runtime subjects");
            var types = census.Types;
            var gate = types[(InfrastructureAssembly, JobRuntimeGate)];
            var boundary = types[(InfrastructureAssembly, JobFatalBoundary)];
            var generation = types[(InfrastructureAssembly, JobGeneration)];
            var application = census.Modules.Single(module => module.Assembly.Name.Name == DeliveryApplication);
            var capabilities = gate.Methods.Where(method => method.Name is ".ctor" or "FailFast" or "Terminate" or "ForPool")
                .Concat(boundary.Methods.Where(method => method.Name is ".ctor" or "Terminate"))
                .Concat(generation.Methods.Where(method => method.Name == ".ctor")).ToArray();
            capabilities.Should().HaveCount(7, "the exact production capability chain must be present");
            foreach (var capability in capabilities)
            {
                foreach (var opcode in new[] { OpCodes.Call, OpCodes.Callvirt, OpCodes.Ldftn, OpCodes.Ldvirtftn, OpCodes.Newobj })
                    AssertMutation(census, "an extra fatal capability or construction through " + opcode, () =>
                        AddCallProbe(application, "ExtraHangfireRuntimeCapability", capability, opcode), HangfireRuntimeOffenders);
                AssertMutation(census, "an async fatal capability escape", () =>
                    AddAsyncCallProbe(application, "AsyncHangfireRuntimeCapability", capability), HangfireRuntimeOffenders);
                AssertMutation(census, "a helper/overload borrowing an approved fatal capability name", () =>
                {
                    var helper = AddCall(capability.DeclaringType, capability.Name, capability, OpCodes.Call);
                    return () => capability.DeclaringType.Methods.Remove(helper);
                }, HangfireRuntimeOffenders);
            }
            var sinkTargets = new[]
            {
            application.ImportReference(typeof(Environment).GetMethod(nameof(Environment.FailFast), [typeof(string)])!),
            application.ImportReference(typeof(Environment).GetMethod(nameof(Environment.FailFast), [typeof(string), typeof(Exception)])!),
            application.ImportReference(typeof(Environment).GetMethod(nameof(Environment.Exit), [typeof(int)])!),
            application.ImportReference(typeof(System.Diagnostics.Process).GetMethod(nameof(System.Diagnostics.Process.Kill), Type.EmptyTypes)!),
            application.ImportReference(typeof(System.Diagnostics.Process).GetMethod(nameof(System.Diagnostics.Process.Kill), [typeof(bool)])!),
        };
            foreach (var target in sinkTargets)
            {
                foreach (var opcode in new[] { OpCodes.Call, OpCodes.Callvirt, OpCodes.Ldftn, OpCodes.Ldvirtftn })
                    AssertMutation(census, "a direct, alternate or delegated process termination sink", () =>
                        AddCallProbe(application, "ExtraProcessTermination", target, opcode), HangfireRuntimeOffenders);
                AssertMutation(census, "an asynchronous process termination sink", () =>
                    AddAsyncCallProbe(application, "AsyncProcessTermination", target), HangfireRuntimeOffenders);
            }
            foreach (var (owner, field) in new[] { (generation, "_boundary"), (boundary, "_gate"), (boundary, "_fatal") })
                AssertMutation(census, "a fatal capability forwarding accessor", () =>
                    AddBoundaryFieldGetter(owner, "ForwardFatalCapability", field), HangfireRuntimeOffenders);
            AssertMutation(census, "an interface forwarding declaration for the pool fatal capability", () =>
            {
                var probe = AddProbe(application, "FatalCapabilityPort");
                probe.Attributes = TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract;
                probe.BaseType = null;
                probe.Methods.Add(new MethodDefinition("Forward", MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual,
                    application.ImportReference(boundary)));
                return () => application.Types.Remove(probe);
            }, HangfireRuntimeOffenders);
            var actualSites = OwnedBodies(census).SelectMany(item => item.Body.Body.Instructions)
                .Where(instruction => instruction.Operand is MethodReference target
                    && (IsHangfireTerminationSink(target) || target.DeclaringType.FullName == CoreProcessingServer && target.Name == ".ctor"
                        || capabilities.Any(capability => SameType(target.DeclaringType, capability.DeclaringType)
                            && target.Name == capability.Name))).ToArray();
            actualSites.Should().NotBeEmpty("the production sink and public constructor must be exercised by mutation controls");
            foreach (var instruction in actualSites)
            {
                AssertMutation(census, "a missing actual fatal/construction capability site", () =>
                    ReplaceInstruction(instruction, OpCodes.Nop, null!), HangfireRuntimeOffenders);
                AssertMutation(census, "a delegate substituted inside an approved fatal/construction caller", () =>
                    ReplaceInstruction(instruction, OpCodes.Ldftn, instruction.Operand), HangfireRuntimeOffenders);
            }
            var coreConstructor = actualSites.Select(instruction => instruction.Operand).OfType<MethodReference>()
                .Single(target => target.DeclaringType.FullName == CoreProcessingServer);
            foreach (var opcode in new[] { OpCodes.Newobj, OpCodes.Call, OpCodes.Ldftn })
                AssertMutation(census, "an additional public Core constructor escape", () =>
                    AddCallProbe(application, "ExtraCoreConstruction", coreConstructor, opcode), HangfireRuntimeOffenders);
            AssertMutation(census, "an asynchronous public Core constructor escape", () =>
                AddAsyncCallProbe(application, "AsyncCoreConstruction", coreConstructor), HangfireRuntimeOffenders);
            var coreSite = actualSites.Single(instruction => instruction.Operand is MethodReference target
                && target.DeclaringType.FullName == CoreProcessingServer);
            var wrongCoreConstructor = new MethodReference(".ctor", application.TypeSystem.Void, application.ImportReference(coreConstructor.DeclaringType))
            { HasThis = true };
            wrongCoreConstructor.Parameters.Add(new ParameterDefinition(application.TypeSystem.String));
            AssertMutation(census, "an alternate public Core constructor overload inside the approved owner", () =>
                ReplaceInstruction(coreSite, OpCodes.Newobj, wrongCoreConstructor), HangfireRuntimeOffenders);
            var sinkSite = actualSites.Single(instruction => instruction.Operand is MethodReference target && IsHangfireTerminationSink(target));
            foreach (var target in sinkTargets.Skip(1))
                AssertMutation(census, "an exception overload or alternate termination sink inside the sole sink owner", () =>
                    ReplaceInstruction(sinkSite, OpCodes.Call, target), HangfireRuntimeOffenders);
            foreach (var name in new[] { "EnsureFinalExitRegistered", "OnFinalExit", "BeginFinalExit" })
            {
                var target = gate.Methods.Single(method => method.Name == name);
                AssertMutation(census, "an additional final-exit authority caller", () =>
                    AddCallProbe(application, "ExtraFinalExitAuthority", target, OpCodes.Call), HangfireRuntimeOffenders);
            }
            var diagnostic = boundary.Methods.Single(method => method.Name == ".ctor").Body.Instructions
                .First(instruction => instruction.OpCode == OpCodes.Ldstr);
            AssertMutation(census, "an unfrozen fatal diagnostic fragment", () =>
                ReplaceInstruction(diagnostic, OpCodes.Ldstr, "unexpected runtime diagnostic"), HangfireRuntimeOffenders);
            foreach (var subject in new[] { gate, boundary, generation })
                AssertMutation(census, "a missing actual runtime subject", () =>
                {
                    var collection = subject.DeclaringType?.NestedTypes ?? subject.Module.Types;
                    collection.Remove(subject);
                    return () => collection.Add(subject);
                }, HangfireRuntimeOffenders);
            var clean = AddProbe(application, "CleanRuntimeStateObserver");
            AddCall(clean, "Observe", gate.Methods.Single(method => method.Name == "get_State"), OpCodes.Callvirt);
            try { HangfireRuntimeOffenders(census).Should().BeEmpty("read-only process state observation grants no fatal capability"); }
            finally { application.Types.Remove(clean); }
        }

    }
}
