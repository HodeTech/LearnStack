using System.Text.RegularExpressions;
using FluentAssertions;
using LearnStack.Api.Messaging;
using LearnStack.Infrastructure.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed partial class OutboxDispatchConventionTests
{
    private const string DeliveryObserver = "LearnStack.Api.Messaging.DeliveryObservation";
    private const string DeliveryHealth = "LearnStack.Api.Messaging.DeliveryHealthCheck";
    private const string ExecutionControl = "LearnStack.Infrastructure.Messaging.DeliveryExecutionControl";
    private const string ExecutionResultTask = "System.Threading.Tasks.Task`1<LearnStack.Infrastructure.Messaging.DeliveryExecutionResult>";
    private static readonly string[] DeliverySubjects =
    [
        DeliveryWorker, DeliveryAttempt, DeliveryObserver, DeliveryHealth,
        "LearnStack.Api.Messaging.DeliveryObservationSnapshot",
    ];

    /// <summary>ADR-0046 and Accepted P02b-3: controlled execution and memory-only snapshot/health graphs.</summary>
    [Fact]
    public void Delivery_Execution_And_Observation_Are_Bounded()
    {
        using var census = ProductionCensus.Read();
        DeliveryExecutionOffenders(census).Should().BeEmpty(
            "Fix: execute only through the task-owned attempt and controlled executor; preserve callback/scope ownership");
        DeliveryObservationOffenders(census).Should().BeEmpty(
            "Fix: keep subscriber gauges and health memory-only through concrete, interface, inherited and async helpers");
        DeliveryRegistrationOffenders(ObservationServices()).Should().BeEmpty(
            "Fix: retain the mandatory subscriber worker and health check bound to their singleton observation");
    }

    [Fact]
    public void Delivery_execution_census_rejects_uncontrolled_direct_delegate_async_and_missing_paths()
    {
        using var census = ProductionCensus.Read();
        var module = census.Modules.Single(item => item.Assembly.Name.Name == DeliveryApplication);
        var executor = census.Types[(InfrastructureAssembly, DeliveryExecutorType)];
        var control = census.Types[(InfrastructureAssembly, ExecutionControl)];
        foreach (var capability in executor.Methods.Where(method => method.Name is "ExecuteAsync" or "ExecuteControlledAsync")
                     .Concat(control.Methods.Where(method => method.Name is "get_Token" or "FinishCallbacksAsync" or "DisposeCancellation")))
        {
            foreach (var opcode in new[] { OpCodes.Call, OpCodes.Callvirt, OpCodes.Ldftn, OpCodes.Ldvirtftn })
                AssertMutation(census, "an extra execution/lifetime capability through " + opcode.Name,
                    () => AddCallProbe(module, "ExecutionEscape", capability, opcode), DeliveryExecutionOffenders);
            AssertMutation(census, "an asynchronous execution/lifetime escape",
                () => AddAsyncCallProbe(module, "AsyncExecutionEscape", capability), DeliveryExecutionOffenders);
        }
        foreach (var subject in DeliverySubjects)
            AssertMutation(census, "a missing real execution subject " + subject, () =>
            {
                var type = census.Types[(BoundaryApi, subject)];
                var ownerModule = type.Module;
                ownerModule.Types.Remove(type);
                return () => ownerModule.Types.Add(type);
            }, DeliveryExecutionOffenders);
        var probe = AddProbe(module, "CleanExecutionObserver");
        AddCall(probe, "Observe", control.Properties.Single(property => property.Name == "Gate").GetMethod, OpCodes.Callvirt);
        try { DeliveryExecutionOffenders(census).Should().BeEmpty("gate observation alone does not own cancellation lifetime"); }
        finally { module.Types.Remove(probe); }
    }

    [Fact]
    public void Delivery_observation_walk_rejects_concrete_interface_inherited_async_and_callback_persistence()
    {
        using var census = ProductionCensus.Read();
        var module = census.Modules.Single(item => item.Assembly.Name.Name == DeliveryApplication);
        foreach (var rootName in new[] { DeliveryObserver, DeliveryHealth })
            foreach (var capability in new[] { typeof(Npgsql.NpgsqlCommand), typeof(System.Data.Common.DbConnection),
                         typeof(IServiceProvider), typeof(IServiceScopeFactory) })
                AssertMutation(census, "a subscriber observation helper carrying " + capability.Name, () =>
                {
                    var root = census.Types[(BoundaryApi, rootName)];
                    var helper = AddProbe(module, "DeliveryObservationHelper");
                    helper.Fields.Add(new FieldDefinition("Capability", FieldAttributes.Public, module.ImportReference(capability)));
                    var field = new FieldDefinition("Probe", FieldAttributes.Private, root.Module.ImportReference(helper));
                    root.Fields.Add(field);
                    return () => { root.Fields.Remove(field); module.Types.Remove(helper); };
                }, DeliveryObservationOffenders);
        foreach (var isInterface in new[] { true, false })
            AssertMutation(census, "a polymorphic asynchronous subscriber gauge helper", () =>
            {
                var root = census.Types[(BoundaryApi, DeliveryObserver)];
                var contract = AddProbe(module, "DeliveryObservationContract");
                if (isInterface) contract.Attributes |= TypeAttributes.Interface | TypeAttributes.Abstract;
                var helper = AddProbe(module, "DeliveryObservationImplementation");
                if (isInterface) helper.Interfaces.Add(new InterfaceImplementation(contract));
                else helper.BaseType = contract;
                AddAsyncCall(helper, "Read", module.ImportReference(typeof(IServiceScopeFactory).GetMethod(nameof(IServiceScopeFactory.CreateScope))!));
                var field = new FieldDefinition("Probe", FieldAttributes.Private, root.Module.ImportReference(contract));
                root.Fields.Add(field);
                return () => { root.Fields.Remove(field); module.Types.Remove(helper); module.Types.Remove(contract); };
            }, DeliveryObservationOffenders);
        foreach (var target in new[]
                 {
                     typeof(Type).GetMethod(nameof(Type.GetType), [typeof(string)])!,
                     typeof(Activator).GetMethod(nameof(Activator.CreateInstance), [typeof(Type)])!,
                     typeof(System.Reflection.MethodBase).GetMethod(nameof(System.Reflection.MethodBase.Invoke), [typeof(object), typeof(object[])])!,
                 })
            foreach (var opcode in new[] { OpCodes.Call, OpCodes.Ldftn })
                AssertMutation(census, "reflective construction/invocation in an asynchronous observation helper", () =>
                {
                    var root = census.Types[(BoundaryApi, DeliveryObserver)];
                    var helper = AddProbe(module, "ReflectiveObservationHelper");
                    AddAsyncCall(helper, "Read", module.ImportReference(target));
                    AddCall(helper, "DelegateRead", module.ImportReference(target), opcode);
                    var field = new FieldDefinition("Probe", FieldAttributes.Private, root.Module.ImportReference(helper));
                    root.Fields.Add(field);
                    return () => { root.Fields.Remove(field); module.Types.Remove(helper); };
                }, DeliveryObservationOffenders);
        var observation = census.Types[(BoundaryApi, DeliveryObserver)];
        var callbacks = observation.Methods.SelectMany(DelegateTargets).Where(target => SameType(target.DeclaringType, observation)).ToArray();
        callbacks.Should().HaveCountGreaterThanOrEqualTo(4, "the actual production gauge callbacks must be nonempty");
        foreach (var callback in callbacks)
            AssertMutation(census, "persistence inside an actual subscriber gauge callback", () =>
            {
                var method = observation.Methods.Single(item => MethodId(item) == MethodId(callback));
                var instruction = Instruction.Create(OpCodes.Newobj, method.Module.ImportReference(typeof(Npgsql.NpgsqlCommand).GetConstructor(Type.EmptyTypes)!));
                method.Body.Instructions.Insert(0, instruction);
                return () => method.Body.Instructions.Remove(instruction);
            }, DeliveryObservationOffenders);
        var clean = AddProbe(module, "CleanDeliveryMemory");
        clean.Fields.Add(new FieldDefinition("Count", FieldAttributes.Public, module.TypeSystem.Int32));
        AddCall(clean, "ReadDeclaredMetadata", module.ImportReference(typeof(Type).GetProperty(nameof(Type.Assembly))!.GetMethod!), OpCodes.Callvirt);
        var cleanField = new FieldDefinition("Probe", FieldAttributes.Private, observation.Module.ImportReference(clean));
        observation.Fields.Add(cleanField);
        try { DeliveryObservationOffenders(census).Should().BeEmpty("pure memory helper graphs remain admitted"); }
        finally { observation.Fields.Remove(cleanField); module.Types.Remove(clean); }
    }

    public sealed class DeliveryCredentialMutationTests
    {
        [Fact]
        public void Delivery_credentials_reject_extra_store_locators_forwarding_overloads_and_wrong_keys()
        {
            using var census = ProductionCensus.Read();
            var module = census.Modules.Single(item => item.Assembly.Name.Name == DeliveryApplication);
            var store = census.Types[(InfrastructureAssembly, DeliveryStore)];
            foreach (var capability in store.Methods.Where(method => method.Name is ".ctor" or "TryClaimAsync" or "ReplayAsync"))
                foreach (var opcode in new[] { OpCodes.Call, OpCodes.Callvirt, OpCodes.Ldftn, OpCodes.Ldvirtftn })
                    AssertMutation(census, "a subscriber privileged store escape through " + opcode.Name,
                        () => AddCallProbe(module, "DeliveryStoreEscape", capability, opcode), CredentialOffenders);
            var locator = typeof(ServiceProviderServiceExtensions).GetMethods().Single(method => method.Name == "GetRequiredService"
                && method.IsGenericMethodDefinition && method.GetParameters().Length == 1).MakeGenericMethod(typeof(DeliveryDispatchStore));
            AssertMutation(census, "an unkeyed subscriber store locator", () =>
                AddAsyncCallProbe(module, "DeliveryStoreLocator", module.ImportReference(locator)), CredentialOffenders);
            var worker = census.Types[(BoundaryApi, DeliveryWorker)];
            foreach (var (type, name, field) in new[] { (worker, "StartAsync", "_source"), (worker, "DispatchAsync", "_store"),
                         (worker, "ClaimNextAsync", "_store"),
                         (store, "ReplayAsync", "_source"), (store, "AcquireAsync", "_source") })
                AssertMutation(census, "an extra privileged forwarding overload", () => AddBoundaryFieldGetter(type, name, field), CredentialOffenders);
            var parameter = worker.Methods.Single(method => method.IsConstructor && !method.IsStatic).Parameters
                .Single(item => item.CustomAttributes.Any(attribute => attribute.AttributeType.Name == "FromKeyedServicesAttribute"));
            var attribute = parameter.CustomAttributes.Single(item => item.AttributeType.Name == "FromKeyedServicesAttribute");
            AssertMutation(census, "subscriber worker crossing to the platform credential", () =>
            {
                var original = attribute.ConstructorArguments[0];
                attribute.ConstructorArguments[0] = new CustomAttributeArgument(original.Type, BoundaryPlatform);
                return () => attribute.ConstructorArguments[0] = original;
            }, CredentialOffenders);
        }
    }

    [Fact]
    public void Delivery_SQL_writers_and_dynamic_helpers_are_closed()
    {
        using var census = ProductionCensus.Read();
        DeliverySqlOffenders(census).Should().BeEmpty(
            "Fix: ledger/inbox literal writes belong only to acceptance, completion, inbox claim and exact subscriber scheduler transitions");
    }

    [Fact]
    public void Delivery_SQL_census_rejects_quoted_CTE_helper_overload_missing_and_payload_paths()
    {
        using var census = ProductionCensus.Read();
        var module = census.Modules.Single(item => item.Assembly.Name.Name == DeliveryApplication);
        foreach (var sql in new[] { "UPDATE event_deliveries SET attempts=0", "WITH x AS (SELECT 1) UPDATE ONLY public.\"event_deliveries\" SET attempts=0",
                     "DELETE FROM inbox_messages", "INSERT/* probe */INTO public.\"inbox_messages\" VALUES(1)",
                     "TRUNCATE another_table, event_deliveries", "MERGE INTO event_deliveries USING x ON true WHEN MATCHED THEN DELETE" })
            AssertMutation(census, "an unauthorized subscriber SQL writer", () => AddSqlProbe(module, sql), DeliverySqlOffenders);
        var store = census.Types[(InfrastructureAssembly, DeliveryStore)];
        foreach (var name in new[] { "ReplayAsync", "UnrelatedSqlHelper" })
            AssertMutation(census, "an extra SQL helper or overload", () =>
            {
                var method = AddSqlMethod(store, name, "UPDATE event_deliveries SET attempts=0");
                return () => store.Methods.Remove(method);
            }, DeliverySqlOffenders);
        foreach (var sql in new[] { "SELECT payload FROM event_deliveries", "SELECT correlation_id FROM event_deliveries",
                     "SELECT * FROM event_deliveries", "SELECT event_id FROM inbox_messages", "UPDATE event_deliveries SET processed_at = now()" })
            AssertMutation(census, "a scheduler exceeding its granted envelope/completion boundary", () =>
            {
                var method = AddSqlMethod(store, "UnexpectedSql", sql);
                return () => store.Methods.Remove(method);
            }, DeliverySqlOffenders);
        foreach (var helper in new[] { "AcquireAsync", "UpdateAsync", "LockOwnedAsync" })
            AssertMutation(census, "arbitrary SQL passed through a scheduler helper", () =>
                AddCallProbe(module, "SqlPassover", store.Methods.Single(method => method.Name == helper), OpCodes.Call), DeliverySqlOffenders);
        var literal = OwnedBodies(census).Where(item => item.Owner.DeclaringType.FullName == DeliveryStore)
            .SelectMany(item => item.Body.Body.Instructions).First(item => item.Operand is string sql && DeliveryMutation.IsMatch(sql));
        AssertMutation(census, "a missing actual subscriber SQL writer", () => ReplaceInstruction(literal, OpCodes.Ldstr, "SELECT 1"), DeliverySqlOffenders);
        foreach (var sql in new[] { "SELECT event_id FROM event_deliveries", "UPDATE event_deliveries_archive SET attempts=0", "INSERT INTO another_inbox_messages VALUES(1)" })
        {
            var undo = AddSqlProbe(module, sql);
            try { DeliverySqlOffenders(census).Should().BeEmpty("unrelated writes and ordinary app-role reads remain admitted"); }
            finally { undo(); }
        }
    }

    [Fact]
    public void Delivery_registration_census_rejects_missing_duplicate_replaced_and_detached_subjects()
    {
        foreach (var change in new[] { "missing", "duplicate" })
        {
            var services = ObservationServices();
            var worker = services.Single(item => item.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
                && HostedAliasIdentity(item) == DeliveryWorker);
            if (change == "missing") services.Remove(worker);
            else services.Add(worker);
            DeliveryRegistrationOffenders(services).Should().NotBeEmpty("mandatory subscriber worker " + change + " must fail");
        }
        foreach (var change in new[] { "missing", "duplicate", "wrong-type" })
        {
            var services = ObservationServices();
            services.PostConfigure<HealthCheckServiceOptions>(options =>
            {
                var check = options.Registrations.Single(item => item.Name == DeliveryHealthCheck.Name);
                if (change != "duplicate") options.Registrations.Remove(check);
                if (change == "duplicate") options.Registrations.Add(check);
                if (change == "wrong-type") options.Registrations.Add(new HealthCheckRegistration(DeliveryHealthCheck.Name,
                    _ => new UnrelatedHealthCheck(), HealthStatus.Unhealthy, []));
            });
            DeliveryRegistrationOffenders(services).Should().NotBeEmpty("subscriber health " + change + " must fail");
        }
        var detachedServices = ObservationServices();
        using var provider = detachedServices.BuildServiceProvider();
        using var detached = new DeliveryObservation(TimeProvider.System, new DeliveryDispatchOptions(),
            provider.GetRequiredService<OutboxEventRegistry>());
        detachedServices.PostConfigure<HealthCheckServiceOptions>(options =>
        {
            options.Registrations.Remove(options.Registrations.Single(item => item.Name == DeliveryHealthCheck.Name));
            options.Registrations.Add(new HealthCheckRegistration(DeliveryHealthCheck.Name,
                _ => new DeliveryHealthCheck(detached), HealthStatus.Unhealthy, []));
        });
        DeliveryRegistrationOffenders(detachedServices).Should().NotBeEmpty("a detached same-type subscriber health check must fail");
        DeliveryRegistrationOffenders(ObservationServices()).Should().BeEmpty("the exact singleton production binding is the clean control");
    }

    [Fact]
    public void Subscriber_terminal_census_rejects_extra_mint_paths_crossed_owners_and_missing_subjects()
    {
        using var census = ProductionCensus.Read();
        var source = census.Types[(KernelAssembly, MintingSource)];
        var factory = source.Methods.Single(method => method.Name == SubscriberMint);
        var module = census.Modules.Single(item => item.Assembly.Name.Name == DeliveryApplication);
        foreach (var opcode in new[] { OpCodes.Call, OpCodes.Callvirt, OpCodes.Ldftn, OpCodes.Ldvirtftn })
            AssertTerminalMutation(census, "an extra subscriber mint caller through " + opcode.Name,
                () => AddCallProbe(module, "SubscriberMintEscape", factory, opcode));
        AssertTerminalMutation(census, "an asynchronous subscriber mint helper", () =>
            AddAsyncCallProbe(module, "AsyncSubscriberMintEscape", factory));
        AssertTerminalMutation(census, "a producer using the subscriber witness mint", () =>
        {
            var owner = census.Types[(InfrastructureAssembly, Store)];
            var helper = AddCall(owner, "UnexpectedSubscriberMint", factory, OpCodes.Call);
            return () => owner.Methods.Remove(helper);
        });
        AssertTerminalMutation(census, "a missing production subscriber store", () =>
        {
            var store = census.Types[(InfrastructureAssembly, DeliveryStore)];
            var ownerModule = store.Module;
            ownerModule.Types.Remove(store);
            return () => ownerModule.Types.Add(store);
        });
    }

    private static readonly Lazy<Regex> DeliveryMutationPattern = new(() => new Regex(
        (OutboxMutation ?? throw new InvalidOperationException("The shared SQL mutation detector must be initialized."))
            .ToString().Replace("outbox_messages", "(?:event_deliveries|inbox_messages)", StringComparison.Ordinal),
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromSeconds(1)));
    private static Regex DeliveryMutation => DeliveryMutationPattern.Value;
    private static readonly Regex SchedulerForbiddenSql = new(@"\b(?:payload|correlation_id|causation_id|actor_user_id|inbox_messages)\b|\bSELECT\s+(?:[a-z_][a-z0-9_]*\.)?\*|\bprocessed_at\s*=",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static SortedSet<string> DeliverySqlOffenders(ProductionCensus census)
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        var types = census.Types;
        var expected = new List<string>();
        foreach (var (typeName, methodName) in new[]
                 {
                     ("LearnStack.Infrastructure.Messaging.PostgresDeliveryAcceptance", "AcceptAsync"),
                     ("LearnStack.Infrastructure.Messaging.PostgresInboxGuard", "TryClaimAsync"),
                     (DeliveryCompletionType, "FenceAsync"),
                     (DeliveryStore, "AcquireAsync"), (DeliveryStore, "UpdateAsync"),
                     (DeliveryStore, "TryTerminalizeAsync"), (DeliveryStore, "ReplayAsync"),
                 })
        {
            if (!types.TryGetValue((InfrastructureAssembly, typeName), out var type))
            { offenders.Add("Missing subscriber SQL subject: " + typeName); continue; }
            var methods = type.Methods.Where(method => method.Name == methodName).ToArray();
            if (methods.Length != 1) offenders.Add("Missing or ambiguous subscriber SQL owner: " + typeName + "." + methodName);
            else expected.Add(MethodId(methods[0]));
        }
        CheckSet(offenders, "subscriber SQL mutation methods", OwnedBodies(census).SelectMany(item => item.Body.Body.Instructions
            .Where(instruction => instruction.Operand is string sql && DeliveryMutation.IsMatch(sql)).Select(_ => MethodId(item.Owner))), expected);
        foreach (var item in OwnedBodies(census).Where(item => item.Owner.DeclaringType.FullName == DeliveryStore))
            foreach (var instruction in item.Body.Body.Instructions.Where(instruction => instruction.Operand is string))
                if (SchedulerForbiddenSql.IsMatch((string)instruction.Operand))
                    offenders.Add(MethodId(item.Owner) + ": scheduler SQL references protected envelope/inbox/completion data");
        if (!types.TryGetValue((InfrastructureAssembly, DeliveryStore), out var store)) return offenders;
        var sites = CallSites(census).ToArray();
        foreach (var (helper, owners) in new[]
                 {
                     ("AcquireAsync", new[] { "TryClaimAsync", "TryAcquireRecoveryAsync", "TryClaimTerminalAsync" }),
                     ("UpdateAsync", new[] { "FailAsync", "RequestTerminalAsync" }),
                     ("LockOwnedAsync", new[] { "UpdateAsync", "TryTerminalizeAsync" }),
                 })
            CheckSet(offenders, "scheduler SQL helper " + helper, sites.Where(site => site.Target.Name == helper
                && SameType(site.Target.DeclaringType, store)).Select(site => site.Owner),
                store.Methods.Where(method => owners.Contains(method.Name, StringComparer.Ordinal)).Select(MethodId));
        return offenders;
    }

    private static SortedSet<string> DeliveryExecutionOffenders(ProductionCensus census)
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        var types = census.Types;
        foreach (var subject in DeliverySubjects.Select(name => (BoundaryApi, name)).Concat(new[]
                 { (InfrastructureAssembly, DeliveryExecutorType), (InfrastructureAssembly, ExecutionControl) }))
            if (!types.ContainsKey(subject)) offenders.Add("Missing delivery execution subject: " + subject.Item2);
        if (offenders.Count != 0) return offenders;
        var attempt = types[(BoundaryApi, DeliveryAttempt)];
        var executor = types[(InfrastructureAssembly, DeliveryExecutorType)];
        var control = types[(InfrastructureAssembly, ExecutionControl)];
        var sites = CallSites(census).ToArray();
        if (!attempt.IsSealed || attempt.IsPublic) offenders.Add("DeliveryAttempt must remain internal sealed");
        RequireBoundaryMethod(offenders, attempt, ".ctor", "System.Void", MethodAttributes.Public,
            DeliveryExecutorType, DeliveryClaimType);
        var constructors = attempt.Methods.Where(method => method.IsConstructor && !method.IsStatic).ToArray();
        var callbacks = constructors.SelectMany(DelegateTargets)
            .Where(method => method.ReturnType.FullName == ExecutionResultTask && method.Parameters.Count == 0).ToArray();
        if (callbacks.Length != 1) offenders.Add("DeliveryAttempt requires one task-owned execution callback");
        RequireBoundaryMethod(offenders, executor, "ExecuteControlledAsync", ExecutionResultTask, MethodAttributes.Public,
            DeliveryClaimType, ExecutionControl);
        CheckSet(offenders, "controlled executor callers", sites.Where(site => site.Target.Name == "ExecuteControlledAsync"
            && SameType(site.Target.DeclaringType, executor)).Select(site => site.Owner), callbacks.Select(MethodId));
        CheckSet(offenders, "uncontrolled production executor callers", sites.Where(site => site.Target.Name == "ExecuteAsync"
            && SameType(site.Target.DeclaringType, executor)).Select(site => site.Owner), []);
        var worker = types[(BoundaryApi, DeliveryWorker)];
        var owner = RequireBoundaryMethod(offenders, worker, "DispatchNormalAsync", BoundaryTask, MethodAttributes.Private,
            DeliveryClaimType, BoundaryCancellation);
        CheckSet(offenders, "delivery attempt construction sites", sites.Where(site => site.Target.Name == ".ctor"
            && SameType(site.Target.DeclaringType, attempt)).Select(site => site.Owner), [owner]);
        // Only executor methods may obtain the private cancellation token or end its
        // callback lifetime. Caller-side cancellation has its separate public seam.
        foreach (var name in new[] { "get_Token", "FinishCallbacksAsync", "DisposeCancellation" })
        {
            var declarations = control.Methods.Where(method => method.Name == name).ToArray();
            if (declarations.Length != 1 || declarations.Any(method => method.IsPublic))
                offenders.Add(ExecutionControl + "." + name + ": requires one nonpublic lifecycle capability");
        }
        CheckSet(offenders, "execution control token readers", sites.Where(site => site.Target.Name == "get_Token"
            && SameType(site.Target.DeclaringType, control)).Select(site => site.Owner),
            executor.Methods.Where(method => method.Name == "ExecuteControlledAsync").Select(MethodId));
        CheckSet(offenders, "execution callback finish callers", sites.Where(site => site.Target.Name == "FinishCallbacksAsync"
            && SameType(site.Target.DeclaringType, control)).Select(site => site.Owner),
            executor.Methods.Where(method => method.Name is "ExecuteControlledAsync" or "ExecuteOwnedAsync").Select(MethodId));
        CheckSet(offenders, "execution cancellation disposal callers", sites.Where(site => site.Target.Name == "DisposeCancellation"
            && SameType(site.Target.DeclaringType, control)).Select(site => site.Owner),
            executor.Methods.Where(method => method.Name == "ExecuteControlledAsync").Select(MethodId));
        return offenders;
    }

    private static SortedSet<string> DeliveryObservationOffenders(ProductionCensus census)
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        var types = census.Types;
        CheckSet(offenders, "delivery observation assemblies", census.Modules.Select(module => module.Assembly.Name.Name), ProductionAssemblies.Names());
        foreach (var subject in DeliverySubjects)
            if (!types.ContainsKey((BoundaryApi, subject))) offenders.Add("Missing delivery observation subject: " + subject);
        if (offenders.Count != 0) return offenders;
        var pending = new Queue<TypeDefinition>(new[] { types[(BoundaryApi, DeliveryObserver)], types[(BoundaryApi, DeliveryHealth)] });
        var visited = new HashSet<(string Assembly, string Type)>();
        while (pending.TryDequeue(out var type))
        {
            if (!visited.Add(Key(type))) continue;
            foreach (var method in Il.Methods(type).Select(item => item.Definition))
                foreach (var target in MethodOperands(method))
                    if (DynamicResolution(target) || target.Name == "Invoke"
                        && target.DeclaringType.FullName is "System.Reflection.MethodBase" or "System.Reflection.MethodInfo" or "System.Reflection.ConstructorInfo")
                        offenders.Add(MethodId(method) + ": reflective construction/invocation is not memory observation");
            foreach (var reference in Il.ReferencedTypeReferences(type))
            {
                if (reference.Namespace.StartsWith("Npgsql", StringComparison.Ordinal)
                    || reference.Namespace.StartsWith("System.Data", StringComparison.Ordinal)
                    || reference.Namespace.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
                    || reference.FullName is "System.IServiceProvider" or "Microsoft.Extensions.DependencyInjection.IServiceScopeFactory"
                        or "Microsoft.Extensions.DependencyInjection.IKeyedServiceProvider")
                    offenders.Add(TypeId(type) + " -> " + reference.FullName);
                if (!types.TryGetValue(Key(reference), out var helper)) continue;
                pending.Enqueue(helper);
                if (!helper.IsSealed)
                    foreach (var implementation in types.Values.Where(candidate => !candidate.IsInterface && Derives(candidate, helper, types)))
                        pending.Enqueue(implementation);
            }
        }
        return offenders;
    }

    private static SortedSet<string> DeliveryRegistrationOffenders(IServiceCollection services)
    {
        var offenders = ObservationRegistrationOffenders(services);
        using var provider = services.BuildServiceProvider();
        var checks = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations
            .Where(check => check.Name == DeliveryHealthCheck.Name).ToArray();
        var check = checks.Length == 1 ? checks[0].Factory(provider) : null;
        if (check is not DeliveryHealthCheck) offenders.Add("Private delivery health registration");
        else
        {
            var observation = provider.GetRequiredService<DeliveryObservation>();
            observation.Started();
            observation.Progress();
            observation.Refreshed(new DeliveryQueueSnapshot(0, 0, 0));
            var context = new HealthCheckContext { Registration = checks[0] };
            if (check.CheckHealthAsync(context).GetAwaiter().GetResult().Status != HealthStatus.Healthy)
                offenders.Add("Delivery check must follow the healthy singleton observation");
            observation.TerminalAuditFailed();
            if (check.CheckHealthAsync(context).GetAwaiter().GetResult().Status != HealthStatus.Unhealthy)
                offenders.Add("Delivery check must follow terminal audit failure");
            observation.TerminalAuditSucceeded();
            if (check.CheckHealthAsync(context).GetAwaiter().GetResult().Status != HealthStatus.Healthy)
                offenders.Add("Delivery check must follow terminal audit recovery");
        }
        return offenders;
    }

    private static string DeliveryWorkerConstructor(SortedSet<string> offenders, TypeDefinition worker) =>
        RequireBoundaryMethod(offenders, worker, ".ctor", "System.Void", MethodAttributes.Public,
            BoundaryGuardedSource, DeliveryStore, DeliveryExecutorType,
            "LearnStack.Infrastructure.Messaging.OutboxEventRegistry", "LearnStack.Infrastructure.Messaging.DeliveryDispatchOptions",
            "System.TimeProvider", DeliveryObserver, "Microsoft.Extensions.Logging.ILogger`1<" + DeliveryWorker + ">");

    private static void CheckDeliveryCredentials(SortedSet<string> offenders, ProductionCensus census,
        MethodDefinition composition, MethodDefinition? factory)
    {
        var types = census.Types;
        var worker = types[(BoundaryApi, DeliveryWorker)];
        var store = types[(InfrastructureAssembly, DeliveryStore)];
        var workerConstructor = DeliveryWorkerConstructor(offenders, worker);
        CheckSourceFieldAccess(offenders, census, worker, workerConstructor,
            [RequireBoundaryMethod(offenders, worker, "StartAsync", BoundaryTask, MethodAttributes.Public, BoundaryCancellation)]);
        var storeConstructor = RequireBoundaryMethod(offenders, store, ".ctor", "System.Void", MethodAttributes.Public,
            BoundaryGuardedSource, "LearnStack.Infrastructure.Messaging.OutboxEventRegistry",
            "LearnStack.Infrastructure.Messaging.DeliveryDispatchOptions", "LearnStack.SharedKernel.Identifiers.IGuidFactory",
            "LearnStack.SharedKernel.Time.IClock", "Microsoft.Extensions.DependencyInjection.IServiceScopeFactory");
        string[] storeAcquisitions =
        [
            RequireBoundaryMethod(offenders, store, "AcquireAsync", "System.Threading.Tasks.Task`1<" + DeliveryClaimType + ">", MethodAttributes.Private,
                "LearnStack.Infrastructure.Messaging.DeliveryClaimKind", "System.String", BoundaryStrings, BoundaryCancellation),
            RequireBoundaryMethod(offenders, store, "UpdateAsync", BoundaryBooleanTask, MethodAttributes.Private,
                DeliveryClaimType, "System.String", "System.String", "System.String", BoundaryCancellation, "System.String"),
            RequireBoundaryMethod(offenders, store, "TryTerminalizeAsync", BoundaryBooleanTask, MethodAttributes.Public,
                DeliveryClaimType, "System.String", BoundaryCancellation),
            RequireBoundaryMethod(offenders, store, "IsDurablyTerminalAsync", BoundaryBooleanTask, MethodAttributes.Private, DeliveryClaimType),
            RequireBoundaryMethod(offenders, store, "ReadSnapshotAsync", "System.Threading.Tasks.Task`1<LearnStack.Infrastructure.Messaging.DeliveryQueueSnapshot>",
                MethodAttributes.Public, BoundaryStrings, BoundaryCancellation),
            RequireBoundaryMethod(offenders, store, "ReplayAsync", BoundaryBooleanTask, MethodAttributes.Public,
                "System.Guid", "System.String", "System.Guid", BoundaryCancellation),
        ];
        var storeReader = RequireBoundaryMethod(offenders, store, "OpenConnectionAsync",
            "System.Threading.Tasks.ValueTask`1<" + DeliveryStore + "/OwnedConnection>", MethodAttributes.Private, BoundaryCancellation);
        CheckSourceFieldAccess(offenders, census, store, storeConstructor, [storeReader]);
        CheckCleanupConnectionOwner(offenders, census, store, storeReader, storeAcquisitions);
        var expected = new[]
        {
            workerConstructor,
            RequireBoundaryMethod(offenders, worker, "ClaimNextAsync", "System.Threading.Tasks.Task`1<" + DeliveryClaimType + ">",
                MethodAttributes.Private, "System.String[]", BoundaryCancellation),
            RequireBoundaryMethod(offenders, worker, "DispatchAsync", BoundaryTask, MethodAttributes.Private, DeliveryClaimType, BoundaryCancellation),
            RequireBoundaryMethod(offenders, worker, "DispatchNormalAsync", BoundaryTask, MethodAttributes.Private, DeliveryClaimType, BoundaryCancellation),
            RequireBoundaryMethod(offenders, worker, "RefreshAsync", BoundaryTask, MethodAttributes.Private, BoundaryCancellation),
            MethodId(composition),
        }.Concat(factory is null ? [] : new[] { MethodId(factory) });
        CheckSet(offenders, "privileged delivery store consumers", OwnedBodies(census)
            .Where(item => item.Owner.DeclaringType.FullName != DeliveryStore && ReferencesCapability(item.Body, DeliveryStore))
            .Select(item => MethodId(item.Owner)).Concat(types.Values.SelectMany(type => type.Methods)
                .Where(method => !method.HasBody && method.DeclaringType.FullName != DeliveryStore && ReferencesCapability(method, DeliveryStore))
                .Select(MethodId)).Distinct(StringComparer.Ordinal), expected.Concat(CleanupOwnerConsumerIds(census, DeliveryStore))
                    .Concat(MessagingStoreConsumerIds(DeliveryStore)));
        var stateMachines = OwnedBodies(census).Where(item => item.Body != item.Owner && item.Owner.DeclaringType.FullName == DeliveryStore)
            .Select(item => Key(item.Body.DeclaringType)).ToHashSet();
        var fields = types.Values.SelectMany(type => type.Fields).Where(field => field.FieldType.FullName == DeliveryStore
            && !(stateMachines.Contains(Key(field.DeclaringType)) && field.Name == "<>4__this")).ToArray();
        CheckSet(offenders, "privileged delivery store holders", fields.Select(field => TypeId(field.DeclaringType) + ":" + field.Name),
            [TypeId(worker) + ":_store", InfrastructureAssembly + ":" + DeliveryStore + "/OwnedConnection:<owner>P",
             BoundaryApi + ":" + MessagingLifetime + ":_deliveryStore"]);
        if (fields.Any(field => !field.IsPrivate || !field.IsInitOnly
                && !(field.DeclaringType.FullName == DeliveryStore + "/OwnedConnection" && field.Name == "<owner>P")))
            offenders.Add("Delivery store holders must be private readonly or the exact construction-only cleanup capture");
    }

    private static bool ReferencesCapability(MethodDefinition method, string capability)
    {
        bool Contains(TypeReference type) => type.FullName == capability
            || type is GenericInstanceType generic && generic.GenericArguments.Any(Contains)
            || type is TypeSpecification specification && Contains(specification.ElementType);
        if (Contains(method.ReturnType) || method.Parameters.Any(parameter => Contains(parameter.ParameterType))) return true;
        if (!method.HasBody) return false;
        return method.Body.Variables.Any(variable => Contains(variable.VariableType))
            || method.Body.Instructions.Any(instruction => instruction.Operand switch
            {
                GenericInstanceMethod generic => generic.GenericArguments.Any(Contains) || Contains(generic.DeclaringType)
                    || Contains(generic.ReturnType) || generic.Parameters.Any(parameter => Contains(parameter.ParameterType)),
                MethodReference target => Contains(target.DeclaringType) || Contains(target.ReturnType)
                    || target.Parameters.Any(parameter => Contains(parameter.ParameterType)),
                FieldReference field => Contains(field.FieldType),
                TypeReference type => Contains(type),
                _ => false,
            });
    }
}
