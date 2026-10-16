using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Jobs;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace LearnStack.Tests.Architecture;

public sealed partial class OutboxDispatchConventionTests
{
    private const string JobRoutingClaim = "LearnStack.Application.Jobs.BackgroundJobRoutingClaim";
    private const string JobClaimSource = JobRoutingClaim + "/MintingSource";
    private const string JobRoutingStore = "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobRoutingStore";
    private const string JobInvocationContract = "LearnStack.Infrastructure.Jobs.HangfireInvocationContract";
    private const string JobClosedActivator = JobInvocationContract + "/ClosedActivator";
    private const string JobBoundInvocation = JobInvocationContract + "/BoundInvocation";
    private const string JobScheduler = "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobScheduler";
    private const string JobExecutorType = "LearnStack.Infrastructure.Jobs.JobExecutor";
    private const string JobReadRequest = "LearnStack.Application.Jobs.ReadBackgroundJobEnvelopeQuery";
    private const string JobStartRequest = "LearnStack.Application.Jobs.AdmitBackgroundJobAttemptCommand";
    private const string JobStartReceipt = "LearnStack.Application.Jobs.ConfirmedStartReceipt";
    private const string JobOrigin = DeliveryProducer + "/JobOrigin";
    private const string JobReadHandler = "LearnStack.Infrastructure.Jobs.ReadBackgroundJobEnvelopeQueryHandler";
    private const string JobStartHandler = "LearnStack.Infrastructure.Jobs.AdmitBackgroundJobAttemptCommandHandler";

    private static void CheckJobFrameworkMetadata()
    {
        var registry = BackgroundJobDefinitionRegistry.CreateEmpty();
        registry.DefinitionKeys.Should().BeEmpty("P02b-4 supplies infrastructure without production business definitions");
        registry.RetiredKeys.Should().BeEmpty();
        BackgroundJobRecurrenceRegistry.CreateEmpty().Keys.Should().BeEmpty("production recurrence definitions belong to their feature phases");
        registry.All.Select(request => (request.RequestType, request.Classification, request.Scope)).Should().BeEquivalentTo(
            new[]
            {
                (typeof(ReadBackgroundJobEnvelopeQuery), BackgroundJobRequestClassification.FrameworkReadOnly, (BackgroundJobScope?)null),
                (typeof(AdmitBackgroundJobAttemptCommand), BackgroundJobRequestClassification.FrameworkReadWrite, (BackgroundJobScope?)null),
            }, "Fix: keep the nonempty exact framework pair distinct from the empty business catalogue");
        var audit = new AuditCatalog([new BackgroundJobAuditCatalogSource()]);
        foreach (var request in registry.All)
        {
            audit.TryGet(request.RequestType, out var classification).Should().BeTrue("framework work has an explicit audit classification");
            classification!.WritesNoRow.Should().BeTrue("framework admission is Audit Off");
            classification.Entries.Should().BeEmpty();
        }
    }

    /// <summary>ADR-0047 Step 3: construction, installation and use stay with the real route/app owner path.</summary>
    private static SortedSet<string> JobStepThreeOffenders(ProductionCensus census)
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        CheckSet(offenders, "job admission production assemblies", census.Modules.Select(module => module.Assembly.Name.Name), ProductionAssemblies.Names());
        var types = census.Types;
        var subjects = new[]
        {
            (DeliveryApplication, JobAdmission), (DeliveryApplication, JobRoutingClaim), (DeliveryApplication, JobClaimSource),
            (DeliveryApplication, JobStartRequest), (DeliveryApplication, JobReadRequest), (DeliveryApplication, JobStartReceipt),
            (DeliveryApplication, JobOrigin), (DeliveryApplication, DeliveryCoordinator), (DeliveryApplication, DeliveryProducer),
            (InfrastructureAssembly, JobRoutingStore), (InfrastructureAssembly, JobScheduler), (InfrastructureAssembly, JobExecutorType),
            (InfrastructureAssembly, JobReadHandler), (InfrastructureAssembly, JobStartHandler), (InfrastructureAssembly, DeliveryExecutorType),
        };
        foreach (var subject in subjects)
            if (!types.ContainsKey(subject)) offenders.Add("Missing live job admission subject: " + subject.Item2);
        if (offenders.Count != 0) return offenders;
        var sites = CallSites(census).ToArray();
        var claim = types[(DeliveryApplication, JobRoutingClaim)];
        var source = types[(DeliveryApplication, JobClaimSource)];
        var receipt = types[(DeliveryApplication, JobStartReceipt)];
        var origin = types[(DeliveryApplication, JobOrigin)];
        foreach (var (type, mutableField) in new[] { (claim, "_entered"), (receipt, "_consumed"), (origin, string.Empty) })
        {
            if (!type.IsSealed || type.Properties.Any(property => property.SetMethod is not null)
                || type.Fields.Any(field => !field.IsStatic && !field.IsInitOnly && field.Name != mutableField))
                offenders.Add(type.FullName + ": opaque identity must be sealed and immutable except its single-use state");
            var constructors = type.Methods.Where(method => method.IsConstructor && !method.IsStatic).ToArray();
            if (constructors.Length != 1 || (type == origin ? !constructors[0].IsAssembly : !constructors[0].IsPrivate))
                offenders.Add(type.FullName + ": unexpected opaque construction surface");
        }
        if (!source.IsAbstract || source.IsSealed) offenders.Add("Routing claims require their closed abstract minting seam");
        CheckSet(offenders, "routing claim minting implementations", types.Values.Where(type => Derives(type, source, types)).Select(TypeId),
            [TypeId(types[(InfrastructureAssembly, JobRoutingStore)])]);

        Capability(DeliveryApplication, JobRoutingClaim, ".ctor", [(DeliveryApplication, JobClaimSource, "MintClaim")]);
        Capability(DeliveryApplication, JobClaimSource, "MintClaim", [(InfrastructureAssembly, JobRoutingStore, "TryClaimOwnedAsync")]);
        Capability(DeliveryApplication, JobRoutingClaim, "EnterOnce", [(DeliveryApplication, JobAdmission, "EnterFramework")]);
        Capability(DeliveryApplication, JobStartReceipt, ".ctor", [(DeliveryApplication, JobStartReceipt, "FromAcknowledged")]);
        Capability(DeliveryApplication, JobStartReceipt, "FromAcknowledged", [(DeliveryApplication, JobStartRequest, "RecordCommitted")]);
        Capability(DeliveryApplication, JobStartReceipt, "Consume", [(DeliveryApplication, JobAdmission, "Enter")]);
        Capability(DeliveryApplication, JobStartRequest, "BindOwner", [(DeliveryApplication, DeliveryCoordinator, "ExecuteFrameAsync")]);
        Capability(DeliveryApplication, JobStartRequest, "MarkCommitting", [(DeliveryApplication, DeliveryCoordinator, "ExecuteFrameAsync")]);
        Capability(DeliveryApplication, JobStartRequest, "RecordCommitted", [(DeliveryApplication, DeliveryCoordinator, "ExecuteFrameAsync")]);
        Capability(DeliveryApplication, JobStartRequest, "RecordAttempt", [(InfrastructureAssembly, JobStartHandler, "Handle")]);
        Capability(DeliveryApplication, JobAdmission, "EnterFramework", [(InfrastructureAssembly, JobExecutorType, "ExecutePublishedAsync")]);
        Capability(DeliveryApplication, JobAdmission, "InstallValidatedEnvelope", [(InfrastructureAssembly, JobExecutorType, "ExecutePublishedAsync")]);
        Capability(DeliveryApplication, JobAdmission, "RequireRead", [(DeliveryApplication, JobAdmission, "InstallValidatedEnvelope"),
            (InfrastructureAssembly, JobReadHandler, "Handle"), (InfrastructureAssembly, JobReadHandler, "Handle")]);
        Capability(DeliveryApplication, JobAdmission, "RequireStart", [(DeliveryApplication, JobStartRequest, "BindOwner"),
            (DeliveryApplication, JobStartRequest, "RecordAttempt"), (InfrastructureAssembly, JobStartHandler, "Handle"),
            (InfrastructureAssembly, JobStartHandler, "Handle")]);
        Capability(DeliveryApplication, JobAdmission, "AnnouncementContext", [(DeliveryApplication, DeliveryCoordinator, "ExecuteFrameAsync")]);
        Capability(DeliveryApplication, JobAdmission, "RequireOperation", [(DeliveryApplication, DeliveryCoordinator, "ExecuteCoreAsync"),
            (DeliveryApplication, JobAdmission, "RequireOwnerEntry")]);
        Capability(DeliveryApplication, JobAdmission, "PermitsUnresolvedRead", [(DeliveryApplication, JobTenantBehavior, "Handle")]);
        Capability(DeliveryApplication, JobOrigin, ".ctor", [(DeliveryApplication, DeliveryProducer, "CaptureJobOrigin")]);
        Capability(DeliveryApplication, DeliveryProducer, "CaptureJobOrigin", [(InfrastructureAssembly, JobScheduler, "SubmitAsync")]);
        Capability(DeliveryApplication, DeliveryProducer, "RequireJobOrigin", Enumerable.Repeat((InfrastructureAssembly, JobScheduler, "SubmitAsync"), 4).ToArray());
        Capability(DeliveryApplication, DeliveryCoordinator, "ExecuteRequestAsync", [(DeliveryApplication, JobTransactionBehavior, "Handle")]);

        var coordinator = types[(DeliveryApplication, DeliveryCoordinator)];
        var typedEntry = coordinator.Methods.Where(method => method.Name == "ExecuteRequestAsync").ToArray();
        if (typedEntry.Length != 1 || !typedEntry[0].IsAssembly || typedEntry[0].GenericParameters.Count != 2
            || !ParametersMatch(typedEntry[0], ["TRequest", "LearnStack.SharedKernel.Persistence.TransactionMode",
                "System.Func`1<System.Threading.Tasks.Task`1<TResponse>>", BoundaryCancellation]))
            offenders.Add("Typed producer request enrollment must remain the internal exact generic pipeline seam");
        else
        {
            var tokens = typedEntry[0].Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Ldtoken
                && instruction.Operand is GenericParameter parameter && parameter.Type == GenericParameterType.Method && parameter.Position == 0);
            if (tokens.Count() != 2) offenders.Add("Typed producer enrollment must derive operation identity from actual typeof(TRequest)");
        }
        var producer = types[(DeliveryApplication, DeliveryProducer)];
        var captures = producer.Methods.Where(method => method.Name == "CaptureJobOrigin").ToArray();
        var revalidations = producer.Methods.Where(method => method.Name == "RequireJobOrigin").ToArray();
        if (captures.Length != 1 || captures[0].ReturnType.FullName != JobOrigin || !ParametersMatch(captures[0], [LifecycleUnit])
            || revalidations.Length != 1 || revalidations[0].ReturnType.FullName != "System.Void" || !ParametersMatch(revalidations[0], [JobOrigin, LifecycleUnit]))
            offenders.Add("Scheduling origin capture/revalidation must bind the exact physical unit");
        CheckDeliveryOperationType();
        CheckFrameworkTokens();
        CheckAcknowledgementOrder();
        CheckExecutorDispatch();
        return offenders;

        void Capability(string assembly, string typeName, string name, (string Assembly, string Type, string Method)[] owners)
        {
            var target = types[(assembly, typeName)];
            if (target.Methods.Count(method => method.Name == name) != 1)
                offenders.Add(typeName + "." + name + ": capability requires one exact declaration");
            CheckSet(offenders, "job capability " + typeName + "." + name,
                sites.Where(site => SameType(site.Target.DeclaringType, target) && site.Target.Name == name).Select(site => site.Owner),
                owners.Select(owner => JobOwner(offenders, types, owner.Assembly, owner.Type, owner.Method)));
        }

        void CheckDeliveryOperationType()
        {
            var executor = types[(InfrastructureAssembly, DeliveryExecutorType)];
            var owners = executor.Methods.Where(method => method.Name == "ExecuteOwnedAsync").ToArray();
            if (owners.Length != 1) { offenders.Add("Delivery operation requires its one exact owner declaration"); return; }
            var owner = owners[0];
            var delegates = OwnedBodies(census).Where(item => item.Owner == owner).SelectMany(item => DelegateTargets(item.Body)).ToArray();
            var typed = delegates.Select(Callback)
                .Where(method => method is not null && MethodOperands(method).Any(target => target.Name == "get_HandlerType"
                    && target.DeclaringType.FullName == "LearnStack.Infrastructure.Messaging.IntegrationEventSubscription"))
                .Cast<MethodDefinition>().Distinct().ToArray();
            if (typed.Length != 1) offenders.Add("Delivery producer identity must come from its one actual subscription.HandlerType callback");
            CheckSet(offenders, "prepared delivery producer entry sites", sites.Where(site => site.Target.Name == "ExecutePreparedDeliveryAsync"
                && site.Target.DeclaringType.FullName == DeliveryCoordinator).Select(site => site.Owner),
                new[] { MethodId(owner) }.Concat(typed.Select(MethodId)));

            MethodDefinition? Callback(MethodReference target)
            {
                if (!types.TryGetValue(Key(target.DeclaringType), out var type)) return null;
                var methods = type.Methods.Where(method => MethodId(method) == MethodId(target)).ToArray();
                if (methods.Length == 1) return methods[0];
                offenders.Add("Delivery operation callback requires its one exact declaration: " + target.FullName);
                return null;
            }
        }

        void CheckFrameworkTokens()
        {
            const string framework = "LearnStack.Application.Jobs.BackgroundJobFrameworkRequests";
            const string audit = "LearnStack.Application.Jobs.BackgroundJobAuditCatalogSource";
            foreach (var subject in new[] { framework, audit })
                if (!types.ContainsKey((DeliveryApplication, subject))) offenders.Add("Missing exact framework metadata: " + subject);
            if (offenders.Any(offender => offender.StartsWith("Missing exact framework metadata:", StringComparison.Ordinal))) return;
            CheckSet(offenders, "framework request metadata types", types[(DeliveryApplication, framework)].Methods.SelectMany(method => method.HasBody
                    ? method.Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Ldtoken && instruction.Operand is TypeReference)
                        .Select(instruction => ((TypeReference)instruction.Operand).FullName) : []),
                [JobReadRequest, JobStartRequest]);
            CheckSet(offenders, "explicit Audit Off framework requests", types[(DeliveryApplication, audit)].Methods.SelectMany(MethodOperands)
                    .OfType<GenericInstanceMethod>().Where(method => method.Name == "Off")
                    .SelectMany(method => method.GenericArguments).Select(type => type.FullName), [JobReadRequest, JobStartRequest]);
            var admission = types[(DeliveryApplication, JobAdmission)];
            foreach (var (name, expected) in new[]
                     {
                         ("IsFramework", new[] { JobReadRequest, JobStartRequest }),
                         ("ModeFor", new[] { "LearnStack.SharedKernel.Tenancy.PublicSurfaceAttribute", JobReadRequest }),
                         ("PermitsUnresolvedRead", new[] { JobReadRequest }),
                         ("AnnouncementContext", new[] { JobReadRequest }),
                     })
            {
                var methods = admission.Methods.Where(method => method.Name == name).ToArray();
                if (methods.Length != 1) { offenders.Add("Missing exact framework gate: " + name); continue; }
                CheckSet(offenders, "closed framework type gate " + name, methods[0].Body.Instructions
                    .Where(instruction => instruction.OpCode == OpCodes.Ldtoken && instruction.Operand is TypeReference)
                    .Select(instruction => ((TypeReference)instruction.Operand).FullName), expected);
            }
        }

        void CheckAcknowledgementOrder()
        {
            var bodies = OwnedBodies(census).Where(item => item.Owner.DeclaringType.FullName == DeliveryCoordinator
                && item.Owner.Name == "ExecuteFrameAsync").ToArray();
            var ordered = bodies.SelectMany(item => item.Body.Body.Instructions).ToArray();
            var begin = Array.FindIndex(ordered, instruction => instruction.Operand is MethodReference target
                && target.DeclaringType.FullName == JobStartRequest && target.Name == "MarkCommitting");
            var complete = Array.FindIndex(ordered, instruction => instruction.Operand is MethodReference target
                && target.DeclaringType.FullName == LifecycleScope && target.Name == "CompleteAsync");
            var record = Array.FindIndex(ordered, instruction => instruction.Operand is MethodReference target
                && target.DeclaringType.FullName == JobStartRequest && target.Name == "RecordCommitted");
            if (begin < 0 || complete <= begin || record <= complete
                || !ordered.Skip(complete + 1).Take(Math.Max(0, record - complete - 1)).Any(instruction => instruction.Operand is MethodReference target
                    && target.Name == "GetResult" && target.DeclaringType.FullName.StartsWith("System.Runtime.CompilerServices.TaskAwaiter", StringComparison.Ordinal)))
                offenders.Add("A confirmed start receipt must be minted only after the actual owner completion acknowledgement");
        }

        void CheckExecutorDispatch()
        {
            var executor = types[(InfrastructureAssembly, JobExecutorType)];
            var owner = JobOwner(offenders, types, InfrastructureAssembly, JobExecutorType, "ExecutePublishedAsync");
            var business = JobOwner(offenders, types, InfrastructureAssembly, JobExecutorType, "ExecuteBusinessAsync");
            var owners = executor.Methods.Select(MethodId).ToHashSet(StringComparer.Ordinal);
            var calls = sites.Where(site => owners.Contains(site.Owner)).ToArray();
            var services = calls.Where(site => site.Target.Name is "GetRequiredService" or "GetService" or "GetServices")
                .Select(site => site.Owner + ":" + (site.Target is GenericInstanceMethod generic ? string.Join(",", generic.GenericArguments.Select(type => type.FullName)) : "dynamic-service"));
            CheckSet(offenders, "framework executor service resolutions", services,
                [owner + ":MediatR.ISender", owner + ":MediatR.ISender", owner + ":" + LifecycleUnit, owner + ":" + LifecycleUnit,
                 business + ":MediatR.ISender", business + ":" + LifecycleUnit,
                 business + ":LearnStack.Application.Jobs.BackgroundJobCompletionParticipant",
                 business + ":LearnStack.Infrastructure.Jobs.BackgroundJobDefinitionRegistry"]);
            var sends = calls.Where(site => site.Target.Name == "Send" && site.Target.DeclaringType.FullName == "MediatR.ISender")
                .Select(site => site.Owner + ":" + (site.Target is GenericInstanceMethod generic ? string.Join(",", generic.GenericArguments.Select(type => type.FullName)) : "dynamic-request"));
            CheckSet(offenders, "framework executor exact request dispatch", sends,
                [owner + ":LearnStack.SharedKernel.Results.Result`1<LearnStack.Application.Jobs.BackgroundJobEnvelope>",
                 owner + ":LearnStack.SharedKernel.Results.Result`1<LearnStack.SharedKernel.Results.None>",
                 business + ":LearnStack.SharedKernel.Results.Result`1<LearnStack.SharedKernel.Results.None>"]);
            foreach (var request in new[] { JobReadRequest, JobStartRequest })
                CheckSet(offenders, "framework request construction " + request,
                    sites.Where(site => site.Target.Name == ".ctor" && site.Target.DeclaringType.FullName == request
                        && site.Target.Parameters.Count is 1 or 2 && site.Target.Parameters[0].ParameterType.FullName == JobRoutingClaim)
                        .Select(site => site.Owner), [owner]);
            foreach (var target in OwnedBodies(census).Where(item => item.Owner.DeclaringType == executor
                         && item.Owner.Name != "ExecuteBusinessAsync").SelectMany(item => MethodOperands(item.Body)))
                if (target.DeclaringType.FullName is "System.Activator" or "System.Reflection.MethodBase" or "System.Reflection.MethodInfo"
                        or "System.Reflection.ConstructorInfo" || target.DeclaringType.FullName == "System.Type" && target.Name is "GetType" or "GetConstructor")
                    offenders.Add("Framework admission cannot dynamically construct or invoke a business request: " + target.FullName);
        }
    }

    private static string JobOwner(SortedSet<string> offenders, Dictionary<(string Assembly, string Type), TypeDefinition> types,
        string assembly, string typeName, string name)
    {
        if (!types.TryGetValue((assembly, typeName), out var type))
        { offenders.Add("Missing actual job owner: " + typeName); return "missing:" + typeName; }
        var methods = type.Methods.Where(method => method.Name == name).ToArray();
        if (methods.Length == 1) return MethodId(methods[0]);
        offenders.Add("Job owner requires one exact declaration: " + typeName + "." + name);
        return "missing:" + typeName + "." + name;
    }

    private static void CheckJobRoutingCredentials(SortedSet<string> offenders, ProductionCensus census,
        MethodDefinition composition, MethodDefinition? factory)
    {
        // Rebuilt on every detector invocation, including every planted mutation.
        var ownedBodies = OwnedBodies(census).ToArray();
        var types = census.Types;
        var store = types[(InfrastructureAssembly, JobRoutingStore)];
        if (!store.IsSealed) offenders.Add("The privileged job-routing store must remain sealed");
        var constructor = RequireBoundaryMethod(offenders, store, ".ctor", "System.Void", MethodAttributes.Public,
            BoundaryGuardedSource, "LearnStack.Infrastructure.Jobs.BackgroundJobDefinitionRegistry",
            "LearnStack.Infrastructure.Jobs.BackgroundJobRuntimeOptions", "LearnStack.SharedKernel.Identifiers.IGuidFactory", JobRuntimeGate);
        var publicReader = RequireBoundaryMethod(offenders, store, "TryClaimAsync", "System.Threading.Tasks.Task`1<" + JobRoutingClaim + ">",
            MethodAttributes.Public, "LearnStack.SharedKernel.Jobs.BackgroundJobId", "System.Int64", BoundaryCancellation);
        var reader = RequireBoundaryMethod(offenders, store, "TryClaimOwnedAsync",
            "System.Threading.Tasks.Task`1<" + JobRoutingStore + "/OwnedRoutingRead>",
            MethodAttributes.Assembly, "LearnStack.SharedKernel.Jobs.BackgroundJobId", "System.Int64", BoundaryCancellation);
        var sourceFields = store.Fields.Where(field => field.Name == "_source").ToArray();
        if (sourceFields.Length != 1 || !sourceFields[0].IsPrivate || !sourceFields[0].IsInitOnly || sourceFields[0].IsStatic
            || sourceFields[0].FieldType.FullName != BoundaryGuardedSource)
            offenders.Add(TypeId(store) + ": requires its private readonly lazy source");
        else
            CheckSet(offenders, TypeId(store) + " privileged source field access", ownedBodies
                .SelectMany(item => item.Body.Body.Instructions.Where(instruction => instruction.Operand is FieldReference field
                        && SameType(field.DeclaringType, store) && field.Name == sourceFields[0].Name)
                    .Select(instruction => MethodId(item.Owner) + ":" + instruction.OpCode.Name)),
                [constructor + ":stfld", reader + ":ldfld"]);
        foreach (var name in new[] { JobInvocationContract, JobClosedActivator, JobBoundInvocation })
            if (!types.ContainsKey((InfrastructureAssembly, name))) offenders.Add("Missing closed routing consumer: " + name);
        if (offenders.Any(offender => offender.StartsWith("Missing closed routing consumer:", StringComparison.Ordinal))) return;
        var contract = types[(InfrastructureAssembly, JobInvocationContract)];
        var activator = types[(InfrastructureAssembly, JobClosedActivator)];
        var invocation = types[(InfrastructureAssembly, JobBoundInvocation)];
        var creates = contract.Methods.Where(method => method.Name == "CreateActivator").ToArray();
        const string control = "LearnStack.Infrastructure.Jobs.BackgroundJobControl";
        string[] createParameters = ["LearnStack.Infrastructure.Jobs.BackgroundJobWorkerPoolRegistration",
            "LearnStack.Infrastructure.Jobs.BackgroundJobInfrastructureRegistry", JobRoutingStore, JobExecutorType,
            "LearnStack.Infrastructure.Jobs.BackgroundJobRecurrenceRegistry"];
        var createPrefix = InfrastructureAssembly + ":Hangfire.JobActivator " + JobInvocationContract + "::CreateActivator(";
        var create = createPrefix + string.Join(",", createParameters) + ")";
        var controlledCreate = createPrefix + string.Join(",", createParameters.Append(control)) + ")";
        if (creates.Length != 2) offenders.Add("The closed job activator factory requires exactly its admission and controlled overloads");
        foreach (var (identity, visibility) in new[] { (create, MethodAttributes.Public), (controlledCreate, MethodAttributes.Assembly) })
        {
            var methods = creates.Where(method => MethodId(method) == identity).ToArray();
            if (methods.Length != 1 || !methods[0].IsStatic || methods[0].HasGenericParameters
                || (methods[0].Attributes & MethodAttributes.MemberAccessMask) != visibility)
                offenders.Add(identity + ": requires its exact closed static declaration");
        }
        CheckSet(offenders, "exact admission and controlled activator factory callers", ownedBodies
            .SelectMany(item => item.Body.Body.Instructions.Where(instruction => instruction.Operand is MethodReference method
                    && method.DeclaringType.FullName == JobInvocationContract && method.Name == "CreateActivator")
                .Select(instruction => MethodId(item.Owner) + ":" + instruction.OpCode.Name + ":" + MethodId((MethodReference)instruction.Operand))),
            [create + ":call:" + controlledCreate, RuntimePreallocateId + ":call:" + controlledCreate]);
        if (!activator.IsNestedPrivate || !activator.IsSealed || !invocation.IsNestedAssembly || !invocation.IsSealed)
            offenders.Add("Routing capability holders must retain their closed nested visibility and sealed identity");
        var activatorConstructor = RequireBoundaryMethod(offenders, activator, ".ctor", "System.Void", MethodAttributes.Public,
            "LearnStack.Infrastructure.Jobs.BackgroundJobWorkerPoolRegistration", JobRoutingStore, JobExecutorType,
            "LearnStack.Infrastructure.Jobs.BackgroundJobRecurrenceRegistry", control, JobGeneration);
        var bind = RequireBoundaryMethod(offenders, activator, "Bind", JobClosedActivator, MethodAttributes.Assembly, JobGeneration);
        var bindGeneration = RequireHangfireStaticMethod(offenders, contract, "BindGeneration", "Hangfire.JobActivator",
            MethodAttributes.Assembly, "Hangfire.JobActivator", JobGeneration);
        var begin = RequireBoundaryMethod(offenders, activator, "BeginScope", "Hangfire.JobActivatorScope", MethodAttributes.Public,
            "Hangfire.Server.PerformContext");
        var invocationConstructor = RequireBoundaryMethod(offenders, invocation, ".ctor", "System.Void", MethodAttributes.Public,
            "Hangfire.Common.Job", JobRoutingStore, JobExecutorType, JobGeneration, control);
        _ = RequireBoundaryMethod(offenders, invocation, "RunJobAsync", BoundaryTask, MethodAttributes.Assembly,
            "LearnStack.SharedKernel.Jobs.BackgroundJobId", "System.Int64");
        var framework = RequireBoundaryMethod(offenders, invocation, "RunFrameworkAsync", BoundaryTask, MethodAttributes.Private,
            "LearnStack.SharedKernel.Jobs.BackgroundJobId", "System.Int64", "LearnStack.Infrastructure.Jobs.HangfireGenerationWork/Invocation");
        CheckHangfireCapabilitySites(offenders, ownedBodies, JobRoutingStore, "TryClaimOwnedAsync", reader,
            [publicReader + ":call", framework + ":callvirt"]);
        CheckHangfireCapabilitySites(offenders, ownedBodies, JobClosedActivator, ".ctor", activatorConstructor,
            [controlledCreate + ":newobj", bind + ":newobj"]);
        CheckHangfireCapabilitySites(offenders, ownedBodies, JobBoundInvocation, ".ctor", invocationConstructor, [begin + ":newobj"]);
        CheckHangfireCapabilitySites(offenders, ownedBodies, JobClosedActivator, "Bind", bind, [bindGeneration + ":callvirt"]);
        CheckHangfireCapabilitySites(offenders, ownedBodies, JobInvocationContract, "BindGeneration", bindGeneration,
            [HangfireGenerationConstructorId + ":call"]);
        var expected = new[] { MethodId(composition), create, controlledCreate, activatorConstructor, bind, begin, invocationConstructor, framework }
            .Concat(factory is null ? [] : new[] { MethodId(factory) })
            .Concat([RuntimeConstructorId, RuntimeFactoryId, RuntimePreallocateId]);
        CheckSet(offenders, "privileged job-routing store consumers", ownedBodies
            .Where(item => item.Owner.DeclaringType.FullName != JobRoutingStore && ReferencesCapability(item.Body, JobRoutingStore))
            .Select(item => MethodId(item.Owner)).Concat(types.Values.SelectMany(type => type.Methods)
                .Where(method => !method.HasBody && method.DeclaringType.FullName != JobRoutingStore && ReferencesCapability(method, JobRoutingStore))
                .Select(MethodId)).Distinct(StringComparer.Ordinal), expected);
        var stateMachines = ownedBodies.Where(item => item.Body != item.Owner && item.Owner.DeclaringType.FullName == JobRoutingStore)
            .Select(item => Key(item.Body.DeclaringType)).ToHashSet();
        var fields = types.Values.SelectMany(type => type.Fields).Where(field => field.FieldType.FullName == JobRoutingStore
            && !(stateMachines.Contains(Key(field.DeclaringType)) && field.Name == "<>4__this"));
        CheckSet(offenders, "privileged job-routing store holders", fields.Select(field => TypeId(field.DeclaringType) + ":" + field.Name),
            [TypeId(activator) + ":<routing>P", TypeId(invocation) + ":<routing>P", InfrastructureAssembly + ":" + JobRuntime + ":_routing"]);
        if (fields.Any(field => !field.IsPrivate || field.IsStatic)) offenders.Add("Routing capability holders must remain private instance fields");
        foreach (var (holder, accesses) in new[]
                 {
                     (activator, new[] { activatorConstructor + ":stfld", bind + ":ldfld", begin + ":ldfld" }),
                     (invocation, new[] { invocationConstructor + ":stfld", framework + ":ldfld" }),
                 })
            CheckSet(offenders, TypeId(holder) + " routing capability field access", ownedBodies
                    .SelectMany(item => item.Body.Body.Instructions
                        .Where(instruction => instruction.Operand is FieldReference field && SameType(field.DeclaringType, holder)
                            && field.Name == "<routing>P")
                        .Select(instruction => MethodId(item.Owner) + ":" + instruction.OpCode.Name)),
                accesses);
    }

    private static void AssertJobStepThreeControls(ProductionCensus census)
    {
        var types = census.Types;
        var application = census.Modules.Single(module => module.Assembly.Name.Name == DeliveryApplication);
        var routing = types[(InfrastructureAssembly, JobRoutingStore)];
        foreach (var capability in routing.Methods.Where(method => method.Name is ".ctor" or "TryClaimAsync" or "TryClaimOwnedAsync"))
            foreach (var opcode in new[] { OpCodes.Call, OpCodes.Callvirt, OpCodes.Ldftn, OpCodes.Ldvirtftn })
                AssertMutation(census, "a confined routing-store escape", () =>
                    AddCallProbe(application, "ExtraRoutingStoreConsumer", capability, opcode), CredentialOffenders);
        AssertMutation(census, "a routing-source forwarding overload", () =>
            AddBoundaryFieldGetter(routing, "TryClaimAsync", "_source"), CredentialOffenders);
        foreach (var holder in new[] { JobClosedActivator, JobBoundInvocation })
            AssertMutation(census, "a closed activator routing-capability forwarding helper", () =>
                AddBoundaryFieldGetter(types[(InfrastructureAssembly, holder)], "UnexpectedRoutingAccessor", "<routing>P"), CredentialOffenders);
        var activator = types[(InfrastructureAssembly, JobClosedActivator)];
        var bound = types[(InfrastructureAssembly, JobBoundInvocation)];
        var binding = types[(InfrastructureAssembly, JobInvocationContract)].Methods.Single(method => method.Name == "BindGeneration");
        var bindingCapabilities = new[] { activator.Methods.Single(method => method.Name == ".ctor"),
            activator.Methods.Single(method => method.Name == "Bind"), bound.Methods.Single(method => method.Name == ".ctor"), binding }
            .Concat(types[(InfrastructureAssembly, JobInvocationContract)].Methods.Where(method => method.Name == "CreateActivator")).ToArray();
        bindingCapabilities.Should().HaveCount(6, "both exact activator factory declarations join the actual binding capability chain");
        foreach (var capability in bindingCapabilities)
        {
            foreach (var opcode in new[] { OpCodes.Call, OpCodes.Callvirt, OpCodes.Ldftn, OpCodes.Ldvirtftn, OpCodes.Newobj })
                AssertMutation(census, "an extra generation-bound activator capability through " + opcode, () =>
                    AddCallProbe(application, "ExtraBoundActivatorCapability", capability, opcode), CredentialOffenders);
            AssertMutation(census, "an asynchronous generation-bound activator capability", () =>
                AddAsyncCallProbe(application, "AsyncBoundActivatorCapability", capability), CredentialOffenders);
            AssertMutation(census, "an activator capability overload borrowing the approved name", () =>
            {
                var method = AddCall(capability.DeclaringType, capability.Name, capability, OpCodes.Call);
                return () => capability.DeclaringType.Methods.Remove(method);
            }, CredentialOffenders);
        }
        var bindingSites = OwnedBodies(census).SelectMany(item => item.Body.Body.Instructions)
            .Where(instruction => instruction.Operand is MethodReference target && bindingCapabilities.Any(capability =>
                SameType(target.DeclaringType, capability.DeclaringType) && target.Name == capability.Name)).ToArray();
        bindingSites.Should().HaveCount(7, "both controlled factory calls, two activator constructors, bound invocation and both binding seams must be live");
        foreach (var instruction in bindingSites)
        {
            AssertMutation(census, "a missing actual generation-bound construction or binding site", () =>
                ReplaceInstruction(instruction, OpCodes.Nop, null!), CredentialOffenders);
            AssertMutation(census, "a delegate escape inside an approved binding caller", () =>
                ReplaceInstruction(instruction, OpCodes.Ldftn, instruction.Operand), CredentialOffenders);
        }
        var cleanActivator = AddProbe(application, "CleanBoundInvocationIdentityObserver");
        AddCall(cleanActivator, "ObserveIdentity", bound.Methods.Single(method => method.Name == "get_Type"), OpCodes.Callvirt);
        try { EvaluateDetector(census, CredentialOffenders).Should().BeEmpty("immutable invocation identity does not forward routing authority"); }
        finally { application.Types.Remove(cleanActivator); }
        var executor = types[(InfrastructureAssembly, JobExecutorType)];
        AssertMutation(census, "dynamic business construction in the framework executor", () =>
        {
            var method = AddCall(executor, "UnexpectedBusinessActivation", executor.Module.ImportReference(
                typeof(Activator).GetMethod(nameof(Activator.CreateInstance), [typeof(Type)])!), OpCodes.Call);
            return () => executor.Methods.Remove(method);
        }, JobStepThreeOffenders);
        var send = OwnedBodies(census).Where(item => item.Owner.DeclaringType == executor)
            .SelectMany(item => MethodOperands(item.Body)).First(target => target.Name == "Send" && target.DeclaringType.FullName == "MediatR.ISender");
        AssertMutation(census, "an extra executor helper dispatching a request", () =>
        {
            var method = AddCall(executor, "UnexpectedFrameworkSend", send, OpCodes.Callvirt);
            return () => executor.Methods.Remove(method);
        }, JobStepThreeOffenders);
        var capabilities = new[]
        {
            (JobAdmission, "EnterFramework"), (JobAdmission, "InstallValidatedEnvelope"), (JobRoutingClaim, "EnterOnce"),
            (JobAdmission, "PermitsUnresolvedRead"), (JobAdmission, "RequireRead"), (JobAdmission, "RequireStart"),
            (JobAdmission, "AnnouncementContext"),
            (JobAdmission, "RequireOperation"),
            (JobClaimSource, "MintClaim"), (JobStartReceipt, "FromAcknowledged"), (JobStartReceipt, "Consume"),
            (JobStartRequest, "BindOwner"), (JobStartRequest, "MarkCommitting"), (JobStartRequest, "RecordAttempt"), (JobStartRequest, "RecordCommitted"),
            (DeliveryProducer, "CaptureJobOrigin"), (DeliveryProducer, "RequireJobOrigin"), (DeliveryCoordinator, "ExecuteRequestAsync"),
        }.Select(subject => types[(DeliveryApplication, subject.Item1)].Methods.Single(method => method.Name == subject.Item2)).ToArray();
        capabilities.Should().NotBeEmpty("the dirty controls must start from live production authority");
        foreach (var capability in capabilities)
        {
            foreach (var opcode in new[] { OpCodes.Call, OpCodes.Callvirt, OpCodes.Ldftn, OpCodes.Ldvirtftn })
                AssertMutation(census, "an extra job capability through " + opcode, () =>
                    AddCallProbe(application, "ExtraJobCapability", capability, opcode), JobStepThreeOffenders);
            AssertMutation(census, "an asynchronous job capability", () =>
                AddAsyncCallProbe(application, "AsyncJobCapability", capability), JobStepThreeOffenders);
            AssertMutation(census, "an overload borrowing an admitted job capability name", () =>
            {
                var method = AddCall(capability.DeclaringType, capability.Name, capability, OpCodes.Call);
                return () => capability.DeclaringType.Methods.Remove(method);
            }, JobStepThreeOffenders);
        }
        foreach (var typeName in new[] { JobRoutingClaim, JobStartReceipt, JobOrigin })
        {
            var type = types[(DeliveryApplication, typeName)];
            var constructor = type.Methods.Single(method => method.IsConstructor && !method.IsStatic);
            AssertMutation(census, "an extra opaque-token constructor call", () =>
                AddCallProbe(application, "OpaqueJobTokenConstructor", constructor, OpCodes.Newobj), JobStepThreeOffenders);
            AssertMutation(census, "an asynchronous opaque-token constructor call", () =>
                AddAsyncCallProbe(application, "AsyncOpaqueJobTokenConstructor", constructor), JobStepThreeOffenders);
            AssertMutation(census, "a public opaque-token constructor", () =>
                ChangeAttributes(() => constructor.Attributes, value => constructor.Attributes = value,
                    (constructor.Attributes & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Public), JobStepThreeOffenders);
        }
        AssertMutation(census, "a second routing minting implementation", () =>
        {
            var probe = AddProbe(application, "AdditionalJobClaimSource");
            probe.BaseType = application.ImportReference(types[(DeliveryApplication, JobClaimSource)]);
            return () => application.Types.Remove(probe);
        }, JobStepThreeOffenders);
        foreach (var typeName in new[] { JobRoutingClaim, JobClaimSource, JobStartReceipt, JobOrigin, JobStartRequest, JobReadRequest })
            AssertMutation(census, "a missing live opaque job subject", () =>
            {
                var type = types[(DeliveryApplication, typeName)];
                var collection = type.DeclaringType?.NestedTypes ?? type.Module.Types;
                collection.Remove(type);
                return () => collection.Add(type);
            }, JobStepThreeOffenders);
        var deliveryTypeSite = census.Types.Values.SelectMany(type => type.Methods).Where(method => method.HasBody && method.Name != "MoveNext"
                && MethodOperands(method).Any(target => target.Name == "ExecutePreparedDeliveryAsync"
                    && target.DeclaringType.FullName == DeliveryCoordinator)
                && MethodOperands(method).Any(target => target.Name == "get_HandlerType"
                    && target.DeclaringType.FullName == "LearnStack.Infrastructure.Messaging.IntegrationEventSubscription"))
            .SelectMany(method => method.Body.Instructions).Single(instruction => instruction.Operand is MethodReference target
                && target.Name == "get_HandlerType" && target.DeclaringType.FullName == "LearnStack.Infrastructure.Messaging.IntegrationEventSubscription");
        AssertMutation(census, "a missing actual subscription handler identity", () =>
            ReplaceInstruction(deliveryTypeSite, OpCodes.Ldstr, "missing subscription handler identity"), JobStepThreeOffenders);
        var committedBody = OwnedBodies(census).Single(item => item.Owner.DeclaringType.FullName == DeliveryCoordinator
            && item.Owner.Name == "ExecuteFrameAsync" && item.Body.Name == "MoveNext").Body.Body;
        var committed = committedBody.Instructions.Single(instruction => instruction.Operand is MethodReference target
            && target.DeclaringType.FullName == JobStartRequest && target.Name == "RecordCommitted");
        AssertMutation(census, "a start receipt minted before the owner acknowledgement", () =>
        {
            var index = committedBody.Instructions.IndexOf(committed);
            committedBody.Instructions.Remove(committed);
            committedBody.Instructions.Insert(0, committed);
            return () => { committedBody.Instructions.Remove(committed); committedBody.Instructions.Insert(index, committed); };
        }, JobStepThreeOffenders);
        var frameworkTokens = types[(DeliveryApplication, "LearnStack.Application.Jobs.BackgroundJobFrameworkRequests")].Methods
            .Concat(types[(DeliveryApplication, JobAdmission)].Methods.Where(method => method.Name is "IsFramework" or "ModeFor" or "PermitsUnresolvedRead" or "AnnouncementContext"))
            .Concat(types[(DeliveryApplication, DeliveryCoordinator)].Methods.Where(method => method.Name == "ExecuteRequestAsync"))
            .Where(method => method.HasBody).SelectMany(method => method.Body.Instructions)
            .Where(instruction => instruction.OpCode == OpCodes.Ldtoken && instruction.Operand is TypeReference).ToArray();
        frameworkTokens.Should().NotBeEmpty("the exact framework and producer type identities must exist in real production IL");
        foreach (var instruction in frameworkTokens)
            AssertMutation(census, "a removed exact framework or trusted operation type", () =>
                ReplaceInstruction(instruction, OpCodes.Ldstr, "missing exact type identity"), JobStepThreeOffenders);
        var realSites = OwnedBodies(census).SelectMany(item => item.Body.Body.Instructions)
            .Where(instruction => instruction.Operand is MethodReference target && capabilities.Any(capability => SameType(target.DeclaringType, capability.DeclaringType)
                && target.Name == capability.Name)).ToArray();
        realSites.Should().NotBeEmpty("the construction/install/use census must inspect actual production sites");
        foreach (var instruction in realSites)
            AssertMutation(census, "a deleted actual job authority site", () =>
                ReplaceInstruction(instruction, OpCodes.Ldstr, "missing job authority site"), JobStepThreeOffenders);
        var clean = AddProbe(application, "CleanJobIdentityObserver");
        foreach (var typeName in new[] { JobRoutingClaim, JobStartReceipt, JobOrigin })
            foreach (var getter in types[(DeliveryApplication, typeName)].Methods.Where(method => method.IsGetter && method.IsPublic))
                AddCall(clean, "Observe" + typeName.Split('/').Last().Split('.').Last() + getter.Name, getter, OpCodes.Callvirt);
        try { JobStepThreeOffenders(census).Should().BeEmpty("reading immutable identity does not install authority"); }
        finally { application.Types.Remove(clean); }
    }
}
