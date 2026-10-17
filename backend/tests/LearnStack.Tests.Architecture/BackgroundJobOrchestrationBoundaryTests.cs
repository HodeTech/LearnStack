using FluentAssertions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed partial class OutboxDispatchConventionTests
{
    private const string OrchestrationControl = "LearnStack.Infrastructure.Jobs.BackgroundJobControl";
    private const string OrchestrationExecution = "LearnStack.Infrastructure.Jobs.BackgroundJobExecutionControl";
    private const string OrchestrationRenewal = "LearnStack.Infrastructure.Jobs.BackgroundJobRenewalOperation";
    private const string OrchestrationIdentity = "LearnStack.Infrastructure.Jobs.BackgroundJobExecutionIdentity";
    private const string OrchestrationInvocation = "LearnStack.Infrastructure.Jobs.HangfireGenerationWork/Invocation";
    private const string OrchestrationBound = "LearnStack.Infrastructure.Jobs.HangfireInvocationContract/BoundInvocation";
    private const string OrchestrationCheckpoint = "LearnStack.Infrastructure.Jobs.BackgroundJobCapacityCheckpointOperation";
    private const string OrchestrationStore = "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobControlStore";

    private static SortedSet<string> JobOrchestrationOffenders(ProductionCensus census)
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        var names = new[] { OrchestrationControl, OrchestrationExecution, OrchestrationRenewal, OrchestrationIdentity,
            OrchestrationInvocation, OrchestrationBound, OrchestrationStore, JobRuntime, OrchestrationCheckpoint, "LearnStack.Infrastructure.Jobs.JobExecutor" };
        var subjects = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (!census.Types.TryGetValue((InfrastructureAssembly, name), out var type))
                offenders.Add("Missing finite job orchestration subject: " + name);
            else subjects.Add(name, type);
        }
        if (offenders.Count != 0) return offenders;
        var sites = CallSites(census).ToArray();
        var sitesByTargetName = sites.ToLookup(site => site.Target.Name, StringComparer.Ordinal);
        var bodies = OwnedBodies(census).ToArray();
        // Rebuild the index inside each detector evaluation so mutations and
        // restoration remain visible without rescanning every instruction per field.
        var fieldSites = bodies.SelectMany(item => item.Body.Body.Instructions
                .Where(instruction => instruction.Operand is FieldReference)
                .Select(instruction => (Owner: MethodId(item.Owner), Instruction: instruction,
                    Field: (FieldReference)instruction.Operand)))
            .ToLookup(item => item.Field.Name, StringComparer.Ordinal);
        RecordDetachedTargets(offenders, sites);
        foreach (var name in names.Take(4).Append(OrchestrationCheckpoint))
        {
            var type = subjects[name];
            if (!type.IsNotPublic || !type.IsSealed)
                offenders.Add("Finite job orchestration owner is internal sealed: " + name);
            var constructors = type.Methods.Where(method => method.Name == ".ctor").ToArray();
            if (constructors.Length != 1 || (name == OrchestrationIdentity ? !constructors[0].IsPrivate : !constructors[0].IsAssembly))
                offenders.Add("Finite job orchestration owner has one closed constructor: " + name);
            if (type.Methods.Where(method => !method.IsPrivate && !method.IsConstructor).SelectMany(method => method.Parameters)
                .Any(parameter => parameter.ParameterType.FullName.StartsWith("System.Func", StringComparison.Ordinal)
                    || parameter.ParameterType.FullName.StartsWith("System.Action", StringComparison.Ordinal)))
                offenders.Add("Job orchestration accepts no arbitrary work callback: " + name);
        }

        Constructor(OrchestrationControl, ["LearnStack.Infrastructure.Persistence.PrivilegedDataSource", "LearnStack.Infrastructure.Jobs.BackgroundJobDefinitionRegistry",
            "LearnStack.Infrastructure.Jobs.JobExecutor", "LearnStack.Infrastructure.Jobs.BackgroundJobRuntimeGate",
            "LearnStack.Infrastructure.Jobs.BackgroundJobRuntimeOptions", "LearnStack.SharedKernel.Identifiers.IGuidFactory", "System.TimeProvider"]);
        Constructor(OrchestrationExecution, [OrchestrationControl, OrchestrationInvocation, "LearnStack.Application.Jobs.BackgroundJobRoutingClaim", "System.TimeProvider"]);
        Constructor(OrchestrationRenewal, [OrchestrationStore, OrchestrationExecution + "[]", "LearnStack.Infrastructure.Jobs.BackgroundJobRuntimeOptions",
            "System.TimeProvider", "System.Threading.CancellationToken"]);
        Constructor(OrchestrationIdentity, ["LearnStack.Application.Jobs.ConfirmedStartReceipt"]);

        Exact(OrchestrationControl, ".ctor", [(JobRuntime, ".ctor")]);
        Exact(OrchestrationExecution, ".ctor", [(OrchestrationControl, "TryBegin")]);
        Exact(OrchestrationRenewal, ".ctor", [(OrchestrationControl, "RunRenewalAsync")]);
        Exact(OrchestrationIdentity, ".ctor", [(OrchestrationIdentity, "FromReceipt")]);
        Exact(OrchestrationIdentity, "FromReceipt", [(OrchestrationExecution, "ObservePublicationAsync")]);
        Exact(OrchestrationControl, "TryBegin", [(OrchestrationBound, "RunFrameworkAsync")]);
        Exact(OrchestrationControl, "Release", [(OrchestrationExecution, "ReleaseAfterPrivateCleanup")]);
        Exact(OrchestrationControl, "Start", [(JobRuntime, "RunAsync")]);
        Exact(OrchestrationControl, "RequestStop", [(JobRuntime, "RequestStop"), (JobRuntime, "JoinOwnersAsync")]);
        Exact(OrchestrationControl, "FinishAdmissionAsync", [(OrchestrationBound, "RunFrameworkAsync")]);
        Exact(OrchestrationControl, "FinishBusinessAsync", [(OrchestrationBound, "RunFrameworkAsync")]);
        Exact(OrchestrationControl, "ScheduleLoops", [(OrchestrationControl, "Start"), (OrchestrationControl, "Start")]);
        foreach (var name in new[] { "RunRenewalAsync", "RunControlAsync", "RunTerminalAsync" })
            Exact(OrchestrationControl, name, [(OrchestrationControl, "ScheduleLoops")]);
        Exact(OrchestrationControl, "RelayStopAsync", [(OrchestrationControl, ".ctor")]);
        Exact(OrchestrationControl, "ObserveCompletionAsync", [(OrchestrationControl, ".ctor")]);
        Exact(OrchestrationExecution, "HoldAdmission", [(OrchestrationBound, "RunFrameworkAsync")]);
        Exact(OrchestrationExecution, "HoldBusiness", [(OrchestrationBound, "RunFrameworkAsync")]);
        Exact(OrchestrationExecution, "WaitForPublicationAsync", [(OrchestrationBound, "RunFrameworkAsync")]);
        Exact(OrchestrationExecution, "JoinAsync", [(OrchestrationBound, "RunFrameworkAsync")]);
        Exact(OrchestrationExecution, "ReleaseAfterPrivateCleanup", [(OrchestrationInvocation, "CompleteAsync")]);
        Exact(OrchestrationExecution, "ObservePublicationAsync", [(OrchestrationExecution, ".ctor")]);
        Exact(OrchestrationExecution, "ObserveDeadlineAsync", [(OrchestrationExecution, "ObservePublicationAsync")]);
        Exact(OrchestrationExecution, "RecordRenewal", [(OrchestrationControl, "RunRenewalAsync")]);
        Exact(OrchestrationExecution, "RefuseRenewal", [(OrchestrationExecution, "RecordRenewal"),
            (OrchestrationExecution, "ObservePublicationAsync"), (OrchestrationControl, "RunRenewalAsync"),
            (OrchestrationControl, "RunRenewalAsync"), (OrchestrationControl, "RunRenewalAsync"), (OrchestrationControl, "RunRenewalAsync")]);
        Exact(OrchestrationExecution, "CloseExecution", [(OrchestrationExecution, "RefuseRenewal"),
            (OrchestrationExecution, "LeaseElapsed"), (OrchestrationExecution, "ObservePublicationAsync"),
            (OrchestrationExecution, "ObservePublicationAsync"), (OrchestrationExecution, "ObserveDeadlineAsync"),
            (OrchestrationExecution, "ObserveDeadlineAsync"), (OrchestrationControl, "RelayStopAsync")]);
        Exact(OrchestrationRenewal, "Start", [(OrchestrationControl, "RunRenewalAsync")]);
        Exact(OrchestrationRenewal, "Schedule", [(OrchestrationRenewal, "Start"), (OrchestrationRenewal, "Start")]);
        Exact(OrchestrationRenewal, "ObserveWaitAsync", [(OrchestrationRenewal, ".ctor")]);
        Exact(OrchestrationExecution, "JoinBusinessObserversAsync", [(OrchestrationExecution, "JoinAsync"),
            (OrchestrationInvocation, "PrepareCapacityCleanupCoreAsync")]);
        Exact(OrchestrationExecution, "JoinBusinessObserversCoreAsync", [(OrchestrationExecution, "JoinBusinessObserversAsync")]);
        Exact(OrchestrationExecution, "HoldCapacityCheckpoint", [(OrchestrationControl, "ProcessCapacityRequestAsync")]);
        Constructor(OrchestrationCheckpoint, ["LearnStack.Infrastructure.Jobs.JobExecutor",
            "LearnStack.Application.Jobs.BackgroundJobCapacityRecoveryReceipt", "LearnStack.Infrastructure.Jobs.BackgroundJobActualCleanup",
            "LearnStack.Infrastructure.Jobs.BackgroundJobRuntimeOptions", "System.TimeProvider"]);
        Exact(OrchestrationCheckpoint, ".ctor", [(OrchestrationControl, "ProcessCapacityRequestAsync")]);
        Exact(OrchestrationCheckpoint, "Start", [(OrchestrationControl, "ProcessCapacityRequestAsync")]);
        Exact(OrchestrationCheckpoint, "Schedule", [(OrchestrationCheckpoint, "Start"), (OrchestrationCheckpoint, "Start")]);
        Exact(OrchestrationCheckpoint, "RelayStopAsync", [(OrchestrationCheckpoint, ".ctor")]);
        Exact(OrchestrationCheckpoint, "ObserveActualAsync", [(OrchestrationCheckpoint, ".ctor")]);
        Exact(OrchestrationCheckpoint, "ObserveWaitAsync", [(OrchestrationCheckpoint, ".ctor")]);
        Exact(OrchestrationCheckpoint, "RequestStop", [(OrchestrationCheckpoint, "Start"),
            (OrchestrationCheckpoint, "ObserveActualAsync"), (OrchestrationCheckpoint, "ObserveWaitAsync"),
            (OrchestrationControl, "RelayStopAsync")]);

        var timerCallback = Callback(OrchestrationExecution, "ObservePublicationAsync", OrchestrationExecution, "LeaseElapsed");
        if (timerCallback is not null) ExactIds(OrchestrationExecution, "LeaseElapsed", [MethodId(timerCallback)]);
        var checkpointCallback = Callback(OrchestrationCheckpoint, "Schedule", "LearnStack.Infrastructure.Jobs.JobExecutor", "ExecuteCapacityCleanupAsync");
        var renewalCallback = Callback(OrchestrationRenewal, "Schedule", OrchestrationStore, "RenewBatchAsync");
        if (renewalCallback is not null) ExactIds(OrchestrationStore, "RenewBatchAsync", [MethodId(renewalCallback)]);
        var timerSites = sites.Where(site => site.Target.DeclaringType?.FullName == "System.TimeProvider"
            && site.Target.Name == "CreateTimer" && site.Owner.Contains(OrchestrationExecution, StringComparison.Ordinal)).ToArray();
        CheckSet(offenders, "job lease timer construction", timerSites.Select(site => site.Owner),
            [Id(OrchestrationExecution, "ObservePublicationAsync")]);
        var wait = Method(OrchestrationRenewal, "ObserveWaitAsync");
        if (wait is not null && sites.Count(site => site.Owner == MethodId(wait) && site.Target.Name == "WaitAsync"
                && ParametersMatch(site.Target, ["System.TimeSpan", "System.TimeProvider", "System.Threading.CancellationToken"])) != 1)
            offenders.Add("Renewal observation must retain its finite actual completion wait");
        RequireBound("TryBegin", 64);
        RequireBound("Enqueue", 128);

        Field(OrchestrationControl, "_executions", true, [".ctor", "TryBegin", "Release", "get_Snapshot", "RunRenewalAsync", "RelayStopAsync", "RetainedIds"]);
        Field(OrchestrationControl, "_requests", true, [".ctor", "get_Snapshot", "Enqueue", "RunControlAsync", "RelayStopAsync", "RetainedIds", "FinishRequest"]);
        Field(OrchestrationControl, "_stop", true, [".ctor", "RunRenewalAsync", "RunControlAsync", "ProcessRequestAsync", "ProcessCapacityRequestAsync", "RunTerminalAsync", "ProbePendingAsync", "RelayStopAsync", "ObserveCompletionAsync", "PauseAsync"]);
        Field(OrchestrationControl, "_stopRequested", true, [".ctor", "RequestStop", "RelayStopAsync"]);
        Field(OrchestrationControl, "_loopsPublished", true, [".ctor", "Start", "RequestStop", "ObserveCompletionAsync"]);
        Field(OrchestrationControl, "_completion", true, [".ctor", "get_Completion", "ObserveCompletionAsync"]);
        Field(OrchestrationControl, "_stopRelay", true, [".ctor", "ObserveCompletionAsync"]);
        Field(OrchestrationControl, "_completionObservation", true, [".ctor"]);
        Field(OrchestrationControl, "_renewalOperation", false, ["RunRenewalAsync"]);
        Field(OrchestrationControl, "_renewalFlight", false, ["RunRenewalAsync"]);
        Field(OrchestrationControl, "_controlOperation", false, ["RunControlAsync", "ProcessRequestAsync", "ProcessCapacityRequestAsync", "RetainedIds"]);
        Field(OrchestrationControl, "_terminalOperation", false, ["RunTerminalAsync", "RetainedIds"]);
        Field(OrchestrationControl, "_controlProbe", false, ["ProcessRequestAsync", "ProcessCapacityRequestAsync", "ProbePendingAsync"]);
        Field(OrchestrationControl, "_terminalProbe", false, ["ProbePendingAsync"]);
        Field(OrchestrationControl, "_pendingControl", false, ["get_Snapshot", "RunControlAsync", "QualifyControlAsync", "ProbePendingAsync", "RetainedIds"]);
        Field(OrchestrationControl, "_pendingTerminal", false, ["get_Snapshot", "RunTerminalAsync", "QualifyTerminalAsync", "ProbePendingAsync", "RetainedIds"]);
        Field(OrchestrationControl, "_terminalActivity", false, ["RunTerminalAsync"]);
        Field(OrchestrationControl, "_cancellation", false, ["RelayStopAsync"]);
        Field(OrchestrationExecution, "_owner", true, [".ctor", "ReleaseAfterPrivateCleanup"]);
        Field(OrchestrationExecution, "_work", true, [".ctor", "get_Token", "LeaseElapsed", "CloseExecution"]);
        Field(OrchestrationExecution, "_deadlineStop", true, [".ctor", "ObserveDeadlineAsync", "JoinBusinessObserversCoreAsync"]);
        Field(OrchestrationExecution, "_publicationObservation", true, [".ctor", "WaitForPublicationAsync", "JoinBusinessObserversCoreAsync"]);
        Field(OrchestrationExecution, "<Publication>k__BackingField", true, [".ctor", "get_Publication"]);
        Field(OrchestrationExecution, "<Gate>k__BackingField", true, [".ctor", "get_Gate"]);
        Field(OrchestrationExecution, "<Claim>k__BackingField", true, [".ctor", "get_Claim"]);
        Field(OrchestrationExecution, "_admission", false, ["HoldAdmission", "JoinAsync"]);
        Field(OrchestrationExecution, "_business", false, ["HoldBusiness", "JoinAsync"]);
        Field(OrchestrationExecution, "_identity", false, ["get_Identity", "ObservePublicationAsync"]);
        Field(OrchestrationExecution, "_deadlineObservation", false, ["ObservePublicationAsync", "JoinBusinessObserversCoreAsync"]);
        Field(OrchestrationExecution, "_deadlineCancellation", false, ["JoinBusinessObserversCoreAsync"]);
        Field(OrchestrationExecution, "_leaseTimer", false, ["ObservePublicationAsync", "RecordRenewal", "JoinBusinessObserversCoreAsync"]);
        Field(OrchestrationExecution, "_observersPublished", true, [".ctor", "JoinBusinessObserversAsync"]);
        Field(OrchestrationExecution, "_capacityCheckpoint", false, ["HoldCapacityCheckpoint", "JoinAsync"]);
        Field(OrchestrationCheckpoint, "_published", true, [".ctor", "Schedule", "ObserveActualAsync"]);
        Field(OrchestrationCheckpoint, "_actual", false, ["Schedule"]);
        Field(OrchestrationCheckpoint, "_stopRequested", true, [".ctor", "RequestStop", "RelayStopAsync"]);
        Field(OrchestrationCheckpoint, "_stopRelay", true, [".ctor", "ObserveActualAsync"]);
        Field(OrchestrationCheckpoint, "_cancellation", false, ["RelayStopAsync"]);
        Field(OrchestrationCheckpoint, "<Completion>k__BackingField", true, [".ctor", "get_Completion"]);
        Field(OrchestrationCheckpoint, "<TimelyReturn>k__BackingField", true, [".ctor", "get_TimelyReturn"]);
        Field(OrchestrationCheckpoint, "<Recovery>k__BackingField", true, [".ctor", "get_Recovery"]);
        Field(OrchestrationCheckpoint, "<Cleanup>k__BackingField", true, [".ctor", "get_Cleanup"]);
        if (checkpointCallback is not null)
        {
            FieldIds(OrchestrationCheckpoint, "_executor", true, [Id(OrchestrationCheckpoint, ".ctor"), MethodId(checkpointCallback)]);
            FieldIds(OrchestrationCheckpoint, "_private", true, [Id(OrchestrationCheckpoint, ".ctor"),
                Id(OrchestrationCheckpoint, "RelayStopAsync"), Id(OrchestrationCheckpoint, "ObserveActualAsync"), MethodId(checkpointCallback)]);
        }
        Field(OrchestrationInvocation, "_cleanupPublished", true, [".ctor", "PrepareCapacityCleanupAsync"]);
        Field(OrchestrationInvocation, "_cleanupStarted", false, ["PrepareCapacityCleanupAsync"]);
        Field(OrchestrationInvocation, "_private", true, [".ctor", "get_Token", "CompleteAsync", "StartCancellationObservation"]);
        Field(OrchestrationInvocation, "_cancellationObservation", false,
            ["PrepareCapacityCleanupCoreAsync", "RequestCancellation", "CompleteAsync", "StartCancellationObservation"]);
        Field(OrchestrationInvocation, "_admission", false, ["HoldAdmission", "PrepareCapacityCleanupCoreAsync", "CompleteAsync"]);
        Field(OrchestrationInvocation, "_business", false, ["HoldBusiness", "PrepareCapacityCleanupCoreAsync", "CompleteAsync"]);
        Field(OrchestrationInvocation, "_execution", false,
            ["HoldExecution", "PrepareCapacityCleanupAsync", "RequestCancellation", "CompleteAsync"]);
        Field(OrchestrationInvocation, "_framework", false, ["HoldInvocation", "CompleteAsync"]);
        Field(OrchestrationInvocation, "_routing", false, ["HoldRouting", "CompleteAsync"]);
        Field(OrchestrationControl, "_executor", true, [".ctor", "ProcessCapacityRequestAsync"]);
        Field(OrchestrationRenewal, "_published", true, [".ctor", "Schedule"]);
        Field(OrchestrationRenewal, "_actual", false, ["Schedule"]);
        Field(OrchestrationRenewal, "<Completion>k__BackingField", true, [".ctor", "get_Completion"]);
        Field(OrchestrationRenewal, "<TimelyReturn>k__BackingField", true, [".ctor", "get_TimelyReturn"]);
        Field(OrchestrationRenewal, "<IssuedAt>k__BackingField", true, [".ctor", "get_IssuedAt"]);
        if (renewalCallback is not null)
        {
            FieldIds(OrchestrationRenewal, "_store", true, [Id(OrchestrationRenewal, ".ctor"), MethodId(renewalCallback)]);
            FieldIds(OrchestrationRenewal, "_identities", true, [Id(OrchestrationRenewal, ".ctor"), MethodId(renewalCallback)]);
            FieldIds(OrchestrationRenewal, "_stop", true,
                [Id(OrchestrationRenewal, ".ctor"), Id(OrchestrationRenewal, "ObserveWaitAsync"), MethodId(renewalCallback)]);
        }
        foreach (var property in new[] { "JobId", "TenantId", "OrganizationId", "DefinitionKey", "Queue", "CapacityGroup", "AttemptId", "Token",
                     "WakeGeneration", "Deadline", "AdmittedAt", "OwnershipUntil", "NormalAttemptOrdinal" })
            Field(OrchestrationIdentity, "<" + property + ">k__BackingField", true, [".ctor", "get_" + property]);
        if (subjects[OrchestrationIdentity].Fields.Count(field => !field.IsStatic) != 13
            || subjects[OrchestrationIdentity].Methods.Any(method => method.Name.StartsWith("set_", StringComparison.Ordinal)))
            offenders.Add("An execution identity contains exactly its thirteen immutable acknowledged facts");
        TaskState(OrchestrationControl, "_completion");
        TaskState(OrchestrationRenewal, "_published");
        TaskState(OrchestrationCheckpoint, "_published");
        return offenders;

        MethodDefinition? Method(string type, string name)
        {
            var found = subjects[type].Methods.Where(method => method.Name == name).ToArray();
            if (found.Length == 1) return found[0];
            offenders.Add("Missing or overloaded orchestration seam: " + type + "::" + name);
            return null;
        }
        void Constructor(string type, string[] parameters)
        {
            var constructor = Method(type, ".ctor");
            if (constructor is not null && !ParametersMatch(constructor, parameters))
                offenders.Add("Unexpected concrete orchestration constructor signature: " + type);
        }
        string Id(string type, string name) => Method(type, name) is { } method ? MethodId(method) : "missing:" + type + "::" + name;
        void Exact(string type, string name, (string Type, string Method)[] expected) =>
            ExactIds(type, name, expected.Select(item => Id(item.Type, item.Method)).ToArray());
        void ExactIds(string type, string name, string[] expected)
        {
            if (Method(type, name) is null) return;
            CheckSet(offenders, "finite orchestration caller " + type + "::" + name,
                sitesByTargetName[name].Where(site => TargetsDeclaredType(site.Target, subjects[type])).Select(site => site.Owner), expected);
        }
        MethodDefinition? Callback(string type, string ownerName, string targetType, string targetName)
        {
            var owner = Method(type, ownerName);
            if (owner is null) return null;
            var delegates = bodies.Where(item => item.Owner == owner).SelectMany(item => DelegateTargets(item.Body))
                .Select(reference => census.Types.Values.SelectMany(candidate => candidate.Methods)
                    .SingleOrDefault(candidate => MethodId(candidate) == MethodId(reference)))
                .Where(candidate => candidate is not null && MethodOperands(candidate).Any(target =>
                    TargetsDeclaredType(target, subjects[targetType]) && target.Name == targetName)).Cast<MethodDefinition>().Distinct().ToArray();
            if (delegates.Length != 1) { offenders.Add("Missing or additional actual orchestration callback: " + type + "::" + ownerName); return null; }
            var callback = delegates[0];
            if (callback.IsPublic || !(callback.DeclaringType == subjects[type] && callback.IsPrivate
                    || callback.DeclaringType.IsNestedPrivate && callback.DeclaringType.DeclaringType == subjects[type]))
                offenders.Add("The actual orchestration callback stays private: " + callback.FullName);
            CheckSet(offenders, "actual orchestration callback issuance " + callback.FullName,
                sites.Where(site => HasAttachedDeclaringType(site.Target) && MethodId(site.Target) == MethodId(callback)).Select(site => site.Owner), [MethodId(owner)]);
            return callback;
        }
        void Field(string type, string name, bool readOnly, string[] owners) => FieldIds(type, name, readOnly, owners.Select(owner => Id(type, owner)).ToArray());
        void FieldIds(string type, string name, bool readOnly, string[] expected)
        {
            var fields = subjects[type].Fields.Where(field => field.Name == name).ToArray();
            if (fields.Length != 1 || !fields[0].IsPrivate || fields[0].IsStatic || fields[0].IsInitOnly != readOnly
                || fields[0].FieldType.FullName != OrchestrationFieldType(type, name))
            { offenders.Add("Missing or retyped orchestration resource owner: " + type + ":" + name); return; }
            var access = fieldSites[name].Where(item => TargetsDeclaredType(item.Field, subjects[type])).ToArray();
            CheckSet(offenders, "retained orchestration resource callers " + type + ":" + name,
                access.Select(item => item.Owner).Distinct(), expected);
            var requiredWrites = readOnly ? 1 : OrchestrationWrites(type, name);
            if (access.Count(item => item.Instruction.OpCode == OpCodes.Stfld) != requiredWrites)
                offenders.Add("Unexpected retained orchestration slot write count: " + type + ":" + name);
            if (type == OrchestrationExecution && name == "_identity")
                CheckSet(offenders, "immutable identity publication addresses", access.Where(item => item.Instruction.OpCode == OpCodes.Ldflda)
                    .Select(item => item.Owner), [Id(type, "ObservePublicationAsync"), Id(type, "ObservePublicationAsync"),
                        Id(type, "ObservePublicationAsync"), Id(type, "get_Identity")]);
            if (readOnly && access.Any(item => item.Instruction.OpCode == OpCodes.Stfld && item.Owner != Id(type, ".ctor")))
                offenders.Add("An immutable orchestration resource is replaced outside its constructor: " + type + ":" + name);
        }
        void RequireBound(string methodName, int bound)
        {
            var method = Method(OrchestrationControl, methodName);
            if (method is not null && bodies.Where(item => item.Owner == method).SelectMany(item => item.Body.Body.Instructions)
                    .Count(instruction => OrchestrationConstant(instruction) == bound) != 1)
                offenders.Add("Finite orchestration bound is missing or enlarged: " + methodName);
        }
        void TaskState(string type, string fieldName)
        {
            var constructor = Method(type, ".ctor");
            if (constructor is null) return;
            var writes = bodies.Where(item => item.Owner == constructor).SelectMany(item => item.Body.Body.Instructions)
                .Where(instruction => instruction.OpCode == OpCodes.Stfld && instruction.Operand is FieldReference field
                    && TargetsDeclaredType(field, subjects[type]) && field.Name == fieldName).ToArray();
            if (writes.Length != 1 || writes[0].Previous?.Operand is not MethodReference create
                || create.Name != ".ctor" || !ParametersMatch(create, ["System.Object", "System.Threading.Tasks.TaskCreationOptions"])
                || writes[0].Previous?.Previous?.Previous?.OpCode != OpCodes.Ldarg_0)
                offenders.Add("A finite orchestration completion retains its actual owner: " + type + ":" + fieldName);
        }
    }

    private static int OrchestrationWrites(string type, string name) => type switch
    {
        OrchestrationControl => name switch
        {
            "_renewalOperation" or "_renewalFlight" => 3,
            "_controlOperation" => 10,
            "_controlProbe" => 8,
            "_terminalOperation" => 4,
            "_terminalProbe" or "_pendingControl" or "_pendingTerminal" or "_terminalActivity" => 2,
            "_cancellation" => 1,
            _ => -1,
        },
        OrchestrationExecution => name == "_identity" ? 0 : 1,
        OrchestrationRenewal => name == "_actual" ? 1 : -1,
        OrchestrationCheckpoint => name is "_actual" or "_cancellation" ? 1 : -1,
        OrchestrationInvocation => name == "_cleanupStarted" ? 1 : 2,
        _ => -1,
    };

    private static string OrchestrationFieldType(string type, string name)
    {
        const string task = "System.Threading.Tasks.Task";
        const string source = "System.Threading.CancellationTokenSource";
        const string signal = "System.Threading.Tasks.TaskCompletionSource";
        const string renewal = "LearnStack.Infrastructure.Jobs.OwnedBackgroundJobControl`1<System.Collections.Generic.IReadOnlyList`1<LearnStack.Infrastructure.Jobs.BackgroundJobRenewal>>";
        const string probe = "LearnStack.Infrastructure.Jobs.OwnedBackgroundJobControl`1<LearnStack.Infrastructure.Jobs.BackgroundJobProbeResult>";
        if (type == OrchestrationControl) return name switch
        {
            "_executions" => "System.Collections.Generic.Dictionary`2<System.Guid," + OrchestrationExecution + ">",
            "_executor" => "LearnStack.Infrastructure.Jobs.JobExecutor",
            "_requests" => "System.Collections.Generic.List`1<" + OrchestrationControl + "/CompletionRequest>",
            "_stop" => source,
            "_stopRequested" or "_completion" => signal,
            "_loopsPublished" => signal + "`1<System.Threading.Tasks.Task[]>",
            "_stopRelay" or "_completionObservation" or "_cancellation" => task,
            "_renewalOperation" => renewal,
            "_renewalFlight" => OrchestrationRenewal,
            "_controlOperation" or "_terminalOperation" => "System.Object",
            "_controlProbe" or "_terminalProbe" => probe,
            "_pendingControl" or "_pendingTerminal" => OrchestrationControl + "/PendingCommitCursor",
            "_terminalActivity" => "System.Diagnostics.Activity",
            _ => "unlisted orchestration field:" + name,
        };
        if (type == OrchestrationExecution) return name switch
        {
            "_owner" => OrchestrationControl,
            "_work" => OrchestrationInvocation,
            "_deadlineStop" => source,
            "_observersPublished" => signal + "`1<" + task + "`1<System.Boolean>>",
            "_capacityCheckpoint" => OrchestrationCheckpoint,
            "_publicationObservation" or "_deadlineObservation" or "_deadlineCancellation" => task,
            "<Publication>k__BackingField" => "LearnStack.Application.Jobs.BackgroundJobStartPublication",
            "<Gate>k__BackingField" => "LearnStack.Application.Jobs.BackgroundJobCommitGate",
            "<Claim>k__BackingField" => "LearnStack.Application.Jobs.BackgroundJobRoutingClaim",
            "_admission" => task + "`1<LearnStack.Infrastructure.Jobs.JobStartExecutionResult>",
            "_business" => task + "`1<LearnStack.Infrastructure.Jobs.JobBusinessExecutionResult>",
            "_identity" => OrchestrationIdentity,
            "_leaseTimer" => "System.Threading.ITimer",
            _ => "unlisted orchestration field:" + name,
        };
        if (type == OrchestrationRenewal) return name switch
        {
            "_published" => signal + "`1<" + task + "`1<" + renewal + ">>",
            "_actual" or "<Completion>k__BackingField" => task + "`1<" + renewal + ">",
            "<TimelyReturn>k__BackingField" => task + "`1<System.Boolean>",
            "<IssuedAt>k__BackingField" => "System.Int64",
            "_store" => OrchestrationStore,
            "_identities" => OrchestrationIdentity + "[]",
            "_stop" => "System.Threading.CancellationToken",
            _ => "unlisted orchestration field:" + name,
        };
        if (type == OrchestrationInvocation) return name switch
        {
            "_private" => source,
            "_cleanupPublished" => signal + "`1<" + task + "`1<LearnStack.Infrastructure.Jobs.BackgroundJobActualCleanup>>",
            "_cleanupStarted" => "System.Boolean",
            "_cancellationObservation" => task + "`1<System.Boolean>",
            "_admission" => task + "`1<LearnStack.Infrastructure.Jobs.JobStartExecutionResult>",
            "_business" => task + "`1<LearnStack.Infrastructure.Jobs.JobBusinessExecutionResult>",
            "_execution" => OrchestrationExecution,
            "_framework" => OrchestrationBound,
            "_routing" => task + "`1<LearnStack.Infrastructure.Jobs.PostgresBackgroundJobRoutingStore/OwnedRoutingRead>",
            _ => "unlisted invocation field:" + name,
        };
        if (type == OrchestrationCheckpoint) return name switch
        {
            "_executor" => "LearnStack.Infrastructure.Jobs.JobExecutor",
            "_private" => source,
            "_stopRequested" => signal,
            "_published" => signal + "`1<" + task + "`1<LearnStack.Infrastructure.Jobs.JobCapacityCleanupResult>>",
            "_actual" or "<Completion>k__BackingField" => task + "`1<LearnStack.Infrastructure.Jobs.JobCapacityCleanupResult>",
            "_stopRelay" or "<TimelyReturn>k__BackingField" => task + "`1<System.Boolean>",
            "_cancellation" => task,
            "<Recovery>k__BackingField" => "LearnStack.Application.Jobs.BackgroundJobCapacityRecoveryReceipt",
            "<Cleanup>k__BackingField" => "LearnStack.Infrastructure.Jobs.BackgroundJobActualCleanup",
            _ => "unlisted checkpoint field:" + name,
        };
        return name switch
        {
            "<JobId>k__BackingField" => "LearnStack.SharedKernel.Jobs.BackgroundJobId",
            "<TenantId>k__BackingField" => "LearnStack.SharedKernel.Identifiers.TenantId",
            "<OrganizationId>k__BackingField" => "System.Nullable`1<LearnStack.SharedKernel.Identifiers.OrganizationId>",
            "<DefinitionKey>k__BackingField" or "<Queue>k__BackingField" or "<CapacityGroup>k__BackingField" => "System.String",
            "<AttemptId>k__BackingField" => "LearnStack.SharedKernel.Jobs.BackgroundJobAttemptId",
            "<Token>k__BackingField" => "System.Guid",
            "<WakeGeneration>k__BackingField" => "System.Int64",
            "<Deadline>k__BackingField" or "<AdmittedAt>k__BackingField" or "<OwnershipUntil>k__BackingField" => "System.DateTimeOffset",
            "<NormalAttemptOrdinal>k__BackingField" => "System.Int32",
            _ => "unlisted identity field:" + name,
        };
    }

    private static int? OrchestrationConstant(Instruction instruction) => instruction.OpCode.Code switch
    {
        Code.Ldc_I4 => (int)instruction.Operand,
        Code.Ldc_I4_S => Convert.ToInt32(instruction.Operand, System.Globalization.CultureInfo.InvariantCulture),
        _ => null,
    };

    [Fact]
    public void Background_job_orchestration_has_closed_finite_owners_and_rejects_extra_callers_and_lost_retention()
    {
        using var census = ProductionCensus.Read();
        EvaluateDetector(census, JobOrchestrationOffenders).Should().BeEmpty();
        var application = census.Modules.Single(module => module.Assembly.Name.Name == DeliveryApplication);
        var guarded = new Dictionary<string, string[]>
        {
            [OrchestrationControl] = [".ctor", "TryBegin", "Release", "Start", "RequestStop", "FinishAdmissionAsync", "FinishBusinessAsync", "RunRenewalAsync", "RunControlAsync", "RunTerminalAsync"],
            [OrchestrationExecution] = [".ctor", "HoldAdmission", "HoldBusiness", "HoldCapacityCheckpoint", "JoinAsync", "JoinBusinessObserversAsync", "JoinBusinessObserversCoreAsync", "ReleaseAfterPrivateCleanup", "ObservePublicationAsync", "ObserveDeadlineAsync", "RecordRenewal", "RefuseRenewal", "CloseExecution", "LeaseElapsed"],
            [OrchestrationRenewal] = [".ctor", "Start", "Schedule", "ObserveWaitAsync"],
            [OrchestrationIdentity] = [".ctor", "FromReceipt"],
            [OrchestrationCheckpoint] = [".ctor", "Start", "Schedule", "RequestStop", "RelayStopAsync", "ObserveActualAsync", "ObserveWaitAsync"],
        };
        foreach (var (typeName, methods) in guarded)
        {
            var type = census.Types[(InfrastructureAssembly, typeName)];
            foreach (var method in type.Methods.Where(method => methods.Contains(method.Name, StringComparer.Ordinal)).ToArray())
            {
                foreach (var opcode in new[] { OpCodes.Call, OpCodes.Callvirt, OpCodes.Ldftn, OpCodes.Ldvirtftn })
                    AssertMutation(census, "an additional finite orchestration capability through " + opcode,
                        () => AddCallProbe(application, "OrchestrationEscape", method, opcode), JobOrchestrationOffenders);
                AssertMutation(census, "an async orchestration capability escape",
                    () => AddAsyncCallProbe(application, "AsyncOrchestrationEscape", method), JobOrchestrationOffenders);
            }
            AssertMutation(census, "a missing concrete orchestration subject", () =>
            {
                var module = type.Module;
                module.Types.Remove(type);
                return () => module.Types.Add(type);
            }, JobOrchestrationOffenders);
        }
        foreach (var (typeName, field) in new[] { (OrchestrationControl, "_renewalFlight"), (OrchestrationControl, "_controlOperation"),
                     (OrchestrationControl, "_terminalOperation"), (OrchestrationExecution, "_admission"),
                     (OrchestrationExecution, "_business"), (OrchestrationExecution, "_leaseTimer"), (OrchestrationExecution, "_capacityCheckpoint"),
                     (OrchestrationExecution, "_observersPublished"), (OrchestrationRenewal, "_actual"), (OrchestrationCheckpoint, "_actual"),
                     (OrchestrationCheckpoint, "_private"), (OrchestrationCheckpoint, "_cancellation"),
                     (OrchestrationInvocation, "_cleanupPublished"), (OrchestrationInvocation, "_cancellationObservation"),
                     (OrchestrationInvocation, "_admission"), (OrchestrationInvocation, "_business") })
            AssertMutation(census, "an approved owner forwarding a retained orchestration resource",
                () => AddBoundaryFieldGetter(census.Types[(InfrastructureAssembly, typeName)], "ForwardOrchestrationResource", field), JobOrchestrationOffenders);
        var control = census.Types[(InfrastructureAssembly, OrchestrationControl)];
        foreach (var (methodName, bound) in new[] { ("TryBegin", 64), ("Enqueue", 128) })
        {
            var instruction = control.Methods.Single(method => method.Name == methodName).Body.Instructions
                .Single(instruction => OrchestrationConstant(instruction) == bound);
            AssertMutation(census, "an enlarged finite orchestration bound",
                () => ReplaceInstruction(instruction, OpCodes.Ldc_I4, bound + 1), JobOrchestrationOffenders);
        }
        var renewal = census.Types[(InfrastructureAssembly, OrchestrationRenewal)];
        var start = renewal.Methods.Single(method => method.Name == "Start");
        AssertMutation(census, "an added renewal-start overload", () =>
        {
            var overload = new MethodDefinition("Start", MethodAttributes.Assembly, renewal.Module.TypeSystem.Void);
            overload.Parameters.Add(new ParameterDefinition(renewal.Module.TypeSystem.String));
            overload.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            renewal.Methods.Add(overload);
            return () => renewal.Methods.Remove(overload);
        }, JobOrchestrationOffenders);
        AssertMutation(census, "a missing actual renewal-start seam", () =>
        {
            renewal.Methods.Remove(start);
            return () => renewal.Methods.Add(start);
        }, JobOrchestrationOffenders);
    }
}
