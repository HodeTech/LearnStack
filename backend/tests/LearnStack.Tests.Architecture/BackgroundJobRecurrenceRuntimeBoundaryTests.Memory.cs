using Mono.Cecil;
using Mono.Cecil.Cil;

namespace LearnStack.Tests.Architecture;

public sealed partial class OutboxDispatchConventionTests
{
    private const string RcInvocation = "LearnStack.Infrastructure.Jobs.HangfireInvocationContract";
    private const string RcBound = RcInvocation + "/BoundInvocation";
    private const string RcTick = "LearnStack.Infrastructure.Jobs.HangfireRecurrenceTick";
    private const string RcFacts = "LearnStack.Infrastructure.Jobs.BackgroundJobRecurrenceFacts";
    private const string RcGeneration = "LearnStack.Infrastructure.Jobs.HangfireGeneration";
    private const string RcWork = "LearnStack.Infrastructure.Jobs.HangfireGenerationWork";
    private const string RcGate = "LearnStack.Infrastructure.Jobs.BackgroundJobRuntimeGate";
    private const string RcRunStop = "LearnStack.Application.Jobs.BackgroundJobRunGate/RuntimeStop";

    // This is the complete reviewed production call closure, not a list of known-dangerous APIs.
    private static SortedSet<string> RecurrenceMemoryOffenders(ProductionCensus census)
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        var types = census.Types;
        const string runtime = "System.Runtime:";
        const string threading = "System.Threading:";
        const string exchange = threading + "System.Int32 System.Threading.Interlocked::Exchange(System.Int32&,System.Int32):call";
        const string compare = threading + "System.Int32 System.Threading.Interlocked::CompareExchange(System.Int32&,System.Int32,System.Int32):call";
        const string enter = threading + "System.Void System.Threading.Monitor::Enter(System.Object,System.Boolean&):call";
        const string exit = threading + "System.Void System.Threading.Monitor::Exit(System.Object):call";
        const string completed = runtime + "System.Threading.Tasks.Task System.Threading.Tasks.Task::get_CompletedTask():call";
        const string exception = runtime + "System.Void System.InvalidOperationException::.ctor(System.String):newobj";
        const string ready = runtime + "System.Threading.Tasks.Task`1<!0> System.Threading.Tasks.TaskCompletionSource`1<System.Boolean>::get_Task():callvirt";
        var bodies = new List<MethodDefinition>();

        Subject(RcTick, "RunAsync", BoundaryTask, ["System.String", "System.Int64"], [Call(RcBound, "RunTickAsync", "callvirt")],
            [F(RcTick, "_invocation", RcBound)]);
        Subject(RcBound, "RunTickAsync", BoundaryTask, ["System.String", "System.Int64"],
            [threading + "System.Int32 System.Threading.Volatile::Read(System.Int32&):call", Call(RcBound, "get_Type", "call"),
             runtime + "System.Type System.Type::GetTypeFromHandle(System.RuntimeTypeHandle):call",
             runtime + "System.Boolean System.Type::op_Inequality(System.Type,System.Type):call",
             "Hangfire.Core:System.Collections.Generic.IReadOnlyList`1<System.Object> Hangfire.Common.Job::get_Args():callvirt",
             "Hangfire.Core:System.Collections.Generic.IReadOnlyList`1<System.Object> Hangfire.Common.Job::get_Args():callvirt",
             runtime + "!0 System.Collections.Generic.IReadOnlyList`1<System.Object>::get_Item(System.Int32):callvirt",
             runtime + "!0 System.Collections.Generic.IReadOnlyList`1<System.Object>::get_Item(System.Int32):callvirt",
             runtime + "System.Boolean System.String::op_Inequality(System.String,System.String):call", exchange,
             Call(RcInvocation, "Refused", "call"), Call(RcInvocation, "Refused", "call"), Call(RcGate, "TryAdmit", "callvirt"),
             Call(RcGeneration, "TryAdmitInvocation", "call"), completed, completed, Call(OrchestrationControl, "SignalRecurrenceTick", "callvirt")],
            [F(RcBound, "_closed", "System.Int32", "ldflda"), F(RcBound, "<job>P", "Hangfire.Common.Job"), F(RcBound, "<job>P", "Hangfire.Common.Job"),
             F(RcBound, "_entered", "System.Int32", "ldflda"), F(RcBound, "<jobs>P", OrchestrationControl), F(RcBound, "<jobs>P", OrchestrationControl),
             F(RcBound, "<runtime>P", RcGate), F(RcBound, "<owner>P", RcGeneration)]);
        Subject(RcBound, "get_Type", "System.Type", [],
            ["Hangfire.Core:System.Type Hangfire.Common.Job::get_Type():callvirt"], [F(RcBound, "<job>P", "Hangfire.Common.Job")]);
        Subject(RcInvocation, "Refused", "System.InvalidOperationException", [], [exception], [], isStatic: true);
        Subject(OrchestrationControl, "SignalRecurrenceTick", "System.Void", ["System.String", "System.Int64"],
            [Call(RcControl, "SignalTick", "callvirt")], [F(OrchestrationControl, "_recurrence", RcControl)]);
        Subject(RcControl, "SignalTick", "System.Void", ["System.String", "System.Int64"],
            [Call(RcRegistry, "TryGet", "callvirt"), Call(RcManifest, "get_Facts", "callvirt"), Call(RcFacts, "get_Revision", "callvirt"),
             exception, enter, Call(RcGate, "TryAdmit", "callvirt"), exchange, exit],
            [F(RcControl, "_registry", RcRegistry), F(RcControl, "_sync", "System.Object"), F(RcControl, "_started", "System.Boolean"),
             F(RcControl, "_stopping", "System.Boolean"), F(RcControl, "_runtime", RcGate), F(RcControl, "_tick", "System.Int32", "ldflda")]);
        Subject(RcRegistry, "TryGet", "System.Boolean", ["System.String", RcManifest + "&"],
            ["System.Collections.Immutable:System.Boolean System.Collections.Frozen.FrozenDictionary`2<System.String," + RcManifest + ">::TryGetValue(!0,!1&):callvirt"],
            [F(RcRegistry, "_recurrences", "System.Collections.Frozen.FrozenDictionary`2<System.String," + RcManifest + ">")]);
        Subject(RcManifest, "get_Facts", RcFacts, [], [], [F(RcManifest, "<Facts>k__BackingField", RcFacts)]);
        Subject(RcFacts, "get_Revision", "System.Int64", [], [], [F(RcFacts, "<Revision>k__BackingField", "System.Int64")]);
        Subject(RcGeneration, "TryAdmitInvocation", "System.Boolean", [],
            [ready, ready, runtime + "System.Boolean System.Threading.Tasks.Task::get_IsCompletedSuccessfully():callvirt",
             runtime + "!0 System.Threading.Tasks.Task`1<System.Boolean>::get_Result():callvirt", Call(RcWork, "get_CanStart", "callvirt"),
             Call(RcGate, "TryAdmit", "callvirt"), compare],
            [F(RcGeneration, "_ready", RcSignal + "`1<System.Boolean>"), F(RcGeneration, "_ready", RcSignal + "`1<System.Boolean>"),
             F(RcGeneration, "_work", RcWork), F(RcGeneration, "_gate", RcGate), F(RcGeneration, "_draining", "System.Int32", "ldflda")]);
        Subject(RcWork, "get_CanStart", "System.Boolean", [], [enter, exit],
            [F(RcWork, "_sync", "System.Object"), F(RcWork, "_closed", "System.Boolean"), F(RcWork, "_retained", "System.Int32")]);
        Subject(RcGate, "TryAdmit", "System.Boolean", [],
            [threading + "!!0 System.Threading.Interlocked::CompareExchange<" + RcGate + "/TerminalState>(!!0&,!!0,!!0):call",
             Call(RcGate, "get_RunStop", "call"), Call(RcRunStop, "TryAdmit", "callvirt")],
            [F(RcGate, "_state", RcGate + "/TerminalState", "ldflda"), F(RcGate, "Running", RcGate + "/TerminalState", "ldsfld"),
             F(RcGate, "Running", RcGate + "/TerminalState", "ldsfld"), F(RcGate, "Running", RcGate + "/TerminalState", "ldsfld")]);
        Subject(RcGate, "get_RunStop", RcRunStop, [], [], [F(RcGate, "<RunStop>k__BackingField", RcRunStop)]);
        Subject(RcRunStop, "TryAdmit", "System.Boolean", [], [compare], [F(RcRunStop, "_closed", "System.Int32", "ldflda")]);

        foreach (var body in bodies)
        {
            foreach (var instruction in body.Body.Instructions)
            {
                if (instruction.OpCode is var opcode && (opcode == OpCodes.Calli || opcode == OpCodes.Jmp
                    || opcode == OpCodes.Ldftn || opcode == OpCodes.Ldvirtftn || opcode == OpCodes.Newarr
                    || opcode == OpCodes.Box || opcode == OpCodes.Localloc || opcode == OpCodes.Switch)
                    || instruction.Operand is Instruction branch && branch.Offset <= instruction.Offset)
                    offenders.Add("The tick closure admits no indirect work, scheduling or loop: " + MethodId(body));
                if (instruction.Operand is MethodReference target)
                {
                    var assembly = Key(target.DeclaringType).Assembly;
                    if (assembly.StartsWith("System.", StringComparison.Ordinal)
                        && (target.DeclaringType.Scope is not AssemblyNameReference scope || scope.Version != new Version(10, 0, 0, 0)
                            || Convert.ToHexString(scope.PublicKeyToken) != "B03F5F7F11D50A3A"))
                        offenders.Add("Memory tick framework leaf requires its actual pinned identity: " + MethodId(target));
                    if (assembly.StartsWith("LearnStack.", StringComparison.Ordinal)
                        && (!types.TryGetValue(Key(target.DeclaringType), out var owner) || !TargetsDeclaredType(target, owner)))
                        offenders.Add("Memory tick production edge must be attached: " + MethodId(target));
                    if (assembly == "Hangfire.Core" && (target.DeclaringType.Scope is not AssemblyNameReference vendor
                            || vendor.Version != new Version(1, 8, 25, 0) || vendor.PublicKeyToken.Length != 0))
                        offenders.Add("Memory tick getters require the pinned stock Hangfire identity");
                }
            }
        }
        var signal = Find(RcControl, "SignalTick");
        var bound = Find(RcBound, "RunTickAsync");
        if (bound is not null)
        {
            var tokens = bound.Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Ldtoken).ToArray();
            if (tokens.Length != 1 || tokens[0].Operand is not TypeReference tick
                || !types.TryGetValue((InfrastructureAssembly, RcTick), out var actualTick) || !SameType(tick, actualTick))
                offenders.Add("The memory hint retains the exact closed recurrence tick type");
        }
        if (signal is not null)
        {
            var wake = signal.Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Ldflda
                && instruction.Operand is FieldReference field && field.Name == "_tick").ToArray();
            if (wake.Length != 1 || wake[0].Next?.OpCode != OpCodes.Ldc_I4_1)
                offenders.Add("The recurrence tick sets exactly one coalesced wake bit");
        }
        var generation = Find(RcGeneration, "TryAdmitInvocation");
        if (generation is not null)
        {
            var instructions = generation.Body.Instructions;
            var check = instructions.SingleOrDefault(instruction => instruction.Operand is MethodReference method && method.Name == "get_IsCompletedSuccessfully");
            var result = instructions.SingleOrDefault(instruction => instruction.Operand is MethodReference method && method.Name == "get_Result");
            var next = check?.Next;
            if (check is null || next is null || next.OpCode != OpCodes.Brfalse_S && next.OpCode != OpCodes.Brfalse
                || next.Operand is not Instruction target || result is null
                || instructions.IndexOf(target) <= instructions.IndexOf(result) || instructions.IndexOf(check) >= instructions.IndexOf(result))
                offenders.Add("Readiness Result remains dominated by its positive completed-task branch");
        }
        return offenders;

        MethodDefinition? Find(string type, string name)
        {
            var assembly = type == RcRunStop ? DeliveryApplication : InfrastructureAssembly;
            if (!types.TryGetValue((assembly, type), out var subject)) return null;
            var candidates = subject.Methods.Where(method => method.Name == name).ToArray();
            return candidates.Length == 1 ? candidates[0] : null;
        }
        string Call(string type, string name, string opcode)
        {
            var target = Find(type, name);
            if (target is not null) return MethodId(target) + ":" + opcode;
            offenders.Add("Missing closed memory tick target: " + type + "." + name);
            return "missing:" + type + "." + name;
        }
        string F(string type, string name, string fieldType, string opcode = "ldfld") =>
            (type == RcRunStop ? DeliveryApplication : InfrastructureAssembly) + ":" + fieldType + " " + type + "::" + name + ":" + opcode;
        void Subject(string type, string name, string result, string[] parameters, string[] calls, string[] fields, bool isStatic = false)
        {
            var method = Find(type, name);
            if (method is null || !method.HasBody || !ParametersMatch(method, parameters) || method.ReturnType.FullName != result
                || method.IsStatic != isStatic || method.HasGenericParameters || method.CustomAttributes.Any(attribute =>
                    attribute.AttributeType.FullName == "System.Runtime.CompilerServices.AsyncStateMachineAttribute"))
            { offenders.Add("Memory tick retains its exact synchronous subject: " + type + "." + name); return; }
            bodies.Add(method);
            CheckSet(offenders, "closed memory tick calls " + MethodId(method), method.Body.Instructions
                .Where(instruction => instruction.Operand is MethodReference)
                .Select(instruction => MethodId((MethodReference)instruction.Operand) + ":" + instruction.OpCode.Name), calls);
            CheckSet(offenders, "closed memory tick fields " + MethodId(method), method.Body.Instructions
                .Where(instruction => instruction.Operand is FieldReference)
                .Select(instruction => TypeId(((FieldReference)instruction.Operand).DeclaringType).Split(':')[0]
                    + ":" + ((FieldReference)instruction.Operand).FullName + ":" + instruction.OpCode.Name), fields);
        }
    }

    private static Action MutateRecurrenceMemory(ProductionCensus census, string mutation)
    {
        if (mutation == "memory_extra_caller")
        {
            var control = census.Types[(InfrastructureAssembly, OrchestrationControl)];
            var extra = new MethodDefinition("UnlistedRecurrenceHint", MethodAttributes.Private, control.Module.TypeSystem.Void);
            control.Methods.Add(extra);
            extra.Body.Instructions.Add(Instruction.Create(OpCodes.Call,
                control.Methods.Single(method => method.Name == "SignalRecurrenceTick")));
            extra.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            return () => control.Methods.Remove(extra);
        }
        if (mutation == "memory_readiness_wait")
        {
            var instructions = census.Types[(InfrastructureAssembly, RcGeneration)].Methods.Single(method => method.Name == "TryAdmitInvocation").Body.Instructions;
            var check = instructions.Single(instruction => instruction.Operand is MethodReference target && target.Name == "get_IsCompletedSuccessfully");
            var result = instructions.Single(instruction => instruction.Operand is MethodReference target && target.Name == "get_Result");
            var original = instructions.IndexOf(result);
            instructions.Remove(result);
            instructions.Insert(instructions.IndexOf(check), result);
            return () => { instructions.Remove(result); instructions.Insert(original, result); };
        }
        if (mutation == "memory_tick_type")
        {
            var bound = census.Types[(InfrastructureAssembly, RcBound)].Methods.Single(method => method.Name == "RunTickAsync");
            var token = bound.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Ldtoken);
            return ReplaceInstruction(token, OpCodes.Ldtoken, census.Types[(InfrastructureAssembly, RcControl)]);
        }
        if (mutation == "memory_vendor_identity")
        {
            var bound = census.Types[(InfrastructureAssembly, RcBound)];
            var getter = bound.Methods.Single(method => method.Name == "get_Type").Body.Instructions.Single(instruction => instruction.Operand is MethodReference);
            var original = (MethodReference)getter.Operand;
            var wrong = new TypeReference(original.DeclaringType.Namespace, original.DeclaringType.Name, bound.Module,
                new AssemblyNameReference("Hangfire.Core", new Version(1, 8, 24, 0)));
            var method = new MethodReference(original.Name, original.ReturnType, wrong) { HasThis = true };
            return ReplaceInstruction(getter, getter.OpCode, method);
        }
        var owner = census.Types[(InfrastructureAssembly, mutation == "memory_helper" ? RcRegistry : RcControl)];
        var body = owner.Methods.Single(method => method.Name == (mutation == "memory_helper" ? "TryGet" : "SignalTick")).Body;
        if (mutation == "memory_wake")
        {
            var field = body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Ldflda
                && instruction.Operand is FieldReference reference && reference.Name == "_tick");
            return ReplaceInstruction(field.Next, OpCodes.Ldc_I4_0, null!);
        }
        if (mutation == "memory_field")
        {
            var field = body.Instructions.Single(instruction => instruction.Operand is FieldReference reference && reference.Name == "_registry");
            return ReplaceInstruction(field, OpCodes.Nop, null!);
        }
        var added = mutation == "memory_indirect"
            ? Instruction.Create(OpCodes.Calli, new CallSite(owner.Module.TypeSystem.Void))
            : Instruction.Create(OpCodes.Call, owner.Module.ImportReference(typeof(Task).GetMethod(nameof(Task.Delay), [typeof(int)])!));
        body.Instructions.Insert(0, added);
        return () => body.Instructions.Remove(added);
    }
}
