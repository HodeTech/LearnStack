using FluentAssertions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed partial class OutboxDispatchConventionTests
{
    private sealed record OrchestrationAsyncSlot(string Method, string Name, string FieldType);
    private static readonly OrchestrationAsyncSlot[] ExternalOrchestrationAsyncSlots =
    [
        new("InvokeExternalAsync", "scope", "System.Nullable`1<Microsoft.Extensions.DependencyInjection.AsyncServiceScope>"),
        new("InvokeExternalAsync", "registration", "System.Nullable`1<System.Threading.CancellationTokenRegistration>"),
        new("InvokeExternalAsync", "denial", "System.IDisposable"),
        new("InvokeExternalAsync", "retained", "System.Collections.Generic.List`1<LearnStack.Infrastructure.Jobs.JobStartCleanupOwnership>"),
        new("ReadExternalStateCoreAsync", "scope", "System.Nullable`1<Microsoft.Extensions.DependencyInjection.AsyncServiceScope>"),
        new("ReadExternalStateCoreAsync", "unit", "LearnStack.SharedKernel.Persistence.IUnitOfWork"),
        new("ReadExternalStateCoreAsync", "registration", "System.Nullable`1<System.Threading.CancellationTokenRegistration>"),
        new("ReadExternalStateCoreAsync", "authority", "System.IDisposable"),
        new("ReadExternalStateCoreAsync", "sourceBinding", "System.IDisposable"),
        new("ReadExternalStateCoreAsync", "retained", "System.Collections.Generic.List`1<LearnStack.Infrastructure.Jobs.JobStartCleanupOwnership>"),
        new("WriteExternalCheckpointAsync", "scope", "System.Nullable`1<Microsoft.Extensions.DependencyInjection.AsyncServiceScope>"),
        new("WriteExternalCheckpointAsync", "unit", "LearnStack.SharedKernel.Persistence.IUnitOfWork"),
        new("WriteExternalCheckpointAsync", "registration", "System.Nullable`1<System.Threading.CancellationTokenRegistration>"),
        new("WriteExternalCheckpointAsync", "authority", "System.IDisposable"),
        new("WriteExternalCheckpointAsync", "sourceBinding", "System.IDisposable"),
        new("WriteExternalCheckpointAsync", "retained", "System.Collections.Generic.List`1<LearnStack.Infrastructure.Jobs.JobStartCleanupOwnership>"),
        new("ExecuteBusinessAsync", "scope", "System.Nullable`1<Microsoft.Extensions.DependencyInjection.AsyncServiceScope>"),
        new("ExecuteBusinessAsync", "unit", "LearnStack.SharedKernel.Persistence.IUnitOfWork"),
        new("ExecuteBusinessAsync", "registration", "System.Threading.CancellationTokenRegistration"),
        new("ExecuteBusinessAsync", "authority", "System.IDisposable"),
        new("ExecuteBusinessAsync", "sourceBinding", "System.IDisposable"),
        new("ExecuteBusinessAsync", "retained", "System.Collections.Generic.List`1<LearnStack.Infrastructure.Jobs.JobStartCleanupOwnership>"),
    ];

    private static void CheckExternalAsyncRetention(ProductionCensus census, SortedSet<string> offenders,
        Dictionary<string, TypeDefinition> subjects, (MethodDefinition Body, MethodDefinition Owner)[] bodies,
        ILookup<string, (string Owner, Instruction Instruction, FieldReference Field)> fieldSites)
    {
        var executor = subjects[OrchestrationExecutor];
        foreach (var group in ExternalOrchestrationAsyncSlots.GroupBy(slot => slot.Method))
        {
            var methods = executor.Methods.Where(candidate => candidate.Name == group.Key).ToArray();
            if (methods.Length != 1) { offenders.Add("Missing or additional actual external resource owner: " + group.Key); continue; }
            var method = methods[0];
            var state = OrchestrationStateMachine(census, method);
            if (state is null) { offenders.Add("Missing attributed external actual-task owner: " + group.Key); continue; }
            foreach (var slot in group)
            {
                var fields = state.Fields.Where(field => field.Name.StartsWith("<" + slot.Name + ">", StringComparison.Ordinal)).ToArray();
                if (fields.Length != 1 || fields[0].IsStatic || fields[0].FieldType.FullName != slot.FieldType)
                    offenders.Add("Missing or retyped actual external resource: " + group.Key + ":" + slot.Name);
                else
                    CheckSet(offenders, "actual async resource stays with its attributed owner " + group.Key + ":" + slot.Name,
                        fieldSites[fields[0].Name].Where(item => TargetsDeclaredType(item.Field, state))
                            .Select(item => item.Owner).Distinct(),
                        [MethodId(method)]);
            }
            foreach (var slot in group.Where(slot => slot.Name is "scope" or "retained"))
                if (state.Fields.Count(field => field.FieldType.FullName == slot.FieldType) != 1)
                    offenders.Add("An actual external scope/task has no extra resource carrier: " + group.Key + ":" + slot.Name);
            var calls = bodies.Where(item => item.Owner == method).SelectMany(item => MethodOperands(item.Body)).ToArray();
            if (calls.Count(target => target.DeclaringType.FullName == OrchestrationExecutor
                    && target.Name == "CleanupScopeAsync") != 1
                || calls.Count(target => target.DeclaringType.FullName == "Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions"
                    && target.Name == "CreateAsyncScope") != 1)
                offenders.Add("Actual external work retains and cleans its one scope: " + group.Key);
            if (group.Key != "ExecuteBusinessAsync"
                && (calls.Count(target => target.DeclaringType.FullName == "System.Threading.CancellationToken"
                        && target.Name == "UnsafeRegister") != 1
                    || calls.Count(target => target.DeclaringType.FullName == "System.Threading.CancellationTokenRegistration"
                        && target.Name == "DisposeAsync") != 1))
                offenders.Add("Actual external cancellation registration is joined: " + group.Key);
        }
    }

    private sealed record OrchestrationAwait(string Owner, string Issue, string TaskSlot, string ResultSlot);
    private static readonly OrchestrationAwait[] TerminalPrivilegedAwaits =
    [
        new("AdvanceAsync", "AcquireExternalTerminalNoStartAsync", "_acquisitionTask", "_acquisition"),
        new("AdvanceAsync", "ProbeExternalCheckpointAsync", "_probeTask", "_probe"),
        new("ObserveAcquisitionAsync", "ProbeExternalRecoveryAsync", "_probeTask", "_probe"),
        new("ObserveAcquisitionAsync", "ProbeExternalTerminalNoStartAcquisitionAsync", "_probeTask", "_probe"),
    ];

    private static void CheckTerminalPrivilegedAwaits(ProductionCensus census, SortedSet<string> offenders,
        TypeDefinition terminal)
    {
        foreach (var group in TerminalPrivilegedAwaits.GroupBy(item => item.Owner))
        {
            var owners = terminal.Methods.Where(method => method.Name == group.Key).ToArray();
            var state = owners.Length == 1 ? OrchestrationStateMachine(census, owners[0]) : null;
            var moves = state?.Methods.Where(method => method.Name == "MoveNext" && method.HasBody).ToArray();
            if (moves is not { Length: 1 })
            { offenders.Add("Missing actual terminal privileged task owner: " + group.Key); continue; }
            var instructions = moves[0].Body.Instructions.ToArray();
            var previousCompletion = -1;
            foreach (var item in group)
            {
                var issues = instructions.Select((instruction, index) => (instruction, index))
                    .Where(pair => pair.instruction.Operand is MethodReference method
                        && method.DeclaringType?.FullName == OrchestrationStore && method.Name == item.Issue).ToArray();
                if (issues.Length != 1)
                { offenders.Add("Missing or additional actual terminal privileged issuance: " + item.Issue); continue; }
                var issue = issues[0].index;
                var issued = (MethodReference)issues[0].instruction.Operand;
                var publication = Array.FindIndex(instructions, issue + 1, instruction =>
                    instruction.OpCode == OpCodes.Stfld && instruction.Operand is FieldReference field
                        && TargetsDeclaredType(field, terminal) && field.Name == item.TaskSlot);
                var awaitCall = Array.FindIndex(instructions, issue + 1, instruction =>
                    instruction.Operand is MethodReference method && method.Name == "ConfigureAwait"
                        && method.DeclaringType?.FullName == issued.ReturnType.FullName);
                var completion = awaitCall < 0 ? -1 : Array.FindIndex(instructions, awaitCall + 1, instruction =>
                    instruction.Operand is MethodReference method && method.Name == "GetResult"
                        && method.DeclaringType?.FullName.StartsWith("System.Runtime.CompilerServices.ConfiguredTaskAwaitable", StringComparison.Ordinal) == true);
                var retainedResult = completion < 0 ? -1 : Array.FindIndex(instructions, completion + 1, instruction =>
                    instruction.OpCode == OpCodes.Stfld && instruction.Operand is FieldReference field
                        && TargetsDeclaredType(field, terminal) && field.Name == item.ResultSlot);
                if (issue <= previousCompletion || publication <= issue || awaitCall <= publication
                    || completion <= awaitCall || retainedResult <= completion)
                    offenders.Add("Terminal privileged task must be retained, awaited and retained as a result in order: " + item.Issue);
                previousCompletion = retainedResult;
            }
        }
    }

    private static TypeDefinition? OrchestrationStateMachine(ProductionCensus census, MethodDefinition method)
    {
        var attributes = method.CustomAttributes.Where(attribute => attribute.AttributeType.FullName
            == "System.Runtime.CompilerServices.AsyncStateMachineAttribute").ToArray();
        return attributes.Length == 1 && attributes[0].ConstructorArguments.Count == 1
            && attributes[0].ConstructorArguments[0].Value is TypeReference state
            && census.Types.TryGetValue(Key(state), out var owner) ? owner : null;
    }

    [Fact]
    public void External_job_orchestration_rejects_missing_seams_helpers_delegates_signatures_and_resource_carriers()
    {
        using var census = ProductionCensus.Read();
        var baseline = EvaluateDetector(census, JobOrchestrationOffenders);
        baseline.Should().BeEmpty("the exact orchestration manifest must match; offenders: {0}",
            string.Join(Environment.NewLine, baseline));
        var application = census.Modules.Single(module => module.Assembly.Name.Name == DeliveryApplication);
        foreach (var seam in ExternalOrchestrationSeams)
        {
            var type = census.Types[(InfrastructureAssembly, seam.Type)];
            var method = type.Methods.Single(candidate => candidate.Name == seam.Name.Split('#')[0]
                && ParametersMatch(candidate, seam.Parameters));
            var methodId = MethodId(method);
            // A removed sanctioned call must fail, not merely an additional caller.
            var call = OwnedBodies(census).SelectMany(item => item.Body.Body.Instructions)
                .First(instruction => instruction.Operand is MethodReference target
                    && target.Name == method.Name && HasAttachedDeclaringType(target) && MethodId(target) == methodId);
            AssertMutation(census, "an omitted actual external orchestration call " + seam.Name,
                () => ReplaceInstruction(call, OpCodes.Nop, null!), JobOrchestrationOffenders);
            AssertMutation(census, "a removed external orchestration seam " + seam.Name, () =>
            {
                type.Methods.Remove(method);
                return () => type.Methods.Add(method);
            }, JobOrchestrationOffenders);
            foreach (var opcode in new[] { OpCodes.Call, OpCodes.Callvirt, OpCodes.Ldftn, OpCodes.Ldvirtftn })
                AssertMutation(census, "an additional external orchestration capability through " + opcode,
                    () => AddCallProbe(application, "ExternalOrchestrationEscape", method, opcode), JobOrchestrationOffenders);
            AssertMutation(census, "an asynchronous external orchestration escape " + seam.Name,
                () => AddAsyncCallProbe(application, "ExternalOrchestrationAsyncEscape", method), JobOrchestrationOffenders);
            AssertMutation(census, "an extra helper inside the approved orchestration owner " + seam.Name, () =>
            {
                var helper = new MethodDefinition("ExtraOrchestrationHelper", MethodAttributes.Private, type.Module.TypeSystem.Void);
                helper.Body.Instructions.Add(Instruction.Create(method.IsConstructor ? OpCodes.Newobj : OpCodes.Call, method));
                helper.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                type.Methods.Add(helper);
                return () => type.Methods.Remove(helper);
            }, JobOrchestrationOffenders);
            AssertMutation(census, "a changed external orchestration signature " + seam.Name, () =>
            {
                var parameter = new ParameterDefinition(type.Module.ImportReference(typeof(Action)));
                method.Parameters.Add(parameter);
                return () => method.Parameters.Remove(parameter);
            }, JobOrchestrationOffenders);
        }
        var terminal = census.Types[(InfrastructureAssembly, OrchestrationTerminal)];
        foreach (var item in TerminalPrivilegedAwaits)
        {
            var owner = terminal.Methods.Single(method => method.Name == item.Owner);
            var move = OrchestrationStateMachine(census, owner)!.Methods.Single(method => method.Name == "MoveNext");
            var instructions = move.Body.Instructions.ToArray();
            var issue = Array.FindIndex(instructions, instruction => instruction.Operand is MethodReference method
                && method.DeclaringType?.FullName == OrchestrationStore && method.Name == item.Issue);
            var issued = (MethodReference)instructions[issue].Operand;
            var publication = Array.FindIndex(instructions, issue + 1, instruction => instruction.OpCode == OpCodes.Stfld
                && instruction.Operand is FieldReference field && field.Name == item.TaskSlot);
            var awaitCall = Array.FindIndex(instructions, issue + 1, instruction => instruction.Operand is MethodReference method
                && method.Name == "ConfigureAwait" && method.DeclaringType?.FullName == issued.ReturnType.FullName);
            foreach (var instruction in new[] { instructions[publication], instructions[awaitCall] })
                AssertMutation(census, "an omitted actual terminal task publication or await " + item.Issue,
                    () => ReplaceInstruction(instruction, OpCodes.Nop, null!), JobOrchestrationOffenders);
        }
        foreach (var slot in ExternalOrchestrationFields)
        {
            var type = census.Types[(InfrastructureAssembly, slot.Type)];
            var field = type.Fields.Single(candidate => candidate.Name == slot.Name);
            AssertMutation(census, "a missing retained external operation slot " + slot.Name, () =>
            {
                type.Fields.Remove(field);
                return () => type.Fields.Add(field);
            }, JobOrchestrationOffenders);
            AssertMutation(census, "a changed retained external operation type " + slot.Name, () =>
            {
                var original = field.FieldType;
                field.FieldType = original.FullName == "System.Object" ? type.Module.TypeSystem.String : type.Module.TypeSystem.Object;
                return () => field.FieldType = original;
            }, JobOrchestrationOffenders);
            AssertMutation(census, "an approved owner forwarding external operation state " + slot.Name,
                () => AddBoundaryFieldGetter(type, "ForwardExternalOrchestrationResource", slot.Name), JobOrchestrationOffenders);
        }
        foreach (var (name, _) in ExternalOrchestrationFieldCensus)
        {
            var type = census.Types[(InfrastructureAssembly, name)];
            AssertMutation(census, "an additional orchestration data carrier " + name, () =>
            {
                var field = new FieldDefinition("_extraOrchestrationCarrier", FieldAttributes.Private, type.Module.TypeSystem.Object);
                type.Fields.Add(field);
                return () => type.Fields.Remove(field);
            }, JobOrchestrationOffenders);
        }
        var identity = census.Types[(InfrastructureAssembly, OrchestrationIdentity)];
        foreach (var property in new[] { "Profile", "LedgerStage", "Stage", "FinalizeAttemptOrdinal", "SelectedNormalAttemptId",
                     "OriginalDueAt", "AdmissionToken", "IsRecovery" })
        {
            var field = identity.Fields.Single(candidate => candidate.Name == "<" + property + ">k__BackingField");
            AssertMutation(census, "a mutable newly acknowledged identity fact " + property, () =>
            {
                field.IsInitOnly = false;
                return () => field.IsInitOnly = true;
            }, JobOrchestrationOffenders);
            AssertMutation(census, "an identity helper forwarding newly acknowledged state " + property,
                () => AddBoundaryFieldGetter(identity, "ForwardExternalIdentity", field.Name), JobOrchestrationOffenders);
        }
        AssertMutation(census, "an extra static identity data carrier", () =>
        {
            var field = new FieldDefinition("_extraIdentityCarrier", FieldAttributes.Private | FieldAttributes.Static,
                identity.Module.TypeSystem.Object);
            identity.Fields.Add(field);
            return () => identity.Fields.Remove(field);
        }, JobOrchestrationOffenders);
        var executor = census.Types[(InfrastructureAssembly, OrchestrationExecutor)];
        foreach (var slot in ExternalOrchestrationAsyncSlots)
        {
            var method = executor.Methods.Single(candidate => candidate.Name == slot.Method);
            var state = OrchestrationStateMachine(census, method)!;
            var field = state.Fields.Single(candidate => candidate.Name.StartsWith("<" + slot.Name + ">", StringComparison.Ordinal));
            AssertMutation(census, "an omitted actual async scope/cancellation resource " + slot.Method + ":" + slot.Name, () =>
            {
                state.Fields.Remove(field);
                return () => state.Fields.Add(field);
            }, JobOrchestrationOffenders);
            AssertMutation(census, "a retyped actual async resource " + slot.Method + ":" + slot.Name, () =>
            {
                var original = field.FieldType;
                field.FieldType = state.Module.TypeSystem.Object;
                return () => field.FieldType = original;
            }, JobOrchestrationOffenders);
            AssertMutation(census, "a helper forwarding an actual async resource " + slot.Method + ":" + slot.Name,
                () => AddBoundaryFieldGetter(state, "ForwardActualExternalResource", field.Name), JobOrchestrationOffenders);
            if (slot.Name == "scope")
                AssertMutation(census, "an extra async scope carrier " + slot.Method, () =>
                {
                    var extra = new FieldDefinition("_extraScope", FieldAttributes.Private, field.FieldType);
                    state.Fields.Add(extra);
                    return () => state.Fields.Remove(extra);
                }, JobOrchestrationOffenders);
        }
    }
}
