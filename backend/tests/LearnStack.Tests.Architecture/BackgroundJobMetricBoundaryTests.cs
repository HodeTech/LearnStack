using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed partial class OutboxDispatchConventionTests
{
    private const string JobObservation = "LearnStack.Infrastructure.Jobs.BackgroundJobObservation";
    private static readonly string[] JobStageExecutorNames =
    [
        "ExecutePublishedAsync", "ExecuteBusinessAsync", "InvokeExternalAsync", "ReadExternalStateCoreAsync",
        "WriteExternalCheckpointAsync", "ExecuteCapacityCleanupAsync", "ExecuteRecipientAsync",
    ];
    private static readonly (string Name, string Factory, string Numeric, string Unit, string Field)[] JobStageInstrumentInventory =
    [
        ("learnstack_job_duration_seconds", "CreateHistogram", "System.Double", "s", "_duration"),
        ("learnstack_job_failed_total", "CreateCounter", "System.Int64", "1", "_failed"),
        ("learnstack_job_unknown_outcome_total", "CreateCounter", "System.Int64", "1", "_unknown"),
    ];
    private static readonly (string Instrument, string Factory, string Callback)[] JobObservableInventory =
    [
        ("learnstack_job_state_count", "CreateObservableGauge", "CollectCounts"),
        ("learnstack_job_oldest_eligible_age_seconds", "CreateObservableGauge", "CollectEligibleAge"),
        ("learnstack_job_heartbeat_age_seconds", "CreateObservableGauge", "CollectHeartbeatAge"),
        ("learnstack_job_component_state", "CreateObservableGauge", "CollectComponents"),
        ("learnstack_job_control_total", "CreateObservableCounter", "CollectControlTotals"),
    ];

    private static List<MethodDefinition> JobMetricRoots(ProductionCensus census, SortedSet<string> offenders)
    {
        var types = census.Types;
        if (!types.TryGetValue((InfrastructureAssembly, JobObservation), out var observation)
            || !types.TryGetValue((InfrastructureAssembly, JobExecutorType), out var executor))
        {
            offenders.Add("Missing required job observation/executor subject");
            return [];
        }
        if (!observation.IsPublic || !observation.IsSealed)
            offenders.Add("Job observation must retain its concrete public sealed owner");
        var observerConstructor = RequireBoundaryMethod(offenders, observation, ".ctor", "System.Void", MethodAttributes.Public,
            "LearnStack.Infrastructure.Jobs.BackgroundJobDefinitionRegistry", "System.TimeProvider");
        var executorConstructor = RequireBoundaryMethod(offenders, executor, ".ctor", "System.Void", MethodAttributes.Public,
            "Microsoft.Extensions.DependencyInjection.IServiceScopeFactory", "LearnStack.SharedKernel.Tenancy.ITenantContextAccessor",
            "LearnStack.Application.Jobs.BackgroundJobExecutionAdmission", "LearnStack.Infrastructure.Jobs.BackgroundJobRuntimeOptions", JobObservation);
        foreach (var type in new[] { observation, executor })
            if (type.Methods.Count(method => method.IsConstructor && !method.IsStatic) != 1
                || type.Methods.Where(method => method.IsConstructor).Any(method => method.Parameters.Any(parameter => parameter.IsOptional)))
                offenders.Add(type.FullName + ": observation has an alternate/optional construction path");
        var getter = RequireBoundaryMethod(offenders, executor, "get_Observation", JobObservation, MethodAttributes.Assembly);
        CheckBridgeCapturedField(offenders, census, executor, "<Observation>k__BackingField", JobObservation,
            [executorConstructor + ":stfld", getter + ":ldfld"]);
        var observationField = executor.Fields.SingleOrDefault(field => field.Name == "<Observation>k__BackingField");
        if (observationField is not { IsPrivate: true, IsInitOnly: true, IsStatic: false })
            offenders.Add("The required job observation property must retain its private readonly backing field");
        var dispose = RequireBoundaryMethod(offenders, observation, "Dispose", "System.Void", MethodAttributes.Public);
        CheckBridgeCapturedField(offenders, census, observation, "_meter", "System.Diagnostics.Metrics.Meter",
            new[] { observerConstructor + ":stfld", dispose + ":ldfld" }
                .Concat(Enumerable.Repeat(observerConstructor + ":ldfld", JobObservableInventory.Length + 3)).ToArray());
        var bind = RequireBoundaryMethod(offenders, observation, "Bind", "System.Void", MethodAttributes.Assembly,
            JobRuntime, "LearnStack.Infrastructure.Jobs.BackgroundJobRecurrenceRegistry", "System.TimeProvider");
        var sites = CallSites(census).ToArray();
        var stageOwners = JobStageExecutorNames.Select(name => JobOwner(offenders, types, InfrastructureAssembly, JobExecutorType, name)).ToArray();
        var startStage = RequireBoundaryMethod(offenders, observation, "StartStage", "System.Nullable`1<System.Int64>", MethodAttributes.Assembly);
        var recordStage = RequireBoundaryMethod(offenders, observation, "RecordStage", "System.Void", MethodAttributes.Assembly,
            "System.Nullable`1<System.Int64>", "System.String", "System.String", "LearnStack.Infrastructure.Jobs.JobObservedStage",
            "LearnStack.Infrastructure.Jobs.JobObservedOutcome", "System.Boolean", "System.String");
        foreach (var (target, name, owners) in new[]
                 {
                     (startStage, "StartStage", stageOwners),
                     (recordStage, "RecordStage", stageOwners.Append(JobOwner(offenders, types, InfrastructureAssembly, JobExecutorType, "InvokeExternalAsync")).ToArray()),
                 })
        {
            CheckSet(offenders, "actual finite job stage metric calls " + target,
                sites.Where(site => HasAttachedDeclaringType(site.Target) && TargetsDeclaredType(site.Target, observation)
                    && site.Target.Name == name && MethodId(site.Target) == target).Select(site => site.Owner), owners);
            CheckSet(offenders, "job stage metric calls have no delegate or alternate opcode " + name,
                census.MethodInstructions(name).Where(site => HasAttachedDeclaringType(site.Target) && TargetsDeclaredType(site.Target, observation))
                    .Select(site => site.OwnerId + ":" + site.Instruction.OpCode.Name), owners.Select(owner => owner + ":callvirt"));
        }
        foreach (var failure in JobMetricRegistrationOffenders(new ServiceCollection().AddLearnStackBackgroundJobContracts()))
            offenders.Add(failure);
        CheckSet(offenders, "exact once job observation binding", sites.Where(site => TargetsDeclaredType(site.Target, observation)
                && site.Target.Name == "Bind").Select(site => site.Owner + ":" + MethodId(site.Target)),
            [RuntimeConstructorId + ":" + bind]);
        CheckSet(offenders, "job observation binding has no delegate or alternate opcode", census.MethodInstructions("Bind")
                .Where(site => TargetsDeclaredType(site.Target, observation))
                .Select(site => site.OwnerId + ":" + site.Instruction.OpCode.Name), [RuntimeConstructorId + ":callvirt"]);
        CheckSet(offenders, "exact job observation getter callers", sites.Where(site => TargetsDeclaredType(site.Target, executor)
                && site.Target.Name == "get_Observation").Select(site => site.Owner),
            new[] { RuntimeConstructorId, InfrastructureAssembly + ":LearnStack.Infrastructure.Jobs.BackgroundJobRuntimeSnapshot " + JobRuntime + "::get_Snapshot()" }
                .Concat(stageOwners).Append(JobOwner(offenders, types, InfrastructureAssembly, OrchestrationControl, "RunTerminalAsync")));
        CheckSet(offenders, "required shared job observation registration", sites.Where(site => site.Target is GenericInstanceMethod
                && (site.Target.Name.StartsWith("Add", StringComparison.Ordinal) || site.Target.Name.StartsWith("TryAdd", StringComparison.Ordinal)))
            .Where(site => ((GenericInstanceMethod)site.Target).GenericArguments.Any(argument => argument.FullName == JobObservation))
            .Select(site => site.Owner + ":" + site.Target.DeclaringType.FullName + ":" + site.Target.Name),
            [InfrastructureAssembly + ":Microsoft.Extensions.DependencyInjection.IServiceCollection LearnStack.Infrastructure.Jobs.BackgroundJobContractsRegistration::AddLearnStackBackgroundJobContracts(Microsoft.Extensions.DependencyInjection.IServiceCollection):Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions:TryAddSingleton"]);
        var runtime = types[(InfrastructureAssembly, JobRuntime)];
        var constructor = runtime.Methods.SingleOrDefault(method => MethodId(method) == RuntimeConstructorId);
        if (constructor?.HasBody == true)
        {
            var instructions = constructor.Body.Instructions;
            var bindings = instructions.Where(instruction => instruction.Operand is MethodReference method
                && TargetsDeclaredType(method, observation) && method.Name == "Bind").ToArray();
            if (bindings.Length != 1) offenders.Add("The actual runtime requires exactly one metric binding instruction");
            else
            {
                var index = instructions.IndexOf(bindings[0]);
                if (index < 6 || !LoadsArgument(instructions[index - 6], 0)
                    || instructions[index - 5].Operand is not FieldReference { Name: "_executor" } executorField
                    || instructions[index - 5].OpCode != OpCodes.Ldfld || !TargetsDeclaredType(executorField, runtime)
                    || instructions[index - 4].Operand is not MethodReference observerGetter || MethodId(observerGetter) != getter
                    || instructions[index - 4].OpCode != OpCodes.Callvirt || !LoadsArgument(instructions[index - 3], 0)
                    || !LoadsArgument(instructions[index - 2], 3) || !LoadsArgument(instructions[index - 1], 9))
                    offenders.Add("Metric binding must consume this exact executor, runtime, recurrence registry and clock");
                if (MetricPathAvoids(constructor, [bindings[0]], instruction => instruction.OpCode == OpCodes.Ret
                        || instruction.Operand is MethodReference method && method.Name == "RetainBackgroundJobsUntil"))
                    offenders.Add("Job metric binding does not dominate normal construction and source pins");
                foreach (var field in new[] { "_sync", "_pools", "_time", "_bounds", "_capacity", "_applicationCapacity",
                             "_bootstrap", "_completion", "_jobs", "_executor", "_messagingCompletion", "_completionObservation", "_sourceLifetime" })
                {
                    var stores = instructions.Where(instruction => instruction.OpCode == OpCodes.Stfld
                        && instruction.Operand is FieldReference target && TargetsDeclaredType(target, runtime) && target.Name == field).ToArray();
                    if (stores.Length == 0 || stores.Any(store => instructions.IndexOf(store) >= index))
                        offenders.Add("Metric publication precedes required runtime initialization: " + field);
                    else if (MetricPathAvoids(constructor, stores, instruction => instruction == bindings[0]))
                        offenders.Add("Metric publication can bypass required runtime initialization: " + field);
                }
                var pins = instructions.Where(instruction => instruction.Operand is MethodReference method
                    && method.Name == "RetainBackgroundJobsUntil").ToArray();
                if (pins.Length != 2 || pins.Any(pin => instructions.IndexOf(pin) <= index))
                    offenders.Add("Metric binding must precede both actual source pins");
            }
        }
        var roots = new List<MethodDefinition>();
        var observed = new List<string>();
        var observerCtor = observation.Methods.SingleOrDefault(method => MethodId(method) == observerConstructor);
        if (observerCtor?.HasBody != true) return roots;
        foreach (var instruction in observerCtor.Body.Instructions)
        {
            if (instruction.Operand is not MethodReference factory || factory.DeclaringType.FullName != "System.Diagnostics.Metrics.Meter"
                || !factory.Name.StartsWith("CreateObservable", StringComparison.Ordinal)) continue;
            // Pin the actual consumed receiver/arguments. Earlier unused strings
            // or function pointers cannot stand in for the registered callback.
            var end = observerCtor.Body.Instructions.IndexOf(instruction);
            var args = observerCtor.Body.Instructions.Skip(Math.Max(0, end - 8)).Take(8).ToArray();
            if (instruction.OpCode != OpCodes.Callvirt || args.Length != 8
                || args[0].OpCode != OpCodes.Ldarg_0 || args[1].OpCode != OpCodes.Ldfld
                || args[1].Operand is not FieldReference { Name: "_meter" } meter || !TargetsDeclaredType(meter, observation)
                || args[2].OpCode != OpCodes.Ldstr || args[2].Operand is not string name
                || args[3].OpCode != OpCodes.Ldarg_0 || args[4].OpCode != OpCodes.Ldftn
                || args[4].Operand is not MethodReference target || !TargetsDeclaredType(target, observation)
                || args[5].OpCode != OpCodes.Newobj || args[5].Operand is not MethodReference delegateCtor
                || delegateCtor.DeclaringType.Name != "Func`1" || delegateCtor.Name != ".ctor"
                || args[6].OpCode != OpCodes.Ldstr || args[6].Operand is not ("1" or "s")
                || args[7].OpCode != OpCodes.Ldnull || factory.Parameters.Count != 4)
                offenders.Add("An actual job metric registration has no exact consumed name/receiver/closed callback arguments");
            else
            {
                observed.Add(name + ":" + factory.Name + ":" + target.Name);
                var floating = name is "learnstack_job_oldest_eligible_age_seconds" or "learnstack_job_heartbeat_age_seconds";
                if (factory is not GenericInstanceMethod { GenericArguments.Count: 1 } numeric
                    || numeric.GenericArguments[0].FullName != (floating ? "System.Double" : "System.Int64")
                    || (string)args[6].Operand != (floating ? "s" : "1"))
                    offenders.Add("An actual job metric changes its exact numeric type or unit: " + name);
                var methods = observation.Methods.Where(method => method.FullName == target.FullName).ToArray();
                if (methods is not [{ IsPrivate: true, IsStatic: false, HasBody: true } root])
                    offenders.Add("An actual metric callback is missing, ambiguous or no longer private instance memory observation");
                else roots.Add(root);
            }
        }
        CheckSet(offenders, "actual nonempty job observable instrument/callback inventory", observed,
            JobObservableInventory.Select(item => item.Instrument + ":" + item.Factory + ":" + item.Callback));
        var stages = new List<string>();
        foreach (var instruction in observerCtor.Body.Instructions)
        {
            if (instruction.Operand is not MethodReference factory || factory.DeclaringType.FullName != "System.Diagnostics.Metrics.Meter"
                || !factory.Name.StartsWith("Create", StringComparison.Ordinal) || factory.Name.StartsWith("CreateObservable", StringComparison.Ordinal)) continue;
            var count = factory.Parameters.Count + 2;
            var end = observerCtor.Body.Instructions.IndexOf(instruction);
            var args = observerCtor.Body.Instructions.Skip(Math.Max(0, end - count)).Take(count).ToArray();
            if (instruction.OpCode != OpCodes.Callvirt || args.Length != count || count < 5
                || args[0].OpCode != OpCodes.Ldarg_0 || args[1].OpCode != OpCodes.Ldfld
                || args[1].Operand is not FieldReference { Name: "_meter" } meter || !TargetsDeclaredType(meter, observation)
                || args[2].OpCode != OpCodes.Ldstr || args[2].Operand is not string name
                || args[3].OpCode != OpCodes.Ldstr || args[3].Operand is not string unit
                || args.Skip(4).Any(argument => argument.OpCode != OpCodes.Ldnull)
                || factory is not GenericInstanceMethod { GenericArguments.Count: 1 } numeric
                || instruction.Next?.OpCode != OpCodes.Stfld || instruction.Next.Operand is not FieldReference field
                || !TargetsDeclaredType(field, observation))
                offenders.Add("A synchronous job metric has no exact consumed receiver, name, unit, numeric type or stored field");
            else stages.Add(name + ":" + factory.Name + ":" + numeric.GenericArguments[0].FullName + ":" + unit + ":" + field.Name);
        }
        CheckSet(offenders, "actual nonempty synchronous job instrument metadata", stages,
            JobStageInstrumentInventory.Select(item => item.Name + ":" + item.Factory + ":" + item.Numeric + ":" + item.Unit + ":" + item.Field));
        if (types.TryGetValue((InfrastructureAssembly, OrchestrationControl), out var control))
        {
            foreach (var (name, visibility, parameters) in new (string, MethodAttributes, string[])[]
                     {
                         ("ObserveExternalRetry", MethodAttributes.Assembly, []),
                         ("ObserveRequestRetry", MethodAttributes.Private, [OrchestrationRequest]),
                         ("ObserveRequestHistory", MethodAttributes.Private, [OrchestrationRequest,
                             "LearnStack.Infrastructure.Jobs.BackgroundJobCommitProbe",
                             "LearnStack.Infrastructure.Jobs.OwnedBackgroundJobControl`1<LearnStack.Infrastructure.Jobs.BackgroundJobProbeResult>"]),
                     })
            {
                var id = RequireBoundaryMethod(offenders, control, name, "System.Void", visibility, parameters);
                roots.AddRange(control.Methods.Where(method => MethodId(method) == id));
            }
            var predicate = control.Methods.Where(method => method.Name == "IsRetryTransition").ToArray();
            if (predicate is not [{ IsPrivate: true, IsStatic: true, HasGenericParameters: false } test]
                || test.ReturnType.FullName != "System.Boolean"
                || !ParametersMatch(test, ["LearnStack.Infrastructure.Jobs.BackgroundJobCommitProbe"]))
                offenders.Add("The retry observation predicate requires its one private static finite signature");
            else roots.Add(test);
        }
        else offenders.Add("Missing actual control counter memory owner");
        if (types.TryGetValue((InfrastructureAssembly, RcControl), out var recurrence))
        {
            var id = RequireBoundaryMethod(offenders, recurrence, "ObserveProgress", "System.Void", MethodAttributes.Private,
                "System.Boolean", "System.Object");
            roots.AddRange(recurrence.Methods.Where(method => MethodId(method) == id));
            var caller = JobOwner(offenders, types, InfrastructureAssembly, RcControl, "ObserveSqlAsync");
            CheckSet(offenders, "exact recurrence progress observation caller", census.MethodInstructions("ObserveProgress")
                .Where(site => TargetsDeclaredType(site.Target, recurrence))
                .Select(site => site.OwnerId + ":" + MethodId(site.Target) + ":" + site.Instruction.OpCode.Name),
                [caller + ":" + id + ":call"]);
        }
        else offenders.Add("Missing actual recurrence progress memory owner");
        if (types.TryGetValue((InfrastructureAssembly, JobBridge), out var bridge)
            && types.TryGetValue((InfrastructureAssembly, JobBridgeClaim), out var bridgeClaim))
        {
            var snapshot = RequireBoundaryMethod(offenders, bridge, "get_Snapshot", "LearnStack.Infrastructure.Jobs.BackgroundJobBridgeSnapshot", MethodAttributes.Public);
            var observe = JobOwner(offenders, types, InfrastructureAssembly, JobBridge, "ObserveAsync");
            foreach (var field in new[] { "_priorHintRepairs", "_expiredTokenRepairs" })
            {
                CheckBridgeCapturedField(offenders, census, bridge, field, "System.Int64",
                    [snapshot + ":ldfld", observe + ":ldfld", observe + ":stfld"]);
                if (bridge.Fields.SingleOrDefault(candidate => candidate.Name == field) is not { IsInitOnly: false })
                    offenders.Add("A bridge repair observation retains its mutable scalar owner: " + field);
            }
            var claimConstructor = RequireBoundaryMethod(offenders, bridgeClaim, ".ctor", "System.Void", MethodAttributes.Assembly,
                "Npgsql.NpgsqlDataReader", "LearnStack.Infrastructure.Jobs.BackgroundJobDefinitionRegistry");
            var kind = RequireBoundaryMethod(offenders, bridgeClaim, "get_Kind", "LearnStack.Infrastructure.Jobs.BackgroundJobBridgeClaimKind", MethodAttributes.Assembly);
            CheckBridgeCapturedField(offenders, census, bridgeClaim, "<Kind>k__BackingField", "LearnStack.Infrastructure.Jobs.BackgroundJobBridgeClaimKind",
                [claimConstructor + ":stfld", kind + ":ldfld"]);
            if (bridgeClaim.Fields.SingleOrDefault(field => field.Name == "<Kind>k__BackingField") is not { IsInitOnly: true })
                offenders.Add("The prior-state bridge classification remains immutable claim metadata");
            CheckSet(offenders, "exact bridge repair classification consumer", census.MethodInstructions("get_Kind")
                .Where(site => TargetsDeclaredType(site.Target, bridgeClaim))
                .Select(site => site.OwnerId + ":" + MethodId(site.Target) + ":" + site.Instruction.OpCode.Name),
                [claimConstructor + ":" + kind + ":call", observe + ":" + kind + ":callvirt"]);
        }
        else offenders.Add("Missing actual bridge repair observation owners");
        roots.AddRange(LeaseObservationRoots(census, offenders));
        roots.AddRange(LateObservationRoots(census, offenders));
        return roots;
    }

    private static bool LoadsArgument(Instruction instruction, int index) => instruction.OpCode.Code switch
    {
        Code.Ldarg_0 => index == 0,
        Code.Ldarg_1 => index == 1,
        Code.Ldarg_2 => index == 2,
        Code.Ldarg_3 => index == 3,
        Code.Ldarg or Code.Ldarg_S => instruction.Operand is ParameterDefinition parameter && parameter.Index + 1 == index,
        _ => false,
    };

    private static List<string> JobMetricRegistrationOffenders(IEnumerable<ServiceDescriptor> services)
    {
        var descriptors = services.Where(descriptor => descriptor.ServiceType == typeof(BackgroundJobObservation)).ToArray();
        return descriptors is [{ IsKeyedService: false, Lifetime: ServiceLifetime.Singleton, ImplementationType: var type }]
            && type == typeof(BackgroundJobObservation) ? [] : ["Job observation requires exactly one concrete unkeyed singleton registration"];
    }

    private static bool MetricPathAvoids(MethodDefinition constructor, IEnumerable<Instruction> blocked,
        Func<Instruction, bool> target)
    {
        var excluded = blocked.ToHashSet();
        var pending = new Queue<Instruction>();
        var visited = new HashSet<Instruction>();
        pending.Enqueue(constructor.Body.Instructions[0]);
        while (pending.TryDequeue(out var instruction))
        {
            if (excluded.Contains(instruction) || !visited.Add(instruction)) continue;
            if (target(instruction)) return true;
            if (instruction.OpCode.FlowControl is FlowControl.Return or FlowControl.Throw) continue;
            if (instruction.Operand is Instruction branch) pending.Enqueue(branch);
            if (instruction.Operand is Instruction[] branches)
                foreach (var branchTarget in branches) pending.Enqueue(branchTarget);
            if (instruction.OpCode.FlowControl != FlowControl.Branch && instruction.Next is { } next) pending.Enqueue(next);
            foreach (var handler in constructor.Body.ExceptionHandlers.Where(handler => handler.HandlerType == ExceptionHandlerType.Catch
                         && constructor.Body.Instructions.IndexOf(instruction) >= constructor.Body.Instructions.IndexOf(handler.TryStart)
                         && (handler.TryEnd is null || constructor.Body.Instructions.IndexOf(instruction) < constructor.Body.Instructions.IndexOf(handler.TryEnd))))
                pending.Enqueue(handler.HandlerStart);
        }
        return false;
    }

    public sealed partial class JobMetricMutationTests
    {
        [Fact]
        public void Background_job_metric_callbacks_follow_the_actual_transitive_memory_graph_and_refuse_persistence_helpers()
        {
            using var census = ProductionCensus.Read();
            var baselineOffenders = EvaluateDetector(census, RuntimeLifetimeOffenders);
            baselineOffenders.Should().BeEmpty("complete baseline offenders: {0}", string.Join("\n", baselineOffenders));
            var ignored = new SortedSet<string>(StringComparer.Ordinal);
            var roots = JobMetricRoots(census, ignored);
            ignored.Should().BeEmpty();
            roots.Should().HaveCount(18, "five actual collectors and thirteen fixed-memory control/execution/recurrence recorders or predicates are independently reached");
            foreach (var recorder in roots.Where(method => method.Name == "ObserveLeaseLoss"))
            {
                var asynchronous = new CustomAttribute(recorder.Module.ImportReference(
                    typeof(System.Runtime.CompilerServices.AsyncStateMachineAttribute).GetConstructor([typeof(Type)])!));
                asynchronous.ConstructorArguments.Add(new(recorder.Module.ImportReference(typeof(Type)), recorder.DeclaringType));
                recorder.CustomAttributes.Add(asynchronous);
                try
                {
                    EvaluateDetector(census, RuntimeLifetimeOffenders).Should().Contain(value =>
                        value.Contains("two concrete synchronous instance helpers", StringComparison.Ordinal),
                        "an async-void recorder cannot retain its synchronous fixed-memory claim");
                }
                finally { recorder.CustomAttributes.Remove(asynchronous); }
                EvaluateDetector(census, RuntimeLifetimeOffenders).Should().BeEmpty();
            }
            foreach (var recorder in roots.Where(method => method.Name is "ObserveLateSettlement" or "HasSettledStage" or "CaptureLateSettlements"))
            {
                var asynchronous = new CustomAttribute(recorder.Module.ImportReference(
                    typeof(System.Runtime.CompilerServices.AsyncStateMachineAttribute).GetConstructor([typeof(Type)])!));
                asynchronous.ConstructorArguments.Add(new(recorder.Module.ImportReference(typeof(Type)), recorder.DeclaringType));
                recorder.CustomAttributes.Add(asynchronous);
                try
                {
                    EvaluateDetector(census, RuntimeLifetimeOffenders).Should().Contain(value =>
                        value.Contains("concrete synchronous helper", StringComparison.Ordinal),
                        "every late classifier/forwarder/recorder remains synchronous memory observation");
                }
                finally { recorder.CustomAttributes.Remove(asynchronous); }
                EvaluateDetector(census, RuntimeLifetimeOffenders).Should().BeEmpty();
            }
            var root = roots.Single(method => method.Name == "CollectCounts");
            var application = census.Modules.Single(module => module.Assembly.Name.Name == DeliveryApplication);
            var helper = AddProbe(application, "MetricMemoryHelper");
            var memory = new MethodDefinition("ReadMemory", MethodAttributes.Public | MethodAttributes.Static, application.TypeSystem.Void);
            memory.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            helper.Methods.Add(memory);
            var sql = AddCall(helper, "ReadSql", application.ImportReference(typeof(Npgsql.NpgsqlCommand).GetConstructor(Type.EmptyTypes)!), OpCodes.Newobj);
            sql.Body.Instructions.Insert(1, Instruction.Create(OpCodes.Pop));
            var call = Instruction.Create(OpCodes.Call, root.Module.ImportReference(memory));
            root.Body.Instructions.Insert(0, call);
            try
            {
                EvaluateDetector(census, RuntimeLifetimeOffenders).Should().BeEmpty(
                    "an unreached SQL sibling does not make a pure memory helper activate persistence");
                var indirect = Instruction.Create(OpCodes.Call, application.ImportReference(sql));
                memory.Body.Instructions.Insert(0, indirect);
                try
                {
                    EvaluateDetector(census, RuntimeLifetimeOffenders).Should().Contain(value =>
                        value.Contains("jobs metrics observation reaches persistence/provider activation", StringComparison.Ordinal),
                        "the actual callback reaches SQL only through the foreign helper's second method");
                }
                finally { memory.Body.Instructions.Remove(indirect); }
                EvaluateDetector(census, RuntimeLifetimeOffenders).Should().BeEmpty();
                foreach (var recorder in roots.Where(method => method.DeclaringType.FullName != JobObservation))
                {
                    var recorderCall = Instruction.Create(OpCodes.Call, recorder.Module.ImportReference(sql));
                    recorder.Body.Instructions.Insert(0, recorderCall);
                    try
                    {
                        EvaluateDetector(census, RuntimeLifetimeOffenders).Should().Contain(value =>
                            value.Contains("jobs metrics observation reaches persistence/provider activation", StringComparison.Ordinal),
                            "each permitted counter helper must reject transitive SQL independently: {0}", recorder.Name);
                    }
                    finally { recorder.Body.Instructions.Remove(recorderCall); }
                    EvaluateDetector(census, RuntimeLifetimeOffenders).Should().BeEmpty();
                }
            }
            finally { root.Body.Instructions.Remove(call); application.Types.Remove(helper); }
            EvaluateDetector(census, RuntimeLifetimeOffenders).Should().BeEmpty();
            AssertMutation(census, "an indirect discarded Task.Delay in a first-loss recorder", () =>
            {
                var recorder = roots.Single(method => method.DeclaringType.FullName == OrchestrationExecution && method.Name == "ObserveLeaseLoss");
                var delayOwner = AddProbe(application, "MetricDelayHelper");
                var delay = AddCall(delayOwner, "Delay", application.ImportReference(typeof(Task).GetMethod("Delay", [typeof(int)])!), OpCodes.Call);
                delay.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Ldc_I4_1));
                delay.Body.Instructions.Insert(2, Instruction.Create(OpCodes.Pop));
                var invokeDelay = Instruction.Create(OpCodes.Call, recorder.Module.ImportReference(delay));
                recorder.Body.Instructions.Insert(0, invokeDelay);
                return () => { recorder.Body.Instructions.Remove(invokeDelay); application.Types.Remove(delayOwner); };
            }, RuntimeLifetimeOffenders);
            AssertMutation(census, "an extra persistent late-settlement recorder caller", () =>
            {
                var owner = census.Types[(InfrastructureAssembly, OrchestrationControl)];
                var extra = new MethodDefinition("InventLateSettlement", MethodAttributes.Private, owner.Module.TypeSystem.Void);
                extra.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                extra.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
                extra.Body.Instructions.Add(Instruction.Create(OpCodes.Call, owner.Methods.Single(method => method.Name == "ObserveLateSettlement")));
                extra.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                owner.Methods.Add(extra);
                return () => owner.Methods.Remove(extra);
            }, RuntimeLifetimeOffenders);
            AssertMutation(census, "an extra writer that could manufacture framework cleanup", () =>
            {
                var owner = census.Types[(InfrastructureAssembly, OrchestrationBound)];
                var extra = new MethodDefinition("InventFrameworkCleanup", MethodAttributes.Private, owner.Module.TypeSystem.Void);
                extra.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                extra.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_1));
                extra.Body.Instructions.Add(Instruction.Create(OpCodes.Stfld, owner.Fields.Single(field => field.Name == "_cleanupConfirmed")));
                extra.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                owner.Methods.Add(extra);
                return () => owner.Methods.Remove(extra);
            }, RuntimeLifetimeOffenders);
            AssertMutation(census, "an extra elapsed-stage writer that could turn stop into clock provenance", () =>
            {
                var owner = census.Types[(InfrastructureAssembly, OrchestrationInvocation)];
                var extra = new MethodDefinition("InventElapsedStage", MethodAttributes.Private, owner.Module.TypeSystem.Void);
                var marker = owner.Fields.Single(field => field.Name == "_elapsedStage");
                extra.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                extra.Body.Instructions.Add(Instruction.Create(OpCodes.Ldflda, marker));
                extra.Body.Instructions.Add(Instruction.Create(OpCodes.Initobj, marker.FieldType));
                extra.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                owner.Methods.Add(extra);
                return () => owner.Methods.Remove(extra);
            }, RuntimeLifetimeOffenders);
            AssertMutation(census, "an unlisted recurrence progress caller outside the actual awaited SQL observation", () =>
            {
                var owner = census.Types[(InfrastructureAssembly, RcControl)];
                var extra = new MethodDefinition("InventRecurrenceProgress", MethodAttributes.Private, owner.Module.TypeSystem.Void);
                extra.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                extra.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_1));
                extra.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
                extra.Body.Instructions.Add(Instruction.Create(OpCodes.Call, owner.Methods.Single(method => method.Name == "ObserveProgress")));
                extra.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                owner.Methods.Add(extra);
                return () => owner.Methods.Remove(extra);
            }, RuntimeLifetimeOffenders);

            var callback = AddProbe(application, "CustomMetricCallback");
            callback.BaseType = application.ImportReference(typeof(MulticastDelegate));
            callback.IsSealed = true;
            var invoke = new MethodDefinition("Invoke", MethodAttributes.Public | MethodAttributes.Virtual,
                application.TypeSystem.Void)
            { ImplAttributes = MethodImplAttributes.Runtime | MethodImplAttributes.Managed };
            callback.Methods.Add(invoke);
            var delegateConstructor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.HideBySig
                | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, application.TypeSystem.Void)
            { ImplAttributes = MethodImplAttributes.Runtime | MethodImplAttributes.Managed };
            delegateConstructor.Parameters.Add(new(application.TypeSystem.Object));
            delegateConstructor.Parameters.Add(new(application.TypeSystem.IntPtr));
            callback.Methods.Add(delegateConstructor);
            var field = new FieldDefinition("Callback", FieldAttributes.Public | FieldAttributes.Static, callback);
            helper = AddProbe(application, "MetricDelegateHolder");
            helper.Fields.Add(field);
            var load = Instruction.Create(OpCodes.Ldsfld, root.Module.ImportReference(field));
            var dispatch = Instruction.Create(OpCodes.Callvirt, root.Module.ImportReference(invoke));
            root.Body.Instructions.Insert(0, dispatch);
            root.Body.Instructions.Insert(0, load);
            try
            {
                EvaluateDetector(census, RuntimeLifetimeOffenders).Should().Contain(value =>
                    value.Contains("jobs metrics observation invokes an arbitrary delegate", StringComparison.Ordinal),
                    "a runtime-implemented custom Invoke has no body whose absence can hide an unknown callback");
            }
            finally
            {
                root.Body.Instructions.Remove(load);
                root.Body.Instructions.Remove(dispatch);
                application.Types.Remove(helper);
                application.Types.Remove(callback);
            }
            EvaluateDetector(census, RuntimeLifetimeOffenders).Should().BeEmpty();
            AssertMutation(census, "an external Predicate Invoke that has no in-census delegate declaration", () =>
            {
                var instructions = new[]
                {
                    Instruction.Create(OpCodes.Ldnull), Instruction.Create(OpCodes.Ldnull),
                    Instruction.Create(OpCodes.Callvirt, root.Module.ImportReference(typeof(Predicate<object>).GetMethod("Invoke")!)),
                    Instruction.Create(OpCodes.Pop),
                };
                for (var index = 0; index < instructions.Length; index++) root.Body.Instructions.Insert(index, instructions[index]);
                return () => { foreach (var instruction in instructions) root.Body.Instructions.Remove(instruction); };
            }, RuntimeLifetimeOffenders);

            helper = AddProbe(application, "MetricStaticHolder");
            var marker = new FieldDefinition("Marker", FieldAttributes.Public | FieldAttributes.Static, application.TypeSystem.Int64);
            helper.Fields.Add(marker);
            var initializer = new MethodDefinition(".cctor", MethodAttributes.Private | MethodAttributes.Static
                | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, application.TypeSystem.Void);
            initializer.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            helper.Methods.Add(initializer);
            try
            {
                foreach (var code in new[] { OpCodes.Ldsfld, OpCodes.Ldsflda, OpCodes.Stsfld })
                {
                    var access = Instruction.Create(code, root.Module.ImportReference(marker));
                    var value = code == OpCodes.Stsfld ? Instruction.Create(OpCodes.Ldc_I8, 0L) : Instruction.Create(OpCodes.Pop);
                    root.Body.Instructions.Insert(0, code == OpCodes.Stsfld ? access : value);
                    root.Body.Instructions.Insert(0, code == OpCodes.Stsfld ? value : access);
                    try
                    {
                        EvaluateDetector(census, RuntimeLifetimeOffenders).Should().BeEmpty("a reached memory-only initializer is safe");
                        var create = Instruction.Create(OpCodes.Newobj,
                            application.ImportReference(typeof(Npgsql.NpgsqlCommand).GetConstructor(Type.EmptyTypes)!));
                        var pop = Instruction.Create(OpCodes.Pop);
                        initializer.Body.Instructions.Insert(0, pop);
                        initializer.Body.Instructions.Insert(0, create);
                        try
                        {
                            EvaluateDetector(census, RuntimeLifetimeOffenders).Should().Contain(value =>
                                value.Contains("jobs metrics observation reaches persistence/provider activation", StringComparison.Ordinal),
                                "the static field access implicitly executes its owner's initializer");
                        }
                        finally { initializer.Body.Instructions.Remove(create); initializer.Body.Instructions.Remove(pop); }
                        EvaluateDetector(census, RuntimeLifetimeOffenders).Should().BeEmpty();
                    }
                    finally { root.Body.Instructions.Remove(access); root.Body.Instructions.Remove(value); }
                }
            }
            finally { application.Types.Remove(helper); }
            EvaluateDetector(census, RuntimeLifetimeOffenders).Should().BeEmpty();
        }
    }

    [Fact]
    public void Background_job_metric_registration_requires_the_actual_singleton_descriptor_and_rejects_substitutions()
    {
        IServiceCollection services = new ServiceCollection();
        services.AddLearnStackBackgroundJobContracts();
        services.AddLearnStackBackgroundJobContracts();
        JobMetricRegistrationOffenders(services).Should().BeEmpty();
        var actual = services.Single(descriptor => descriptor.ServiceType == typeof(BackgroundJobObservation));
        using var instance = new BackgroundJobObservation(BackgroundJobDefinitionRegistry.CreateEmpty(), TimeProvider.System);
        foreach (var replacement in new[]
                 {
                     ServiceDescriptor.Scoped<BackgroundJobObservation, BackgroundJobObservation>(),
                     ServiceDescriptor.Singleton<BackgroundJobObservation>(_ => throw new InvalidOperationException("Never activate the dirty factory.")),
                     ServiceDescriptor.Singleton(instance),
                 })
        {
            services.Remove(actual);
            services.Add(replacement);
            try { JobMetricRegistrationOffenders(services).Should().NotBeEmpty(); }
            finally { services.Remove(replacement); services.Add(actual); }
            JobMetricRegistrationOffenders(services).Should().BeEmpty();
        }
        services.Add(ServiceDescriptor.Scoped<BackgroundJobObservation, BackgroundJobObservation>());
        JobMetricRegistrationOffenders(services).Should().NotBeEmpty("a last scoped override cannot hide behind the original TryAddSingleton");
        services.RemoveAt(services.Count - 1);
        services.Remove(actual);
        JobMetricRegistrationOffenders(services).Should().NotBeEmpty();
        services.Add(actual);
        JobMetricRegistrationOffenders(services).Should().BeEmpty();
    }

    public sealed partial class JobMetricMutationTests
    {
        [Fact]
        public void Background_job_metric_census_rejects_bait_callbacks_hidden_registration_and_skipped_binding()
        {
            using var census = ProductionCensus.Read();
            var baselineOffenders = EvaluateDetector(census, RuntimeLifetimeOffenders);
            baselineOffenders.Should().BeEmpty("complete baseline offenders: {0}", string.Join("\n", baselineOffenders));
            var observation = census.Types[(InfrastructureAssembly, JobObservation)];
            var ctor = observation.Methods.Single(method => method.IsConstructor && !method.IsStatic);
            var gauge = ctor.Body.Instructions.First(instruction => instruction.Operand is MethodReference method
                && method.DeclaringType.FullName == "System.Diagnostics.Metrics.Meter" && method.Name == "CreateObservableGauge");
            var original = ctor.Body.Instructions.Skip(ctor.Body.Instructions.IndexOf(gauge) - 8).Take(9).ToArray();
            AssertMutation(census, "an extra UpDownCounter outside the approved observable inventory", () =>
            {
                var upDown = typeof(System.Diagnostics.Metrics.Meter).GetMethods().Single(method => method.Name == "CreateObservableUpDownCounter"
                    && method.IsGenericMethodDefinition && method.GetParameters().Length == 4
                    && method.GetParameters()[1].ParameterType.GetGenericArguments()[0] is { IsGenericType: true } result
                    && result.GetGenericTypeDefinition() == typeof(IEnumerable<>)).MakeGenericMethod(typeof(long));
                var extra = original.Select(instruction =>
                {
                    var clone = Instruction.Create(OpCodes.Nop);
                    clone.OpCode = instruction.OpCode;
                    clone.Operand = instruction.Operand;
                    return clone;
                }).Append(Instruction.Create(OpCodes.Pop)).ToArray();
                extra[2].Operand = "learnstack_job_unapproved";
                extra[8].Operand = ctor.Module.ImportReference(upDown);
                foreach (var instruction in extra) ctor.Body.Instructions.Insert(ctor.Body.Instructions.Count - 1, instruction);
                return () => { foreach (var instruction in extra) ctor.Body.Instructions.Remove(instruction); };
            }, RuntimeLifetimeOffenders);
            var actualName = original[2];
            AssertMutation(census, "an unused correct instrument name followed by the actual foreign name", () =>
            {
                var bait = Instruction.Create(OpCodes.Ldstr, (string)actualName.Operand);
                var pop = Instruction.Create(OpCodes.Pop);
                ctor.Body.Instructions.Insert(ctor.Body.Instructions.IndexOf(actualName), bait);
                ctor.Body.Instructions.Insert(ctor.Body.Instructions.IndexOf(actualName), pop);
                var old = actualName.Operand;
                actualName.Operand = "learnstack_job_unapproved";
                return () => { actualName.Operand = old; ctor.Body.Instructions.Remove(bait); ctor.Body.Instructions.Remove(pop); };
            }, RuntimeLifetimeOffenders);
            AssertMutation(census, "an unused correct delegate followed by an actual factory-supplied callback", () =>
            {
                var factory = new MethodDefinition("CreateCallback", MethodAttributes.Private | MethodAttributes.Static,
                    ((MethodReference)original[5].Operand).DeclaringType);
                factory.Parameters.Add(new(observation));
                factory.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                factory.Body.Instructions.Add(Instruction.Create(OpCodes.Ldftn, (MethodReference)original[4].Operand));
                factory.Body.Instructions.Add(Instruction.Create(OpCodes.Newobj, (MethodReference)original[5].Operand));
                factory.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                observation.Methods.Add(factory);
                var bait = new[]
                {
                    Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldftn, (MethodReference)original[4].Operand),
                    Instruction.Create(OpCodes.Newobj, (MethodReference)original[5].Operand), Instruction.Create(OpCodes.Pop),
                };
                foreach (var instruction in bait) ctor.Body.Instructions.Insert(ctor.Body.Instructions.IndexOf(original[0]), instruction);
                var pointer = original[4].Operand;
                var delegateConstructor = original[5].Operand;
                original[4].OpCode = OpCodes.Call;
                original[4].Operand = factory;
                original[5].OpCode = OpCodes.Nop;
                original[5].Operand = null;
                return () =>
                {
                    original[4].OpCode = OpCodes.Ldftn; original[4].Operand = pointer;
                    original[5].OpCode = OpCodes.Newobj; original[5].Operand = delegateConstructor;
                    foreach (var instruction in bait) ctor.Body.Instructions.Remove(instruction);
                    observation.Methods.Remove(factory);
                };
            }, RuntimeLifetimeOffenders);
            AssertMutation(census, "an observer helper that creates its own named Meter without reading the owned field", () =>
            {
                var registrar = new MethodDefinition("RegisterOwnMeter", MethodAttributes.Private | MethodAttributes.Static, ctor.Module.TypeSystem.Void);
                registrar.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "learnstack.jobs"));
                registrar.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
                registrar.Body.Instructions.Add(Instruction.Create(OpCodes.Newobj, ctor.Module.ImportReference(
                    typeof(System.Diagnostics.Metrics.Meter).GetConstructor([typeof(string), typeof(string)])!)));
                registrar.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
                registrar.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                observation.Methods.Add(registrar);
                var call = Instruction.Create(OpCodes.Call, registrar);
                ctor.Body.Instructions.Insert(ctor.Body.Instructions.Count - 1, call);
                return () => { ctor.Body.Instructions.Remove(call); observation.Methods.Remove(registrar); };
            }, RuntimeLifetimeOffenders);
            var application = census.Modules.Single(module => module.Assembly.Name.Name == DeliveryApplication);
            var holder = AddProbe(application, "MetricRegistrationStaticHolder");
            var marker = new FieldDefinition("Marker", FieldAttributes.Public | FieldAttributes.Static, application.TypeSystem.Int64);
            holder.Fields.Add(marker);
            var initializer = new MethodDefinition(".cctor", MethodAttributes.Private | MethodAttributes.Static
                | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, application.TypeSystem.Void);
            initializer.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            holder.Methods.Add(initializer);
            var markerRead = Instruction.Create(OpCodes.Ldsfld, ctor.Module.ImportReference(marker));
            var markerPop = Instruction.Create(OpCodes.Pop);
            ctor.Body.Instructions.Insert(0, markerPop);
            ctor.Body.Instructions.Insert(0, markerRead);
            try
            {
                EvaluateDetector(census, RuntimeLifetimeOffenders).Should().BeEmpty();
                AssertMutation(census, "an observer constructor whose static marker initializes a second Meter", () =>
                {
                    var instructions = new[]
                    {
                        Instruction.Create(OpCodes.Ldstr, "learnstack.jobs"), Instruction.Create(OpCodes.Ldnull),
                        Instruction.Create(OpCodes.Newobj, application.ImportReference(
                            typeof(System.Diagnostics.Metrics.Meter).GetConstructor([typeof(string), typeof(string)])!)),
                        Instruction.Create(OpCodes.Pop),
                    };
                    for (var index = 0; index < instructions.Length; index++) initializer.Body.Instructions.Insert(index, instructions[index]);
                    return () => { foreach (var instruction in instructions) initializer.Body.Instructions.Remove(instruction); };
                }, RuntimeLifetimeOffenders);
                EvaluateDetector(census, RuntimeLifetimeOffenders).Should().BeEmpty();
            }
            finally { ctor.Body.Instructions.Remove(markerRead); ctor.Body.Instructions.Remove(markerPop); application.Types.Remove(holder); }
            var actualInitializer = observation.Methods.Single(method => method.IsConstructor && method.IsStatic);
            AssertMutation(census, "a hidden Meter registered in the actual observer static constructor", () =>
            {
                var instructions = new[]
                {
                    Instruction.Create(OpCodes.Ldstr, "learnstack.jobs"), Instruction.Create(OpCodes.Ldnull),
                    Instruction.Create(OpCodes.Newobj, actualInitializer.Module.ImportReference(
                        typeof(System.Diagnostics.Metrics.Meter).GetConstructor([typeof(string), typeof(string)])!)),
                    Instruction.Create(OpCodes.Pop),
                };
                for (var index = 0; index < instructions.Length; index++) actualInitializer.Body.Instructions.Insert(index, instructions[index]);
                return () => { foreach (var instruction in instructions) actualInitializer.Body.Instructions.Remove(instruction); };
            }, RuntimeLifetimeOffenders);
            var hidden = new MethodDefinition("RegisterHidden", MethodAttributes.Private, ctor.Module.TypeSystem.Void);
            foreach (var instruction in original)
            {
                var clone = Instruction.Create(OpCodes.Nop);
                clone.OpCode = instruction.OpCode;
                clone.Operand = instruction.Operand;
                hidden.Body.Instructions.Add(clone);
            }
            hidden.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
            hidden.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            AssertMutation(census, "a second observable registration moved behind an owner helper", () =>
            {
                observation.Methods.Add(hidden);
                var receiver = Instruction.Create(OpCodes.Ldarg_0);
                var call = Instruction.Create(OpCodes.Call, hidden);
                ctor.Body.Instructions.Insert(ctor.Body.Instructions.Count - 1, receiver);
                ctor.Body.Instructions.Insert(ctor.Body.Instructions.Count - 1, call);
                return () => { ctor.Body.Instructions.Remove(receiver); ctor.Body.Instructions.Remove(call); observation.Methods.Remove(hidden); };
            }, RuntimeLifetimeOffenders);
            var executor = census.Types[(InfrastructureAssembly, JobExecutorType)];
            var execution = census.Types[(InfrastructureAssembly, OrchestrationExecution)];
            var leaseRecorder = execution.Methods.Single(method => method.Name == "ObserveLeaseLoss");
            AssertMutation(census, "a balanced removal of the first-loss CompareExchange gate", () =>
            {
                var compare = leaseRecorder.Body.Instructions.Single(instruction => instruction.Operand is MethodReference target
                    && target.Name == "CompareExchange" && target.DeclaringType.FullName == "System.Threading.Interlocked");
                var original = compare.Operand;
                compare.OpCode = OpCodes.Pop;
                compare.Operand = null;
                var replacements = new[] { Instruction.Create(OpCodes.Pop), Instruction.Create(OpCodes.Pop), Instruction.Create(OpCodes.Ldc_I4_0) };
                for (var index = 0; index < replacements.Length; index++)
                    leaseRecorder.Body.Instructions.Insert(leaseRecorder.Body.Instructions.IndexOf(compare) + 1 + index, replacements[index]);
                return () =>
                {
                    compare.OpCode = OpCodes.Call; compare.Operand = original;
                    foreach (var instruction in replacements) leaseRecorder.Body.Instructions.Remove(instruction);
                };
            }, RuntimeLifetimeOffenders);
            AssertMutation(census, "a constructor-unrelated acknowledged lease eligibility writer", () =>
            {
                var extra = new MethodDefinition("InventLeaseEligibility", MethodAttributes.Private, execution.Module.TypeSystem.Void);
                extra.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                extra.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_1));
                extra.Body.Instructions.Add(Instruction.Create(OpCodes.Stfld, execution.Fields.Single(field => field.Name == "_leaseObservationEligible")));
                extra.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                execution.Methods.Add(extra);
                return () => execution.Methods.Remove(extra);
            }, RuntimeLifetimeOffenders);
            AssertMutation(census, "an unlisted direct persistent lease-loss recorder caller", () =>
            {
                var parent = census.Types[(InfrastructureAssembly, OrchestrationControl)];
                var extra = new MethodDefinition("InventPersistentLeaseLoss", MethodAttributes.Private, parent.Module.TypeSystem.Void);
                extra.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                extra.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_2));
                extra.Body.Instructions.Add(Instruction.Create(OpCodes.Call, parent.Methods.Single(method => method.Name == "ObserveLeaseLoss")));
                extra.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                parent.Methods.Add(extra);
                return () => parent.Methods.Remove(extra);
            }, RuntimeLifetimeOffenders);
            var bridge = census.Types[(InfrastructureAssembly, JobBridge)];
            foreach (var fieldName in new[] { "_priorHintRepairs", "_expiredTokenRepairs" })
                AssertMutation(census, "an unlisted bridge repair field reader " + fieldName,
                    () => AddBoundaryFieldGetter(bridge, "ReadRepairOutsideSnapshot", fieldName), RuntimeLifetimeOffenders);
            var bridgeClaim = census.Types[(InfrastructureAssembly, JobBridgeClaim)];
            AssertMutation(census, "a mutable original bridge claim classification", () =>
            {
                var classification = bridgeClaim.Fields.Single(field => field.Name == "<Kind>k__BackingField");
                classification.IsInitOnly = false;
                return () => classification.IsInitOnly = true;
            }, RuntimeLifetimeOffenders);
            var field = executor.Fields.Single(candidate => candidate.Name == "<Observation>k__BackingField");
            AssertMutation(census, "removing required observer readonly metadata", () =>
            {
                field.IsInitOnly = false;
                return () => field.IsInitOnly = true;
            }, RuntimeLifetimeOffenders);
            var runtimeCtor = census.Types[(InfrastructureAssembly, JobRuntime)].Methods.Single(method => MethodId(method) == RuntimeConstructorId);
            var bind = runtimeCtor.Body.Instructions.Single(instruction => instruction.Operand is MethodReference method
                && method.DeclaringType.FullName == JobObservation && method.Name == "Bind");
            AssertMutation(census, "a captured Bind method group without invoking the required binding", () =>
            {
                var arguments = runtimeCtor.Body.Instructions.Skip(runtimeCtor.Body.Instructions.IndexOf(bind) - 3).Take(3).ToArray();
                var oldArguments = arguments.Select(instruction => (instruction.OpCode, instruction.Operand)).ToArray();
                foreach (var argument in arguments) { argument.OpCode = OpCodes.Nop; argument.Operand = null; }
                var target = bind.Operand;
                bind.OpCode = OpCodes.Ldftn;
                var create = Instruction.Create(OpCodes.Newobj, runtimeCtor.Module.ImportReference(
                    typeof(Action<BackgroundJobRuntime, BackgroundJobRecurrenceRegistry, TimeProvider>).GetConstructor([typeof(object), typeof(IntPtr)])!));
                var pop = Instruction.Create(OpCodes.Pop);
                runtimeCtor.Body.Instructions.Insert(runtimeCtor.Body.Instructions.IndexOf(bind) + 1, create);
                runtimeCtor.Body.Instructions.Insert(runtimeCtor.Body.Instructions.IndexOf(create) + 1, pop);
                return () =>
                {
                    for (var index = 0; index < arguments.Length; index++)
                    { arguments[index].OpCode = oldArguments[index].OpCode; arguments[index].Operand = oldArguments[index].Operand; }
                    bind.OpCode = OpCodes.Callvirt; bind.Operand = target;
                    runtimeCtor.Body.Instructions.Remove(create); runtimeCtor.Body.Instructions.Remove(pop);
                };
            }, RuntimeLifetimeOffenders);
            AssertMutation(census, "an initial-state false branch that skips binding but still pins both sources", () =>
            {
                var entry = runtimeCtor.Body.Instructions[runtimeCtor.Body.Instructions.IndexOf(bind) - 6];
                var old = (entry.OpCode, entry.Operand);
                // Both suppression-flow paths branch to this original instruction.
                // Inserting a prefix before it would leave the dirty branch unreachable.
                entry.OpCode = OpCodes.Ldc_I4_0;
                entry.Operand = null;
                var skip = Instruction.Create(OpCodes.Brfalse, bind.Next!);
                var receiver = Instruction.Create(OpCodes.Ldarg_0);
                runtimeCtor.Body.Instructions.Insert(runtimeCtor.Body.Instructions.IndexOf(entry) + 1, skip);
                runtimeCtor.Body.Instructions.Insert(runtimeCtor.Body.Instructions.IndexOf(skip) + 1, receiver);
                return () =>
                {
                    entry.OpCode = old.OpCode; entry.Operand = old.Operand;
                    runtimeCtor.Body.Instructions.Remove(skip); runtimeCtor.Body.Instructions.Remove(receiver);
                };
            }, RuntimeLifetimeOffenders);
            var histogram = ctor.Body.Instructions.Single(instruction => instruction.Operand is MethodReference method
                && method.DeclaringType.FullName == "System.Diagnostics.Metrics.Meter" && method.Name == "CreateHistogram");
            AssertMutation(census, "a duration histogram whose emitted unit silently changes to milliseconds", () =>
            {
                var unit = ctor.Body.Instructions[ctor.Body.Instructions.IndexOf(histogram) - 4];
                unit.OpCode.Should().Be(OpCodes.Ldstr);
                unit.Operand.Should().Be("s");
                unit.Operand = "ms";
                return () => unit.Operand = "s";
            }, RuntimeLifetimeOffenders);

            var executorBody = OwnedBodies(census).Single(item => item.Owner.DeclaringType == executor
                && item.Owner.Name == "ExecutePublishedAsync" && item.Body.Body.Instructions.Any(instruction =>
                    instruction.Operand is MethodReference method && method.DeclaringType.FullName == JobObservation && method.Name == "StartStage")).Body;
            var start = executorBody.Body.Instructions.Single(instruction => instruction.Operand is MethodReference method
                && method.DeclaringType.FullName == JobObservation && method.Name == "StartStage");
            AssertMutation(census, "an unused StartStage method group replacing the actual timing call", () =>
            {
                var local = new VariableDefinition(executorBody.Module.ImportReference(typeof(long?)));
                var initialized = executorBody.Body.InitLocals;
                executorBody.Body.InitLocals = true;
                executorBody.Body.Variables.Add(local);
                start.OpCode = OpCodes.Ldftn;
                var replacements = new[]
                {
                    Instruction.Create(OpCodes.Newobj, executorBody.Module.ImportReference(
                        typeof(Func<long?>).GetConstructor([typeof(object), typeof(IntPtr)])!)),
                    Instruction.Create(OpCodes.Pop), Instruction.Create(OpCodes.Ldloca, local),
                    Instruction.Create(OpCodes.Initobj, local.VariableType), Instruction.Create(OpCodes.Ldloc, local),
                };
                for (var index = 0; index < replacements.Length; index++)
                    executorBody.Body.Instructions.Insert(executorBody.Body.Instructions.IndexOf(start) + 1 + index, replacements[index]);
                return () =>
                {
                    start.OpCode = OpCodes.Callvirt;
                    foreach (var instruction in replacements) executorBody.Body.Instructions.Remove(instruction);
                    executorBody.Body.Variables.Remove(local);
                    executorBody.Body.InitLocals = initialized;
                };
            }, RuntimeLifetimeOffenders);

            foreach (var method in observation.Methods.Where(method => method.Name is "StartStage" or "RecordStage"))
            {
                EvaluateDetector(census, RuntimeLifetimeOffenders).Should().BeEmpty();
                var instructions = new[]
                {
                    Instruction.Create(OpCodes.Newobj, method.Module.ImportReference(typeof(Npgsql.NpgsqlCommand).GetConstructor(Type.EmptyTypes)!)),
                    Instruction.Create(OpCodes.Pop),
                };
                method.Body.Instructions.Insert(0, instructions[1]);
                method.Body.Instructions.Insert(0, instructions[0]);
                try
                {
                    EvaluateDetector(census, RuntimeLifetimeOffenders).Should().Contain(value =>
                        value.Contains("job stage metrics observation reaches persistence/provider activation", StringComparison.Ordinal),
                        "each actual synchronous stage root must be independently selected by the memory walk");
                }
                finally { foreach (var instruction in instructions) method.Body.Instructions.Remove(instruction); }
                EvaluateDetector(census, RuntimeLifetimeOffenders).Should().BeEmpty();
            }
            EvaluateDetector(census, RuntimeLifetimeOffenders).Should().BeEmpty();
        }
    }
}
