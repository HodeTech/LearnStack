using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed partial class OutboxDispatchConventionTests
{
    private const string JobBridgeClaim = "LearnStack.Infrastructure.Jobs.BackgroundJobBridgeClaim";
    private const string JobBridgeDeferral = "LearnStack.Infrastructure.Jobs.BackgroundJobBridgeDeferral";
    private const string JobBridgeSourceField = "<source>P";

    private static string CheckJobBridgeCredentials(SortedSet<string> offenders, ProductionCensus census)
    {
        if (!census.Types.TryGetValue((InfrastructureAssembly, JobBridgeStore), out var store)
            || !census.Types.TryGetValue((InfrastructureAssembly, JobBridge), out var bridge))
        {
            offenders.Add("Missing privileged bridge store or its independent bridge owner");
            return "missing:" + JobBridgeStore;
        }
        if (!store.IsPublic || !store.IsSealed || !bridge.IsPublic || !bridge.IsSealed)
            offenders.Add("The privileged bridge store and bridge owner must remain public sealed subjects");
        var constructor = RequireBoundaryMethod(offenders, store, ".ctor", "System.Void", MethodAttributes.Public,
            BoundaryGuardedSource, "LearnStack.Infrastructure.Jobs.BackgroundJobDefinitionRegistry",
            "LearnStack.Infrastructure.Jobs.BackgroundJobRuntimeOptions", "LearnStack.SharedKernel.Identifiers.IGuidFactory", JobRuntimeGate);
        var reader = RequireBoundaryMethod(offenders, store, "OpenConnectionAsync",
            "System.Threading.Tasks.ValueTask`1<" + JobBridgeStore + "/OwnedConnection>", MethodAttributes.Private, "System.Boolean", BoundaryCancellation);
        var claim = RequireBoundaryMethod(offenders, store, "TryClaimAsync", "System.Threading.Tasks.Task`1<" + JobBridgeClaim + ">",
            MethodAttributes.Assembly, "System.Collections.Generic.IReadOnlyCollection`1<LearnStack.SharedKernel.Jobs.BackgroundJobId>",
            BoundaryCancellation);
        var update = RequireBoundaryMethod(offenders, store, "UpdateAsync", BoundaryBooleanTask, MethodAttributes.Private,
            JobBridgeClaim, "System.String", "System.Nullable`1<" + JobBridgeDeferral + ">", BoundaryCancellation);
        var sites = CallSites(census).ToArray();
        CheckCleanupConnectionOwner(offenders, census, store, reader, [claim, update]);
        CheckBridgeCapturedField(offenders, census, store, JobBridgeSourceField, BoundaryGuardedSource,
            [constructor + ":stfld", reader + ":ldfld"]);
        CheckSet(offenders, "bridge captured guarded-source fields", store.Fields
            .Where(field => ContainsBridgeCapability(field.FieldType, BoundaryGuardedSource)).Select(field => field.Name),
            [JobBridgeSourceField]);

        var bridgeConstructor = RequireBoundaryMethod(offenders, bridge, ".ctor", "System.Void", MethodAttributes.Public,
            JobBridgeStore, "LearnStack.Infrastructure.Jobs.HangfireWakeupClient",
            "LearnStack.Infrastructure.Jobs.BackgroundJobRuntimeOptions", "System.TimeProvider", JobRuntimeGate);
        var quantum = RequireBoundaryMethod(offenders, bridge, "RunQuantumAsync", BoundaryBooleanTask,
            MethodAttributes.Public, BoundaryCancellation);
        var stop = RequireBoundaryMethod(offenders, bridge, "StopAsync", BoundaryTask, MethodAttributes.Public, BoundaryCancellation);
        var observe = RequireBoundaryMethod(offenders, bridge, "ObserveAsync", BoundaryTask, MethodAttributes.Private,
            JobBridge + "/BridgeOperation");
        var cleanup = RequireBoundaryMethod(offenders, bridge, "get_HasUnconfirmedCleanup", "System.Boolean", MethodAttributes.Assembly);
        var admit = RequireBoundaryMethod(offenders, bridge, "TryAdmitStart", "System.Boolean", MethodAttributes.Private);
        var preallocate = RuntimePreallocateId;
        CheckSet(offenders, "privileged bridge-store consumers", OwnedBodies(census)
            .Where(item => item.Owner.DeclaringType.FullName != JobBridgeStore && ReferencesCapability(item.Body, JobBridgeStore))
            .Select(item => MethodId(item.Owner)).Concat(census.Types.Values.SelectMany(type => type.Methods)
                .Where(method => !method.HasBody && method.DeclaringType.FullName != JobBridgeStore
                    && ReferencesCapability(method, JobBridgeStore)).Select(MethodId)).Distinct(StringComparer.Ordinal),
            new[] { bridgeConstructor, quantum, stop, observe, cleanup, admit, preallocate }
                .Concat(CleanupOwnerConsumerIds(census, JobBridgeStore)));
        CheckSet(offenders, "privileged bridge-store construction callers", sites.Where(site => SameType(site.Target.DeclaringType, store)
            && site.Target.Name == ".ctor").Select(site => site.Owner), [preallocate]);
        var stateMachines = OwnedBodies(census).Where(item => item.Body.Name == "MoveNext" && item.Body != item.Owner
                && (MethodId(item.Owner) == claim || MethodId(item.Owner) == update || MethodId(item.Owner) == reader))
            .Select(item => Key(item.Body.DeclaringType)).ToHashSet();
        var holders = census.Types.Values.SelectMany(type => type.Fields)
            .Where(field => ContainsBridgeCapability(field.FieldType, JobBridgeStore)
                && !(stateMachines.Contains(Key(field.DeclaringType)) && field.Name == "<>4__this"
                    && field.FieldType.FullName == JobBridgeStore)).ToArray();
        CheckSet(offenders, "privileged bridge-store holders", holders.Select(field => TypeId(field.DeclaringType) + ":" + field.Name),
            [TypeId(bridge) + ":<store>P", InfrastructureAssembly + ":" + JobBridgeStore + "/OwnedConnection:<owner>P"]);
        CheckBridgeCapturedField(offenders, census, bridge, "<store>P", JobBridgeStore,
            [bridgeConstructor + ":stfld", cleanup + ":ldfld", quantum + ":ldfld", quantum + ":ldfld", quantum + ":ldfld", stop + ":ldfld",
             observe + ":ldfld", observe + ":ldfld", observe + ":ldfld", admit + ":ldfld"]);
        return reader;
    }

    private static bool ContainsBridgeCapability(TypeReference type, string capability) => type.FullName == capability
        || type is GenericInstanceType generic && generic.GenericArguments.Any(argument => ContainsBridgeCapability(argument, capability))
        || type is TypeSpecification specification && ContainsBridgeCapability(specification.ElementType, capability);

    private static void CheckBridgeCapturedField(SortedSet<string> offenders, ProductionCensus census,
        TypeDefinition type, string name, string capability, string[] expected)
    {
        // C# primary-constructor captures are not readonly metadata. Their exact
        // write census confines mutation to construction without changing runtime.
        var fields = type.Fields.Where(field => field.Name == name).ToArray();
        if (fields.Length != 1 || !fields[0].IsPrivate || fields[0].IsStatic || fields[0].FieldType.FullName != capability)
            offenders.Add(TypeId(type) + ": requires its exact private instance capture " + name);
        CheckSet(offenders, TypeId(type) + " captured capability field access", census.FieldInstructions(name)
            .Where(item => SameType(item.Target.DeclaringType, type))
            .Select(item => item.OwnerId + ":" + item.Instruction.OpCode.Name), expected);
    }

    [Fact]
    public void Bridge_credential_census_rejects_extra_acquisition_forwarding_and_store_consumers()
    {
        using var census = ProductionCensus.Read();
        EvaluateDetector(census, CredentialOffenders).Should().BeEmpty("the actual bridge store and its bridge owner must be nonempty clean subjects");
        var store = census.Types[(InfrastructureAssembly, JobBridgeStore)];
        var bridge = census.Types[(InfrastructureAssembly, JobBridge)];
        var application = census.Modules.Single(module => module.Assembly.Name.Name == DeliveryApplication);
        var reader = store.Methods.Single(method => method.Name == "OpenConnectionAsync");
        var constructor = store.Methods.Single(method => method.IsConstructor);
        var claim = store.Methods.Single(method => method.Name == "TryClaimAsync");
        foreach (var target in new[] { reader, constructor, claim })
        {
            foreach (var opcode in new[] { OpCodes.Call, OpCodes.Callvirt, OpCodes.Ldftn, OpCodes.Ldvirtftn })
                AssertMutation(census, "an extra bridge capability consumer through " + opcode, () =>
                    AddCallProbe(application, "ExtraBridgeCapability", target, opcode), CredentialOffenders);
            AssertMutation(census, "an async bridge capability consumer", () =>
                AddAsyncCallProbe(application, "AsyncBridgeCapability", target), CredentialOffenders);
        }
        AssertMutation(census, "an extra bridge-store constructor call", () =>
            AddCallProbe(application, "ExtraBridgeConstruction", constructor, OpCodes.Newobj), CredentialOffenders);
        AssertMutation(census, "an extra acquisition helper on the sanctioned bridge store", () =>
        {
            var method = AddCall(store, "ExtraBridgeAcquisition", reader, OpCodes.Call);
            return () => store.Methods.Remove(method);
        }, CredentialOffenders);
        AssertMutation(census, "a second acquisition inside an approved bridge caller", () =>
        {
            var body = OwnedBodies(census).Single(item => item.Owner == claim && item.Body.Name == "MoveNext").Body;
            var instruction = Instruction.Create(OpCodes.Call, reader);
            body.Body.Instructions.Add(instruction);
            return () => body.Body.Instructions.Remove(instruction);
        }, CredentialOffenders);
        foreach (var name in new[] { "ForwardSource", "OpenConnectionAsync", "TryClaimAsync", "UpdateAsync" })
            AssertMutation(census, "a source-forwarding bridge helper or approved-name overload", () =>
                AddBoundaryFieldGetter(store, name, JobBridgeSourceField), CredentialOffenders);
        AssertMutation(census, "a bridge owner forwarding its store capability", () =>
            AddBoundaryFieldGetter(bridge, "ForwardStore", "<store>P"), CredentialOffenders);
        foreach (var opcode in new[] { OpCodes.Ldfld, OpCodes.Ldflda, OpCodes.Stfld })
            AssertMutation(census, "an extra source read, address or write inside the exact reader through " + opcode, () =>
            {
                var instruction = Instruction.Create(opcode, store.Fields.Single(field => field.Name == JobBridgeSourceField));
                reader.Body.Instructions.Add(instruction);
                return () => reader.Body.Instructions.Remove(instruction);
            }, CredentialOffenders);
        AssertMutation(census, "an unkeyed locator resolving the public bridge store", () =>
        {
            var resolve = typeof(ServiceProviderServiceExtensions).GetMethods().Single(method => method.Name == "GetRequiredService"
                && method.IsGenericMethodDefinition && method.GetParameters().Length == 1).MakeGenericMethod(typeof(PostgresBackgroundJobBridgeStore));
            return AddCallProbe(application, "LocatedBridgeStore", application.ImportReference(resolve), OpCodes.Call);
        }, CredentialOffenders);
        foreach (var wrapped in new[] { false, true })
            AssertMutation(census, "an extra direct or wrapped bridge-store holder", () =>
            {
                var type = AddProbe(application, "ExtraBridgeStoreHolder");
                TypeReference capability = application.ImportReference(store);
                if (wrapped)
                {
                    var wrapper = new GenericInstanceType(application.ImportReference(typeof(List<>)));
                    wrapper.GenericArguments.Add(capability);
                    capability = wrapper;
                }
                type.Fields.Add(new FieldDefinition("_store", FieldAttributes.Private, capability));
                return () => application.Types.Remove(type);
            }, CredentialOffenders);
    }

    [Fact]
    public void Bridge_credential_census_rejects_changed_signatures_captures_and_missing_subjects()
    {
        using var census = ProductionCensus.Read();
        var store = census.Types[(InfrastructureAssembly, JobBridgeStore)];
        var bridge = census.Types[(InfrastructureAssembly, JobBridge)];
        foreach (var method in store.Methods.Where(method => method.Name is ".ctor" or "OpenConnectionAsync" or "TryClaimAsync" or "UpdateAsync").ToArray())
        {
            AssertMutation(census, "changed visibility on a pinned bridge capability", () =>
                ChangeAttributes(() => method.Attributes, value => method.Attributes = value,
                    (method.Attributes & ~MethodAttributes.MemberAccessMask) | (method.IsPublic ? MethodAttributes.Private : MethodAttributes.Public)), CredentialOffenders);
            AssertMutation(census, "a static pinned bridge capability", () =>
                ChangeAttributes(() => method.Attributes, value => method.Attributes = value, method.Attributes | MethodAttributes.Static), CredentialOffenders);
            AssertMutation(census, "a changed pinned bridge return type", () =>
            {
                var original = method.ReturnType;
                method.ReturnType = method.Module.TypeSystem.Object;
                return () => method.ReturnType = original;
            }, CredentialOffenders);
            AssertMutation(census, "a changed pinned bridge parameter type", () =>
            {
                var parameter = method.Parameters[0];
                var original = parameter.ParameterType;
                parameter.ParameterType = method.Module.TypeSystem.Int32;
                return () => parameter.ParameterType = original;
            }, CredentialOffenders);
            AssertMutation(census, "a generic pinned bridge capability", () =>
            {
                var parameter = new GenericParameter("Unexpected", method);
                method.GenericParameters.Add(parameter);
                return () => method.GenericParameters.Remove(parameter);
            }, CredentialOffenders);
        }
        foreach (var (holder, name) in new[] { (store, JobBridgeSourceField), (bridge, "<store>P") })
        {
            var field = holder.Fields.Single(item => item.Name == name);
            AssertMutation(census, "a public captured bridge capability", () =>
                ChangeAttributes(() => field.Attributes, value => field.Attributes = value,
                    (field.Attributes & ~FieldAttributes.FieldAccessMask) | FieldAttributes.Public), CredentialOffenders);
            AssertMutation(census, "a static captured bridge capability", () =>
                ChangeAttributes(() => field.Attributes, value => field.Attributes = value, field.Attributes | FieldAttributes.Static), CredentialOffenders);
            AssertMutation(census, "a changed captured bridge capability type", () =>
            {
                var original = field.FieldType;
                field.FieldType = holder.Module.TypeSystem.Object;
                return () => field.FieldType = original;
            }, CredentialOffenders);
        }
        AssertMutation(census, "another guarded-source field on the bridge store", () =>
        {
            var field = new FieldDefinition("_extraSource", FieldAttributes.Private, store.Fields.Single(item => item.Name == JobBridgeSourceField).FieldType);
            store.Fields.Add(field);
            return () => store.Fields.Remove(field);
        }, CredentialOffenders);
        foreach (var subject in new[] { store, bridge })
        {
            AssertMutation(census, "an unsealed bridge capability subject", () =>
                ChangeAttributes(() => subject.Attributes, value => subject.Attributes = value, subject.Attributes & ~TypeAttributes.Sealed), CredentialOffenders);
            AssertMutation(census, "a missing real bridge capability subject", () =>
            {
                var module = subject.Module;
                module.Types.Remove(subject);
                return () => module.Types.Add(subject);
            }, CredentialOffenders);
        }
    }
}
