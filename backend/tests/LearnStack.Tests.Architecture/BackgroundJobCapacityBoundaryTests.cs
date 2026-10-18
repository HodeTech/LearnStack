using FluentAssertions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed partial class OutboxDispatchConventionTests
{
    private const string CapacityActual = "LearnStack.Infrastructure.Jobs.BackgroundJobActualCleanup";
    private const string CapacityActualSource = CapacityActual + "/MintingSource";
    private const string CapacityRecovery = "LearnStack.Application.Jobs.BackgroundJobCapacityRecoveryReceipt";
    private const string CapacityRecoverySource = CapacityRecovery + "/MintingSource";
    private const string CapacityCandidate = CapacityRecoverySource + "/Candidate";
    private const string CapacityCommand = "LearnStack.Application.Jobs.RecordBackgroundJobCapacityCleanupCommand";
    private const string CapacityEvidence = "LearnStack.Application.Jobs.BackgroundJobCapacityCleanupEvidence";
    private const string CapacityHandler = "LearnStack.Infrastructure.Jobs.RecordBackgroundJobCapacityCleanupCommandHandler";
    private const string CapacityOperation = "LearnStack.Infrastructure.Jobs.BackgroundJobCapacityCheckpointOperation";
    private const string CapacityResult = "LearnStack.Infrastructure.Jobs.JobCapacityCleanupResult";
    private const string CapacityAdmissionResult = "LearnStack.Infrastructure.Jobs.JobStartExecutionResult";
    private const string CapacityBusinessResult = "LearnStack.Infrastructure.Jobs.JobBusinessExecutionResult";
    private const string CapacityEnvelope = "LearnStack.Application.Jobs.BackgroundJobEnvelope";

    /// <summary>ADR-0047 capacity admission: positive cleanup and nonexecuting recovery retain their exact existing owners.</summary>
    [Fact]
    public void BackgroundJob_Capacity_Is_Tenant_Wide_And_Quiescence_Bound()
    {
        using var census = ProductionCensus.Read();
        EvaluateDetector(census, JobCapacityOffenders).Should().BeEmpty(
            "Fix: actual local cleanup and acknowledged nonexecuting recovery must meet on the same sequential application owner");
    }

    private static (string Assembly, string Type, string[] Names)[] CapacityCapabilityKeys() =>
    [
        (InfrastructureAssembly, CapacityActual, [".ctor", "RequireRecovery"]),
        (InfrastructureAssembly, CapacityActualSource, [".ctor", "MintCleanup"]),
        (DeliveryApplication, CapacityRecovery, [".ctor"]),
        (DeliveryApplication, CapacityRecoverySource, [".ctor", "CaptureCandidate", "ConfirmCandidate"]),
        (DeliveryApplication, CapacityCandidate, [".ctor", "get_Receipt"]),
        (DeliveryApplication, CapacityCommand, [".ctor", "BindOwner", "RequireOwner", "RecordCleanup", "MarkCommitting", "RecordCommitted", "RecordRolledBack",
            "set_CommitStarted", "set_CommitAcknowledged", "set_KnownRollback"]),
        (DeliveryApplication, JobAdmission, ["EnterCapacityCleanup", "RequireCapacityCleanup"]),
        (InfrastructureAssembly, OrchestrationInvocation, ["PrepareCapacityCleanupAsync", "PrepareCapacityCleanupCoreAsync"]),
        (InfrastructureAssembly, OrchestrationExecution, ["HoldCapacityCheckpoint", "JoinBusinessObserversAsync", "JoinBusinessObserversCoreAsync"]),
        (InfrastructureAssembly, JobExecutorType, ["ExecuteCapacityCleanupAsync"]),
        (InfrastructureAssembly, CapacityOperation, [".ctor", "Start", "Schedule", "RequestStop", "RelayStopAsync", "ObserveActualAsync", "ObserveWaitAsync"]),
        (InfrastructureAssembly, OrchestrationStore, ["AcquireCapacityRecoveryAsync", "FinishCapacityRecoveryAsync", "ReadRecoveryCandidateAsync", "ProbeCapacityAsync"]),
        (DeliveryApplication, JobCommitGate, ["TrySealUnusedExecution"]),
        .. RecoveryMinterSeams().GroupBy(seam => seam.Type).Select(group =>
            (DeliveryApplication, group.Key, group.Select(seam => seam.Name).Distinct(StringComparer.Ordinal).ToArray())),
    ];

    private static SortedSet<string> JobCapacityOffenders(ProductionCensus census)
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        var types = census.Types;
        var required = CapacityCapabilityKeys().Select(item => (item.Assembly, item.Type))
            .Concat([(DeliveryApplication, CapacityEvidence), (InfrastructureAssembly, CapacityHandler),
                (InfrastructureAssembly, CapacityResult), (InfrastructureAssembly, OrchestrationControl),
                (InfrastructureAssembly, JobBoundInvocation), (DeliveryApplication, DeliveryCoordinator)]).Distinct().ToArray();
        foreach (var subject in required)
            if (!types.ContainsKey(subject)) offenders.Add("Missing closed capacity subject: " + subject.Item2);
        if (offenders.Count != 0) return offenders;
        var sites = CallSites(census).ToArray();
        var bodies = OwnedBodies(census).ToArray();
        var instructions = bodies.SelectMany(item => item.Body.Body.Instructions)
            .Where(instruction => instruction.Operand is MethodReference).ToArray();
        RecordDetachedTargets(offenders, sites);
        // Removed Cecil definitions are already explicit offenders. Their missing
        // declaration cannot safely participate in subsequent identity checks.
        if (offenders.Count != 0) return offenders;
        var sitesByName = sites.ToLookup(site => site.Target.Name, StringComparer.Ordinal);
        var instructionsByName = instructions.ToLookup(instruction => ((MethodReference)instruction.Operand).Name, StringComparer.Ordinal);
        var actual = types[(InfrastructureAssembly, CapacityActual)];
        var recovery = types[(DeliveryApplication, CapacityRecovery)];
        var candidate = types[(DeliveryApplication, CapacityCandidate)];
        var operation = types[(InfrastructureAssembly, CapacityOperation)];
        var command = types[(DeliveryApplication, CapacityCommand)];
        Immutable(actual, MethodAttributes.Private, ["Evidence", "Execution", "Claim", "Envelope", "SourceOwner", "Receipt", "BusinessResult"]);
        Immutable(recovery, MethodAttributes.Private, ["JobId", "TenantId", "OrganizationId", "DefinitionKey", "Profile", "Queue", "CapacityGroup",
            "OriginalDueAt", "AttemptId", "NormalAttemptOrdinal", "AdmittedAt", "Deadline", "AdmissionToken", "WakeGeneration",
            "RecoveryToken", "OwnershipUntil", "DbNow"]);
        Immutable(candidate, MethodAttributes.Assembly, ["Receipt"]);
        Facts(actual, [("Evidence", CapacityEvidence), ("Execution", OrchestrationExecution), ("Claim", JobRoutingClaim),
            ("Envelope", CapacityEnvelope), ("SourceOwner", AppJobOwner), ("Receipt", JobStartReceipt), ("BusinessResult", CapacityBusinessResult)]);
        Facts(recovery, [("JobId", "LearnStack.SharedKernel.Jobs.BackgroundJobId"),
            ("TenantId", "LearnStack.SharedKernel.Identifiers.TenantId"),
            ("OrganizationId", "System.Nullable`1<LearnStack.SharedKernel.Identifiers.OrganizationId>"),
            ("DefinitionKey", "System.String"), ("Profile", "System.String"), ("Queue", "System.String"), ("CapacityGroup", "System.String"),
            ("OriginalDueAt", "System.DateTimeOffset"), ("AttemptId", "LearnStack.SharedKernel.Jobs.BackgroundJobAttemptId"),
            ("NormalAttemptOrdinal", "System.Int32"), ("AdmittedAt", "System.DateTimeOffset"), ("Deadline", "System.DateTimeOffset"),
            ("AdmissionToken", "System.Guid"), ("WakeGeneration", "System.Int64"), ("RecoveryToken", "System.Guid"),
            ("OwnershipUntil", "System.DateTimeOffset"), ("DbNow", "System.DateTimeOffset")]);
        Facts(candidate, [("Receipt", CapacityRecovery)]);
        Facts(command, [("Recovery", CapacityRecovery), ("Evidence", CapacityEvidence), ("SourceOwner", AppJobOwner), ("Gate", JobCommitGate),
            ("CommitStarted", "System.Boolean"), ("CommitAcknowledged", "System.Boolean"), ("KnownRollback", "System.Boolean")]);
        if (!actual.IsNotPublic || !recovery.IsPublic || !candidate.IsNestedFamily || !operation.IsNotPublic || !operation.IsSealed
            || !command.IsPublic || !command.IsSealed)
            offenders.Add("Capacity cleanup, recovery, candidate and operation keep their closed declared visibility");
        Surface(actual, [".ctor", "RequireRecovery", "get_Evidence", "get_Execution", "get_Claim", "get_Envelope", "get_SourceOwner", "get_Receipt", "get_BusinessResult"]);
        Surface(recovery, [".ctor", .. recovery.Properties.Select(property => "get_" + property.Name)]);
        Surface(candidate, [".ctor", "get_Receipt"]);
        Surface(command, [".ctor", "BindOwner", "RequireOwner", "RecordCleanup", "MarkCommitting", "RecordCommitted", "RecordRolledBack",
            "get_Recovery", "get_Evidence", "get_SourceOwner", "get_Gate", "get_CommitStarted", "set_CommitStarted", "get_CommitAcknowledged",
            "set_CommitAcknowledged", "get_KnownRollback", "set_KnownRollback"]);
        if (command.Properties.Where(property => property.SetMethod is not null).Any(property => !property.SetMethod!.IsPrivate)
            || command.Properties.Where(property => property.Name is "Recovery" or "Evidence" or "SourceOwner" or "Gate").Any(property => property.SetMethod is not null))
            offenders.Add("Checkpoint identity is immutable and only its typed owner records durability facts");
        var evidence = types[(DeliveryApplication, CapacityEvidence)];
        CheckSet(offenders, "exact capacity cleanup discriminants", evidence.Fields.Where(field => field.IsLiteral).Select(field => field.Name),
            ["TransactionalRollback", "NeverStarted"]);
        if (!evidence.IsEnum) offenders.Add("Capacity evidence is the closed two-value enum");
        Source(CapacityActualSource, InfrastructureAssembly, OrchestrationInvocation, [".ctor", "MintCleanup"]);
        Source(CapacityRecoverySource, DeliveryApplication, OrchestrationStore, RecoveryMinterSurface());

        Guard(InfrastructureAssembly, CapacityActual, ".ctor", "System.Void",
            [OrchestrationExecution, CapacityAdmissionResult, CapacityEnvelope, CapacityBusinessResult], MethodAttributes.Private, false,
            [Id(InfrastructureAssembly, CapacityActualSource, "MintCleanup")]);
        Guard(InfrastructureAssembly, CapacityActualSource, ".ctor", "System.Void", [], MethodAttributes.Family, false,
            [Id(InfrastructureAssembly, OrchestrationInvocation, ".ctor")]);
        Guard(InfrastructureAssembly, CapacityActualSource, "MintCleanup", CapacityActual,
            [OrchestrationExecution, CapacityAdmissionResult, CapacityEnvelope, CapacityBusinessResult], MethodAttributes.Family, true,
            [Id(InfrastructureAssembly, OrchestrationInvocation, "PrepareCapacityCleanupCoreAsync")]);
        Guard(InfrastructureAssembly, CapacityActual, "RequireRecovery", "System.Void", [CapacityRecovery], MethodAttributes.Assembly, false,
            [Id(InfrastructureAssembly, CapacityOperation, ".ctor"), Id(InfrastructureAssembly, JobExecutorType, "ExecuteCapacityCleanupAsync"),
                Id(InfrastructureAssembly, OrchestrationControl, "ProcessCapacityRequestAsync"), Id(InfrastructureAssembly, OrchestrationStore, "AcquireCapacityRecoveryAsync")]);
        Guard(DeliveryApplication, CapacityRecovery, ".ctor", "System.Void", ["System.Data.Common.DbDataReader"], MethodAttributes.Private, false,
            [Id(DeliveryApplication, CapacityCandidate, ".ctor")]);
        Guard(DeliveryApplication, CapacityRecoverySource, ".ctor", "System.Void", [], MethodAttributes.Family, false,
            [Id(InfrastructureAssembly, OrchestrationStore, ".ctor")]);
        Guard(DeliveryApplication, CapacityCandidate, ".ctor", "System.Void", ["System.Data.Common.DbDataReader"], MethodAttributes.Assembly, false,
            [Id(DeliveryApplication, CapacityRecoverySource, "CaptureCandidate")]);
        Guard(DeliveryApplication, CapacityCandidate, "get_Receipt", CapacityRecovery, [], MethodAttributes.Assembly, false,
            [Id(DeliveryApplication, CapacityRecoverySource, "ConfirmCandidate")]);
        Guard(DeliveryApplication, CapacityRecoverySource, "CaptureCandidate", CapacityCandidate, ["System.Data.Common.DbDataReader"], MethodAttributes.Family, true,
            [Id(InfrastructureAssembly, OrchestrationStore, "ReadRecoveryCandidateAsync")]);
        Guard(DeliveryApplication, CapacityRecoverySource, "ConfirmCandidate", CapacityRecovery, [CapacityCandidate], MethodAttributes.Family, true,
            [Id(InfrastructureAssembly, OrchestrationStore, "AcquireCapacityRecoveryAsync"), Id(InfrastructureAssembly, OrchestrationStore, "ProbeCommitAsync")]);
        Guard(DeliveryApplication, CapacityCommand, ".ctor", "System.Void", [CapacityRecovery, CapacityEvidence, JobCommitGate], MethodAttributes.Public, false,
            [Id(InfrastructureAssembly, JobExecutorType, "ExecuteCapacityCleanupAsync")]);
        Guard(DeliveryApplication, CapacityCommand, "BindOwner", "System.Void", [LifecycleUnit, "System.Boolean"], MethodAttributes.Assembly, false,
            [Id(DeliveryApplication, DeliveryCoordinator, "ExecuteFrameAsync")]);
        Guard(DeliveryApplication, CapacityCommand, "RequireOwner", "System.Void", [LifecycleUnit], MethodAttributes.Public, false,
            new[] { Id(DeliveryApplication, CapacityCommand, "BindOwner"), Id(DeliveryApplication, CapacityCommand, "RecordCleanup"),
                Id(DeliveryApplication, CapacityCommand, "MarkCommitting"), Id(InfrastructureAssembly, CapacityHandler, "Command") }
                .Concat(Enumerable.Repeat(Id(InfrastructureAssembly, CapacityHandler, "Handle"), 11)).ToArray());
        Guard(DeliveryApplication, CapacityCommand, "RecordCleanup", "System.Void", [LifecycleUnit], MethodAttributes.Public, false,
            Enumerable.Repeat(Id(InfrastructureAssembly, CapacityHandler, "Handle"), 2).ToArray());
        foreach (var name in new[] { "MarkCommitting", "RecordCommitted", "RecordRolledBack" })
            Guard(DeliveryApplication, CapacityCommand, name, "System.Void", name == "RecordRolledBack" ? [] : [LifecycleUnit], MethodAttributes.Assembly, false,
                Enumerable.Repeat(Id(DeliveryApplication, DeliveryCoordinator, "ExecuteFrameAsync"), name == "RecordRolledBack" ? 2 : 1).ToArray());
        foreach (var (property, writer) in new[] { ("CommitStarted", "MarkCommitting"), ("CommitAcknowledged", "RecordCommitted"),
                     ("KnownRollback", "RecordRolledBack") })
            Guard(DeliveryApplication, CapacityCommand, "set_" + property, "System.Void", ["System.Boolean"], MethodAttributes.Private, false,
                [Id(DeliveryApplication, CapacityCommand, writer)]);
        Guard(DeliveryApplication, JobAdmission, "EnterCapacityCleanup", "System.IDisposable", [CapacityCommand, LifecycleUnit], MethodAttributes.Public, false,
            [Id(InfrastructureAssembly, JobExecutorType, "ExecuteCapacityCleanupAsync")]);
        Guard(DeliveryApplication, JobAdmission, "RequireCapacityCleanup", "System.Void", [CapacityCommand], MethodAttributes.Public, true,
            [Id(DeliveryApplication, CapacityCommand, "BindOwner"), Id(DeliveryApplication, CapacityCommand, "RequireOwner"),
                Id(DeliveryApplication, JobAdmission, "RequireOwnerEntry")]);
        Guard(InfrastructureAssembly, OrchestrationInvocation, "PrepareCapacityCleanupAsync", TaskOf(CapacityActual), [OrchestrationExecution], MethodAttributes.Assembly, false,
            Enumerable.Repeat(Id(InfrastructureAssembly, JobBoundInvocation, "RunFrameworkAsync"), 2).ToArray());
        Guard(InfrastructureAssembly, OrchestrationInvocation, "PrepareCapacityCleanupCoreAsync", TaskOf(CapacityActual), [OrchestrationExecution], MethodAttributes.Private, false,
            [Id(InfrastructureAssembly, OrchestrationInvocation, "PrepareCapacityCleanupAsync")]);
        Guard(InfrastructureAssembly, OrchestrationExecution, "HoldCapacityCheckpoint", "System.Void", [CapacityOperation], MethodAttributes.Assembly, false,
            [Id(InfrastructureAssembly, OrchestrationControl, "ProcessCapacityRequestAsync")]);
        Guard(InfrastructureAssembly, OrchestrationExecution, "JoinBusinessObserversAsync", TaskOf("System.Boolean"), [], MethodAttributes.Assembly, false,
            [Id(InfrastructureAssembly, OrchestrationInvocation, "PrepareCapacityCleanupCoreAsync"), Id(InfrastructureAssembly, OrchestrationExecution, "JoinAsync"),
                Id(InfrastructureAssembly, OrchestrationExecution, "FinishExternalFinalizeAsync"),
                Id(InfrastructureAssembly, OrchestrationExecution, "RequestNoPlanTerminalAfterPrepareAsync")]);
        Guard(InfrastructureAssembly, OrchestrationExecution, "JoinBusinessObserversCoreAsync", TaskOf("System.Boolean"), [], MethodAttributes.Private, false,
            [Id(InfrastructureAssembly, OrchestrationExecution, "JoinBusinessObserversAsync")]);
        Guard(InfrastructureAssembly, CapacityOperation, ".ctor", "System.Void",
            [JobExecutorType, CapacityRecovery, CapacityActual, JobRunGate + "/RuntimeStop", "LearnStack.Infrastructure.Jobs.BackgroundJobRuntimeOptions", "System.TimeProvider"], MethodAttributes.Assembly, false,
            [Id(InfrastructureAssembly, OrchestrationControl, "ProcessCapacityRequestAsync")]);
        Guard(InfrastructureAssembly, CapacityOperation, "Start", "System.Void", [], MethodAttributes.Assembly, false,
            [Id(InfrastructureAssembly, OrchestrationControl, "ProcessCapacityRequestAsync")]);
        Guard(InfrastructureAssembly, CapacityOperation, "Schedule", "System.Void", [], MethodAttributes.Private, false,
            Enumerable.Repeat(Id(InfrastructureAssembly, CapacityOperation, "Start"), 2).ToArray());
        Guard(InfrastructureAssembly, CapacityOperation, "RequestStop", "System.Void", [], MethodAttributes.Assembly, false,
            [Id(InfrastructureAssembly, CapacityOperation, "Start"), Id(InfrastructureAssembly, CapacityOperation, "ObserveActualAsync"),
                Id(InfrastructureAssembly, CapacityOperation, "ObserveWaitAsync"), Id(InfrastructureAssembly, OrchestrationControl, "RelayStopAsync")]);
        Guard(InfrastructureAssembly, CapacityOperation, "RelayStopAsync", TaskOf("System.Boolean"), [], MethodAttributes.Private, false,
            [Id(InfrastructureAssembly, CapacityOperation, ".ctor")]);
        Guard(InfrastructureAssembly, CapacityOperation, "ObserveActualAsync", TaskOf(CapacityResult), [], MethodAttributes.Private, false,
            [Id(InfrastructureAssembly, CapacityOperation, ".ctor")]);
        Guard(InfrastructureAssembly, CapacityOperation, "ObserveWaitAsync", TaskOf("System.Boolean"), ["System.TimeSpan", "System.TimeProvider"], MethodAttributes.Private, false,
            [Id(InfrastructureAssembly, CapacityOperation, ".ctor")]);
        var callback = CapacityScheduleCallback(census, offenders);
        Guard(InfrastructureAssembly, JobExecutorType, "ExecuteCapacityCleanupAsync", TaskOf(CapacityResult),
            [CapacityRecovery, CapacityActual, JobCommitGate, BoundaryCancellation], MethodAttributes.Assembly, false,
            [callback is null ? "missing actual checkpoint callback" : MethodId(callback)]);
        Guard(InfrastructureAssembly, OrchestrationStore, "AcquireCapacityRecoveryAsync",
            TaskOf("LearnStack.Infrastructure.Jobs.OwnedBackgroundJobControl`1<" + CapacityRecovery + ">"), [CapacityActual, BoundaryCancellation], MethodAttributes.Assembly, false,
            [Id(InfrastructureAssembly, OrchestrationControl, "ProcessCapacityRequestAsync")]);
        Guard(InfrastructureAssembly, OrchestrationStore, "FinishCapacityRecoveryAsync",
            TaskOf("LearnStack.Infrastructure.Jobs.OwnedBackgroundJobControl`1<LearnStack.Infrastructure.Jobs.BackgroundJobControlChange>"),
            [CapacityRecovery, "LearnStack.Infrastructure.Jobs.BackgroundJobFailureDecision", BoundaryCancellation], MethodAttributes.Assembly, false,
            [Id(InfrastructureAssembly, OrchestrationControl, "ProcessCapacityRequestAsync")]);
        Guard(InfrastructureAssembly, OrchestrationStore, "ReadRecoveryCandidateAsync", TaskOf(CapacityCandidate),
            ["LearnStack.Infrastructure.Jobs.BackgroundJobControlResources", "System.Guid", "System.Guid", BoundaryCancellation], MethodAttributes.Private, false,
            [Id(InfrastructureAssembly, OrchestrationStore, "AcquireCapacityRecoveryAsync"), Id(InfrastructureAssembly, OrchestrationStore, "ProbeCapacityAsync")]);
        Guard(InfrastructureAssembly, OrchestrationStore, "ProbeCapacityAsync", TaskOf(OrchestrationStore + "/CapacityProbe"),
            ["LearnStack.Infrastructure.Jobs.BackgroundJobControlResources", "LearnStack.Infrastructure.Jobs.BackgroundJobCommitProbe",
                OrchestrationStore + "/ControlRow", "System.DateTimeOffset", BoundaryCancellation], MethodAttributes.Private, false,
            [Id(InfrastructureAssembly, OrchestrationStore, "ProbeCommitAsync")]);
        Guard(DeliveryApplication, JobCommitGate, "TrySealUnusedExecution", "System.Boolean", [], MethodAttributes.Public, false,
            [Id(InfrastructureAssembly, OrchestrationInvocation, "PrepareCapacityCleanupCoreAsync")]);
        CheckCapacityOrder(census, offenders);
        CheckRecoveryMinterSeams(census, offenders, sitesByName, instructionsByName, bodies);
        return offenders;

        string Id(string assembly, string type, string method) => JobOwner(offenders, types, assembly, type, method);
        void Guard(string assembly, string typeName, string name, string result, string[] parameters,
            MethodAttributes visibility, bool isStatic, string[] owners)
        {
            var type = types[(assembly, typeName)];
            var methods = type.Methods.Where(method => method.Name == name).ToArray();
            if (methods.Length != 1 || methods[0].HasGenericParameters || methods[0].IsStatic != isStatic
                || (methods[0].Attributes & MethodAttributes.MemberAccessMask) != visibility
                || methods[0].ReturnType.FullName != result || !ParametersMatch(methods[0], parameters))
                offenders.Add("Capacity capability requires its exact closed signature: " + typeName + "::" + name);
            CheckSet(offenders, "capacity authority callers " + typeName + "::" + name,
                sitesByName[name].Where(site => TargetsDeclaredType(site.Target, type) && site.Target.Name == name).Select(site => site.Owner), owners);
            foreach (var instruction in instructionsByName[name].Where(instruction => instruction.Operand is MethodReference target
                         && TargetsDeclaredType(target, type) && target.Name == name))
                if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt
                    && !(name == ".ctor" && instruction.OpCode == OpCodes.Newobj))
                    offenders.Add("Capacity capability cannot escape as a delegate: " + typeName + "::" + name);
        }
        void Immutable(TypeDefinition type, MethodAttributes visibility, string[] properties)
        {
            var constructors = type.Methods.Where(method => method.IsConstructor && !method.IsStatic).ToArray();
            if (!type.IsSealed || type.Fields.Any(field => !field.IsStatic && !field.IsInitOnly)
                || type.Properties.Any(property => property.SetMethod is not null) || constructors.Length != 1
                || (constructors[0].Attributes & MethodAttributes.MemberAccessMask) != visibility)
                offenders.Add("Capacity witness construction and facts must remain closed and immutable: " + type.FullName);
            CheckSet(offenders, "capacity immutable fact surface " + type.FullName, type.Properties.Select(property => property.Name), properties);
            CheckSet(offenders, "capacity immutable retained field surface " + type.FullName, type.Fields.Select(field => field.Name),
                properties.Select(property => "<" + property + ">k__BackingField"));
        }
        void Surface(TypeDefinition type, string[] names) => CheckSet(offenders, "closed capacity method surface " + type.FullName,
            type.Methods.Select(method => method.Name), names);
        void Facts(TypeDefinition type, (string Name, string Type)[] properties) => CheckSet(offenders,
            "exact capacity fact types " + type.FullName, type.Properties.Select(property => property.Name + ":" + property.PropertyType.FullName),
            properties.Select(property => property.Name + ":" + property.Type));
        void Source(string typeName, string assembly, string implementation, string[] methods)
        {
            var source = types[(assembly, typeName)];
            if (!source.IsAbstract || source.IsSealed || (typeName == CapacityActualSource ? !source.IsNestedAssembly : !source.IsNestedPublic))
                offenders.Add("Capacity minting source remains abstract with its exact nested visibility: " + typeName);
            Surface(source, methods);
            CheckSet(offenders, "closed capacity minting subtype " + typeName, types.Values.Where(type => Derives(type, source, types)).Select(TypeId),
                [TypeId(types[(InfrastructureAssembly, implementation)])]);
        }
    }

    private static string TaskOf(string type) => "System.Threading.Tasks.Task`1<" + type + ">";

    private static void CheckCapacityOrder(ProductionCensus census, SortedSet<string> offenders)
    {
        var bodies = OwnedBodies(census).ToArray();
        Instruction[] Body(string type, string name) => bodies.Where(item => item.Owner.DeclaringType.FullName == type
                && item.Owner.Name == name && (item.Body.Name == "MoveNext" || item.Body == item.Owner))
            .SelectMany(item => item.Body.Body.Instructions).ToArray();
        static int Find(Instruction[] body, string type, string name) => Array.FindIndex(body,
            instruction => instruction.Operand is MethodReference target && target.DeclaringType.FullName == type && target.Name == name);
        static bool AwaitedBetween(Instruction[] body, int before, int after) => before >= 0 && after > before
            && body.Skip(before + 1).Take(after - before - 1).Any(IsCapacityAwaitResult);
        static bool NoAwaitBetween(Instruction[] body, int before, int after) => before >= 0 && after > before
            && !body.Skip(before + 1).Take(after - before - 1).Any(instruction => instruction.Operand is MethodReference target
                && target.Name == "GetAwaiter");

        var frame = Body(DeliveryCoordinator, "ExecuteFrameAsync");
        var bind = Find(frame, CapacityCommand, "BindOwner");
        var handler = Array.FindIndex(frame, instruction => instruction.Operand is MethodReference target
            && target.Name == "Invoke" && target.DeclaringType.FullName.StartsWith("System.Func`1", StringComparison.Ordinal));
        var committing = Find(frame, CapacityCommand, "MarkCommitting");
        var complete = Find(frame, LifecycleScope, "CompleteAsync");
        var acknowledged = Find(frame, CapacityCommand, "RecordCommitted");
        if (bind < 0 || handler <= bind || committing <= handler || !NoAwaitBetween(frame, committing, complete)
            || !AwaitedBetween(frame, complete, acknowledged))
            offenders.Add("Capacity command binds before dispatch and records ACK only after the actual common owned Complete returns");

        var prepare = Body(OrchestrationInvocation, "PrepareCapacityCleanupCoreAsync");
        var seal = Find(prepare, JobCommitGate, "TrySealUnusedExecution");
        var observers = Find(prepare, OrchestrationExecution, "JoinBusinessObserversAsync");
        var cancel = Find(prepare, OrchestrationInvocation, "RequestCancellation");
        var mint = Find(prepare, CapacityActualSource, "MintCleanup");
        if (seal < 0 || observers <= seal || !AwaitedBetween(prepare, observers, cancel)
            || !AwaitedBetween(prepare, cancel, mint)
            || prepare.Skip(Math.Max(0, observers + 1)).Take(Math.Max(0, mint - observers - 1)).Count(IsCapacityAwaitResult) != 2)
            offenders.Add("Actual cleanup seals unused entry, joins execution observers and then private callbacks before minting");

        var native = Body(JobBoundInvocation, "RunFrameworkAsync");
        var preparations = native.Select((instruction, index) => (instruction, index)).Where(item => item.instruction.Operand is MethodReference target
            && target.DeclaringType.FullName == OrchestrationInvocation && target.Name == "PrepareCapacityCleanupAsync").Select(item => item.index).ToArray();
        var finishBusiness = Find(native, OrchestrationControl, "FinishBusinessAsync");
        var finishAdmission = Find(native, OrchestrationControl, "FinishAdmissionAsync");
        if (preparations.Length != 2 || !AwaitedBetween(native, preparations[0], finishBusiness)
            || preparations[1] <= finishBusiness || !AwaitedBetween(native, preparations[1], finishAdmission))
            offenders.Add("Both native grouped completion branches join actual cleanup before control disposition");

        var request = Body(OrchestrationControl, "ProcessCapacityRequestAsync");
        var construct = Find(request, CapacityOperation, ".ctor");
        var hold = Find(request, OrchestrationExecution, "HoldCapacityCheckpoint");
        var start = Find(request, CapacityOperation, "Start");
        if (!NoAwaitBetween(request, construct, hold) || !NoAwaitBetween(request, hold, start))
            offenders.Add("The original execution retains a prebound checkpoint before its actual task starts");

        var acquire = Body(OrchestrationStore, "AcquireCapacityRecoveryAsync");
        var commit = Find(acquire, OrchestrationStore, "CommitAsync");
        var confirm = Find(acquire, CapacityRecoverySource, "ConfirmCandidate");
        if (!AwaitedBetween(acquire, commit, confirm))
            offenders.Add("A candidate is not recovery authority before the actual acquisition COMMIT ACK");
        var probe = Body(OrchestrationStore, "ProbeCommitAsync");
        var projected = Find(probe, OrchestrationStore, "ProbeCapacityAsync");
        var rolledBack = Array.FindIndex(probe, instruction => instruction.Operand is MethodReference target
            && IntegrationLifecycleCapability(target, census.Types) == "database.RollbackAsync");
        var cleaned = Find(probe, "LearnStack.Infrastructure.Jobs.BackgroundJobControlResources", "CleanupAsync");
        var rollbackAck = Find(probe, "LearnStack.Infrastructure.Jobs.BackgroundJobControlResources", "get_ProbeRollbackAcknowledged");
        var cleanupAck = Find(probe, "LearnStack.Infrastructure.Jobs.BackgroundJobControlResources", "get_CleanupConfirmed");
        var failed = Find(probe, "LearnStack.Infrastructure.Jobs.BackgroundJobControlResources", "get_Failure");
        var confirmed = Find(probe, CapacityRecoverySource, "ConfirmCandidate");
        if (!AwaitedBetween(probe, projected, rolledBack) || !AwaitedBetween(probe, rolledBack, cleaned)
            || !AwaitedBetween(probe, cleaned, confirmed)
            || rollbackAck <= cleaned || cleanupAck <= rollbackAck || failed <= cleanupAck || confirmed <= failed)
            offenders.Add("Recovery probe confirmation follows its actual projection, acknowledged rollback and positive physical cleanup");

        var unused = Body(JobCommitGate, "TrySealUnusedExecution");
        var compare = Find(unused, "System.Threading.Interlocked", "CompareExchange");
        var close = Find(unused, JobCommitGate, "Close");
        if (compare < 3 || close <= compare || unused[compare - 3].OpCode != OpCodes.Ldflda
            || unused[compare - 3].Operand is not FieldReference field || field.Name != "_started"
            || field.DeclaringType.FullName != JobCommitGate || unused[compare - 2].OpCode != OpCodes.Ldc_I4_M1
            || unused[compare - 1].OpCode != OpCodes.Ldc_I4_0)
            offenders.Add("NeverStarted atomically claims the permanently sealed unused-start state before closing commit admission");
        var entry = Body(JobCommitGate, "TryStartExecution");
        var enter = Find(entry, "System.Threading.Interlocked", "CompareExchange");
        if (enter < 3 || entry[enter - 3].OpCode != OpCodes.Ldflda
            || entry[enter - 3].Operand is not FieldReference startField || startField.Name != "_started"
            || startField.DeclaringType.FullName != JobCommitGate || entry[enter - 2].OpCode != OpCodes.Ldc_I4_1
            || entry[enter - 1].OpCode != OpCodes.Ldc_I4_0)
            offenders.Add("Business entry atomically consumes only the unused state and cannot reopen a no-start seal");
        var startStateUses = bodies.SelectMany(item => item.Body.Body.Instructions.Where(instruction =>
                instruction.Operand is FieldReference target && target.DeclaringType.FullName == JobCommitGate && target.Name == "_started")
            .Select(instruction => (Owner: item.Owner, Instruction: instruction))).ToArray();
        CheckSet(offenders, "exclusive atomic unused-start owners", startStateUses.Select(item => item.Owner.Name),
            ["TryStartExecution", "TrySealUnusedExecution"]);
        if (startStateUses.Any(item => item.Owner.DeclaringType.FullName != JobCommitGate || item.Instruction.OpCode != OpCodes.Ldflda))
            offenders.Add("Only the two atomic start transitions may address the unused-start state");
    }

    private static bool IsCapacityAwaitResult(Instruction instruction) => instruction.Operand is MethodReference target
        && target.Name == "GetResult" && target.DeclaringType.FullName.StartsWith("System.Runtime.CompilerServices.", StringComparison.Ordinal)
        && target.DeclaringType.FullName.Contains("Await", StringComparison.Ordinal);

    private static MethodDefinition? CapacityScheduleCallback(ProductionCensus census, SortedSet<string> offenders)
    {
        var type = census.Types[(InfrastructureAssembly, CapacityOperation)];
        var schedulers = type.Methods.Where(method => method.Name == "Schedule").ToArray();
        if (schedulers.Length != 1) { offenders.Add("Capacity checkpoint has one exact actual task scheduler"); return null; }
        var delegates = DelegateTargets(schedulers[0]).ToArray();
        if (delegates.Length != 1) { offenders.Add("Capacity checkpoint scheduling binds one actual executor callback"); return null; }
        var methods = type.Methods.Where(method => MethodId(method) == MethodId(delegates[0])).ToArray();
        if (methods.Length != 1 || !methods[0].IsPrivate || methods[0].IsStatic || methods[0].HasGenericParameters
            || methods[0].Parameters.Count != 0 || methods[0].ReturnType.FullName != TaskOf(CapacityResult))
        { offenders.Add("Capacity scheduling cannot accept another task factory"); return null; }
        var callback = methods[0];
        var callbackId = MethodId(callback);
        CheckSet(offenders, "actual capacity scheduling callback callers", CallSites(census)
            .Where(site => site.Target.Name == callback.Name && MethodId(site.Target) == callbackId)
            .Select(site => site.Owner), [MethodId(schedulers[0])]);
        return callback;
    }
}
