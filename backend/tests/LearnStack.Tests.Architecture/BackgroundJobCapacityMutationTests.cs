using FluentAssertions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed partial class OutboxDispatchConventionTests
{
    /// <summary>ADR-0047 capacity boundaries: planted escapes must fail the same production capability detector.</summary>
    [Fact]
    public void Background_job_capacity_census_rejects_forged_cleanup_recovery_and_checkpoint_owners()
    {
        using var census = ProductionCensus.Read();
        var application = census.Modules.Single(module => module.Assembly.Name.Name == DeliveryApplication);
        var capabilities = CapacityCapabilityKeys().SelectMany(item => census.Types[(item.Assembly, item.Type)].Methods
            .Where(method => item.Names.Contains(method.Name, StringComparer.Ordinal))).ToArray();
        capabilities.Should().NotBeEmpty("actual framework authority remains present with an empty business definition catalogue");
        foreach (var capability in capabilities)
        {
            foreach (var opcode in new[] { OpCodes.Call, OpCodes.Callvirt, OpCodes.Ldftn, OpCodes.Ldvirtftn })
                AssertMutation(census, "an extra capacity capability through " + opcode, () =>
                    AddCallProbe(application, "AdditionalCapacityOwner", capability, opcode), JobCapacityOffenders);
            AssertMutation(census, "an asynchronous capacity capability escape", () =>
                AddAsyncCallProbe(application, "AsyncCapacityOwner", capability), JobCapacityOffenders);
            AssertMutation(census, "an overload borrowing a closed capacity method name", () =>
            {
                var method = AddCall(capability.DeclaringType, capability.Name, capability, OpCodes.Call);
                return () => capability.DeclaringType.Methods.Remove(method);
            }, JobCapacityOffenders);
            AssertMutation(census, "a missing actual capacity method", () =>
            {
                var methods = capability.DeclaringType.Methods;
                var index = methods.IndexOf(capability);
                methods.Remove(capability);
                return () => methods.Insert(index, capability);
            }, JobCapacityOffenders);
        }

        var actualSites = OwnedBodies(census).SelectMany(item => item.Body.Body.Instructions)
            .Where(instruction => instruction.Operand is MethodReference target && capabilities.Any(capability =>
                TargetsDeclaredType(target, capability.DeclaringType) && target.Name == capability.Name)).ToArray();
        actualSites.Should().NotBeEmpty("the mutation controls start from real production authority calls");
        foreach (var site in actualSites)
        {
            AssertMutation(census, "a removed real capacity capability call", () =>
                ReplaceInstruction(site, OpCodes.Nop, null!), JobCapacityOffenders);
            AssertMutation(census, "a capacity capability captured by its approved caller", () =>
                ReplaceInstruction(site, OpCodes.Ldftn, site.Operand), JobCapacityOffenders);
        }

        foreach (var subject in CapacityCapabilityKeys().Select(item => (item.Assembly, item.Type)).Distinct()
                     .Concat([(DeliveryApplication, CapacityEvidence), (InfrastructureAssembly, CapacityHandler),
                         (InfrastructureAssembly, CapacityResult)]))
            AssertMutation(census, "a missing closed capacity subject", () =>
            {
                var type = census.Types[subject];
                var collection = type.IsNested ? type.DeclaringType.NestedTypes : type.Module.Types;
                var index = collection.IndexOf(type);
                collection.Remove(type);
                return () => collection.Insert(index, type);
            }, JobCapacityOffenders);

        foreach (var (assembly, name) in new[] { (InfrastructureAssembly, CapacityActualSource), (DeliveryApplication, CapacityRecoverySource) })
            AssertMutation(census, "an additional derived capacity minting source", () =>
            {
                var type = AddProbe(application, "AdditionalCapacityMint");
                type.BaseType = application.ImportReference(census.Types[(assembly, name)]);
                return () => application.Types.Remove(type);
            }, JobCapacityOffenders);

        foreach (var (assembly, name) in new[] { (InfrastructureAssembly, CapacityActual), (DeliveryApplication, CapacityRecovery),
                     (DeliveryApplication, CapacityCandidate) })
        {
            var type = census.Types[(assembly, name)];
            var constructor = type.Methods.Single(method => method.IsConstructor && !method.IsStatic);
            AssertMutation(census, "a publicly constructible capacity witness", () =>
                ChangeAttributes(() => constructor.Attributes, value => constructor.Attributes = value,
                    (constructor.Attributes & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Public), JobCapacityOffenders);
            AssertMutation(census, "an inheritable capacity witness", () =>
                ChangeAttributes(() => type.Attributes, value => type.Attributes = value, type.Attributes & ~TypeAttributes.Sealed), JobCapacityOffenders);
            foreach (var field in type.Fields.Where(field => !field.IsStatic))
                AssertMutation(census, "a mutable retained capacity fact", () =>
                    ChangeAttributes(() => field.Attributes, value => field.Attributes = value, field.Attributes & ~FieldAttributes.InitOnly), JobCapacityOffenders);
            var property = type.Properties.First();
            AssertMutation(census, "a substituted capacity witness fact type", () =>
                ChangeAttributes(() => property.PropertyType, value => property.PropertyType = value,
                    type.Module.TypeSystem.Object), JobCapacityOffenders);
            AssertMutation(census, "a writable capacity witness property", () =>
            {
                var setter = new MethodDefinition("set_" + property.Name, MethodAttributes.Public | MethodAttributes.SpecialName,
                    type.Module.TypeSystem.Void);
                setter.Parameters.Add(new ParameterDefinition(property.PropertyType));
                type.Methods.Add(setter);
                property.SetMethod = setter;
                return () => { property.SetMethod = null; type.Methods.Remove(setter); };
            }, JobCapacityOffenders);
        }
        var command = census.Types[(DeliveryApplication, CapacityCommand)];
        var acknowledgementSetter = command.Properties.Single(property => property.Name == "CommitAcknowledged").SetMethod!;
        AssertMutation(census, "a publicly writable checkpoint acknowledgement", () =>
            ChangeAttributes(() => acknowledgementSetter.Attributes, value => acknowledgementSetter.Attributes = value,
                (acknowledgementSetter.Attributes & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Public), JobCapacityOffenders);
        var evidence = census.Types[(DeliveryApplication, CapacityEvidence)];
        AssertMutation(census, "an invented cleanup evidence alternative", () =>
        {
            var field = new FieldDefinition("Unobserved", FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal,
                evidence)
            { Constant = 2 };
            evidence.Fields.Add(field);
            return () => evidence.Fields.Remove(field);
        }, JobCapacityOffenders);

        AssertEarlyCapacityCall(census, DeliveryCoordinator, "ExecuteFrameAsync", CapacityCommand, "RecordCommitted",
            "checkpoint acknowledgement before the actual common Complete");
        AssertEarlyCapacityCall(census, OrchestrationControl, "ProcessCapacityRequestAsync", CapacityOperation, "Start",
            "checkpoint task issuance before its original execution retains it");
        AssertEarlyCapacityCall(census, OrchestrationInvocation, "PrepareCapacityCleanupCoreAsync", CapacityActualSource, "MintCleanup",
            "cleanup mint before actual callback and observer joins");
        AssertEarlyCapacityCall(census, OrchestrationStore, "AcquireCapacityRecoveryAsync", CapacityRecoverySource, "ConfirmCandidate",
            "recovery authority before acquisition COMMIT ACK");
        AssertEarlyCapacityCall(census, OrchestrationStore, "ProbeCommitAsync", CapacityRecoverySource, "ConfirmCandidate",
            "historical recovery authority before actual projection, rollback and cleanup");
        var history = OwnedBodies(census).Single(item => item.Owner.DeclaringType.FullName == OrchestrationStore
            && item.Owner.Name == "ProbeCommitAsync" && item.Body.Name == "MoveNext").Body.Body;
        foreach (var (type, name) in new[]
        {
            ("database", "RollbackAsync"),
            ("LearnStack.Infrastructure.Jobs.BackgroundJobControlResources", "CleanupAsync"),
            ("LearnStack.Infrastructure.Jobs.BackgroundJobControlResources", "get_ProbeRollbackAcknowledged"),
            ("LearnStack.Infrastructure.Jobs.BackgroundJobControlResources", "get_CleanupConfirmed"),
            ("LearnStack.Infrastructure.Jobs.BackgroundJobControlResources", "get_Failure"),
        })
        {
            var calls = history.Instructions.Where(instruction => instruction.Operand is MethodReference target
                && (type == "database" ? IntegrationLifecycleCapability(target, census.Types) == "database." + name
                    : target.DeclaringType.FullName == type && target.Name == name)).ToArray();
            calls.Should().ContainSingle("each exact physical cleanup/confirmation guard is a live production subject");
            AssertMutation(census, "a missing actual historical recovery guard " + name, () =>
                ReplaceInstruction(calls[0], OpCodes.Nop, null!), JobCapacityOffenders);
        }

        var preparation = OwnedBodies(census).Single(item => item.Owner.DeclaringType.FullName == OrchestrationInvocation
            && item.Owner.Name == "PrepareCapacityCleanupCoreAsync" && item.Body.Name == "MoveNext").Body.Body;
        var waits = preparation.Instructions.Where(IsCapacityAwaitResult).ToArray();
        waits.Should().HaveCount(2, "actual execution observers and private cancellation callbacks are separate positive joins");
        foreach (var wait in waits)
            AssertMutation(census, "a removed actual precleanup await result", () =>
                ReplaceInstruction(wait, OpCodes.Nop, null!), JobCapacityOffenders);
        var unused = census.Types[(DeliveryApplication, JobCommitGate)].Methods.Single(method => method.Name == "TrySealUnusedExecution");
        var negativeState = unused.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Ldc_I4_M1);
        AssertMutation(census, "a no-start seal that leaves the unused-start state open", () =>
            ReplaceInstruction(negativeState, OpCodes.Ldc_I4_0, null!), JobCapacityOffenders);
        var businessEntry = census.Types[(DeliveryApplication, JobCommitGate)].Methods.Single(method => method.Name == "TryStartExecution");
        var consumedState = businessEntry.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Ldc_I4_1);
        AssertMutation(census, "a business entry that accepts the sealed unused-start state", () =>
            ReplaceInstruction(consumedState, OpCodes.Ldc_I4_M1, null!), JobCapacityOffenders);
        var callback = CapacityScheduleCallback(census, new(StringComparer.Ordinal))!;
        AssertMutation(census, "another checkpoint scheduler callback caller", () =>
            AddAsyncCallProbe(application, "AlternateCheckpointSchedule", callback), JobCapacityOffenders);

        AssertRecoveryMinterMutations(census);

        // Reading an already issued immutable fact creates no new authority.
        var clean = AddProbe(application, "CapacityReadOnlyConsumer");
        try
        {
            foreach (var type in new[] { census.Types[(InfrastructureAssembly, CapacityActual)], census.Types[(DeliveryApplication, CapacityRecovery)] })
                foreach (var property in type.Properties)
                    AddCall(clean, "Read" + type.Name + property.Name, property.GetMethod!, OpCodes.Call);
            EvaluateDetector(census, JobCapacityOffenders).Should().BeEmpty("read-only use of existing cleanup and recovery facts is allowed");
        }
        finally { application.Types.Remove(clean); }
        EvaluateDetector(census, JobCapacityOffenders).Should().BeEmpty("removing the clean probe preserves the actual production census");
    }

    private static void AssertEarlyCapacityCall(ProductionCensus census, string ownerType, string ownerName,
        string targetType, string targetName, string description)
    {
        var body = OwnedBodies(census).Single(item => item.Owner.DeclaringType.FullName == ownerType
            && item.Owner.Name == ownerName && item.Body.Name == "MoveNext").Body.Body;
        var call = body.Instructions.Single(instruction => instruction.Operand is MethodReference target
            && target.DeclaringType.FullName == targetType && target.Name == targetName);
        AssertMutation(census, description, () =>
        {
            var index = body.Instructions.IndexOf(call);
            body.Instructions.Remove(call);
            body.Instructions.Insert(0, call);
            return () => { body.Instructions.Remove(call); body.Instructions.Insert(index, call); };
        }, JobCapacityOffenders);
    }
}
