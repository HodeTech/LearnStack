using Mono.Cecil;
using Mono.Cecil.Cil;

namespace LearnStack.Tests.Architecture;

public sealed partial class OutboxDispatchConventionTests
{
    private const string JobInvocationStage = "LearnStack.Infrastructure.Jobs.BackgroundJobInvocationStage";
    private const string JobLateSnapshot = "LearnStack.Infrastructure.Jobs.BackgroundJobLateSettlementSnapshot";
    private static readonly string[] LateStageNames =
    [
        "Framework", "Transactional", "Prepare", "ExternalRead", "Arm", "Execute", "Outcome", "Finalize",
        "PrepareHistory", "ArmHistory", "OutcomeHistory", "FinalizeHistory", "RecoveryAcquire",
        "RecoveryConfirmation", "Reconcile", "NoStart", "RecoveryDisposition",
    ];

    private static MethodDefinition[] LateObservationRoots(ProductionCensus census, SortedSet<string> offenders)
    {
        var types = census.Types;
        var definitions = new List<MethodDefinition>();
        foreach (var (type, name, result, visibility, parameters) in new (string, string, string, MethodAttributes, string[])[]
                 {
                     (OrchestrationInvocation, "ObserveLateSettlement", "System.Void", MethodAttributes.Private, []),
                     (OrchestrationBound, "HasSettledStage", "System.Boolean", MethodAttributes.Assembly, [JobInvocationStage, OrchestrationExecution]),
                     (OrchestrationBound, "ObserveLateSettlement", "System.Void", MethodAttributes.Assembly, [JobInvocationStage]),
                     (OrchestrationExecution, "HasSettledStage", "System.Boolean", MethodAttributes.Assembly, [JobInvocationStage]),
                     (OrchestrationControl, "ObserveLateSettlement", "System.Void", MethodAttributes.Assembly, [JobInvocationStage]),
                     (OrchestrationControl, "CaptureLateSettlements", JobLateSnapshot, MethodAttributes.Private, []),
                 })
        {
            if (!types.TryGetValue((InfrastructureAssembly, type), out var owner))
            { offenders.Add("Missing late observation subject: " + type); continue; }
            var id = RequireBoundaryMethod(offenders, owner, name, result, visibility, parameters);
            var subject = owner.Methods.SingleOrDefault(method => MethodId(method) == id);
            if (subject is null || !subject.HasBody || subject.IsStatic || subject.IsVirtual || subject.HasGenericParameters
                || subject.CustomAttributes.Any(attribute => attribute.AttributeType.FullName == "System.Runtime.CompilerServices.AsyncStateMachineAttribute"))
                offenders.Add("Late observation requires its concrete synchronous helper: " + type + "." + name);
            else definitions.Add(subject);
        }
        if (definitions.Count != 6) return definitions.ToArray();
        var invocation = types[(InfrastructureAssembly, OrchestrationInvocation)];
        var bound = types[(InfrastructureAssembly, OrchestrationBound)];
        var control = types[(InfrastructureAssembly, OrchestrationControl)];
        var consume = definitions.Single(method => method.DeclaringType == invocation);
        var record = definitions.Single(method => method.DeclaringType == control && method.Name == "ObserveLateSettlement");
        var capture = definitions.Single(method => method.Name == "CaptureLateSettlements");
        var clock = JobOwner(offenders, types, InfrastructureAssembly, OrchestrationInvocation, "RecordStageClockClosure");
        var boundClassifier = JobOwner(offenders, types, InfrastructureAssembly, OrchestrationBound, "HasSettledStage");
        var framework = JobOwner(offenders, types, InfrastructureAssembly, OrchestrationBound, "RunFrameworkAsync");
        var operationObserver = JobOwner(offenders, types, InfrastructureAssembly, OrchestrationBound, "ObserveOperationAsync");
        var boundConstructor = JobOwner(offenders, types, InfrastructureAssembly, OrchestrationBound, ".ctor");
        CheckBridgeCapturedField(offenders, census, bound, "_cleanupConfirmed", "System.Boolean",
            [framework + ":stfld", operationObserver + ":ldfld", boundClassifier + ":ldfld"]);
        foreach (var (name, fieldType, readOnly, expected) in new (string, string, bool, string[])[]
                 {
                     ("_operationPublished", "System.Threading.Tasks.TaskCompletionSource`1<System.Threading.Tasks.Task>", true,
                         [boundConstructor + ":stfld", operationObserver + ":ldfld", boundClassifier + ":ldfld",
                          JobOwner(offenders, types, InfrastructureAssembly, OrchestrationBound, "RunJobAsync") + ":ldfld"]),
                     ("<jobs>P", OrchestrationControl, false,
                         [boundConstructor + ":stfld", framework + ":ldfld", boundClassifier + ":ldfld",
                          JobOwner(offenders, types, InfrastructureAssembly, OrchestrationBound, "RunTickAsync") + ":ldfld",
                          JobOwner(offenders, types, InfrastructureAssembly, OrchestrationBound, "ObserveLateSettlement") + ":ldfld"]),
                 })
        {
            if (bound.Fields.SingleOrDefault(field => field.Name == name) is not { IsPrivate: true, IsStatic: false } actualField
                || actualField.FieldType.FullName != fieldType || actualField.IsInitOnly != readOnly)
                offenders.Add("The late framework proof retains its concrete captured field: " + name);
            // Close reader/writer owners and access kinds. Repeated reads by
            // the same actual operation are intentionally not a count claim.
            CheckSet(offenders, "late framework captured field owners " + name, census.FieldInstructions(name)
                .Where(site => TargetsDeclaredType(site.Target, bound)).Select(site => site.OwnerId + ":" + site.Instruction.OpCode.Name).Distinct(), expected);
        }
        foreach (var subject in definitions)
        {
            var caller = (subject.DeclaringType.FullName, subject.Name) switch
            {
                (OrchestrationInvocation, _) => JobOwner(offenders, types, InfrastructureAssembly, OrchestrationInvocation, "CompleteAsync"),
                (OrchestrationBound, _) => MethodId(consume),
                (OrchestrationExecution, _) => JobOwner(offenders, types, InfrastructureAssembly, OrchestrationBound, "HasSettledStage"),
                (_, "CaptureLateSettlements") => JobOwner(offenders, types, InfrastructureAssembly, OrchestrationControl, "get_Snapshot"),
                _ => JobOwner(offenders, types, InfrastructureAssembly, OrchestrationBound, "ObserveLateSettlement"),
            };
            CheckSet(offenders, "exact late observation helper caller " + MethodId(subject), census.MethodInstructions(subject.Name)
                .Where(site => TargetsDeclaredType(site.Target, subject.DeclaringType) && MethodId(site.Target) == MethodId(subject))
                .Select(site => site.OwnerId + ":" + site.Instruction.OpCode.Name),
                [caller + ":" + (subject.IsPrivate || subject.Name == "HasSettledStage" ? "call" : "callvirt")]);
        }
        CheckBridgeCapturedField(offenders, census, invocation, "_elapsedStage", "System.Nullable`1<" + JobInvocationStage + ">",
            [clock + ":stfld", MethodId(consume) + ":ldfld"]);
        CheckBridgeCapturedField(offenders, census, invocation, "_elapsedStageConsumed", "System.Boolean",
            [MethodId(consume) + ":ldfld", MethodId(consume) + ":ldfld", MethodId(consume) + ":stfld"]);
        var marker = census.FieldInstructions("_elapsedStage").Where(site => TargetsDeclaredType(site.Target, invocation)
            && site.Instruction.OpCode == OpCodes.Stfld).ToArray();
        if (marker.Length != 1 || marker[0].Instruction.Previous is not
            { OpCode.Code: Code.Ldfld, Operand: FieldReference { Name: "_stageWaitStage" } provenance }
            || !TargetsDeclaredType(provenance, invocation))
            offenders.Add("Elapsed stage consumes only its effective current stage at clock closure");
        if (!types.TryGetValue((InfrastructureAssembly, JobInvocationStage), out var stage))
            offenders.Add("Missing finite late observation stage enum");
        else CheckSet(offenders, "exact late settlement stages", stage.Fields.Where(field => field.IsLiteral)
            .Select(field => field.Name + ":" + field.Constant), LateStageNames.Select((name, index) => name + ":" + index));
        const string increment = "System.Threading:System.Int64 System.Threading.Interlocked::Increment(System.Int64&)";
        const string read = "System.Threading:System.Int64 System.Threading.Interlocked::Read(System.Int64&)";
        CheckSet(offenders, "late settlement parent is Interlocked-only", record.Body.Instructions
            .Where(instruction => instruction.Operand is MethodReference)
            .Select(instruction => MethodId((MethodReference)instruction.Operand) + ":" + instruction.OpCode.Name),
            Enumerable.Repeat(increment + ":call", LateStageNames.Length));
        foreach (var name in LateStageNames)
        {
            CheckBridgeCapturedField(offenders, census, control, "_late" + name, "System.Int64",
                [MethodId(record) + ":ldflda", MethodId(capture) + ":ldflda"]);
            CheckSet(offenders, "late settlement consumed atomics " + name, census.FieldInstructions("_late" + name)
                .Where(site => TargetsDeclaredType(site.Target, control)).Select(site =>
                    site.OwnerId + ":" + (site.Instruction.Next is { OpCode.Code: Code.Call, Operand: MethodReference atomic }
                        ? MethodId(atomic) : "not-atomic")),
                [MethodId(record) + ":" + increment, MethodId(capture) + ":" + read]);
        }
        // Ordering here complements held-cleanup behavior controls. It does not
        // claim that source order alone proves branch dominance or quiescence.
        var complete = types[(InfrastructureAssembly, OrchestrationInvocation)].Methods.Single(method => method.Name == "CompleteAsync");
        var actual = OwnedBodies(census).Where(body => body.Owner == complete && body.Body.HasBody)
            .Select(body => body.Body).SingleOrDefault(body => body.Body.Instructions.Any(instruction =>
                instruction.Operand is MethodReference call && MethodId(call) == MethodId(consume)));
        if (actual is null) offenders.Add("Actual invocation cleanup must consume elapsed observation once");
        else
        {
            var instructions = actual.Body.Instructions;
            var observed = instructions.Select((instruction, index) => (instruction, index))
                .Where(item => item.instruction.Operand is MethodReference call && MethodId(call) == MethodId(consume)).ToArray();
            var disposed = instructions.Select((instruction, index) => (instruction, index)).Where(item =>
                item.instruction.Operand is MethodReference { Name: "Dispose", DeclaringType.FullName: "System.Threading.CancellationTokenSource" }).ToArray();
            var released = instructions.Select((instruction, index) => (instruction, index)).Where(item =>
                item.instruction.Operand is MethodReference { Name: "ReleaseAfterPrivateCleanup" }).ToArray();
            if (observed.Length != 1 || disposed.Length != 1 || released.Length != 1
                || disposed[0].index >= observed[0].index || observed[0].index >= released[0].index)
                offenders.Add("Late observation follows actual private disposal and precedes execution release");
        }
        return definitions.ToArray();
    }
}
