using FluentAssertions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed partial class OutboxDispatchConventionTests
{
    private const string DeliveryApplication = "LearnStack.Application";
    private const string DeliveryCoordinator = DeliveryApplication + ".Persistence.TransactionExecutionCoordinator";
    private const string DeliveryPrepared = DeliveryCoordinator + "/PreparedDeliveryOwner";
    private const string DeliveryParticipant = DeliveryApplication + ".Messaging.DeliveryCompletionParticipant";
    private const string DeliveryGate = DeliveryApplication + ".Messaging.DeliveryCommitGate";
    private const string DeliveryProducer = DeliveryApplication + ".Messaging.ProducerExecution";
    private const string DeliveryExecutorType = "LearnStack.Infrastructure.Messaging.DeliveryExecutor";
    private const string DeliveryCompletionType = "LearnStack.Infrastructure.Messaging.PostgresDeliveryCompletion";
    private const string DeliveryInbox = "LearnStack.SharedKernel.Messaging.IInboxGuard";
    private const string DeliveryInboxImplementation = "LearnStack.Infrastructure.Messaging.PostgresInboxGuard";

    [Fact]
    public void Delivery_owner_and_inbox_capabilities_are_closed()
    {
        using var census = ProductionCensus.Read();
        DeliveryOwnerOffenders(census).Should().BeEmpty(
            "Fix: delivery preparation, inbox claim, final fence and commit admission stay in the enumerated owner path");
    }

    [Fact]
    public void Delivery_owner_census_rejects_extra_direct_delegate_async_and_inherited_capabilities()
    {
        using var census = ProductionCensus.Read();
        var types = census.Types;
        var module = census.Modules.Single(item => item.Assembly.Name.Name == DeliveryApplication);
        var capabilities = new[]
        {
            types[(DeliveryApplication, DeliveryGate)].Methods.Single(method => method.Name == "TryBeginCommit"),
            types[(DeliveryApplication, DeliveryGate)].Methods.Single(method => method.Name == "RecordCommitted"),
            types[(DeliveryApplication, DeliveryGate)].Methods.Single(method => method.Name == "TryStartExecution"),
            types[(DeliveryApplication, DeliveryCoordinator)].Methods.Single(method => method.Name == "PrepareDeliveryOwnerAsync"),
            types[(DeliveryApplication, DeliveryPrepared)].Methods.Single(method => method.Name == "CreateAsync"),
            types[(DeliveryApplication, DeliveryPrepared)].Methods.Single(method => method.Name == "Consume"),
            types[(DeliveryApplication, DeliveryParticipant)].Methods.Single(method => method.Name == "FenceOwnerAsync"),
            types[(KernelAssembly, DeliveryInbox)].Methods.Single(method => method.Name == "TryClaimAsync"),
            types[(InfrastructureAssembly, DeliveryInboxImplementation)].Methods.Single(method => method.Name == "TryClaimAsync"),
        };
        foreach (var capability in capabilities)
        {
            foreach (var opcode in new[] { OpCodes.Call, OpCodes.Callvirt, OpCodes.Ldftn, OpCodes.Ldvirtftn })
                AssertMutation(census, "an extra delivery capability through " + opcode.Name,
                    () => AddCallProbe(module, "DeliveryCapabilityProbe", capability, opcode), DeliveryOwnerOffenders);
            AssertMutation(census, "an extra asynchronous delivery capability",
                () => AddAsyncCallProbe(module, "AsyncDeliveryCapabilityProbe", capability), DeliveryOwnerOffenders);
        }

        var participant = types[(DeliveryApplication, DeliveryParticipant)];
        AssertMutation(census, "an additional inherited completion participant", () =>
        {
            var parent = AddProbe(module, "AdditionalCompletionBase");
            parent.Attributes |= TypeAttributes.Abstract;
            parent.BaseType = module.ImportReference(participant);
            var child = AddProbe(module, "AdditionalCompletionImplementation");
            child.BaseType = parent;
            return () => { module.Types.Remove(child); module.Types.Remove(parent); };
        }, DeliveryOwnerOffenders);
        var owner = types[(DeliveryApplication, DeliveryPrepared)];
        var constructor = owner.Methods.Single(method => method.IsConstructor && !method.IsStatic);
        AssertMutation(census, "a public prepared owner constructor", () =>
            ChangeAttributes(() => constructor.Attributes, value => constructor.Attributes = value,
                (constructor.Attributes & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Public), DeliveryOwnerOffenders);
        AssertMutation(census, "a direct prepared owner constructor caller", () =>
            AddCallProbe(module, "PreparedOwnerProbe", constructor, OpCodes.Newobj), DeliveryOwnerOffenders);
        AssertMutation(census, "a missing completion participant subject", () =>
        {
            var completion = types[(InfrastructureAssembly, DeliveryCompletionType)];
            var completionModule = completion.Module;
            completionModule.Types.Remove(completion);
            return () => completionModule.Types.Add(completion);
        }, DeliveryOwnerOffenders);

        var clean = AddProbe(module, "CleanDeliveryObserver");
        AddCall(clean, "Observe", types[(DeliveryApplication, DeliveryGate)].Properties
            .Single(property => property.Name == "CommitAcknowledged").GetMethod, OpCodes.Callvirt);
        try
        {
            DeliveryOwnerOffenders(census).Should().BeEmpty("observation does not confer commit authority");
        }
        finally { module.Types.Remove(clean); }
    }

    private static SortedSet<string> DeliveryOwnerOffenders(ProductionCensus census)
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        var types = census.Types;
        CheckSet(offenders, "delivery production assemblies", census.Modules.Select(module => module.Assembly.Name.Name),
            ProductionAssemblies.Names());
        var subjects = new[]
        {
            (DeliveryApplication, DeliveryCoordinator), (DeliveryApplication, DeliveryPrepared),
            (DeliveryApplication, DeliveryParticipant), (DeliveryApplication, DeliveryGate),
            (DeliveryApplication, DeliveryProducer), (InfrastructureAssembly, DeliveryExecutorType),
            (InfrastructureAssembly, DeliveryCompletionType), (KernelAssembly, DeliveryInbox),
            (InfrastructureAssembly, DeliveryInboxImplementation),
        };
        foreach (var subject in subjects)
            if (!types.ContainsKey(subject)) offenders.Add("Missing delivery subject: " + subject.Item2);
        if (offenders.Count != 0) return offenders;
        var coordinator = types[(DeliveryApplication, DeliveryCoordinator)];
        var prepared = types[(DeliveryApplication, DeliveryPrepared)];
        var participant = types[(DeliveryApplication, DeliveryParticipant)];
        var gate = types[(DeliveryApplication, DeliveryGate)];
        var executor = types[(InfrastructureAssembly, DeliveryExecutorType)];
        var completion = types[(InfrastructureAssembly, DeliveryCompletionType)];
        var inbox = types[(KernelAssembly, DeliveryInbox)];
        var inboxImplementation = types[(InfrastructureAssembly, DeliveryInboxImplementation)];
        if (!prepared.IsSealed || prepared.Methods.Count(method => method.IsConstructor && !method.IsStatic) != 1
            || prepared.Methods.Any(method => method.IsConstructor && !method.IsStatic && !method.IsPrivate)
            || prepared.Methods.Any(method => method.IsPublic && method.Name is "CompleteAsync" or "CommitAsync")
            || prepared.Fields.Count(field => field.IsInitOnly && !field.IsStatic) < 4)
            offenders.Add("Prepared owner must retain its private construction and bound immutable frame identity");
        if (!participant.IsAbstract || participant.IsSealed || !completion.IsSealed)
            offenders.Add("Completion must use the closed abstract seam and sealed concrete participant");
        if (!gate.IsSealed || gate.Properties.Any(property => property.SetMethod is not null)
            || gate.Methods.Any(method => method.IsPublic && method.Name is "TryBeginCommit" or "RecordCommitted"))
            offenders.Add("Commit admission/acknowledgement must not be public capabilities");
        CheckSet(offenders, "delivery completion implementations",
            types.Values.Where(type => Derives(type, participant, types)).Select(TypeId), [TypeId(completion)]);
        CheckSet(offenders, "inbox implementations",
            types.Values.Where(type => !type.IsInterface && Derives(type, inbox, types)).Select(TypeId), [TypeId(inboxImplementation)]);

        var sites = CallSites(census).ToArray();
        CheckCapability(prepared, ".ctor", prepared, "CreateAsync");
        CheckCapability(prepared, "CreateAsync", coordinator, "PrepareDeliveryOwnerAsync");
        CheckCapability(prepared, "Consume", coordinator, "ExecutePreparedDeliveryAsync");
        CheckCapability(coordinator, "PrepareDeliveryOwnerAsync", executor, "ExecuteOwnedAsync");
        CheckCapability(gate, "TryStartExecution", executor, "ExecuteAsync");
        CheckCapability(gate, "TryBeginCommit", coordinator, "ExecuteFrameAsync");
        CheckCapability(gate, "RecordCommitted", coordinator, "ExecuteFrameAsync");
        CheckCapability(participant, "FenceOwnerAsync", coordinator, "ExecuteFrameAsync");
        CheckCapability(participant, "FenceAsync", participant, "FenceOwnerAsync");
        var inboxSites = sites.Where(site => site.Target.Name == "TryClaimAsync"
            && (SameType(site.Target.DeclaringType, inbox) || Derives(site.Target.DeclaringType, inbox, types)));
        CheckSet(offenders, "transport inbox claim sites", inboxSites.Select(site => site.Owner),
            executor.Methods.Where(method => method.Name == "ExecuteOwnedAsync").Select(MethodId));
        CheckCapability(types[(DeliveryApplication, DeliveryProducer)], "BeginInvocation", coordinator, "ExecuteFrameAsync");
        return offenders;

        void CheckCapability(TypeDefinition targetType, string targetName, TypeDefinition ownerType, string ownerName)
        {
            var declarations = targetType.Methods.Where(method => method.Name == targetName).ToArray();
            var owners = ownerType.Methods.Where(method => method.Name == ownerName).ToArray();
            if (declarations.Length != 1 || owners.Length != 1)
                offenders.Add(targetType.FullName + "." + targetName + ": capability and owner must each have one declaration");
            CheckSet(offenders, targetType.FullName + "." + targetName + " sites", sites
                .Where(site => site.Target.Name == targetName && SameType(site.Target.DeclaringType, targetType))
                .Select(site => site.Owner), owners.Select(MethodId));
        }
    }
}
