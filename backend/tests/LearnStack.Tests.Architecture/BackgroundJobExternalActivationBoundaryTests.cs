using FluentAssertions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed partial class OutboxDispatchConventionTests
{
    private const string JobExternalObservation = "LearnStack.Infrastructure.Jobs.BackgroundJobRetainedExternalObservation";
    private const string JobExternalPort = "LearnStack.SharedKernel.Jobs.IBackgroundJobExternalOperation`2";
    private const string JobExternalBinding = "LearnStack.Application.Jobs.BackgroundJobExternalRecoveryBinding";

    // Provider reflection is one private typed bridge, selected from the frozen
    // registration after leaving the application source. Its reports confer no
    // checkpoint authority. App reads/writes use only the three sealed requests.
    private static SortedSet<string> JobExternalActivationOffenders(ProductionCensus census)
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        var types = census.Types;
        foreach (var subject in new[] { (InfrastructureAssembly, JobExecutorType), (InfrastructureAssembly, JobExternalObservation),
                     (DeliveryApplication, JobAdmission), (DeliveryApplication, JobRunGate), (KernelAssembly, JobExternalPort) })
            if (!types.ContainsKey(subject)) offenders.Add("Missing actual external activation subject: " + subject.Item2);
        if (offenders.Count != 0) return offenders;
        var executor = types[(InfrastructureAssembly, JobExecutorType)];
        var admission = types[(DeliveryApplication, JobAdmission)];
        // Removing a Cecil declaration also clears its DeclaringType on existing
        // method operands. Reject a missing capability before traversing those
        // operands, so the planted control reports the missing authority.
        CheckSet(offenders, "external capability declaration inventory",
            executor.Methods.Where(method => method.Name is "CallExternalExecuteAsync" or "CallExternalReconcileAsync")
                .Select(method => JobExecutorType + "::" + method.Name)
                .Concat(admission.Methods.Where(method => method.Name is "EnterExternal" or "EnterExternalCheckpoint")
                    .Select(method => JobAdmission + "::" + method.Name))
                .Concat(types[(DeliveryApplication, JobRunGate)].Methods.Where(method =>
                        method.Name is "TryIssueExternalExecute" or "TryIssueExternalReconcile")
                    .Select(method => JobRunGate + "::" + method.Name))
                .Concat(types[(InfrastructureAssembly, JobExternalObservation)].Methods.Where(method => method.Name == "Capture")
                    .Select(method => JobExternalObservation + "::" + method.Name)),
            [JobExecutorType + "::CallExternalExecuteAsync", JobExecutorType + "::CallExternalReconcileAsync",
             JobAdmission + "::EnterExternal", JobAdmission + "::EnterExternalCheckpoint", JobAdmission + "::EnterExternalCheckpoint",
             JobAdmission + "::EnterExternalCheckpoint", JobAdmission + "::EnterExternalCheckpoint",
             JobRunGate + "::TryIssueExternalExecute", JobRunGate + "::TryIssueExternalReconcile", JobExternalObservation + "::Capture"]);
        if (offenders.Count != 0) return offenders;
        var bodies = OwnedBodies(census).ToArray();
        var sites = OwnedCallSites(census).ToArray();
        var provider = JobOwner(offenders, types, InfrastructureAssembly, JobExecutorType, "InvokeExternalAsync");
        var read = JobOwner(offenders, types, InfrastructureAssembly, JobExecutorType, "ReadExternalStateCoreAsync");
        var write = JobOwner(offenders, types, InfrastructureAssembly, JobExecutorType, "WriteExternalCheckpointAsync");
        var bridgeIds = new List<string>();
        foreach (var (name, gate, authority, portMethod) in new[]
                 {
                     ("CallExternalExecuteAsync", "TryIssueExternalExecute", JobCommitGate, "ExecuteAsync"),
                     ("CallExternalReconcileAsync", "TryIssueExternalReconcile", JobExternalBinding, "ReconcileAsync"),
                 })
        {
            _ = RequireBoundaryMethod(offenders, types[(DeliveryApplication, JobRunGate)], gate, "System.Boolean",
                MethodAttributes.Public, authority);
            var methods = executor.Methods.Where(method => method.Name == name).ToArray();
            if (methods.Length != 1) { offenders.Add("The provider bridge requires one exact declaration: " + name); continue; }
            var method = methods[0];
            bridgeIds.Add(MethodId(method));
            if (!method.IsPrivate || !method.IsStatic || method.GenericParameters.Count != 2
                || method.ReturnType.FullName != "System.Threading.Tasks.Task`1<" + JobExternalObservation + ">"
                || !ParametersMatch(method, ["System.Object", "System.Object", "LearnStack.SharedKernel.Jobs.BackgroundJobExternalContext",
                    "System.String", authority, JobExecutorType + "/ExternalInvocationObservation", BoundaryCancellation]))
                offenders.Add("The provider bridge lost its closed typed signature: " + name);
            if (method.GenericParameters.Count == 2)
                for (var index = 0; index < 2; index++)
                    CheckSet(offenders, "provider bridge generic constraint " + name + ":" + index,
                        method.GenericParameters[index].Constraints.Select(constraint => constraint.ConstraintType.FullName),
                        ["LearnStack.SharedKernel.Jobs." + (index == 0 ? "IBackgroundJobPlan" : "IBackgroundJobOutcome")]);
            // Reflection is the only issuance route; direct helper/delegate calls
            // cannot borrow the bridge's generic constraints or port privileges.
            CheckSet(offenders, "provider bridge direct callers " + name,
                sites.Where(site => SameType(site.Target.DeclaringType, executor) && site.Target.Name == name).Select(site => MethodId(site.Owner)), []);
            CheckSet(offenders, "provider issuance " + gate,
                sites.Where(site => site.Target.DeclaringType.FullName == JobRunGate && site.Target.Name == gate).Select(site => MethodId(site.Owner)), [MethodId(method)]);
            CheckSet(offenders, "typed provider call " + portMethod,
                sites.Where(site => site.Target.DeclaringType.GetElementType().FullName == JobExternalPort && site.Target.Name == portMethod)
                    .Select(site => MethodId(site.Owner)), [MethodId(method)]);
            var instructions = bodies.Where(item => item.Owner == method).SelectMany(item => item.Body.Body.Instructions).ToArray();
            var issued = Array.FindIndex(instructions, instruction => instruction.Operand is MethodReference target
                && target.DeclaringType.FullName == JobRunGate && target.Name == gate);
            var called = Array.FindIndex(instructions, instruction => instruction.Operand is MethodReference target
                && target.DeclaringType.GetElementType().FullName == JobExternalPort && target.Name == portMethod);
            var captured = Array.FindIndex(instructions, instruction => instruction.Operand is MethodReference target
                && target.DeclaringType.FullName == JobExternalObservation && target.Name == "Capture");
            if (issued < 0 || called <= issued || captured <= called
                || !instructions.Skip(called + 1).Take(Math.Max(0, captured - called - 1)).Any(instruction =>
                    instruction.Operand is MethodReference target && target.Name == "GetResult"))
                offenders.Add("The provider bridge must issue once, await its actual report, then capture observation: " + name);
        }
        var captures = types[(InfrastructureAssembly, JobExternalObservation)].Methods.Where(method => method.Name == "Capture").ToArray();
        if (captures.Length != 1 || !captures[0].IsAssembly || !captures[0].IsStatic || captures[0].GenericParameters.Count != 1
            || captures[0].ReturnType.FullName != JobExternalObservation
            || !ParametersMatch(captures[0], ["LearnStack.SharedKernel.Jobs.BackgroundJobExternalContext", "System.String",
                "LearnStack.SharedKernel.Jobs.BackgroundJobExternalResult`1<TOutcome>"]))
            offenders.Add("A provider observation is captured only by its exact internal generic report seam");
        var externalEntries = admission.Methods.Where(method => method.Name == "EnterExternal").ToArray();
        if (externalEntries.Length != 1 || !externalEntries[0].IsPublic || !externalEntries[0].IsStatic
            || externalEntries[0].HasGenericParameters || externalEntries[0].Parameters.Count != 0
            || externalEntries[0].ReturnType.FullName != "System.IDisposable")
            offenders.Add("External provider denial entry remains one closed static scope factory");
        CheckSet(offenders, "actual provider report capture", sites.Where(site => site.Target.DeclaringType.FullName == JobExternalObservation
                && site.Target.Name == "Capture").Select(site => MethodId(site.Owner)), bridgeIds);
        CheckSet(offenders, "provider denial scope entry", sites.Where(site => site.Target.DeclaringType.FullName == JobAdmission
            && site.Target.Name == "EnterExternal").Select(site => MethodId(site.Owner)), [provider]);

        var entries = admission.Methods.Where(method => method.Name == "EnterExternalCheckpoint").ToArray();
        if (entries.Length != 4) offenders.Add("External app entry requires exactly three sealed request overloads and one private owner implementation");
        var wrapperIds = new List<string>();
        foreach (var (request, owner) in new[] { (JobExternalReadRequest, read), (JobArmRequest, write), (JobOutcomeRequest, write) })
        {
            var matching = entries.Where(method => ParametersMatch(method, [request, LifecycleUnit])).ToArray();
            if (matching.Length != 1) { offenders.Add("Missing exact external request entry: " + request); continue; }
            var entry = matching[0];
            wrapperIds.Add(MethodId(entry));
            if (!entry.IsPublic || entry.IsStatic || entry.HasGenericParameters || entry.ReturnType.FullName != "System.IDisposable")
                offenders.Add("External request entry is a closed instance capability: " + request);
            CheckSet(offenders, "external request entry callers " + request, sites.Where(site => SameType(site.Target.DeclaringType, admission)
                && site.Target.Name == entry.Name && ParametersMatch(site.Target, [request, LifecycleUnit])).Select(site => MethodId(site.Owner)), [owner]);
        }
        var cores = entries.Where(method => ParametersMatch(method, [JobCheckpointOwner, LifecycleUnit])).ToArray();
        if (cores.Length != 1 || !cores[0].IsPrivate || cores[0].IsStatic || cores[0].HasGenericParameters
            || cores[0].ReturnType.FullName != JobAdmission + "/Scope")
            offenders.Add("External checkpoint owner entry remains one private exact implementation");
        CheckSet(offenders, "private external checkpoint entry callers", sites.Where(site => SameType(site.Target.DeclaringType, admission)
            && site.Target.Name == "EnterExternalCheckpoint" && ParametersMatch(site.Target, [JobCheckpointOwner, LifecycleUnit]))
            .Select(site => MethodId(site.Owner)), wrapperIds);

        var providerBodies = bodies.Where(item => item.Owner.Name == "InvokeExternalAsync"
                && item.Owner.DeclaringType.FullName == JobExecutorType && MethodId(item.Owner) == provider)
            .SelectMany(item => item.Body.Body.Instructions).ToArray();
        CheckSet(offenders, "closed provider reflected bridge names", providerBodies.Where(instruction => instruction.OpCode == OpCodes.Ldstr
            && instruction.Operand is string text && text.StartsWith("CallExternal", StringComparison.Ordinal)).Select(instruction => (string)instruction.Operand),
            ["CallExternalExecuteAsync", "CallExternalReconcileAsync"]);
        CheckSet(offenders, "closed provider reflected declaring type", providerBodies.Where(instruction => instruction.OpCode == OpCodes.Ldtoken
            && instruction.Operand is TypeReference).Select(instruction => ((TypeReference)instruction.Operand).FullName), [JobExecutorType]);
        CheckSet(offenders, "closed provider reflection operations", bodies.Where(item => item.Owner.DeclaringType == executor
                && item.Owner.Name != "ExecuteBusinessAsync").SelectMany(item => MethodOperands(item.Body)
                .Where(IsExternalReflection).Select(target => MethodId(item.Owner) + ":" + target.FullName)),
            [provider + ":System.Reflection.MethodInfo System.Type::GetMethod(System.String,System.Reflection.BindingFlags)",
             provider + ":System.Reflection.MethodInfo System.Reflection.MethodInfo::MakeGenericMethod(System.Type[])",
             provider + ":System.Object System.Reflection.MethodBase::Invoke(System.Object,System.Object[])"]);
        foreach (var instruction in bodies.SelectMany(item => item.Body.Body.Instructions).Where(instruction => instruction.Operand is MethodReference target
                     && (target.DeclaringType.FullName == JobAdmission && target.Name is "EnterExternal" or "EnterExternalCheckpoint"
                         || target.DeclaringType.FullName == JobRunGate && target.Name is "TryIssueExternalExecute" or "TryIssueExternalReconcile"
                         || target.DeclaringType.GetElementType().FullName == JobExternalPort && target.Name is "ExecuteAsync" or "ReconcileAsync"
                         || target.DeclaringType.FullName == JobExternalObservation && target.Name == "Capture")))
            if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt)
                offenders.Add("External entry/report authority cannot escape as a delegate");
        return offenders;
    }

    private static bool IsExternalReflection(MethodReference target) => target.DeclaringType.FullName
        is "System.Activator" or "System.Reflection.MethodBase" or "System.Reflection.MethodInfo" or "System.Reflection.ConstructorInfo"
        || target.DeclaringType.FullName == "System.Type" && target.Name is "GetType" or "GetConstructor" or "GetMethod" or "MakeGenericType";

    [Fact]
    public void BackgroundJob_External_Work_Has_No_Business_Transaction()
    {
        using var census = ProductionCensus.Read();
        EvaluateDetector(census, JobExternalActivationOffenders).Should().BeEmpty();
        var application = census.Modules.Single(module => module.Assembly.Name.Name == DeliveryApplication);
        var executor = census.Types[(InfrastructureAssembly, JobExecutorType)];
        var admission = census.Types[(DeliveryApplication, JobAdmission)];
        var capabilities = executor.Methods.Where(method => method.Name is "CallExternalExecuteAsync" or "CallExternalReconcileAsync")
            .Concat(admission.Methods.Where(method => method.Name is "EnterExternal" or "EnterExternalCheckpoint"))
            .Concat(census.Types[(DeliveryApplication, JobRunGate)].Methods.Where(method => method.Name is "TryIssueExternalExecute" or "TryIssueExternalReconcile"))
            .Concat(census.Types[(InfrastructureAssembly, JobExternalObservation)].Methods.Where(method => method.Name == "Capture")).ToArray();
        capabilities.Should().HaveCount(10, "two bridges, five admission entries, two issuance gates and one report capture are live");
        foreach (var capability in capabilities)
        {
            AssertMutation(census, "a missing exact external capability declaration", () =>
            {
                var methods = capability.DeclaringType.Methods;
                var index = methods.IndexOf(capability);
                methods.RemoveAt(index);
                return () => methods.Insert(index, capability);
            }, JobExternalActivationOffenders);
            foreach (var opcode in new[] { OpCodes.Call, OpCodes.Callvirt, OpCodes.Ldftn, OpCodes.Ldvirtftn })
                AssertMutation(census, "an extra external capability or delegate", () =>
                    AddCallProbe(application, "ExternalActivationEscape", capability, opcode), JobExternalActivationOffenders);
            AssertMutation(census, "an asynchronous external capability escape", () =>
                AddAsyncCallProbe(application, "AsyncExternalActivationEscape", capability), JobExternalActivationOffenders);
        }
        foreach (var bridge in executor.Methods.Where(method => method.Name is "CallExternalExecuteAsync" or "CallExternalReconcileAsync").ToArray())
        {
            AssertMutation(census, "a removed actual typed provider bridge", () =>
            {
                executor.Methods.Remove(bridge);
                return () => executor.Methods.Add(bridge);
            }, JobExternalActivationOffenders);
            foreach (var generic in bridge.GenericParameters)
                AssertMutation(census, "a provider bridge losing its exact plan/outcome constraint", () =>
                {
                    var original = generic.Constraints.ToArray();
                    generic.Constraints.Clear();
                    return () => { foreach (var constraint in original) generic.Constraints.Add(constraint); };
                }, JobExternalActivationOffenders);
        }
        var actual = OwnedBodies(census).SelectMany(item => item.Body.Body.Instructions)
            .Where(instruction => instruction.Operand is MethodReference target && (capabilities.Any(capability =>
                    SameType(capability.DeclaringType, target.DeclaringType) && capability.Name == target.Name) || target.DeclaringType.GetElementType().FullName == JobExternalPort
                    && target.Name is "ExecuteAsync" or "ReconcileAsync")).ToArray();
        actual.Should().NotBeEmpty();
        foreach (var instruction in actual)
        {
            AssertMutation(census, "a removed actual external entry or report capture", () => ReplaceInstruction(instruction, OpCodes.Nop, null!), JobExternalActivationOffenders);
            AssertMutation(census, "an approved external entry escaping as a delegate", () => ReplaceInstruction(instruction, OpCodes.Ldftn, instruction.Operand), JobExternalActivationOffenders);
        }
        var reflected = OwnedBodies(census).Where(item => item.Owner.DeclaringType == executor && item.Owner.Name == "InvokeExternalAsync")
            .SelectMany(item => item.Body.Body.Instructions).Where(instruction => instruction.Operand is MethodReference target && IsExternalReflection(target)
                || instruction.OpCode == OpCodes.Ldtoken && instruction.Operand is TypeReference
                || instruction.OpCode == OpCodes.Ldstr && instruction.Operand is string text && text.StartsWith("CallExternal", StringComparison.Ordinal)).ToArray();
        reflected.Should().HaveCount(6, "both bridge names, the exact executor type and three reflection operations remain present");
        foreach (var instruction in reflected)
            AssertMutation(census, "a changed reflected provider bridge selector", () => ReplaceInstruction(instruction, OpCodes.Nop, null!), JobExternalActivationOffenders);
    }
}
