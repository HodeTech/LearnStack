using FluentAssertions;
using LearnStack.Api.Composition;
using Microsoft.Extensions.DependencyInjection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed partial class OutboxDispatchConventionTests
{
    private const string JobRuntime = "LearnStack.Infrastructure.Jobs.BackgroundJobRuntime";
    private const string JobRuntimeRegistration = "LearnStack.Infrastructure.Jobs.BackgroundJobRuntimeRegistration";
    private const string JobPoolSettings = "LearnStack.Infrastructure.Jobs.BackgroundJobPhysicalPoolSettings";
    private const string JobCapacity = "LearnStack.Infrastructure.Jobs.BackgroundJobPhysicalCapacity";
    private const string JobHostedService = "LearnStack.Api.Jobs.BackgroundJobHostedService";
    private const string JobHealth = "LearnStack.Api.Jobs.BackgroundJobHealthCheck";
    private const string MessagingLifetime = "LearnStack.Api.Messaging.MessagingPrivilegedLifetime";
    private const string CompletionSource = "System.Threading.Tasks.TaskCompletionSource";
    private static readonly string[] RuntimeConstructorParameters =
    [
        BoundaryGuardedSource, "LearnStack.Infrastructure.Jobs.BackgroundJobDefinitionRegistry",
        "LearnStack.Infrastructure.Jobs.BackgroundJobRecurrenceRegistry", JobExecutorType, JobRoutingStore,
        JobRuntimeGate, "LearnStack.Infrastructure.Jobs.BackgroundJobRuntimeOptions", "LearnStack.SharedKernel.Identifiers.IGuidFactory",
        "System.TimeProvider", JobPoolSettings, "LearnStack.Infrastructure.Jobs.HangfireBoundedLogProvider",
        "LearnStack.Infrastructure.Persistence.ApplicationDataSourceLifetime",
        "LearnStack.SharedKernel.Jobs.BackgroundJobLifecycleProjection",
    ];
    private static string RuntimeConstructorId => InfrastructureAssembly + ":System.Void " + JobRuntime
        + "::.ctor(" + string.Join(",", RuntimeConstructorParameters) + ")";
    private static string RuntimeFactoryId => InfrastructureAssembly + ":" + JobRuntime + " " + JobRuntimeRegistration
        + "::CreateRuntime(System.IServiceProvider)";
    private static string RuntimePreallocateId => InfrastructureAssembly + ":System.Void " + JobRuntime
        + "::PreallocateOwners(Hangfire.PostgreSql.PostgreSqlStorage)";
    private static IEnumerable<string> MessagingStoreConsumerIds(string store) =>
    [
        BoundaryApi + ":System.Void " + MessagingLifetime + "::.ctor(" + store + ")",
        BoundaryApi + ":System.Threading.Tasks.Task " + MessagingLifetime + "::ObserveExecutionAsync(System.Threading.Tasks.Task)",
    ];

    private static bool IsDeclaredOwner(string owner, TypeDefinition type) =>
        type.Methods.Any(method => MethodId(method) == owner);

    private static bool HasAttachedDeclaringType(MemberReference member) => member.DeclaringType?.Scope is not null;

    private static bool TargetsDeclaredType(MemberReference member, TypeDefinition type) =>
        HasAttachedDeclaringType(member) && type.Scope is not null && SameType(member.DeclaringType, type);

    private static void RecordDetachedTargets(SortedSet<string> offenders, IEnumerable<(string Owner, MethodReference Target)> sites)
    {
        // Cecil removal controls leave references to detached definitions. They
        // remain offenders; they must never become an exemption or crash the walk.
        foreach (var site in sites.Where(site => !HasAttachedDeclaringType(site.Target)))
            offenders.Add(site.Owner + ": detached production method target " + site.Target.Name);
    }

    // A hosted factory is admitted only by its exact named method and typed return.
    // Arbitrary factories, including another locator for the same worker, fail.
    private static string HostedAliasIdentity(ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationType is { } type)
            return type.FullName is BoundaryWorker or DeliveryWorker or JobHostedService
                ? "detached worker registration:" + type.FullName : type.FullName!;
        var method = descriptor.ImplementationFactory?.Method;
        if (method?.DeclaringType != typeof(OutboxCompositionExtensions) || !method.IsPrivate || !method.IsStatic
            || method.IsGenericMethod || method.GetParameters() is not [{ ParameterType: var parameter }]
            || parameter != typeof(IServiceProvider)) return "non-type registration";
        var expected = method.Name switch
        {
            "ResolveOutboxHostedService" => BoundaryWorker,
            "ResolveDeliveryHostedService" => DeliveryWorker,
            "ResolveJobHostedService" => JobHostedService,
            _ => null,
        };
        return method.ReturnType.FullName == expected ? expected! : "non-type registration";
    }

    private static SortedSet<string> RuntimeLifetimeOffenders(ProductionCensus census)
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        var types = census.Types;
        foreach (var subject in new[] { (InfrastructureAssembly, JobRuntime), (InfrastructureAssembly, JobRuntimeRegistration),
                     (InfrastructureAssembly, BoundaryGuardedSource), (InfrastructureAssembly, JobPoolSettings),
                     (InfrastructureAssembly, JobCapacity), (BoundaryApi, JobHostedService), (BoundaryApi, JobHealth),
                     (BoundaryApi, MessagingLifetime), (BoundaryApi, BoundaryWorker), (BoundaryApi, DeliveryWorker) })
            if (!types.ContainsKey(subject)) offenders.Add("Missing runtime lifetime subject: " + subject.Item2);
        if (offenders.Count != 0) return offenders;
        var runtime = types[(InfrastructureAssembly, JobRuntime)];
        var registration = types[(InfrastructureAssembly, JobRuntimeRegistration)];
        var source = types[(InfrastructureAssembly, BoundaryGuardedSource)];
        var hosted = types[(BoundaryApi, JobHostedService)];
        var health = types[(BoundaryApi, JobHealth)];
        var messaging = types[(BoundaryApi, MessagingLifetime)];
        var outbox = types[(BoundaryApi, BoundaryWorker)];
        var delivery = types[(BoundaryApi, DeliveryWorker)];
        if (!runtime.IsPublic || !runtime.IsSealed || !hosted.IsPublic || !hosted.IsSealed
            || !messaging.IsNotPublic || !messaging.IsSealed)
            offenders.Add("Runtime facade, hosted owner and fixed messaging lifetime must retain their exact sealed visibility");
        var constructor = RequireBoundaryMethod(offenders, runtime, ".ctor", "System.Void", MethodAttributes.Assembly, RuntimeConstructorParameters);
        var factory = RequireHangfireStaticMethod(offenders, registration, "CreateRuntime", JobRuntime, MethodAttributes.Private, "System.IServiceProvider");
        var registrationRoot = RequireHangfireStaticMethod(offenders, registration, "AddLearnStackBackgroundJobRuntime",
            "Microsoft.Extensions.DependencyInjection.IServiceCollection", MethodAttributes.Public,
            "Microsoft.Extensions.DependencyInjection.IServiceCollection", JobPoolSettings);
        var preallocate = RequireBoundaryMethod(offenders, runtime, "PreallocateOwners", "System.Void", MethodAttributes.Private,
            "Hangfire.PostgreSql.PostgreSqlStorage");
        var bind = RequireBoundaryMethod(offenders, runtime, "BindMessagingLifetime", "System.Void", MethodAttributes.Public, BoundaryTask, BoundaryTask);
        var sourceLifetime = RequireBoundaryMethod(offenders, runtime, "ObserveSourceLifetimeAsync", BoundaryTask, MethodAttributes.Private);
        var messagingObservation = RequireBoundaryMethod(offenders, runtime, "ObserveMessagingAsync", BoundaryTask, MethodAttributes.Private);
        var runtimeObservation = RequireBoundaryMethod(offenders, runtime, "ObserveRuntimeAsync", BoundaryTask, MethodAttributes.Private);
        var completion = RequireBoundaryMethod(offenders, runtime, "get_Completion", BoundaryTask, MethodAttributes.Public);
        var start = RequireBoundaryMethod(offenders, runtime, "StartAsync", BoundaryTask, MethodAttributes.Public, BoundaryCancellation);
        var stop = RequireBoundaryMethod(offenders, runtime, "StopAsync", BoundaryTask, MethodAttributes.Public, BoundaryCancellation);
        var requestStop = RequireBoundaryMethod(offenders, runtime, "RequestStop", "System.Void", MethodAttributes.Public);
        var run = RequireBoundaryMethod(offenders, runtime, "RunAsync", BoundaryTask, MethodAttributes.Private);
        var bridgeLoop = RequireBoundaryMethod(offenders, runtime, "RunBridgeAsync", BoundaryTask, MethodAttributes.Private);
        var joins = RequireBoundaryMethod(offenders, runtime, "JoinOwnersAsync", BoundaryTask, MethodAttributes.Private);
        var retainCleanup = RequireBoundaryMethod(offenders, runtime, "RetainCleanup", "System.Void", MethodAttributes.Private);
        var expire = RequireBoundaryMethod(offenders, runtime, "ExpireWait", "System.Void", MethodAttributes.Private);
        var hostedConstructor = RequireBoundaryMethod(offenders, hosted, ".ctor", "System.Void", MethodAttributes.Public,
            JobRuntime, BoundaryWorker, DeliveryWorker);
        var hostedStart = RequireBoundaryMethod(offenders, hosted, "StartAsync", BoundaryTask, MethodAttributes.Public, BoundaryCancellation);
        var hostedStop = RequireBoundaryMethod(offenders, hosted, "StopAsync", BoundaryTask, MethodAttributes.Public, BoundaryCancellation);
        var hostedDispose = RequireBoundaryMethod(offenders, hosted, "Dispose", "System.Void", MethodAttributes.Public);
        var bootstrapExecute = InfrastructureAssembly + ":System.Void " + JobRuntime + "/BootstrapInvocation::Execute()";
        var ownedBodies = OwnedBodies(census).ToArray();
        var sites = CallSites(census).ToArray();
        RecordDetachedTargets(offenders, sites);
        Exact(runtime, ".ctor", constructor, [factory + ":newobj"]);
        Exact(registration, "CreateRuntime", factory, [registrationRoot + ":ldftn"]);
        Exact(runtime, "BindMessagingLifetime", bind, [hostedConstructor + ":callvirt"]);
        Exact(runtime, "StartAsync", start, [hostedStart + ":callvirt"]);
        Exact(runtime, "StopAsync", stop, [hostedStop + ":callvirt"]);
        Exact(runtime, "RequestStop", requestStop,
            [hostedDispose + ":callvirt", start + ":call", stop + ":call", run + ":call", expire + ":call", retainCleanup + ":call"]);
        Exact(runtime, "ObserveSourceLifetimeAsync", sourceLifetime, [constructor + ":call", constructor + ":call"]);
        Exact(runtime, "ObserveMessagingAsync", messagingObservation, [bind + ":call", bind + ":call"]);
        Exact(runtime, "ObserveRuntimeAsync", runtimeObservation, [constructor + ":call", constructor + ":call"]);
        Exact(runtime, "RunAsync", run, [start + ":call", start + ":call"]);
        Exact(runtime, "PreallocateOwners", preallocate, [run + ":call"]);
        Exact(runtime, "JoinOwnersAsync", joins, [run + ":call"]);
        CheckReadonlyField(runtime, "_source", BoundaryGuardedSource,
            [constructor + ":stfld", constructor + ":ldfld", preallocate + ":ldfld", bootstrapExecute + ":ldfld"]);
        CheckReadonlyField(runtime, "_routing", JobRoutingStore, [constructor + ":stfld", preallocate + ":ldfld"]);
        CheckReadonlyField(runtime, "_sourceLifetime", BoundaryTask,
            [constructor + ":stfld", constructor + ":stfld", constructor + ":ldfld", constructor + ":ldfld"]);
        CheckReadonlyField(runtime, "_completion", CompletionSource,
            [constructor + ":stfld", completion + ":ldfld", runtimeObservation + ":ldfld"]);
        CheckReadonlyField(runtime, "_messagingCompletion", CompletionSource,
            [constructor + ":stfld", messagingObservation + ":ldfld", sourceLifetime + ":ldfld"]);
        CheckBridgeCapturedField(offenders, census, runtime, "_outboxCompletion", BoundaryTask,
            [bind + ":stfld", messagingObservation + ":ldfld"]);
        CheckBridgeCapturedField(offenders, census, runtime, "_deliveryCompletion", BoundaryTask,
            [bind + ":stfld", messagingObservation + ":ldfld"]);
        CheckTaskOwnerState(runtime, "_completion", constructor);
        CheckTaskOwnerState(runtime, "_messagingCompletion", constructor);
        CheckSet(offenders, "runtime retained owner keepalive", sites.Where(site => site.Target.DeclaringType?.FullName == "System.GC"
                && site.Target.Name == "KeepAlive" && IsDeclaredOwner(site.Owner, runtime))
            .Select(site => site.Owner), [sourceLifetime]);
        CheckSet(offenders, "runtime source lifetime actual witnesses", sites.Where(site => site.Owner == sourceLifetime
                && (site.Target.DeclaringType?.FullName == JobRuntime && site.Target.Name == "get_Completion"
                    || site.Target.DeclaringType?.FullName == CompletionSource && site.Target.Name == "get_Task"))
            .Select(site => site.Target.DeclaringType?.FullName + ":" + site.Target.Name), [JobRuntime + ":get_Completion", CompletionSource + ":get_Task"]);

        var pin = RequireBoundaryMethod(offenders, source, "RetainBackgroundJobsUntil", "System.Void", MethodAttributes.Assembly, BoundaryTask);
        var sourceConstructor = RequireBoundaryMethod(offenders, source, ".ctor", "System.Void", MethodAttributes.Public,
            "System.Func`1<Npgsql.NpgsqlDataSource>");
        var resolveSource = RequireBoundaryMethod(offenders, source, "Resolve", "Npgsql.NpgsqlDataSource", MethodAttributes.Private);
        var openSource = RequireBoundaryMethod(offenders, source, "OpenConnection", "Npgsql.NpgsqlConnection", MethodAttributes.Public);
        var openSourceAsync = RequireBoundaryMethod(offenders, source, "OpenConnectionAsync",
            "System.Threading.Tasks.ValueTask`1<Npgsql.NpgsqlConnection>", MethodAttributes.Public, BoundaryCancellation);
        var sourceCreated = RequireBoundaryMethod(offenders, source, "get_IsValueCreated", "System.Boolean", MethodAttributes.Public);
        var observePin = RequireBoundaryMethod(offenders, source, "ObserveRetentionAsync", BoundaryTask, MethodAttributes.Private);
        var take = RequireBoundaryMethod(offenders, source, "TakeForDisposal", "Npgsql.NpgsqlDataSource", MethodAttributes.Private);
        var takeSource = RequireBoundaryMethod(offenders, source, "TakeSourceUnderGate", "Npgsql.NpgsqlDataSource", MethodAttributes.Private);
        var disposeSource = RequireBoundaryMethod(offenders, source, "DisposeSourceAsync", "System.Threading.Tasks.ValueTask", MethodAttributes.Private, "Npgsql.NpgsqlDataSource");
        var dispose = RequireBoundaryMethod(offenders, source, "Dispose", "System.Void", MethodAttributes.Public);
        var disposeAsync = RequireBoundaryMethod(offenders, source, "DisposeAsync", "System.Threading.Tasks.ValueTask", MethodAttributes.Public);
        var sourceCleanup = RequireBoundaryMethod(offenders, source, "get_HasUnconfirmedCleanup", "System.Boolean", MethodAttributes.Assembly);
        RequireBoundaryMethod(offenders, source, "get_IsDisposalComplete", "System.Boolean", MethodAttributes.Assembly);
        Exact(source, "RetainBackgroundJobsUntil", pin, [constructor + ":callvirt"]);
        Exact(source, "Resolve", resolveSource, [openSource + ":call", openSourceAsync + ":call"]);
        Exact(source, "ObserveRetentionAsync", observePin, [pin + ":call", pin + ":call"]);
        Exact(source, "TakeForDisposal", take, [dispose + ":call", disposeAsync + ":call"]);
        Exact(source, "TakeSourceUnderGate", takeSource, [take + ":call", observePin + ":call"]);
        Exact(source, "DisposeSourceAsync", disposeSource, [disposeAsync + ":call", observePin + ":call"]);
        CheckBridgeCapturedField(offenders, census, source, "_positiveCompletion", BoundaryTask,
            [pin + ":stfld", observePin + ":ldfld"]);
        CheckReadonlyField(source, "_source", "System.Lazy`1<Npgsql.NpgsqlDataSource>",
            [sourceConstructor + ":stfld", sourceCreated + ":ldfld", resolveSource + ":ldfld", takeSource + ":ldfld", takeSource + ":ldfld"]);
        CheckSet(offenders, "source raw pool declaration boundary", source.Methods.Where(method =>
                ContainsBridgeCapability(method.ReturnType, "Npgsql.NpgsqlDataSource")
                || method.Parameters.Any(parameter => ContainsBridgeCapability(parameter.ParameterType, "Npgsql.NpgsqlDataSource")))
            .Select(MethodId), [sourceConstructor, resolveSource, take, takeSource, disposeSource]);
        CheckBridgeCapturedField(offenders, census, source, "_positiveCompletionConfirmed", "System.Boolean",
            [observePin + ":stfld", observePin + ":ldfld"]);
        CheckBridgeCapturedField(offenders, census, source, "_retentionFailure", "System.Exception",
            [observePin + ":stfld", sourceCleanup + ":ldfld"]);
        CheckBridgeCapturedField(offenders, census, source, "_disposalFailure", "System.Exception",
            [dispose + ":stfld", disposeSource + ":stfld", sourceCleanup + ":ldfld"]);
        CheckSet(offenders, "source pin concrete pool disposal", sites.Where(site => site.Target.Name is "Dispose" or "DisposeAsync"
                && site.Target.DeclaringType?.FullName is "Npgsql.NpgsqlDataSource" or "System.Data.Common.DbDataSource"
                && IsDeclaredOwner(site.Owner, source))
            .Select(site => site.Owner + ":" + site.Target.Name), [dispose + ":Dispose", disposeSource + ":DisposeAsync"]);

        CheckMessagingLifetime();
        CheckCapacityInput();
        CheckHostedAliases();
        CheckRuntimeOwnerConstruction();
        CheckHealthObservation();
        CheckMemoryObservation(JobMetricRoots(census, offenders), "jobs metrics");
        if (types.TryGetValue((InfrastructureAssembly, JobObservation), out var observation))
        {
            CheckMemoryObservation(observation.Methods.Where(method => method.IsConstructor && !method.IsStatic), "job observer construction");
            CheckMemoryObservation(observation.Methods.Where(method => method.Name is "StartStage" or "RecordStage"), "job stage metrics");
        }
        return offenders;

        void Exact(TypeDefinition type, string name, string target, string[] expected) =>
            CheckSet(offenders, "exact runtime lifetime callers " + type.FullName + "." + name, census.MethodInstructions(name)
                .Where(item => TargetsDeclaredType(item.Target, type))
                .Select(item => item.OwnerId + ":" + item.Instruction.OpCode.Name + ":" + MethodId(item.Target)),
                expected.Select(caller => caller + ":" + target));

        void CheckReadonlyField(TypeDefinition type, string name, string capability, string[] expected)
        {
            CheckBridgeCapturedField(offenders, census, type, name, capability, expected);
            if (type.Fields.SingleOrDefault(field => field.Name == name)?.IsInitOnly != true)
                offenders.Add(type.FullName + ": requires its readonly capability " + name);
        }

        void CheckTaskOwnerState(TypeDefinition type, string name, string constructorId)
        {
            var writes = ownedBodies.Where(item => MethodId(item.Owner) == constructorId).SelectMany(item => item.Body.Body.Instructions)
                .Where(instruction => instruction.OpCode == OpCodes.Stfld && instruction.Operand is FieldReference field
                    && TargetsDeclaredType(field, type) && field.Name == name).ToArray();
            if (writes.Length != 1 || writes[0].Previous?.Operand is not MethodReference taskConstructor
                || taskConstructor.DeclaringType?.FullName != CompletionSource || taskConstructor.Name != ".ctor"
                || !ParametersMatch(taskConstructor, ["System.Object", "System.Threading.Tasks.TaskCreationOptions"])
                || writes[0].Previous?.Previous?.Previous?.OpCode != OpCodes.Ldarg_0)
                offenders.Add(type.FullName + ": positive completion must retain its concrete owner as task state: " + name);
        }

        void CheckMessagingLifetime()
        {
            var outboxConstructor = BoundaryWorkerConstructor(offenders, outbox);
            var deliveryConstructor = DeliveryWorkerConstructor(offenders, delivery);
            var outboxStart = RequireBoundaryMethod(offenders, outbox, "StartAsync", BoundaryTask, MethodAttributes.Public, BoundaryCancellation);
            var deliveryStart = RequireBoundaryMethod(offenders, delivery, "StartAsync", BoundaryTask, MethodAttributes.Public, BoundaryCancellation);
            var outboxCompletion = RequireBoundaryMethod(offenders, outbox, "get_PrivilegedCompletion", BoundaryTask, MethodAttributes.Public);
            var deliveryCompletion = RequireBoundaryMethod(offenders, delivery, "get_PrivilegedCompletion", BoundaryTask, MethodAttributes.Public);
            var privateConstructor = messaging.Methods.Where(method => method.IsConstructor && !method.IsStatic && method.Parameters.Count == 0).ToArray();
            if (privateConstructor.Length != 1 || !privateConstructor[0].IsPrivate)
                offenders.Add("Messaging lifetime requires its one private task-state constructor");
            var privateConstructorId = BoundaryApi + ":System.Void " + MessagingLifetime + "::.ctor()";
            var messagingConstructors = new[] { Store, DeliveryStore }.Select(store =>
                BoundaryApi + ":System.Void " + MessagingLifetime + "::.ctor(" + store + ")").ToArray();
            CheckSet(offenders, "fixed messaging lifetime constructors", messaging.Methods.Where(method => method.IsConstructor).Select(MethodId),
                messagingConstructors.Append(privateConstructorId));
            foreach (var method in messaging.Methods.Where(method => method.IsConstructor && method.Parameters.Count == 1))
                if (!method.IsAssembly || method.IsStatic || method.HasGenericParameters)
                    offenders.Add("Messaging lifetime store constructors must remain internal instance entries");
            CheckSet(offenders, "fixed messaging lifetime construction callers", sites.Where(site => TargetsDeclaredType(site.Target, messaging)
                    && site.Target.Name == ".ctor").Select(site => site.Owner + ":" + MethodId(site.Target)),
                [outboxConstructor + ":" + messagingConstructors[0], deliveryConstructor + ":" + messagingConstructors[1],
                 messagingConstructors[0] + ":" + privateConstructorId, messagingConstructors[1] + ":" + privateConstructorId]);
            Exact(outbox, "get_PrivilegedCompletion", outboxCompletion, [hostedConstructor + ":callvirt"]);
            Exact(delivery, "get_PrivilegedCompletion", deliveryCompletion, [hostedConstructor + ":callvirt"]);
            var binding = hosted.Methods.Single(method => MethodId(method) == hostedConstructor).Body.Instructions
                .Where(instruction => instruction.Operand is MethodReference target
                    && target.DeclaringType?.FullName == JobRuntime && target.Name == "BindMessagingLifetime").ToArray();
            if (binding.Length != 1 || binding[0].Previous?.Operand is not MethodReference deliveryWitness
                || !HasAttachedDeclaringType(deliveryWitness) || MethodId(deliveryWitness) != deliveryCompletion
                || binding[0].Previous?.Previous?.Previous?.Operand is not MethodReference outboxWitness
                || !HasAttachedDeclaringType(outboxWitness) || MethodId(outboxWitness) != outboxCompletion)
                offenders.Add("The hosted owner must bind the actual two messaging completion getters directly");
            var observeExecution = RequireBoundaryMethod(offenders, messaging, "ObserveExecutionAsync", BoundaryTask, MethodAttributes.Private, BoundaryTask);
            var tryComplete = RequireBoundaryMethod(offenders, messaging, "TryComplete", "System.Void", MethodAttributes.Private);
            foreach (var (name, result, parameters, callers) in new[]
                     {
                         ("get_Completion", BoundaryTask, Array.Empty<string>(), new[] { outboxCompletion, deliveryCompletion }),
                         ("BeginStart", "System.Void", Array.Empty<string>(), new[] { outboxStart, deliveryStart }),
                         ("DisposeStartupConnectionAsync", BoundaryTask, new[] { "Npgsql.NpgsqlConnection" }, new[] { outboxStart, deliveryStart }),
                         ("TryReserveExecution", "System.Boolean", Array.Empty<string>(), new[] { outboxStart, deliveryStart }),
                         ("ObserveExecution", "System.Void", new[] { BoundaryTask }, new[] { outboxStart, deliveryStart }),
                         ("get_IsStopping", "System.Boolean", Array.Empty<string>(), new[] { outboxStart, deliveryStart }),
                         ("FinishStart", "System.Void", Array.Empty<string>(), new[] { outboxStart, deliveryStart }),
                         ("SealStopping", "System.Void", Array.Empty<string>(), new[]
                         {
                             RequireBoundaryMethod(offenders, outbox, "StopAsync", BoundaryTask, MethodAttributes.Public, BoundaryCancellation),
                             RequireBoundaryMethod(offenders, delivery, "StopAsync", BoundaryTask, MethodAttributes.Public, BoundaryCancellation),
                             RequireBoundaryMethod(offenders, outbox, "Dispose", "System.Void", MethodAttributes.Public),
                             RequireBoundaryMethod(offenders, delivery, "Dispose", "System.Void", MethodAttributes.Public),
                         }),
                     })
            {
                var target = RequireBoundaryMethod(offenders, messaging, name, result, MethodAttributes.Assembly, parameters);
                Exact(messaging, name, target, callers.Select(caller => caller + ":callvirt").ToArray());
            }
            var observe = BoundaryApi + ":System.Void " + MessagingLifetime + "::ObserveExecution(System.Threading.Tasks.Task)";
            Exact(messaging, "ObserveExecutionAsync", observeExecution, [observe + ":call"]);
            Exact(messaging, "TryComplete", tryComplete,
                [observeExecution + ":call", BoundaryApi + ":System.Void " + MessagingLifetime + "::FinishStart():call",
                 BoundaryApi + ":System.Void " + MessagingLifetime + "::SealStopping():call"]);
            CheckReadonlyField(messaging, "_outboxStore", Store, [messagingConstructors[0] + ":stfld", observeExecution + ":ldfld"]);
            CheckReadonlyField(messaging, "_deliveryStore", DeliveryStore, [messagingConstructors[1] + ":stfld", observeExecution + ":ldfld"]);
            CheckReadonlyField(messaging, "_completion", CompletionSource,
                [privateConstructorId + ":stfld", BoundaryApi + ":System.Threading.Tasks.Task " + MessagingLifetime + "::get_Completion():ldfld",
                 tryComplete + ":ldfld", tryComplete + ":ldfld"]);
            CheckTaskOwnerState(messaging, "_completion", privateConstructorId);
            var startupDispose = BoundaryApi + ":System.Threading.Tasks.Task " + MessagingLifetime
                + "::DisposeStartupConnectionAsync(Npgsql.NpgsqlConnection)";
            CheckSet(offenders, "messaging retained startup connection disposal", sites.Where(site =>
                    site.Target.DeclaringType?.FullName is "Npgsql.NpgsqlConnection" or "System.Data.Common.DbConnection"
                    && site.Target.Name is "Dispose" or "DisposeAsync"
                    && (IsDeclaredOwner(site.Owner, messaging) || IsDeclaredOwner(site.Owner, outbox)
                        || IsDeclaredOwner(site.Owner, delivery)))
                .Select(site => site.Owner + ":" + site.Target.Name), [startupDispose + ":DisposeAsync"]);
        }

        void CheckCapacityInput()
        {
            var composition = types[(BoundaryApi, BoundaryComposition)].Methods.Single(method => method.Name == "AddLearnStackOutboxDispatcher");
            var captures = composition.Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Stfld
                && instruction.Previous?.Operand is MethodReference read && IsConnectionStringRead(read)).ToArray();
            var parsers = composition.Body.Instructions.Where(instruction => instruction.Operand is MethodReference target
                && target.DeclaringType?.FullName == "Npgsql.NpgsqlConnectionStringBuilder" && target.Name == ".ctor").ToArray();
            if (captures.Length != 1 || parsers.Length != 1 || parsers[0].Previous?.Operand is not FieldReference parsed
                || captures[0].Operand is not FieldReference captured || !HasAttachedDeclaringType(parsed)
                || !HasAttachedDeclaringType(captured) || parsed.FullName != captured.FullName)
                offenders.Add("Job physical settings must parse the same immutable dispatcher credential capture");
            var settings = types[(InfrastructureAssembly, JobPoolSettings)];
            CheckSet(offenders, "same parsed physical settings getters", MethodOperands(composition).Where(target =>
                    target.DeclaringType?.FullName == "Npgsql.NpgsqlConnectionStringBuilder"
                    && target.Name is "get_Pooling" or "get_MaxPoolSize").Select(target => target.Name),
                ["get_Pooling", "get_MaxPoolSize"]);
            CheckSet(offenders, "sanitized physical settings instance fields", settings.Fields.Where(field => !field.IsStatic)
                .Select(field => field.FieldType.FullName + ":" + field.Name),
                ["System.Boolean:<Pooling>k__BackingField", "System.Int32:<MaxPoolSize>k__BackingField"]);
            var settingsConstructor = InfrastructureAssembly + ":System.Void " + JobPoolSettings + "::.ctor(System.Boolean,System.Int32)";
            var settingsConstructors = settings.Methods.Where(method => method.Name == ".ctor"
                && ParametersMatch(method, ["System.Boolean", "System.Int32"])).ToArray();
            if (settingsConstructors.Length != 1 || !settingsConstructors[0].IsPublic || settingsConstructors[0].IsStatic)
                offenders.Add("Sanitized physical settings require their exact Boolean/Int32 public constructor");
            // Records also own a protected/private copy constructor; pin the public
            // settings constructor separately without authorizing that copy as input.
            CheckSet(offenders, "physical settings construction from the single dispatcher capture", sites.Where(site =>
                    site.Target.DeclaringType?.FullName == JobPoolSettings && site.Target.Name == ".ctor"
                    && ParametersMatch(site.Target, ["System.Boolean", "System.Int32"]))
                .Select(site => site.Owner + ":" + MethodId(site.Target)), [MethodId(composition) + ":" + settingsConstructor]);
            CheckSet(offenders, "runtime registration caller", sites.Where(site => site.Target.DeclaringType?.FullName == JobRuntimeRegistration
                && site.Target.Name == "AddLearnStackBackgroundJobRuntime").Select(site => site.Owner), [MethodId(composition)]);
        }

        void CheckHostedAliases()
        {
            var composition = types[(BoundaryApi, BoundaryComposition)];
            var root = composition.Methods.Single(method => method.Name == "AddLearnStackOutboxDispatcher");
            foreach (var (name, worker) in new[] { ("ResolveOutboxHostedService", BoundaryWorker),
                         ("ResolveDeliveryHostedService", DeliveryWorker), ("ResolveJobHostedService", JobHostedService) })
            {
                var alias = RequireHangfireStaticMethod(offenders, composition, name, worker, MethodAttributes.Private, "System.IServiceProvider");
                Exact(composition, name, alias, [MethodId(root) + ":ldftn"]);
                var method = composition.Methods.SingleOrDefault(candidate => MethodId(candidate) == alias);
                if (method is null) continue;
                var calls = MethodOperands(method).ToArray();
                if (calls.Length != 1 || calls[0] is not GenericInstanceMethod resolve
                    || resolve.DeclaringType?.FullName != "Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions"
                    || resolve.Name != "GetRequiredService" || resolve.GenericArguments.Count != 1
                    || resolve.GenericArguments[0].FullName != worker)
                    offenders.Add("Hosted alias must return its exact singleton concrete owner: " + alias);
            }
        }

        void CheckRuntimeOwnerConstruction()
        {
            foreach (var (typeName, method, callers) in new[]
                     {
                         (VendorBootstrap, "Create", new[] { bootstrapExecute }),
                         (JobBridge, ".ctor", new[] { preallocate }),
                         ("LearnStack.Infrastructure.Jobs.HangfirePoolOwner", ".ctor", new[] { preallocate }),
                         ("LearnStack.Infrastructure.Jobs.HangfireSharedComponents", ".ctor", new[] { preallocate }),
                         ("LearnStack.Infrastructure.Jobs.HangfireWakeupClient", ".ctor", new[] { preallocate }),
                         (JobInvocationContract, "CreateActivator", new[] { preallocate,
                             InfrastructureAssembly + ":Hangfire.JobActivator " + JobInvocationContract + "::CreateActivator("
                             + "LearnStack.Infrastructure.Jobs.BackgroundJobWorkerPoolRegistration,LearnStack.Infrastructure.Jobs.BackgroundJobInfrastructureRegistry,"
                             + JobRoutingStore + "," + JobExecutorType + ",LearnStack.Infrastructure.Jobs.BackgroundJobRecurrenceRegistry," + JobRuntimeGate + ")" }),
                         ("LearnStack.Infrastructure.Jobs.HangfirePoolOwner", "Start", new[] { run }),
                         ("LearnStack.Infrastructure.Jobs.HangfireSharedComponents", "Start", new[] { run }),
                         (JobBridge, "RunQuantumAsync", new[] { bridgeLoop }),
                         (JobBridge, "StopAsync", new[] { joins }),
                     })
                CheckSet(offenders, "facade actual owner capability " + typeName + "." + method,
                    sites.Where(site => site.Target.DeclaringType?.FullName == typeName && site.Target.Name == method)
                        .Select(site => site.Owner), callers);
        }

        void CheckHealthObservation()
        {
            var healthConstructor = RequireBoundaryMethod(offenders, health, ".ctor", "System.Void", MethodAttributes.Public, JobRuntime);
            var check = RequireBoundaryMethod(offenders, health, "CheckHealthAsync",
                "System.Threading.Tasks.Task`1<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult>", MethodAttributes.Public,
                "Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext", BoundaryCancellation);
            CheckBridgeCapturedField(offenders, census, health, "<runtime>P", JobRuntime,
                [healthConstructor + ":stfld", check + ":ldfld"]);
            CheckMemoryObservation(health.Methods.Where(method => MethodId(method) == check), "jobs health");
        }

        void CheckMemoryObservation(IEnumerable<MethodDefinition> roots, string label)
        {
            var pending = new Queue<MethodDefinition>(roots);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (pending.TryDequeue(out var method))
            {
                if (!visited.Add(MethodId(method))) continue;
                foreach (var body in ownedBodies.Where(item => item.Owner == method).Select(item => item.Body))
                {
                    var metadata = new[] { body.ReturnType }.Concat(body.Parameters.Select(parameter => parameter.ParameterType))
                        .Concat(body.GenericParameters.SelectMany(parameter => parameter.Constraints).Select(constraint => constraint.ConstraintType))
                        .Concat(body.Body.Variables.Select(variable => variable.VariableType))
                        .Concat(body.Body.ExceptionHandlers.Where(handler => handler.CatchType is not null).Select(handler => handler.CatchType));
                    foreach (var reference in metadata.SelectMany(Il.ExpandedTypeReferences).Where(IsPersistenceObservationType))
                        offenders.Add(MethodId(method) + ": " + label + " observation carries persistence/provider signature state " + reference.FullName);
                    foreach (var instruction in body.Body.Instructions)
                    {
                        if (instruction.OpCode == OpCodes.Calli)
                            offenders.Add(MethodId(method) + ": " + label + " observation reaches an indirect call");
                        var reference = instruction.Operand switch
                        {
                            MethodReference target => target.DeclaringType,
                            FieldReference field => field.FieldType,
                            TypeReference type => type,
                            _ => null,
                        };
                        if (reference is not null)
                            foreach (var expanded in Il.ExpandedTypeReferences(reference).Where(IsPersistenceObservationType))
                                offenders.Add(MethodId(method) + ": " + label + " observation carries persistence/provider operand state " + expanded.FullName);
                        if (reference is not null && !IsGenerationContextNullObservation(method, instruction)
                            && (reference.Namespace.StartsWith("Npgsql", StringComparison.Ordinal)
                                || reference.Namespace.StartsWith("System.Data", StringComparison.Ordinal)
                                || reference.Namespace.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
                                || reference.Scope is not null && Key(reference).Assembly is "Hangfire.Core" or "Hangfire.PostgreSql"
                                || reference.FullName is "System.IServiceProvider" or "Microsoft.Extensions.DependencyInjection.IServiceScopeFactory"
                                    or "Microsoft.Extensions.DependencyInjection.IKeyedServiceProvider"))
                            offenders.Add(MethodId(method) + ": " + label + " observation reaches persistence/provider activation " + reference.FullName);
                        if (instruction.Operand is FieldReference staticField
                            && instruction.OpCode.Code is Code.Ldsfld or Code.Ldsflda or Code.Stsfld
                            && types.TryGetValue(Key(staticField.DeclaringType), out var fieldOwner))
                            foreach (var initializer in fieldOwner.Methods.Where(target => target.IsConstructor && target.IsStatic))
                                pending.Enqueue(initializer);
                        if (instruction.Operand is not MethodReference call) continue;
                        if (call.DeclaringType.FullName == "System.Diagnostics.Metrics.Meter"
                            && (call.Name == ".ctor" || call.Name.StartsWith("Create", StringComparison.Ordinal))
                            && (method.DeclaringType.FullName != JobObservation || !method.IsConstructor || method.IsStatic || body != method))
                            offenders.Add(MethodId(method) + ": " + label + " observation forwards a hidden Meter or instrument registration");
                        var callTypes = new[] { call.DeclaringType, call.ReturnType }
                            .Concat(call.Parameters.Select(parameter => parameter.ParameterType))
                            .Concat(call is GenericInstanceMethod instance ? instance.GenericArguments : Enumerable.Empty<TypeReference>());
                        foreach (var referenceType in callTypes.SelectMany(Il.ExpandedTypeReferences).Where(IsPersistenceObservationType))
                            offenders.Add(MethodId(method) + ": " + label + " observation carries persistence/provider call state " + referenceType.FullName);
                        if (!HasAttachedDeclaringType(call))
                        {
                            offenders.Add(MethodId(method) + ": " + label + " observation reaches a detached method target " + call.Name);
                            continue;
                        }
                        if (call.DeclaringType.FullName == "System.Threading.Tasks.Task" && call.Name == "Run"
                            || call.DeclaringType.FullName == "System.Threading.Thread" && call.Name is ".ctor" or "Start"
                            || call.Name == "Invoke" && call.DeclaringType.Namespace == "System"
                                && (call.DeclaringType.Name.StartsWith("Func`", StringComparison.Ordinal)
                                    || call.DeclaringType.Name.StartsWith("Action", StringComparison.Ordinal)))
                            offenders.Add(MethodId(method) + ": " + label + " observation schedules work or invokes an arbitrary delegate");
                        if (DynamicResolution(call) || call.Name == "Invoke" && call.DeclaringType.Namespace == "System.Reflection")
                            offenders.Add(MethodId(method) + ": " + label + " observation reaches dynamic activation");
                        if (call.DeclaringType.FullName is "System.Delegate" or "System.MulticastDelegate"
                            && call.Name is "DynamicInvoke" or "DynamicInvokeImpl" or "Invoke")
                            offenders.Add(MethodId(method) + ": " + label + " observation invokes an arbitrary delegate");
                        if (!types.TryGetValue(Key(call.DeclaringType), out var targetType))
                        {
                            if (call.Name is "Invoke" or "BeginInvoke" or "EndInvoke" or "DynamicInvoke")
                                offenders.Add(MethodId(method) + ": " + label + " observation invokes an unresolved external delegate");
                            continue;
                        }
                        foreach (var initializer in targetType.Methods.Where(target => target.IsConstructor && target.IsStatic))
                            pending.Enqueue(initializer);
                        if (targetType.BaseType?.FullName is "System.MulticastDelegate" or "System.Delegate"
                            && call.Name is "Invoke" or "BeginInvoke" or "EndInvoke")
                            offenders.Add(MethodId(method) + ": " + label + " observation invokes an arbitrary delegate");
                        var declaration = call is GenericInstanceMethod generic ? generic.ElementMethod : call;
                        var signature = declaration.FullName.Replace(declaration.DeclaringType.FullName, targetType.FullName, StringComparison.Ordinal);
                        var targets = targetType.Methods.Where(candidate => candidate.FullName == signature).ToArray();
                        if (targets.Length != 1)
                            offenders.Add(MethodId(method) + ": " + label + " observation reaches a missing or ambiguous production declaration " + signature);
                        foreach (var target in targets)
                            pending.Enqueue(target);
                        if (targetType.IsInterface || !targetType.IsSealed)
                            foreach (var implementation in types.Values.Where(candidate => !candidate.IsInterface && Derives(candidate, targetType, types)))
                                foreach (var target in implementation.Methods.Where(candidate => candidate.Name == call.Name
                                             && ParametersMatch(candidate, call.Parameters.Select(parameter => parameter.ParameterType.FullName).ToArray())))
                                    pending.Enqueue(target);
                    }
                }
            }
        }
    }

    private static bool IsPersistenceObservationType(TypeReference reference) =>
        reference.Namespace.StartsWith("Npgsql", StringComparison.Ordinal)
        || reference.Namespace.StartsWith("System.Data", StringComparison.Ordinal)
        || reference.Namespace.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
        || reference.FullName is "System.IServiceProvider" or "Microsoft.Extensions.DependencyInjection.IServiceScopeFactory"
            or "Microsoft.Extensions.DependencyInjection.IKeyedServiceProvider";

    private static bool IsGenerationContextNullObservation(MethodDefinition method, Instruction instruction) =>
        method.DeclaringType.FullName == JobGeneration && method.Name == "get_Snapshot"
        && instruction.OpCode == OpCodes.Ldflda && instruction.Operand is FieldReference field
        && field.DeclaringType?.Scope is not null && Key(field.DeclaringType) == (InfrastructureAssembly, JobGeneration) && field.Name == "_context"
        && field.FieldType.Scope is not null && Key(field.FieldType) == ("Hangfire.Core", "Hangfire.Server.BackgroundServerContext")
        // This reads whether the already-owned context exists. It invokes no
        // context/provider member and never obtains or returns that capability.
        && instruction.Next?.Operand is GenericInstanceMethod read
        && read.DeclaringType?.FullName == "System.Threading.Volatile" && read.Name == "Read"
        && read.GenericArguments.Count == 1 && read.GenericArguments[0].Scope is not null
        && SameType(read.GenericArguments[0], field.FieldType)
        && instruction.Next?.Next?.OpCode == OpCodes.Ldnull
        && instruction.Next?.Next?.Next?.OpCode == OpCodes.Ceq
        && instruction.Next?.Next?.Next?.Next?.OpCode == OpCodes.Ldc_I4_0
        && instruction.Next?.Next?.Next?.Next?.Next?.OpCode == OpCodes.Ceq;

    [Fact]
    public void Background_job_runtime_source_pin_and_messaging_owners_reject_off_owner_escapes()
    {
        using var census = ProductionCensus.Read();
        EvaluateDetector(census, RuntimeLifetimeOffenders).Should().BeEmpty("the actual runtime, source and two messaging lifetimes must be clean nonempty subjects");
        var application = census.Modules.Single(module => module.Assembly.Name.Name == DeliveryApplication);
        var types = census.Types;
        var bridge = types[(InfrastructureAssembly, JobBridge)];
        var bridgeModule = bridge.Module;
        AssertMutation(census, "a removed bridge type leaving detached method targets", () =>
        {
            bridgeModule.Types.Remove(bridge);
            return () => bridgeModule.Types.Add(bridge);
        }, RuntimeLifetimeOffenders);
        var source = types[(InfrastructureAssembly, BoundaryGuardedSource)];
        var resolveSource = source.Methods.Single(method => method.Name == "Resolve");
        AssertMutation(census, "a removed source method leaving a null declaring type", () =>
        {
            source.Methods.Remove(resolveSource);
            return () => source.Methods.Add(resolveSource);
        }, RuntimeLifetimeOffenders);
        foreach (var (assembly, type, names) in new[]
                 {
                     (InfrastructureAssembly, JobRuntime, new[] { ".ctor", "BindMessagingLifetime", "ObserveSourceLifetimeAsync", "ObserveRuntimeAsync", "PreallocateOwners", "RunAsync", "JoinOwnersAsync", "StartAsync", "StopAsync", "RequestStop" }),
                     (InfrastructureAssembly, BoundaryGuardedSource, new[] { "RetainBackgroundJobsUntil", "Resolve", "ObserveRetentionAsync", "TakeSourceUnderGate", "DisposeSourceAsync" }),
                     (BoundaryApi, MessagingLifetime, new[] { ".ctor", "FinishStart", "SealStopping", "ObserveExecution", "DisposeStartupConnectionAsync", "TryComplete" }),
                     (BoundaryApi, BoundaryWorker, new[] { "get_PrivilegedCompletion" }),
                     (BoundaryApi, DeliveryWorker, new[] { "get_PrivilegedCompletion" }),
                     (BoundaryApi, BoundaryComposition, new[] { "ResolveOutboxHostedService", "ResolveDeliveryHostedService", "ResolveJobHostedService" }),
                 })
            foreach (var target in types[(assembly, type)].Methods.Where(method => names.Contains(method.Name, StringComparer.Ordinal)).ToArray())
            {
                foreach (var opcode in new[] { OpCodes.Call, OpCodes.Callvirt, OpCodes.Ldftn, OpCodes.Ldvirtftn })
                    AssertMutation(census, "an off-owner runtime/lifetime capability through " + opcode, () =>
                        AddCallProbe(application, "RuntimeLifetimeEscape", target, opcode), RuntimeLifetimeOffenders);
                AssertMutation(census, "an async off-owner runtime/lifetime capability", () =>
                    AddAsyncCallProbe(application, "AsyncRuntimeLifetimeEscape", target), RuntimeLifetimeOffenders);
            }
        foreach (var (assembly, type, field) in new[]
                 {
                     (InfrastructureAssembly, JobRuntime, "_source"), (InfrastructureAssembly, JobRuntime, "_sourceLifetime"),
                     (InfrastructureAssembly, JobRuntime, "_outboxCompletion"), (InfrastructureAssembly, JobRuntime, "_deliveryCompletion"),
                     (InfrastructureAssembly, BoundaryGuardedSource, "_positiveCompletion"),
                     (InfrastructureAssembly, BoundaryGuardedSource, "_source"),
                     (BoundaryApi, MessagingLifetime, "_outboxStore"), (BoundaryApi, MessagingLifetime, "_deliveryStore"),
                 })
            AssertMutation(census, "an approved type forwarding a retained capability or task", () =>
                AddBoundaryFieldGetter(types[(assembly, type)], "ForwardRetainedCapability", field), RuntimeLifetimeOffenders);
        foreach (var (assembly, type, field) in new[]
                 {
                     (InfrastructureAssembly, JobRuntime, "_completion"), (InfrastructureAssembly, JobRuntime, "_messagingCompletion"),
                     (BoundaryApi, MessagingLifetime, "_completion"),
                 })
        {
            var owner = types[(assembly, type)];
            var write = owner.Methods.Where(method => method.IsConstructor && method.HasBody).SelectMany(method => method.Body.Instructions)
                .Single(instruction => instruction.OpCode == OpCodes.Stfld && instruction.Operand is FieldReference target && target.Name == field);
            AssertMutation(census, "a positive completion task that loses its concrete owner state", () =>
                ReplaceInstruction(write.Previous!.Previous!.Previous!, OpCodes.Ldnull, null!), RuntimeLifetimeOffenders);
        }
        var keepalive = OwnedBodies(census).Where(item => item.Owner.DeclaringType.FullName == JobRuntime
                && item.Owner.Name == "ObserveSourceLifetimeAsync").SelectMany(item => item.Body.Body.Instructions)
            .Single(instruction => instruction.Operand is MethodReference target && target.DeclaringType.FullName == "System.GC" && target.Name == "KeepAlive");
        AssertMutation(census, "a source lifetime that drops its retained concrete runtime owner", () =>
            ReplaceInstruction(keepalive, OpCodes.Nop, null!), RuntimeLifetimeOffenders);
        AssertMutation(census, "persistence inside the actual job-health snapshot helper graph", () =>
        {
            var getter = types[(InfrastructureAssembly, JobRuntime)].Methods.Single(method => method.Name == "get_Snapshot");
            var instruction = Instruction.Create(OpCodes.Newobj, getter.Module.ImportReference(typeof(Npgsql.NpgsqlCommand).GetConstructor(Type.EmptyTypes)!));
            getter.Body.Instructions.Add(instruction);
            return () => getter.Body.Instructions.Remove(instruction);
        }, RuntimeLifetimeOffenders);
        var generationSnapshot = types[(InfrastructureAssembly, JobGeneration)].Methods.Single(method => method.Name == "get_Snapshot");
        var contextRead = generationSnapshot.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Ldflda
            && instruction.Operand is FieldReference { Name: "_context" });
        AssertMutation(census, "a provider context observation without its pinned null-test inversion", () =>
            ReplaceInstruction(contextRead.Next!.Next!.Next!.Next!, OpCodes.Nop, null!), RuntimeLifetimeOffenders);
        var coreConstructor = types[(InfrastructureAssembly, JobGeneration)].Methods.Single(method => method.Name == "Construct")
            .Body.Instructions.Select(instruction => instruction.Operand).OfType<MethodReference>()
            .Single(target => target.DeclaringType.FullName == CoreProcessingServer && target.Name == ".ctor");
        foreach (var (opcode, target) in new[]
                 {
                     (OpCodes.Newobj, coreConstructor),
                     (OpCodes.Callvirt, generationSnapshot.Module.ImportReference(typeof(Hangfire.Server.BackgroundProcessingServer)
                         .GetMethod(nameof(Hangfire.Server.BackgroundProcessingServer.SendStop), Type.EmptyTypes)!)),
                 })
            AssertMutation(census, "actual job-health observation activating or controlling the provider", () =>
            {
                var instruction = Instruction.Create(opcode, target);
                generationSnapshot.Body.Instructions.Add(instruction);
                return () => generationSnapshot.Body.Instructions.Remove(instruction);
            }, RuntimeLifetimeOffenders);
        var compositionRoot = types[(BoundaryApi, BoundaryComposition)].Methods.Single(method => method.Name == "AddLearnStackOutboxDispatcher");
        var maximum = compositionRoot.Body.Instructions.Single(instruction => instruction.Operand is MethodReference target
            && target.DeclaringType.FullName == "Npgsql.NpgsqlConnectionStringBuilder" && target.Name == "get_MaxPoolSize");
        AssertMutation(census, "capacity that drops the actual parsed source maximum", () =>
            ReplaceInstruction(maximum, OpCodes.Nop, null!), RuntimeLifetimeOffenders);
        var clean = AddProbe(application, "CleanRuntimeLifetimeObserver");
        AddCall(clean, "ReadTaskStatus", application.ImportReference(typeof(Task).GetProperty(nameof(Task.IsCompletedSuccessfully))!.GetMethod!), OpCodes.Callvirt);
        try { EvaluateDetector(census, RuntimeLifetimeOffenders).Should().BeEmpty("ordinary task-state observations acquire no lifetime authority"); }
        finally { application.Types.Remove(clean); }
    }
}
