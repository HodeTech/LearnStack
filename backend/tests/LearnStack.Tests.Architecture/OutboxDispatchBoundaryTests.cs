using FluentAssertions;
using LearnStack.Api.Composition;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.MultiTenancy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed partial class OutboxDispatchConventionTests
{
    private const string BoundaryApi = "LearnStack.Api";
    private const string BoundaryComposition = "LearnStack.Api.Composition.OutboxCompositionExtensions";
    private const string BoundaryPersistence = "LearnStack.Api.Composition.PersistenceCompositionExtensions";
    private const string BoundarySource = "LearnStack.Infrastructure.Messaging.OutboxDispatcherDataSource";
    private const string BoundaryWorker = "LearnStack.Api.Messaging.OutboxProcessor";
    private const string BoundaryAttempt = "LearnStack.Api.Messaging.OutboxPublishAttempt";
    private const string BoundaryPlatform = "LearnStack.Infrastructure.MultiTenancy.PlatformAdminScope";
    private const string BoundaryBus = "LearnStack.SharedKernel.Messaging.IEventBus";
    private const string BoundaryTransport = "LearnStack.Infrastructure.Messaging.InProcessEventBus";
    private const string BoundaryDataSource = "Npgsql.NpgsqlDataSource";
    private const string BoundaryLazySource = "System.Lazy`1<Npgsql.NpgsqlDataSource>";

    /// <summary>
    /// ADR-0046: census every production assembly, exact keyed factories and parameter
    /// bindings. Role/connectivity/grant behavior is proved by the PostgreSQL suites.
    /// </summary>
    [Fact]
    public void LearnStack_OutboxAdmin_Role_OnlyUsedBy_OutboxProcessor()
    {
        using var census = ProductionCensus.Read();
        CredentialOffenders(census).Should().BeEmpty(
            "Fix: confine each keyed source to its exact production registration, lazy factory and consumer; "
            + "the platform key never authorizes the dispatcher or its helpers");
    }

    internal static void AssertOnlyWorkerPublishes()
    {
        using var census = ProductionCensus.Read();
        PublicationOffenders(census).Should().BeEmpty(
            "Fix: only OutboxProcessor.DispatchNormalAsync constructs the owned publication attempt, "
            + "whose private PublishAsync is the sole production IEventBus publisher; producers use IOutbox");
    }

    [Fact]
    public void Credential_census_rejects_generic_direct_delegate_async_and_extra_helper_resolution()
    {
        using var census = ProductionCensus.Read();
        var application = census.Modules.Single(module => module.Assembly.Name.Name == "LearnStack.Application");
        var extensions = typeof(ServiceProviderKeyedServiceExtensions).GetMethods()
            .Where(method => method.Name is "GetRequiredKeyedService" or "GetKeyedService" or "GetKeyedServices")
            .ToArray();
        var targets = extensions.Concat(typeof(IKeyedServiceProvider).GetMethods())
            .Concat(typeof(ServiceProvider).GetMethods().Where(method => method.Name is "GetRequiredKeyedService" or "GetKeyedService"));
        foreach (var target in targets)
            foreach (var opcode in new[] { OpCodes.Call, OpCodes.Callvirt, OpCodes.Ldftn, OpCodes.Ldvirtftn })
                AssertMutation(census, "extra keyed resolution through " + target.DeclaringType!.Name + "." + target.Name + "/" + opcode,
                    () => AddCallProbe(application, "CredentialCaller", application.ImportReference(target), opcode), CredentialOffenders);

        var resolver = application.ImportReference(extensions.First());
        AssertMutation(census, "a cross-assembly async keyed resolver", () =>
            AddAsyncCallProbe(application, "AsyncCredentialCaller", resolver), CredentialOffenders);
        AssertMutation(census, "an unrelated helper on the sanctioned composition type", () =>
        {
            var type = census.Types[(BoundaryApi, BoundaryComposition)];
            var method = AddCall(type, "UnrelatedCredentialHelper", resolver, OpCodes.Call);
            return () => type.Methods.Remove(method);
        }, CredentialOffenders);
        AssertMutation(census, "a generated-name impersonation of the composition root", () =>
        {
            var type = census.Types[(BoundaryApi, BoundaryComposition)];
            var generated = new TypeDefinition(string.Empty, "<AddLearnStackOutboxDispatcher>d__999", TypeAttributes.NestedPrivate,
                type.Module.TypeSystem.Object);
            type.NestedTypes.Add(generated);
            AddCall(generated, "MoveNext", resolver, OpCodes.Call);
            return () => type.NestedTypes.Remove(generated);
        }, CredentialOffenders);
        AssertMutation(census, "AnyKey enumeration even inside a sanctioned root", () =>
        {
            var type = census.Types[(BoundaryApi, BoundaryComposition)];
            var method = AddCall(type, "AnyCredential", type.Module.ImportReference(typeof(KeyedService)
                .GetProperty(nameof(KeyedService.AnyKey))!.GetMethod!), OpCodes.Call);
            return () => type.Methods.Remove(method);
        }, CredentialOffenders);
        AssertMutation(census, "an outbox credential indexer in another production assembly", () =>
            AddStringProbe(application, "CredentialIndexer", "ConnectionStrings:OutboxDispatcher"), CredentialOffenders);
        AssertMutation(census, "an outbox environment credential in another production assembly", () =>
            AddStringProbe(application, "CredentialEnvironment", "ConnectionStrings__OutboxDispatcher"), CredentialOffenders);
        foreach (var alias in new[] { "OUTBOXDISPATCHER", "CONNECTIONSTRINGS:OUTBOXDISPATCHER", "CONNECTIONSTRINGS__OUTBOXDISPATCHER" })
            AssertMutation(census, "a case-insensitive configuration alias " + alias, () =>
                AddStringProbe(application, "UppercaseCredentialAlias", alias), CredentialOffenders);
        foreach (var subject in new[] { (BoundaryApi, BoundaryWorker), (InfrastructureAssembly, Store) })
            foreach (var opcode in new[] { OpCodes.Ldfld, OpCodes.Ldflda })
                AssertMutation(census, "an extra privileged-source forwarding helper using " + opcode, () =>
                {
                    var type = census.Types[subject];
                    var source = type.Fields.Single(field => field.Name == "_source");
                    var method = new MethodDefinition("ForwardSource", MethodAttributes.Private, type.Module.TypeSystem.Void);
                    method.Body.Instructions.Add(Instruction.Create(opcode, source));
                    method.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
                    method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                    type.Methods.Add(method);
                    return () => type.Methods.Remove(method);
                }, CredentialOffenders);
        var store = census.Types[(InfrastructureAssembly, Store)];
        var storeConstructor = store.Methods.Single(method => method.IsConstructor && !method.IsStatic);
        AssertMutation(census, "a second consumer constructing the privileged store", () =>
            AddCallProbe(application, "AdditionalStoreConsumer", storeConstructor, OpCodes.Newobj), CredentialOffenders);
        var resolveStore = typeof(ServiceProviderServiceExtensions).GetMethods().Single(method => method.Name == "GetRequiredService"
            && method.IsGenericMethodDefinition && method.GetParameters().Length == 1).MakeGenericMethod(typeof(OutboxDispatchStore));
        AssertMutation(census, "an unkeyed locator escaping through the privileged store", () =>
            AddCallProbe(application, "LocatedStoreConsumer", application.ImportReference(resolveStore), OpCodes.Call), CredentialOffenders);
        AssertMutation(census, "an unrelated helper on the approved worker using the privileged store", () =>
        {
            var worker = census.Types[(BoundaryApi, BoundaryWorker)];
            var method = AddCall(worker, "UnrelatedStoreConsumer", store.Methods.Single(method => method.Name == "TryClaimAsync"), OpCodes.Call);
            return () => worker.Methods.Remove(method);
        }, CredentialOffenders);
    }

    [Fact]
    public void Credential_census_rejects_crossed_keys_changed_closures_and_missing_subjects()
    {
        using var census = ProductionCensus.Read();
        foreach (var subject in new[] { (BoundaryApi, BoundaryWorker), (InfrastructureAssembly, BoundaryPlatform) })
        {
            var parameter = census.Types[subject].Methods.Single(method => method.IsConstructor && !method.IsStatic)
                .Parameters.Single(parameter => parameter.CustomAttributes.Any(attribute =>
                    attribute.AttributeType.Name == "FromKeyedServicesAttribute"));
            var attribute = parameter.CustomAttributes.Single(item => item.AttributeType.Name == "FromKeyedServicesAttribute");
            AssertMutation(census, "a consumer resolving the other privileged key", () =>
            {
                var original = attribute.ConstructorArguments[0];
                attribute.ConstructorArguments[0] = new CustomAttributeArgument(original.Type,
                    subject.Item2 == BoundaryWorker ? PlatformAdminScope.PlatformDataSourceKey : OutboxDispatcherDataSource.Key);
                return () => attribute.ConstructorArguments[0] = original;
            }, CredentialOffenders);
            AssertMutation(census, "a raw source replacing the required lazy binding", () =>
            {
                var original = parameter.ParameterType;
                parameter.ParameterType = census.Types[subject].Module.ImportReference(typeof(NpgsqlDataSource));
                return () => parameter.ParameterType = original;
            }, CredentialOffenders);
        }

        foreach (var key in new[] { OutboxDispatcherDataSource.Key, PlatformAdminScope.PlatformDataSourceKey })
        {
            var descriptor = BoundaryRegistrations().Single(item => item.IsKeyedService && Equals(item.ServiceKey, key)
                && item.ServiceType == typeof(Lazy<NpgsqlDataSource>));
            var factory = FindRuntimeMethod(census, descriptor.KeyedImplementationFactory!.Method)!;
            var leafReference = DelegateTargets(factory).Single();
            var leaf = census.Types[Key(leafReference.DeclaringType)].Methods.Single(method => MethodId(method) == MethodId(leafReference));
            var call = leaf.Body.Instructions.Single(instruction => instruction.Operand is MethodReference method && IsKeyedResolution(method));
            AssertMutation(census, "a lazy callback substituting the other key", () =>
                ReplaceInstruction(call.Previous!, OpCodes.Ldstr, key == OutboxDispatcherDataSource.Key
                    ? PlatformAdminScope.PlatformDataSourceKey : OutboxDispatcherDataSource.Key), CredentialOffenders);
            var captured = (FieldReference)call.Previous!.Operand;
            var assignment = factory.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Stfld
                && instruction.Operand is FieldReference field && field.FullName == captured.FullName);
            AssertMutation(census, "a factory capturing a different key instead of its registered key", () =>
                ReplaceInstruction(assignment.Previous!, OpCodes.Ldstr, "unrelated-key"), CredentialOffenders);
        }

        var root = census.Types[(BoundaryApi, BoundaryComposition)].Methods.Single(method => method.Name == "AddLearnStackOutboxDispatcher");
        var registrationKey = root.Body.Instructions.First(instruction => Equals(instruction.Operand, OutboxDispatcherDataSource.Key));
        AssertMutation(census, "a changed actual registration key", () =>
            ReplaceInstruction(registrationKey, OpCodes.Ldstr, PlatformAdminScope.PlatformDataSourceKey), CredentialOffenders);
        var configurationKey = root.Body.Instructions.Single(instruction => Equals(instruction.Operand, "OutboxDispatcher"));
        AssertMutation(census, "the dispatcher reading the platform configuration", () =>
            ReplaceInstruction(configurationKey, OpCodes.Ldstr, "PlatformAdmin"), CredentialOffenders);
        var source = census.Types[(InfrastructureAssembly, BoundarySource)];
        AssertMutation(census, "a missing production data-source subject", () =>
        {
            var module = source.Module;
            module.Types.Remove(source);
            return () => module.Types.Add(source);
        }, CredentialOffenders);
    }

    [Fact]
    public void Credential_registration_detector_rejects_missing_duplicate_wrong_key_and_non_singleton_sources()
    {
        IServiceCollection services = BoundaryRegistrations();
        RegistrationOffenders(services).Should().BeEmpty("Fix: the real production composition must be the clean control");
        var descriptor = services.Single(item => item.IsKeyedService && Equals(item.ServiceKey, OutboxDispatcherDataSource.Key)
            && item.ServiceType == typeof(NpgsqlDataSource));
        services.Remove(descriptor);
        RegistrationOffenders(services).Should().NotBeEmpty("Fix: a missing privileged source must fail");
        services.Add(descriptor);
        services.Add(descriptor);
        RegistrationOffenders(services).Should().NotBeEmpty("Fix: a duplicate privileged source must fail");
        services.Remove(descriptor);
        services.Remove(descriptor);
        var wrongKey = new ServiceCollection();
        wrongKey.AddKeyedSingleton<NpgsqlDataSource>("unrelated-key", (_, _) => throw new InvalidOperationException("Never resolved"));
        services.Add(wrongKey.Single());
        RegistrationOffenders(services).Should().NotBeEmpty("Fix: a renamed privileged source must fail");
        services.Remove(wrongKey.Single());
        var scoped = new ServiceCollection();
        scoped.AddKeyedScoped<NpgsqlDataSource>(OutboxDispatcherDataSource.Key, (_, _) => throw new InvalidOperationException("Never resolved"));
        services.Add(scoped.Single());
        RegistrationOffenders(services).Should().NotBeEmpty("Fix: a non-singleton privileged source must fail");
        services.Remove(scoped.Single());
        services.Add(descriptor);
        RegistrationOffenders(services).Should().BeEmpty("Fix: restoring the actual descriptors must restore the clean control");
    }

    [Fact]
    public void Publication_census_rejects_interface_concrete_delegate_async_and_unowned_attempt_paths()
    {
        using var census = ProductionCensus.Read();
        var port = census.Types[(KernelAssembly, BoundaryBus)].Methods.Single(method => method.Name == "PublishAsync");
        var concrete = census.Types[(InfrastructureAssembly, BoundaryTransport)].Methods.Single(method => method.Name == "PublishAsync");
        foreach (var assembly in new[] { BoundaryApi, "LearnStack.Application", "LearnStack.Tools.Seeder", "LearnStack.Modules.Tenancy.Application" })
        {
            var module = census.Modules.Single(item => item.Assembly.Name.Name == assembly);
            foreach (var target in new[] { port, concrete })
                foreach (var opcode in new[] { OpCodes.Call, OpCodes.Callvirt, OpCodes.Ldftn, OpCodes.Ldvirtftn })
                    AssertMutation(census, assembly + " publication through " + target.DeclaringType.Name + "/" + opcode,
                        () => AddCallProbe(module, "ExtraPublisher", target, opcode), PublicationOffenders);
            AssertMutation(census, assembly + " async publisher", () => AddAsyncCallProbe(module, "AsyncPublisher", port), PublicationOffenders);
        }
        var attempt = census.Types[(BoundaryApi, BoundaryAttempt)];
        var worker = census.Types[(BoundaryApi, BoundaryWorker)];
        var constructor = attempt.Methods.Single(method => method.IsConstructor && !method.IsStatic);
        foreach (var target in new[] { constructor, attempt.Methods.Single(method => method.Name == "PublishAsync"), port })
            AssertMutation(census, "an unrelated method on the permitted worker/attempt type", () =>
            {
                var owner = target == port ? attempt : worker;
                var method = AddCall(owner, "UnrelatedPublisher", target, target == constructor ? OpCodes.Newobj : OpCodes.Call);
                return () => owner.Methods.Remove(method);
            }, PublicationOffenders);
        AssertMutation(census, "a generated name impersonating the real publication owner", () =>
        {
            var generated = new TypeDefinition(string.Empty, "<PublishAsync>d__999", TypeAttributes.NestedPrivate, attempt.Module.TypeSystem.Object);
            attempt.NestedTypes.Add(generated);
            AddCall(generated, "MoveNext", port, OpCodes.Callvirt);
            return () => attempt.NestedTypes.Remove(generated);
        }, PublicationOffenders);
        var application = census.Modules.Single(module => module.Assembly.Name.Name == "LearnStack.Application");
        AssertMutation(census, "a second inherited event-bus implementation", () =>
        {
            var type = AddProbe(application, "InheritedEventBus");
            type.BaseType = application.ImportReference(concrete.DeclaringType);
            return () => application.Types.Remove(type);
        }, PublicationOffenders);
        AssertMutation(census, "an extra publish call inside the sanctioned method", () =>
        {
            var owner = census.Types.Values.SelectMany(type => type.Methods).Single(method => method.HasBody
                && method.Body.Instructions.Any(instruction => instruction.Operand is MethodReference target
                    && target.Name == "PublishAsync" && target.DeclaringType.FullName == BoundaryBus));
            var instruction = Instruction.Create(OpCodes.Callvirt, owner.Module.ImportReference(port));
            owner.Body.Instructions.Add(instruction);
            return () => owner.Body.Instructions.Remove(instruction);
        }, PublicationOffenders);
    }

    [Fact]
    public void Boundary_censuses_preserve_unkeyed_services_unrelated_publishers_and_both_real_keys()
    {
        using var census = ProductionCensus.Read();
        var module = census.Modules.Single(item => item.Assembly.Name.Name == "LearnStack.Application");
        var type = AddProbe(module, "CleanBoundaryHelper");
        var ordinaryResolver = typeof(ServiceProviderServiceExtensions).GetMethods().Single(method =>
            method.Name == "GetRequiredService" && method.IsGenericMethodDefinition && method.GetParameters().Length == 1);
        AddCall(type, "ResolveOrdinaryService", module.ImportReference(ordinaryResolver), OpCodes.Call);
        var unrelated = new MethodDefinition("PublishAsync", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
        unrelated.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        type.Methods.Add(unrelated);
        AddCall(type, "CallUnrelatedPublisher", unrelated, OpCodes.Call);
        try
        {
            CredentialOffenders(census).Should().BeEmpty("Fix: both distinct production keys and ordinary unkeyed resolution remain valid");
            PublicationOffenders(census).Should().BeEmpty("Fix: unrelated methods named PublishAsync are not event-bus publication");
        }
        finally { module.Types.Remove(type); }
    }

    private static Action ReplaceInstruction(Instruction instruction, OpCode opcode, object operand)
    {
        var originalCode = instruction.OpCode;
        var originalOperand = instruction.Operand;
        instruction.OpCode = opcode;
        instruction.Operand = operand;
        return () => { instruction.OpCode = originalCode; instruction.Operand = originalOperand; };
    }

    private static Action AddStringProbe(ModuleDefinition module, string name, string value)
    {
        var type = AddProbe(module, name);
        var method = new MethodDefinition("Run", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, value));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        type.Methods.Add(method);
        return () => module.Types.Remove(type);
    }

    private static ServiceCollection BoundaryRegistrations()
    {
        var services = new ServiceCollection();
        // Real composition, no provider build, pool construction or connection.
        services.AddLearnStackPersistence(new ConfigurationBuilder().Build());
        return services;
    }

    private static SortedSet<string> RegistrationOffenders(IEnumerable<ServiceDescriptor> services)
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        var expected = new[]
        {
            PlatformAdminScope.PlatformDataSourceKey + ":" + typeof(NpgsqlDataSource),
            PlatformAdminScope.PlatformDataSourceKey + ":" + typeof(Lazy<NpgsqlDataSource>),
            OutboxDispatcherDataSource.Key + ":" + typeof(NpgsqlDataSource),
            OutboxDispatcherDataSource.Key + ":" + typeof(Lazy<NpgsqlDataSource>),
        };
        var keyed = services.Where(descriptor => descriptor.IsKeyedService).ToArray();
        CheckSet(offenders, "keyed production registrations", keyed.Select(descriptor =>
            descriptor.ServiceKey + ":" + descriptor.ServiceType), expected);
        foreach (var descriptor in keyed)
            if (descriptor.Lifetime != ServiceLifetime.Singleton || descriptor.KeyedImplementationFactory is null)
                offenders.Add("Keyed source must be a container-owned singleton factory: " + descriptor.ServiceType);
        return offenders;
    }

    private static SortedSet<string> CredentialOffenders(ProductionCensus census)
    {
        var services = BoundaryRegistrations();
        var offenders = RegistrationOffenders(services);
        var types = census.Types;
        CheckSet(offenders, "production assemblies", census.Modules.Select(module => module.Assembly.Name.Name), ProductionAssemblies.Names());
        foreach (var subject in new[] { (BoundaryApi, BoundaryComposition), (BoundaryApi, BoundaryPersistence),
                     (BoundaryApi, BoundaryWorker), (InfrastructureAssembly, BoundaryPlatform),
                     (InfrastructureAssembly, BoundarySource), (InfrastructureAssembly, Store) })
            if (!types.ContainsKey(subject)) offenders.Add("Missing credential subject: " + subject.Item2);
        if (offenders.Count != 0) return offenders;

        var outboxRoot = types[(BoundaryApi, BoundaryComposition)].Methods.Single(method => method.Name == "AddLearnStackOutboxDispatcher");
        var platformRoot = types[(BoundaryApi, BoundaryPersistence)].Methods.Single(method => method.Name == "AddLearnStackPersistence");
        var roots = new[] { (Root: outboxRoot, Key: OutboxDispatcherDataSource.Key),
            (Root: platformRoot, Key: PlatformAdminScope.PlatformDataSourceKey) };
        var expectedResolvers = new List<string>();
        var expectedFactories = new List<string>();
        MethodDefinition? outboxBuildFactory = null;

        foreach (var (root, key) in roots)
        {
            var descriptors = services.Where(descriptor => descriptor.IsKeyedService && Equals(descriptor.ServiceKey, key)).ToArray();
            foreach (var descriptor in descriptors)
            {
                var factory = FindRuntimeMethod(census, descriptor.KeyedImplementationFactory!.Method);
                if (factory is null) { offenders.Add("Missing keyed factory: " + key); continue; }
                expectedFactories.Add(MethodId(root));
                if (!DelegateTargets(root).Any(target => MethodId(target) == MethodId(factory)))
                    offenders.Add(MethodId(root) + ": must directly register its actual factory " + MethodId(factory));
                if (descriptor.ServiceType == typeof(NpgsqlDataSource))
                {
                    if (key == OutboxDispatcherDataSource.Key) outboxBuildFactory = factory;
                    continue;
                }

                var leaves = DelegateTargets(factory).ToArray();
                if (leaves.Length != 1 || !types.TryGetValue(Key(leaves[0].DeclaringType), out var leafType))
                { offenders.Add(MethodId(factory) + ": requires one actual lazy delegate"); continue; }
                var leaf = leafType.Methods.SingleOrDefault(method => MethodId(method) == MethodId(leaves[0]));
                if (leaf is null) { offenders.Add("Missing lazy delegate: " + leaves[0].FullName); continue; }
                expectedResolvers.Add(MethodId(leaf));
                CheckCapturedKeyResolver(offenders, factory, leaf);
            }

            // Pin registration keys independently from callback keys. A changed key in
            // the in-memory IL cannot be hidden by the unmodified reflection descriptors.
            CheckSet(offenders, MethodId(root) + " registration keys", root.Body.Instructions
                .Where(instruction => instruction.Operand is string value
                    && (value == OutboxDispatcherDataSource.Key || value == PlatformAdminScope.PlatformDataSourceKey)
                    && instruction.Next?.Operand is not MethodReference { Name: "GetConnectionString" })
                .Select(instruction => (string)instruction.Operand), [key, key]);
            var keyCalls = MethodOperands(root).Where(IsKeyedRegistration).ToArray();
            CheckSet(offenders, MethodId(root) + " registration service types", keyCalls.Select(GenericService),
                [BoundaryDataSource, BoundaryLazySource]);
        }

        var storeDescriptor = services.Single(descriptor => !descriptor.IsKeyedService && descriptor.ServiceType == typeof(OutboxDispatchStore));
        var storeFactory = FindRuntimeMethod(census, storeDescriptor.ImplementationFactory!.Method);
        if (storeFactory is null) offenders.Add("Missing actual dispatcher-store factory");
        else
        {
            expectedResolvers.Add(MethodId(storeFactory));
            if (!DelegateTargets(outboxRoot).Any(target => MethodId(target) == MethodId(storeFactory)))
                offenders.Add("Dispatcher store factory must be registered by the outbox root");
            CheckLiteralKeyResolver(offenders, storeFactory, BoundaryLazySource, OutboxDispatcherDataSource.Key);
        }

        var sites = CallSites(census).ToArray();
        CheckSet(offenders, "keyed resolver callers", sites.Where(site => IsKeyedResolution(site.Target)).Select(site => site.Owner), expectedResolvers);
        CheckSet(offenders, "keyed registration callers", sites.Where(site => IsKeyedRegistration(site.Target)).Select(site => site.Owner), expectedFactories);
        CheckSet(offenders, "outbox pool construction", sites.Where(site => site.Target.DeclaringType.FullName == BoundarySource
            && site.Target.Name == "Build").Select(site => site.Owner), outboxBuildFactory is null ? [] : [MethodId(outboxBuildFactory)]);
        CheckSet(offenders, "outbox configuration validation", sites.Where(site => site.Target.DeclaringType.FullName == BoundarySource
            && site.Target.Name == "Validate").Select(site => site.Owner), [MethodId(outboxRoot)]);
        CheckSet(offenders, "connection-string reader census", sites.Where(site => IsConnectionStringRead(site.Target))
            .Select(site => site.Owner), [MethodId(outboxRoot), MethodId(platformRoot), MethodId(platformRoot)]);

        var injections = new List<string>();
        foreach (var method in types.Values.SelectMany(type => type.Methods))
        {
            foreach (var parameter in method.Parameters)
                foreach (var attribute in parameter.CustomAttributes.Where(attribute =>
                             attribute.AttributeType.FullName == "Microsoft.Extensions.DependencyInjection.FromKeyedServicesAttribute"))
                    injections.Add(MethodId(method) + ":" + parameter.ParameterType.FullName + ":"
                        + BoundaryAttributeValue(attribute.ConstructorArguments.FirstOrDefault()));
            if (!method.HasBody) continue;
            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.Operand is MethodReference call && IsConnectionStringRead(call))
                {
                    var key = instruction.Previous?.Operand as string;
                    var expected = method == outboxRoot ? new[] { "OutboxDispatcher" }
                        : method == platformRoot ? ["Default", "PlatformAdmin"] : [];
                    if (key is null || !expected.Contains(key, StringComparer.Ordinal))
                        offenders.Add(MethodId(method) + ": unauthorized connection-string read");
                }
                if (instruction.Operand is FieldReference field && field.DeclaringType.FullName == "Microsoft.Extensions.DependencyInjection.KeyedService"
                    && field.Name == "AnyKey") offenders.Add(MethodId(method) + ": AnyKey reopens both credentials");
                if (instruction.Operand is MethodReference anyKey && anyKey.DeclaringType.FullName == "Microsoft.Extensions.DependencyInjection.KeyedService"
                    && anyKey.Name == "get_AnyKey") offenders.Add(MethodId(method) + ": AnyKey reopens both credentials");
                if (instruction.Operand is string literal && (literal.Equals("OutboxDispatcher", StringComparison.OrdinalIgnoreCase)
                        || literal.Equals("ConnectionStrings:OutboxDispatcher", StringComparison.OrdinalIgnoreCase)
                        || literal.Equals("ConnectionStrings__OutboxDispatcher", StringComparison.OrdinalIgnoreCase)) && method != outboxRoot)
                    offenders.Add(MethodId(method) + ": dispatcher credential spelling outside its reader");
            }
        }
        var workerConstructor = types[(BoundaryApi, BoundaryWorker)].Methods.Single(method => method.IsConstructor && !method.IsStatic);
        var scopeConstructor = types[(InfrastructureAssembly, BoundaryPlatform)].Methods.Single(method => method.IsConstructor && !method.IsStatic);
        CheckSet(offenders, "keyed parameter bindings", injections,
            [MethodId(workerConstructor) + ":" + BoundaryLazySource + ":" + OutboxDispatcherDataSource.Key,
             MethodId(scopeConstructor) + ":" + BoundaryLazySource + ":" + PlatformAdminScope.PlatformDataSourceKey]);
        CheckSourceFieldAccess(offenders, census, types[(BoundaryApi, BoundaryWorker)], ["StartAsync"]);
        CheckSourceFieldAccess(offenders, census, types[(InfrastructureAssembly, Store)],
            ["AcquireAsync", "UpdateAsync", "TryTerminalizeAsync", "IsDurablyTerminalAsync", "ReadSnapshotAsync"]);
        if (storeFactory is not null) CheckStoreConsumers(offenders, census, outboxRoot, storeFactory);
        return offenders;
    }

    private static void CheckStoreConsumers(SortedSet<string> offenders, ProductionCensus census,
        MethodDefinition composition, MethodDefinition factory)
    {
        // A caller can obtain the same bypass through the unkeyed store facade.
        // Confine typed consumers, including generic locator calls and wrappers;
        // reflection and arbitrary runtime dataflow are not claimed by this census.
        var owners = OwnedBodies(census).Where(item => item.Owner.DeclaringType.FullName != Store
                && ReferencesStore(item.Body)).Select(item => MethodId(item.Owner))
            .Concat(census.Types.Values.SelectMany(type => type.Methods).Where(method => !method.HasBody
                && method.DeclaringType.FullName != Store && ReferencesStore(method)).Select(MethodId))
            .Distinct(StringComparer.Ordinal);
        var worker = census.Types[(BoundaryApi, BoundaryWorker)];
        var workerMethods = worker.Methods.Where(method => method.IsConstructor && !method.IsStatic
            || method.Name is "ExecuteAsync" or "DispatchAsync" or "DispatchNormalAsync" or "RefreshAsync");
        CheckSet(offenders, "privileged store consumers", owners,
            workerMethods.Select(MethodId).Concat([MethodId(composition), MethodId(factory)]));
        // Only an attribute-linked Store state machine may carry its own captured
        // instance. A similarly named nested type gains no authority from its name.
        var stateMachines = OwnedBodies(census).Where(item => item.Body.Name == "MoveNext" && item.Body != item.Owner
                && item.Owner.DeclaringType.FullName == Store).Select(item => Key(item.Body.DeclaringType)).ToHashSet();
        // Direct instance holders carry the capability. Generic wrappers (including
        // the compiler's cached registration delegate) remain covered at every typed
        // method/field-use site above, rather than receiving a generated-name exception.
        var fields = census.Types.Values.SelectMany(type => type.Fields).Where(field => field.FieldType.FullName == Store
            && !(stateMachines.Contains(Key(field.DeclaringType)) && field.Name == "<>4__this"
                && field.FieldType.FullName == Store)).ToArray();
        CheckSet(offenders, "privileged store fields", fields.Select(field => TypeId(field.DeclaringType) + ":" + field.Name),
            [TypeId(worker) + ":_store"]);
        foreach (var field in fields)
            if (!field.IsPrivate || !field.IsInitOnly) offenders.Add(field.FullName + ": store capability must remain private and readonly");
    }

    private static bool ReferencesStore(MethodDefinition method)
    {
        if (ContainsStore(method.ReturnType) || method.Parameters.Any(parameter => ContainsStore(parameter.ParameterType))) return true;
        if (!method.HasBody) return false;
        if (method.Body.Variables.Any(variable => ContainsStore(variable.VariableType))) return true;
        return method.Body.Instructions.Any(instruction => instruction.Operand switch
        {
            GenericInstanceMethod generic => generic.GenericArguments.Any(ContainsStore) || ContainsStore(generic.DeclaringType)
                || ContainsStore(generic.ReturnType) || generic.Parameters.Any(parameter => ContainsStore(parameter.ParameterType)),
            MethodReference call => ContainsStore(call.DeclaringType) || ContainsStore(call.ReturnType)
                || call.Parameters.Any(parameter => ContainsStore(parameter.ParameterType)),
            FieldReference field => ContainsStore(field.FieldType),
            TypeReference type => ContainsStore(type),
            _ => false,
        });
    }

    private static bool ContainsStore(TypeReference type) => type.FullName == Store
        || (type is GenericInstanceType generic && generic.GenericArguments.Any(ContainsStore))
        || (type is TypeSpecification specification && ContainsStore(specification.ElementType));

    private static void CheckSourceFieldAccess(SortedSet<string> offenders, ProductionCensus census,
        TypeDefinition type, string[] readers)
    {
        var fields = type.Fields.Where(field => field.Name == "_source").ToArray();
        if (fields.Length != 1 || !fields[0].IsPrivate || !fields[0].IsInitOnly || fields[0].IsStatic
            || fields[0].FieldType.FullName != BoundaryLazySource)
        { offenders.Add(TypeId(type) + ": requires its private readonly lazy source"); return; }
        // Close forwarding of an already injected capability as well as fresh keyed
        // resolution. Exact access counts also catch another use in an allowed method.
        var actual = OwnedBodies(census).SelectMany(item => item.Body.Body.Instructions
            .Where(instruction => instruction.Operand is FieldReference field && SameType(field.DeclaringType, type)
                && field.Name == fields[0].Name)
            .Select(instruction => MethodId(item.Owner) + ":" + instruction.OpCode.Name));
        var expected = type.Methods.Where(method => method.IsConstructor && !method.IsStatic)
            .Select(method => MethodId(method) + ":stfld")
            .Concat(type.Methods.Where(method => readers.Contains(method.Name, StringComparer.Ordinal))
                .Select(method => MethodId(method) + ":ldfld"));
        CheckSet(offenders, TypeId(type) + " privileged source field access", actual, expected);
    }

    private static void CheckCapturedKeyResolver(SortedSet<string> offenders, MethodDefinition factory, MethodDefinition leaf)
    {
        // Recognize this one compiler-emitted closure: (provider, key) =>
        // new Lazy<NpgsqlDataSource>(() => provider.GetRequiredKeyedService<NpgsqlDataSource>(key)).
        // No general dataflow interpreter or generated-name permission is involved.
        var calls = leaf.Body.Instructions.Where(instruction => instruction.Operand is MethodReference call && IsKeyedResolution(call)).ToArray();
        var call = calls.Length == 1 ? calls[0] : null;
        var keyField = call?.Previous?.Operand as FieldReference;
        var providerField = call?.Previous?.Previous?.Previous?.Operand as FieldReference;
        if (call?.Operand is not MethodReference target || target.Name != "GetRequiredKeyedService"
            || GenericService(target) != BoundaryDataSource || keyField is null || providerField is null
            || call.Previous!.OpCode != OpCodes.Ldfld || call.Previous.Previous!.OpCode != OpCodes.Ldarg_0
            || call.Previous.Previous.Previous!.OpCode != OpCodes.Ldfld
            || !CapturesParameter(factory, keyField, 1) || !CapturesParameter(factory, providerField, 0))
            offenders.Add(MethodId(leaf) + ": lazy resolver must forward its own registered key and provider unchanged");
    }

    private static bool CapturesParameter(MethodDefinition factory, FieldReference field, int parameter) =>
        factory.Body.Instructions.Count(instruction => instruction.OpCode == OpCodes.Stfld
            && instruction.Operand is FieldReference assigned && assigned.FullName == field.FullName
            && LoadsArgument(instruction.Previous, parameter + (factory.HasThis ? 1 : 0))) == 1;

    private static bool LoadsArgument(Instruction? instruction, int index) => instruction is not null && (index switch
    {
        0 => instruction.OpCode == OpCodes.Ldarg_0,
        1 => instruction.OpCode == OpCodes.Ldarg_1,
        2 => instruction.OpCode == OpCodes.Ldarg_2,
        3 => instruction.OpCode == OpCodes.Ldarg_3,
        _ => false,
    });

    private static void CheckLiteralKeyResolver(SortedSet<string> offenders, MethodDefinition owner, string service, string key)
    {
        var calls = owner.Body.Instructions.Where(instruction => instruction.Operand is MethodReference call && IsKeyedResolution(call)).ToArray();
        if (calls.Length != 1 || calls[0].Operand is not MethodReference target
            || target.Name != "GetRequiredKeyedService" || GenericService(target) != service
            || calls[0].Previous?.OpCode != OpCodes.Ldstr || !Equals(calls[0].Previous.Operand, key))
            offenders.Add(MethodId(owner) + ": requires exactly its declared keyed service");
    }

    private static bool IsKeyedResolution(MethodReference method) =>
        method.DeclaringType.Namespace.StartsWith("Microsoft.Extensions.DependencyInjection", StringComparison.Ordinal)
        && method.Name is "GetKeyedService" or "GetRequiredKeyedService" or "GetKeyedServices";

    private static bool IsConnectionStringRead(MethodReference method) =>
        method.DeclaringType.FullName == "Microsoft.Extensions.Configuration.ConfigurationExtensions" && method.Name == "GetConnectionString";

    private static object? BoundaryAttributeValue(CustomAttributeArgument argument, int depth = 0) => depth >= 8 ? null
        : argument.Value is CustomAttributeArgument nested ? BoundaryAttributeValue(nested, depth + 1) : argument.Value;

    private static bool IsKeyedRegistration(MethodReference method) =>
        method.DeclaringType.Namespace.StartsWith("Microsoft.Extensions.DependencyInjection", StringComparison.Ordinal)
        && (method.Name.StartsWith("AddKeyed", StringComparison.Ordinal) || method.Name.StartsWith("TryAddKeyed", StringComparison.Ordinal)
            || method.Name.StartsWith("KeyedSingleton", StringComparison.Ordinal) || method.Name.StartsWith("KeyedScoped", StringComparison.Ordinal)
            || method.Name.StartsWith("KeyedTransient", StringComparison.Ordinal)
            || method.Name == "DescribeKeyed"
            || (method.Name == ".ctor" && method.DeclaringType.Name == "ServiceDescriptor"
                && method.Parameters.Count >= 3 && method.Parameters[1].ParameterType.FullName == "System.Object"));

    private static string GenericService(MethodReference method) => method is GenericInstanceMethod generic
        && generic.GenericArguments.Count == 1 ? generic.GenericArguments[0].FullName : "non-generic:" + method.FullName;

    private static IEnumerable<MethodReference> MethodOperands(MethodDefinition method) => method.HasBody
        ? method.Body.Instructions.Select(instruction => instruction.Operand).OfType<MethodReference>() : [];

    private static IEnumerable<MethodReference> DelegateTargets(MethodDefinition method) => method.HasBody
        ? method.Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Ldftn || instruction.OpCode == OpCodes.Ldvirtftn)
            .Select(instruction => instruction.Operand).OfType<MethodReference>() : [];

    private static MethodDefinition? FindRuntimeMethod(ProductionCensus census, System.Reflection.MethodInfo method) =>
        census.Modules.Single(module => module.Assembly.Name.Name == method.Module.Assembly.GetName().Name)
            .GetTypes().SelectMany(type => type.Methods).SingleOrDefault(candidate => candidate.MetadataToken.ToInt32() == method.MetadataToken);

    private static SortedSet<string> PublicationOffenders(ProductionCensus census)
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        var types = census.Types;
        CheckSet(offenders, "production assemblies", census.Modules.Select(module => module.Assembly.Name.Name), ProductionAssemblies.Names());
        foreach (var subject in new[] { (KernelAssembly, BoundaryBus), (InfrastructureAssembly, BoundaryTransport),
                     (BoundaryApi, BoundaryWorker), (BoundaryApi, BoundaryAttempt) })
            if (!types.ContainsKey(subject)) offenders.Add("Missing publication subject: " + subject.Item2);
        if (offenders.Count != 0) return offenders;
        var port = types[(KernelAssembly, BoundaryBus)];
        var attempt = types[(BoundaryApi, BoundaryAttempt)];
        var worker = types[(BoundaryApi, BoundaryWorker)];
        CheckSet(offenders, "production event-bus implementations", types.Values.Where(type => !type.IsInterface
            && !type.IsAbstract && Derives(type, port, types)).Select(TypeId),
            [TypeId(types[(InfrastructureAssembly, BoundaryTransport)])]);
        var publishers = attempt.Methods.Where(method => method.Name == "PublishAsync").ToArray();
        var constructors = attempt.Methods.Where(method => method.IsConstructor && !method.IsStatic).ToArray();
        var dispatchers = worker.Methods.Where(method => method.Name == "DispatchNormalAsync").ToArray();
        if (publishers.Length != 1 || !publishers[0].IsPrivate || publishers[0].IsStatic
            || publishers[0].ReturnType.FullName != "System.Threading.Tasks.Task`1<System.Boolean>"
            || !ParametersMatch(publishers[0], ["Microsoft.Extensions.DependencyInjection.IServiceScopeFactory",
                "LearnStack.SharedKernel.Messaging.IntegrationEventEnvelope"]) || !attempt.IsSealed || attempt.IsPublic)
            offenders.Add(BoundaryAttempt + ": requires its internal sealed, private publication boundary");
        if (constructors.Length != 1 || dispatchers.Length != 1)
            offenders.Add(BoundaryAttempt + ": requires one constructor and one worker owner");
        if (offenders.Count != 0) return offenders;
        var sites = CallSites(census).ToArray();
        CheckSet(offenders, "event-bus publication callers", sites.Where(site => site.Target.Name.EndsWith("PublishAsync", StringComparison.Ordinal)
            && (SameType(site.Target.DeclaringType, port) || Derives(site.Target.DeclaringType, port, types)))
            .Select(site => site.Owner), [MethodId(publishers[0])]);
        CheckSet(offenders, "publication attempt constructors", sites.Where(site => site.Target.Name == ".ctor"
            && SameType(site.Target.DeclaringType, attempt)).Select(site => site.Owner), [MethodId(dispatchers[0])]);
        var callbacks = DelegateTargets(constructors[0]).ToArray();
        if (callbacks.Length != 1) offenders.Add(BoundaryAttempt + ": requires exactly one task-owned callback");
        CheckSet(offenders, "private publication callback callers", sites.Where(site => site.Target.Name == "PublishAsync"
            && SameType(site.Target.DeclaringType, attempt)).Select(site => site.Owner), callbacks.Select(MethodId));
        return offenders;
    }
}
