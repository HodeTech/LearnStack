using FluentAssertions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed partial class OutboxDispatchConventionTests
{
    private const string RecipientClaim = "LearnStack.Application.Jobs.BackgroundJobRecipientClaim";
    private const string RecipientClaimSource = RecipientClaim + "/MintingSource";
    private const string RecipientStore = "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobControlStore";
    private const string RecipientMinter = RecipientStore + "/RecipientClaims";
    private const string RecipientPostgres = "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobRecipientCompletion";
    private const string RecipientHandler = "LearnStack.Infrastructure.Jobs.SubmitBackgroundJobRecipientCommandHandler";
    private const string RecipientProjection = "LearnStack.SharedKernel.Jobs.BackgroundJobLifecycleProjection";
    private const string RecipientOccurrence = "LearnStack.SharedKernel.Jobs.BackgroundJobOccurrenceParameters";
    private const string RecipientId = "LearnStack.SharedKernel.Jobs.BackgroundJobRecipientId";
    private const string RecipientChild = "LearnStack.SharedKernel.Jobs.BackgroundJobId";
    private const string RecipientRow = "System.Data.Common.DbDataReader";
    private const string RecipientSubmission = "LearnStack.Infrastructure.Jobs.JobRecipientSubmissionResult";
    private const string RecipientInsertAwaiter = "System.Runtime.CompilerServices.ConfiguredTaskAwaitable`1/ConfiguredTaskAwaiter<System.Int32>";
    private static readonly string[] RecipientRoutePredicates =
    [
        "id=@recipient", "tenant_id=@tenant", "organization_id IS NOT DISTINCT FROM @organization",
        "occurrence_id=@occurrence", "recurrence_key=@key", "revision=@revision", "manifest_hash=@hash",
        "target_definition_key=@target", "recipient_policy=@policy", "scheduled_at=@slot", "due_at=@due",
    ];

    /// <summary>ADR-0047: the actual recipient row grants one closed submission on the existing request owner.</summary>
    [Fact]
    public void BackgroundJob_Fanout_Uses_Recipient_Bound_Tenant_Admission()
    {
        using var census = ProductionCensus.Read();
        EvaluateDetector(census, RecipientOffenders).Should().BeEmpty(
            "Fix: keep recipient authority on the exact actual-row mint, framework owner and reciprocal receipt path");
    }

    /// <summary>ADR-0047: private fields and exact writers must distinguish the real owner from namesakes.</summary>
    [Theory]
    [InlineData("copy_visibility")]
    [InlineData("copy_signature")]
    [InlineData("off_type_writer")]
    [InlineData("state_opcode")]
    [InlineData("claim_visibility")]
    public void Background_job_recipient_field_and_record_controls_detect_the_exact_owner_and_opcode(string mutation)
    {
        using var census = ProductionCensus.Read();
        var types = census.Types;
        var participant = types[(DeliveryApplication, JobRecipientParticipant)];
        var claim = types[(DeliveryApplication, RecipientClaim)];
        var occurrence = types[(KernelAssembly, RecipientOccurrence)];
        var copy = occurrence.Methods.Single(method => method.IsConstructor && !method.IsStatic
            && ParametersMatch(method, [RecipientOccurrence]));
        AssertMutation(census, mutation, () =>
        {
            if (mutation is "copy_visibility" or "claim_visibility")
            {
                if (mutation == "copy_visibility")
                    return ChangeAttributes(() => copy.Attributes, value => copy.Attributes = value,
                        (copy.Attributes & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Public);
                var field = claim.Fields.Single(field => field.Name == "_entered");
                var original = field.Attributes;
                field.Attributes = (original & ~FieldAttributes.FieldAccessMask) | FieldAttributes.Public;
                return () => field.Attributes = original;
            }
            if (mutation == "copy_signature")
            {
                var parameter = copy.Parameters[0];
                var original = parameter.ParameterType;
                parameter.ParameterType = occurrence.Module.TypeSystem.Int32;
                return () => parameter.ParameterType = original;
            }
            if (mutation == "state_opcode")
            {
                var state = participant.Methods.Single(method => method.Name == "MarkCommitting").Body.Instructions
                    .Single(instruction => instruction.Operand is FieldReference field && field.Name == "_budgetState");
                return ReplaceInstruction(state, OpCodes.Stfld, state.Operand);
            }
            var writer = participant.Methods.Single(method => method.Name == "RecordChild");
            var write = writer.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Stfld
                && instruction.Operand is FieldReference field && field.Name == "_child");
            var index = writer.Body.Instructions.IndexOf(write);
            var helper = new TypeDefinition("", "NamesakeRecipientWriter", TypeAttributes.NestedPrivate | TypeAttributes.Class,
                participant.Module.TypeSystem.Object);
            participant.NestedTypes.Add(helper);
            var namesake = new MethodDefinition("RecordChild", MethodAttributes.Private | MethodAttributes.Static, participant.Module.TypeSystem.Void);
            helper.Methods.Add(namesake);
            writer.Body.Instructions.Remove(write);
            namesake.Body.Instructions.Add(write);
            namesake.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            return () => { participant.NestedTypes.Remove(helper); writer.Body.Instructions.Insert(index, write); };
        }, RecipientOffenders);
    }

    /// <summary>ADR-0057: only an acknowledged INSERT records produced work; SQL failure poisons its owner.</summary>
    [Theory]
    [InlineData("receipt_before_ack")]
    [InlineData("receipt_in_catch")]
    [InlineData("missing_receipt")]
    [InlineData("missing_rollback")]
    [InlineData("unprotected_wire")]
    [InlineData("unprotected_ack")]
    [InlineData("receipt_delegated")]
    [InlineData("wire_delegated")]
    [InlineData("ack_delegated")]
    [InlineData("rollback_delegated")]
    public void Background_job_recipient_shared_insert_controls_preserve_successful_production(string mutation)
    {
        using var census = ProductionCensus.Read();
        var body = OwnedBodies(census).Single(item => item.Owner.DeclaringType.FullName == JobScheduler
            && item.Owner.Name == "InsertAsync" && item.Body.Name == "MoveNext").Body.Body;
        var receipt = body.Instructions.Single(instruction => instruction.Operand is MethodReference target
            && target.DeclaringType.FullName == DeliveryProducer && target.Name == "RecordProducedWork"
            && target.Parameters.Count == 0);
        var rollback = body.Instructions.Single(instruction => instruction.Operand is MethodReference target
            && target.DeclaringType.FullName == LifecycleUnit && target.Name == "MarkRollbackOnly");
        var wire = body.Instructions.Single(instruction => instruction.Operand is MethodReference target
            && target.DeclaringType.FullName == "System.Data.Common.DbCommand" && target.Name == "ExecuteNonQueryAsync");
        var ack = body.Instructions.Single(instruction => instruction.Operand is MethodReference target
            && target.DeclaringType.FullName == RecipientInsertAwaiter && target.Name == "GetResult");
        AssertMutation(census, mutation, () =>
        {
            if (mutation == "missing_receipt") return ReplaceInstruction(receipt, OpCodes.Nop, null!);
            if (mutation == "missing_rollback") return ReplaceInstruction(rollback, OpCodes.Nop, null!);
            if (mutation.EndsWith("_delegated", StringComparison.Ordinal))
            {
                var instruction = mutation switch
                {
                    "receipt_delegated" => receipt,
                    "wire_delegated" => wire,
                    "ack_delegated" => ack,
                    _ => rollback,
                };
                return ReplaceInstruction(instruction, OpCodes.Ldftn, instruction.Operand);
            }
            if (mutation is "unprotected_wire" or "unprotected_ack")
            {
                var handler = body.ExceptionHandlers.Single(handler => handler.HandlerType == ExceptionHandlerType.Catch
                    && body.Instructions.IndexOf(handler.HandlerStart) <= body.Instructions.IndexOf(rollback)
                    && body.Instructions.IndexOf(handler.HandlerEnd) > body.Instructions.IndexOf(rollback));
                var originalStart = handler.TryStart;
                var originalEnd = handler.TryEnd;
                if (mutation == "unprotected_wire") handler.TryStart = wire.Next;
                else handler.TryEnd = ack;
                return () => { handler.TryStart = originalStart; handler.TryEnd = originalEnd; };
            }
            var index = body.Instructions.IndexOf(receipt);
            var destination = mutation == "receipt_in_catch" ? rollback : ack;
            body.Instructions.Remove(receipt);
            body.Instructions.Insert(body.Instructions.IndexOf(destination), receipt);
            return () => { body.Instructions.Remove(receipt); body.Instructions.Insert(index, receipt); };
        }, RecipientOffenders);
    }

    /// <summary>ADR-0047: history needs original cleanup, a real row lock and the exact acquisition fence.</summary>
    [Theory]
    [InlineData("cleanup")]
    [InlineData("lock")]
    [InlineData("token")]
    [InlineData("route")]
    public void Background_job_recipient_history_controls_reject_detached_cleanup_and_unfenced_status(string mutation)
    {
        using var census = ProductionCensus.Read();
        var methods = OwnedBodies(census).Where(item => item.Owner.DeclaringType.FullName == RecipientStore
            && item.Owner.Name is "ResolveRecipientAsync" or "ResolveLockedRecipientAsync").ToArray();
        AssertMutation(census, mutation, () =>
        {
            if (mutation == "cleanup")
            {
                var getter = methods.SelectMany(item => item.Body.Body.Instructions).Single(instruction =>
                    instruction.Operand is MethodReference target && target.DeclaringType.FullName == RecipientSubmission
                    && target.Name == "get_CleanupFailed");
                return ReplaceInstruction(getter, OpCodes.Nop, null!);
            }
            var sql = methods.SelectMany(item => item.Body.Body.Instructions).Single(instruction =>
                instruction.OpCode == OpCodes.Ldstr && instruction.Operand is string value && (mutation switch
                {
                    "lock" => value.EndsWith("FOR UPDATE SKIP LOCKED", StringComparison.Ordinal),
                    "token" => value.StartsWith("id=@recipient", StringComparison.Ordinal) && value.Contains("ownership_token=@token", StringComparison.Ordinal),
                    _ => value.StartsWith("SELECT accepted_job_id,skipped_at", StringComparison.Ordinal),
                }));
            var removed = mutation switch { "lock" => "FOR UPDATE SKIP LOCKED", "token" => "ownership_token=@token", _ => "manifest_hash=@hash" };
            return ReplaceInstruction(sql, OpCodes.Ldstr, ((string)sql.Operand).Replace(removed, "TRUE", StringComparison.Ordinal));
        }, RecipientOffenders);
    }

    public sealed class JobRecipientMutationTests
    {

        /// <summary>ADR-0047: the production detector rejects added, removed, delegated and overloaded authority.</summary>
        [Fact]
        public void Background_job_recipient_census_detects_extra_authority_and_missing_real_fences()
        {
            using var census = ProductionCensus.Read();
            EvaluateDetector(census, RecipientOffenders).Should().BeEmpty("dirty controls require the actual production path to be clean first");
            var application = census.Modules.Single(module => module.Assembly.Name.Name == DeliveryApplication);
            var capabilities = RecipientCapabilityMethods(census).ToArray();
            capabilities.Should().NotBeEmpty();
            foreach (var capability in capabilities)
            {
                foreach (var opcode in new[] { OpCodes.Call, OpCodes.Callvirt, OpCodes.Ldftn, OpCodes.Ldvirtftn })
                    AssertMutation(census, "an off-owner recipient capability through " + opcode, () =>
                        AddCallProbe(application, "AdditionalRecipientAuthority", capability, opcode), RecipientOffenders);
                AssertMutation(census, "an asynchronous recipient capability escape", () =>
                    AddAsyncCallProbe(application, "AsyncRecipientAuthority", capability), RecipientOffenders);
                AssertMutation(census, "an overloaded recipient capability", () =>
                {
                    var extra = AddCall(capability.DeclaringType, capability.Name, capability, OpCodes.Call);
                    return () => capability.DeclaringType.Methods.Remove(extra);
                }, RecipientOffenders);
            }
            var actualSites = OwnedBodies(census).SelectMany(item => item.Body.Body.Instructions)
                .Where(instruction => instruction.Operand is MethodReference target && capabilities.Any(capability =>
                    MethodId(target) == MethodId(capability))).ToArray();
            actualSites.Should().NotBeEmpty("the detector must inspect real production construction and ownership");
            foreach (var site in actualSites)
            {
                var target = ((MethodReference)site.Operand).FullName;
                AssertMutation(census, "a removed actual recipient boundary " + target, () =>
                    ReplaceInstruction(site, OpCodes.Nop, null!), RecipientOffenders);
                AssertMutation(census, "a delegated actual recipient boundary " + target, () =>
                    ReplaceInstruction(site, OpCodes.Ldftn, site.Operand), RecipientOffenders);
            }
            foreach (var (assembly, name) in RecipientSubjects())
                AssertMutation(census, "a removed recipient subject " + name, () =>
                {
                    var type = census.Types[(assembly, name)];
                    var siblings = type.IsNested ? type.DeclaringType.NestedTypes : type.Module.Types;
                    var index = siblings.IndexOf(type);
                    siblings.Remove(type);
                    return () => siblings.Insert(index, type);
                }, RecipientOffenders);
            foreach (var parent in new[] { RecipientClaimSource, JobRecipientParticipant })
                AssertMutation(census, "a second recipient authority implementation", () =>
                {
                    var probe = AddProbe(application, "ExtraRecipientImplementation");
                    probe.BaseType = application.ImportReference(census.Types[(DeliveryApplication, parent)]);
                    return () => application.Types.Remove(probe);
                }, RecipientOffenders);
            var claim = census.Types[(DeliveryApplication, RecipientClaim)];
            var constructor = claim.Methods.Single(method => method.IsConstructor && !method.IsStatic);
            AssertMutation(census, "a publicly forgeable recipient witness", () =>
                ChangeAttributes(() => constructor.Attributes, value => constructor.Attributes = value,
                    (constructor.Attributes & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Public), RecipientOffenders);
            var coordinator = OwnedBodies(census).Single(item => item.Owner.DeclaringType.FullName == DeliveryCoordinator
                && item.Owner.Name == "ExecuteFrameAsync" && item.Body.Name == "MoveNext").Body.Body;
            var ack = coordinator.Instructions.Single(instruction => instruction.Operand is MethodReference target
                && target.DeclaringType.FullName == JobRecipientParticipant && target.Name == "RecordCommitted");
            AssertMutation(census, "recipient acknowledgement before owned COMMIT", () =>
            {
                var index = coordinator.Instructions.IndexOf(ack);
                coordinator.Instructions.Remove(ack);
                coordinator.Instructions.Insert(0, ack);
                return () => { coordinator.Instructions.Remove(ack); coordinator.Instructions.Insert(index, ack); };
            }, RecipientOffenders);
            var providerBodies = OwnedBodies(census).Where(item => item.Owner.DeclaringType.FullName == RecipientPostgres).ToArray();
            var routeSql = providerBodies.SelectMany(item => item.Body.Body.Instructions).Where(instruction =>
                instruction.OpCode == OpCodes.Ldstr && instruction.Operand is string sql && sql.Contains("occurrence_id=@occurrence", StringComparison.Ordinal)).ToArray();
            routeSql.Should().HaveCount(3, "the actual header, budget and receipt UPDATE each carry the full immutable route");
            foreach (var sql in routeSql)
                foreach (var predicate in RecipientRoutePredicates)
                    AssertMutation(census, "an app-role recipient fence without " + predicate, () =>
                        ReplaceInstruction(sql, OpCodes.Ldstr, ((string)sql.Operand).Replace(predicate, "TRUE", StringComparison.Ordinal)), RecipientOffenders);
            var budgetBody = providerBodies.Single(item => item.Owner.Name == "FenceReceiptAsync" && item.Body.Name == "MoveNext").Body.Body;
            var budgetBegin = budgetBody.Instructions.Single(instruction => instruction.Operand is MethodReference target
                && target.DeclaringType.FullName == JobRecipientParticipant && target.Name == "BeginCommitBudgetRead");
            var budgetQuery = budgetBody.Instructions.Single(instruction => instruction.Operand is MethodReference target
                && target.DeclaringType.FullName == "Npgsql.NpgsqlCommand" && target.Name == "ExecuteReaderAsync");
            AssertMutation(census, "a recipient budget started after the actual wire observation", () =>
                MoveBudgetInstructionAfter(budgetBody, budgetBegin, budgetQuery), RecipientOffenders);
            var clean = AddProbe(application, "CleanRecipientObservation");
            foreach (var getter in claim.Methods.Where(method => method.IsGetter && method.IsPublic))
                AddCall(clean, "Observe" + getter.Name, getter, OpCodes.Callvirt);
            try { EvaluateDetector(census, RecipientOffenders).Should().BeEmpty("observing immutable routing facts grants no authority"); }
            finally { application.Types.Remove(clean); }
        }

    }

    private static (string Assembly, string Type)[] RecipientSubjects() =>
    [
        (DeliveryApplication, RecipientClaim), (DeliveryApplication, RecipientClaimSource),
        (DeliveryApplication, JobRecipientRequest), (DeliveryApplication, JobRecipientParticipant),
        (DeliveryApplication, JobAdmission), (DeliveryApplication, DeliveryCoordinator),
        (InfrastructureAssembly, RecipientMinter), (InfrastructureAssembly, RecipientPostgres),
        (InfrastructureAssembly, RecipientHandler), (InfrastructureAssembly, JobScheduler),
        (InfrastructureAssembly, JobExecutorType), (InfrastructureAssembly, RecipientSubmission), (KernelAssembly, RecipientOccurrence),
    ];

    private static IEnumerable<MethodDefinition> RecipientCapabilityMethods(ProductionCensus census)
    {
        foreach (var (assembly, type, names) in new[]
                 {
                     (DeliveryApplication, RecipientClaim, new[] { ".ctor", "EnterOnce" }),
                     (DeliveryApplication, RecipientClaimSource, new[] { "MintRecipient" }),
                     (InfrastructureAssembly, RecipientMinter, new[] { "FromRow" }),
                     (DeliveryApplication, JobRecipientRequest, new[] { ".ctor" }),
                     (DeliveryApplication, JobRecipientParticipant, new[] { ".ctor", "Arm", "RequireArmed", "BindOwner",
                         "RequireOwner", "RecordChild", "RecordIneligible", "BeginCommitBudgetRead", "RecordCommitBudget",
                         "FenceOwnerAsync", "FenceReceiptAsync", "MarkCommitting", "RecordCommitted", "RecordRolledBack", "PermitsContext" }),
                     (DeliveryApplication, JobAdmission, new[] { "EnterRecipient", "RequireRecipient", "ProducerOperationType" }),
                     (InfrastructureAssembly, JobScheduler, new[] { "SubmitRecipientAsync", "InsertAsync" }),
                     (InfrastructureAssembly, JobExecutorType, new[] { "ExecuteRecipientAsync" }),
                     (InfrastructureAssembly, RecipientSubmission, new[] { ".ctor" }),
                     (InfrastructureAssembly, RecipientStore, new[] { "ResolveRecipientAsync" }),
                     (InfrastructureAssembly, RecipientPostgres, new[] { "ReadAndLockAsync", "FenceReceiptAsync" }),
                     (KernelAssembly, RecipientOccurrence, new[] { ".ctor" }),
                 })
            foreach (var name in names)
                foreach (var method in census.Types[(assembly, type)].Methods.Where(method => method.Name == name
                             && (type != RecipientOccurrence || ParametersMatch(method, ["LearnStack.SharedKernel.Jobs.BackgroundJobOccurrenceId"]))))
                    yield return method;
    }

    private static SortedSet<string> RecipientOffenders(ProductionCensus census)
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        var types = census.Types;
        foreach (var subject in RecipientSubjects())
            if (!types.ContainsKey(subject)) offenders.Add("Missing recipient authority subject: " + subject.Type);
        if (offenders.Count != 0) return offenders;
        var sites = OwnedCallSites(census).ToArray();
        RecordDetachedTargets(offenders, sites);
        if (offenders.Count != 0) return offenders;
        var bodies = OwnedBodies(census).ToArray();
        var calls = sites.ToLookup(site => (site.Target.DeclaringType.FullName, site.Target.Name));
        var instructions = bodies.SelectMany(item => item.Body.Body.Instructions)
            .Where(instruction => instruction.Operand is MethodReference)
            .ToLookup(instruction => (((MethodReference)instruction.Operand).DeclaringType.FullName, ((MethodReference)instruction.Operand).Name));
        var fieldWrites = bodies.SelectMany(item => item.Body.Body.Instructions.Where(instruction =>
                instruction.Operand is FieldReference target
                && (target.DeclaringType.FullName == RecipientClaim || target.DeclaringType.FullName == JobRecipientParticipant)
                && (instruction.OpCode == OpCodes.Stfld || instruction.OpCode == OpCodes.Ldflda))
            .Select(instruction => (Field: (FieldReference)instruction.Operand, Writer: MethodId(item.Owner) + ":" + instruction.OpCode.Name)))
            .ToLookup(write => (write.Field.DeclaringType.FullName, write.Field.Name), write => write.Writer);
        var claim = types[(DeliveryApplication, RecipientClaim)];
        var participant = types[(DeliveryApplication, JobRecipientParticipant)];
        var provider = types[(InfrastructureAssembly, RecipientPostgres)];
        var submitted = types[(InfrastructureAssembly, RecipientSubmission)];
        if (!submitted.IsSealed || submitted.Properties.Where(property => property.Name != "Failure").Any(property => property.SetMethod is not null))
            offenders.Add("Original recipient cleanup evidence remains one immutable executor result, without record cloning");
        if (!claim.IsSealed || claim.Methods.Any(method => method.IsConstructor && !method.IsStatic && !method.IsPrivate)
            || claim.Properties.Any(property => property.SetMethod is not null)
            || claim.Fields.Any(field => !field.IsStatic && !field.IsInitOnly && field.Name != "_entered"))
            offenders.Add("Recipient witness must remain sealed, privately constructed and immutable except its single-use flag");
        var consumed = claim.Fields.Where(field => field.Name == "_entered").ToArray();
        if (consumed.Length != 1 || !consumed[0].IsPrivate || consumed[0].IsStatic || consumed[0].FieldType.FullName != "System.Int32")
            offenders.Add("Recipient single-use state requires its private instance Int32 flag");
        CheckSet(offenders, "recipient claim one-way consumption", fieldWrites[(RecipientClaim, "_entered")],
            [JobOwner(offenders, types, DeliveryApplication, RecipientClaim, "EnterOnce") + ":ldflda"]);
        if (!participant.IsAbstract || !provider.IsSealed) offenders.Add("Recipient completion retains one closed provider implementation");
        CheckSet(offenders, "recipient completion implementations", types.Values.Where(type => Derives(type, participant, types)).Select(TypeId), [TypeId(provider)]);
        var minting = types[(DeliveryApplication, RecipientClaimSource)];
        var minter = types[(InfrastructureAssembly, RecipientMinter)];
        if (!minter.IsNestedPrivate || !minter.IsSealed) offenders.Add("Recipient row minter remains a private sealed control-store type");
        CheckSet(offenders, "recipient row minting implementations", types.Values.Where(type => Derives(type, minting, types)).Select(TypeId), [TypeId(minter)]);
        if (!types[(DeliveryApplication, JobRecipientRequest)].IsSealed
            || types[(DeliveryApplication, JobRecipientRequest)].Properties.Any(property => property.SetMethod is not null)
            || types[(DeliveryApplication, JobRecipientRequest)].Fields.Any(field => !field.IsPrivate || !field.IsInitOnly))
            offenders.Add("Recipient command retains immutable exact authority and route references");
        foreach (var fieldName in new[] { "_unit", "_accessor" })
        {
            var fields = participant.Fields.Where(field => field.Name == fieldName).ToArray();
            if (fields.Length != 1 || !fields[0].IsPrivate || !fields[0].IsInitOnly || fields[0].IsStatic)
                offenders.Add("Recipient completion retains its private readonly dependency: " + fieldName);
        }

        Cap(DeliveryApplication, RecipientClaim, ".ctor", "System.Void", [RecipientRow, "System.Int64", "System.Type"], MethodAttributes.Private,
            [(DeliveryApplication, RecipientClaimSource, "MintRecipient")]);
        Cap(DeliveryApplication, RecipientClaimSource, "MintRecipient", RecipientClaim, [RecipientRow, "System.Int64", "System.Type"], MethodAttributes.Family,
            [(InfrastructureAssembly, RecipientMinter, "FromRow")]);
        Cap(InfrastructureAssembly, RecipientMinter, "FromRow", RecipientClaim, [RecipientRow, "System.Int64", "System.Type"], MethodAttributes.Assembly,
            [(InfrastructureAssembly, RecipientStore, "TryClaimRecipientAsync")]);
        Cap(DeliveryApplication, RecipientClaim, "EnterOnce", "System.Void", [], MethodAttributes.Assembly,
            [(DeliveryApplication, JobAdmission, "EnterRecipient")]);
        Cap(DeliveryApplication, JobRecipientRequest, ".ctor", "System.Void", [RecipientClaim, JobCommitGate, JobRecipientParticipant, RecipientProjection], MethodAttributes.Public,
            [(InfrastructureAssembly, JobExecutorType, "ExecuteRecipientAsync")]);
        Cap(DeliveryApplication, JobRecipientParticipant, ".ctor", "System.Void", [LifecycleUnit, "LearnStack.SharedKernel.Tenancy.ITenantContextAccessor"], MethodAttributes.Family,
            [(InfrastructureAssembly, RecipientPostgres, ".ctor")]);
        Cap(DeliveryApplication, JobRecipientParticipant, "Arm", "System.Void", [JobRecipientRequest], MethodAttributes.Public,
            [(InfrastructureAssembly, JobExecutorType, "ExecuteRecipientAsync")]);
        Cap(DeliveryApplication, JobRecipientParticipant, "RequireArmed", "System.Void", [JobRecipientRequest, LifecycleUnit], MethodAttributes.Assembly,
            [(DeliveryApplication, JobAdmission, "EnterRecipient")]);
        Cap(DeliveryApplication, JobRecipientParticipant, "PermitsContext", "System.Boolean", ["LearnStack.SharedKernel.Tenancy.ITenantContext"], MethodAttributes.Assembly,
            [(DeliveryApplication, JobRecipientParticipant, "Arm"), (DeliveryApplication, JobRecipientParticipant, "RequireOwner"),
             (DeliveryApplication, JobAdmission, "PermitsRequest"), (DeliveryApplication, JobAdmission, "RequireContext")]);
        Cap(DeliveryApplication, JobRecipientParticipant, "BindOwner", "System.Void", [LifecycleUnit, "System.Boolean"], MethodAttributes.Assembly,
            [(DeliveryApplication, DeliveryCoordinator, "ExecuteFrameAsync")]);
        Cap(DeliveryApplication, JobRecipientParticipant, "FenceOwnerAsync", BoundaryTask, [LifecycleUnit, "LearnStack.SharedKernel.Results.IResultBase", BoundaryCancellation], MethodAttributes.Assembly,
            [(DeliveryApplication, DeliveryCoordinator, "ExecuteFrameAsync")]);
        Cap(DeliveryApplication, JobRecipientParticipant, "FenceReceiptAsync", BoundaryTask, [BoundaryCancellation], MethodAttributes.Family,
            [(DeliveryApplication, JobRecipientParticipant, "FenceOwnerAsync")], allowVirtual: true);
        Cap(InfrastructureAssembly, RecipientPostgres, "FenceReceiptAsync", BoundaryTask, [BoundaryCancellation], MethodAttributes.Family, [], allowVirtual: true);
        Cap(DeliveryApplication, JobRecipientParticipant, "MarkCommitting", "System.Void", [LifecycleUnit], MethodAttributes.Assembly,
            [(DeliveryApplication, DeliveryCoordinator, "ExecuteFrameAsync")]);
        Cap(DeliveryApplication, JobRecipientParticipant, "RecordCommitted", "System.Void", [LifecycleUnit], MethodAttributes.Assembly,
            [(DeliveryApplication, DeliveryCoordinator, "ExecuteFrameAsync")]);
        Cap(DeliveryApplication, JobRecipientParticipant, "RecordRolledBack", "System.Void", [], MethodAttributes.Assembly,
            [(DeliveryApplication, DeliveryCoordinator, "ExecuteFrameAsync"), (DeliveryApplication, DeliveryCoordinator, "ExecuteFrameAsync")]);
        Cap(DeliveryApplication, JobRecipientParticipant, "RecordChild", "System.Void", [LifecycleUnit, RecipientChild, "System.Boolean"], MethodAttributes.Public,
            [(InfrastructureAssembly, RecipientPostgres, "ReadAndLockAsync"), (InfrastructureAssembly, JobScheduler, "SubmitRecipientAsync")]);
        Cap(DeliveryApplication, JobRecipientParticipant, "RecordIneligible", "System.Void", [], MethodAttributes.Family,
            [(InfrastructureAssembly, RecipientPostgres, "ReadAndLockAsync")]);
        Cap(DeliveryApplication, JobRecipientParticipant, "BeginCommitBudgetRead", "System.Void", [], MethodAttributes.Family,
            [(InfrastructureAssembly, RecipientPostgres, "FenceReceiptAsync")]);
        Cap(DeliveryApplication, JobRecipientParticipant, "RecordCommitBudget", "System.Void", [RecipientRow], MethodAttributes.Family,
            [(InfrastructureAssembly, RecipientPostgres, "FenceReceiptAsync")]);
        Cap(DeliveryApplication, JobRecipientParticipant, "RequireOwner", "System.Void", [LifecycleUnit], MethodAttributes.Public,
            [(DeliveryApplication, JobRecipientParticipant, "BindOwner"), (DeliveryApplication, JobRecipientParticipant, "RecordChild"),
             (DeliveryApplication, JobRecipientParticipant, "RecordIneligible"), (DeliveryApplication, JobRecipientParticipant, "BeginCommitBudgetRead"),
             (DeliveryApplication, JobRecipientParticipant, "RecordCommitBudget"), (DeliveryApplication, JobRecipientParticipant, "FenceOwnerAsync"),
             (DeliveryApplication, JobRecipientParticipant, "FenceOwnerAsync"), (DeliveryApplication, JobRecipientParticipant, "MarkCommitting"),
             (InfrastructureAssembly, RecipientPostgres, "ReadAndLockAsync"), (InfrastructureAssembly, RecipientPostgres, "ReadAndLockAsync"),
             (InfrastructureAssembly, RecipientPostgres, "ReadAndLockAsync"), (InfrastructureAssembly, RecipientPostgres, "ReadAndLockAsync"),
             (InfrastructureAssembly, RecipientPostgres, "ReadAndLockAsync"), (InfrastructureAssembly, RecipientPostgres, "FenceReceiptAsync"),
             (InfrastructureAssembly, RecipientPostgres, "FenceReceiptAsync"), (InfrastructureAssembly, RecipientPostgres, "CreateCommand"),
             (InfrastructureAssembly, JobScheduler, "SubmitRecipientAsync"), (InfrastructureAssembly, JobScheduler, "SubmitRecipientAsync"),
             (InfrastructureAssembly, JobScheduler, "SubmitRecipientAsync")]);
        Cap(DeliveryApplication, JobAdmission, "EnterRecipient", "System.IDisposable", [JobRecipientRequest, LifecycleUnit], MethodAttributes.Public,
            [(InfrastructureAssembly, JobExecutorType, "ExecuteRecipientAsync")]);
        Cap(DeliveryApplication, JobAdmission, "RequireRecipient", "System.Void", [JobRecipientRequest], MethodAttributes.Public,
            [(DeliveryApplication, JobRecipientParticipant, "BindOwner"), (DeliveryApplication, JobRecipientParticipant, "RequireOwner"),
             (InfrastructureAssembly, RecipientHandler, "Handle"), (DeliveryApplication, JobAdmission, "RequireOwnerEntry"),
             (DeliveryApplication, JobAdmission, "ProducerOperationType")]);
        Cap(DeliveryApplication, JobAdmission, "ProducerOperationType", "System.Type", ["System.Type"], MethodAttributes.Assembly,
            [(DeliveryApplication, DeliveryCoordinator, "ExecuteFrameAsync")]);
        Cap(InfrastructureAssembly, JobScheduler, "SubmitRecipientAsync", "System.Threading.Tasks.Task`1<" + RecipientChild + ">", [JobRecipientRequest, BoundaryCancellation], MethodAttributes.Assembly,
            [(InfrastructureAssembly, RecipientHandler, "Handle")]);
        // Only the concrete retained application operation enters this seam.
        Cap(InfrastructureAssembly, JobExecutorType, "ExecuteRecipientAsync",
            "System.Threading.Tasks.Task`1<LearnStack.Infrastructure.Jobs.JobRecipientSubmissionResult>",
            [RecipientClaim, RecipientProjection, JobCommitGate, BoundaryCancellation], MethodAttributes.Assembly,
            [(InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.BackgroundJobRecipientOperation", "ExecuteApplicationAsync")]);
        Cap(InfrastructureAssembly, RecipientSubmission, ".ctor", "System.Void",
            ["LearnStack.Infrastructure.Jobs.JobRecipientSubmissionStatus", JobRecipientRequest, "System.Boolean",
             "LearnStack.SharedKernel.Jobs.JobApplicationSourceOwner",
             "System.Collections.Generic.IReadOnlyList`1<LearnStack.Infrastructure.Jobs.JobStartCleanupOwnership>"], MethodAttributes.Assembly,
            [(InfrastructureAssembly, JobExecutorType, "ExecuteRecipientAsync")]);
        Cap(InfrastructureAssembly, RecipientStore, "ResolveRecipientAsync",
            "System.Threading.Tasks.Task`1<LearnStack.Infrastructure.Jobs.OwnedBackgroundJobControl`1<LearnStack.Infrastructure.Jobs.BackgroundJobRecipientResolution>>",
            [RecipientSubmission, RecipientProjection, BoundaryCancellation], MethodAttributes.Assembly,
            [(InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.BackgroundJobRecurrenceControl", "ResolveOneAsync")]);
        Cap(InfrastructureAssembly, JobScheduler, "InsertAsync", "System.Threading.Tasks.Task`1<" + RecipientChild + ">",
            ["LearnStack.SharedKernel.Jobs.BackgroundJobDefinitionDescriptor", RecipientChild, "System.String", "System.DateTimeOffset", JobOrigin,
             "System.Nullable`1<" + RecipientId + ">", BoundaryCancellation], MethodAttributes.Private,
            [(InfrastructureAssembly, JobScheduler, "SubmitAsync"), (InfrastructureAssembly, JobScheduler, "SubmitRecipientAsync")]);
        Cap(InfrastructureAssembly, RecipientPostgres, "ReadAndLockAsync", "System.Threading.Tasks.Task`1<LearnStack.Infrastructure.Jobs.BackgroundJobRecipientRead>",
            [BoundaryCancellation], MethodAttributes.Assembly, [(InfrastructureAssembly, RecipientHandler, "Handle")]);
        Cap(KernelAssembly, RecipientOccurrence, ".ctor", "System.Void", ["LearnStack.SharedKernel.Jobs.BackgroundJobOccurrenceId"], MethodAttributes.Public,
            [(InfrastructureAssembly, JobScheduler, "SubmitRecipientAsync")]);

        var insert = bodies.Single(item => item.Owner.DeclaringType.FullName == JobScheduler
            && item.Owner.Name == "InsertAsync" && item.Body.Name == "MoveNext").Body.Body;
        var insertInstructions = insert.Instructions.ToArray();
        var wire = Array.FindAll(insertInstructions, instruction => instruction.Operand is MethodReference target
            && target.DeclaringType.FullName == "System.Data.Common.DbCommand" && target.Name == "ExecuteNonQueryAsync"
            && target.ReturnType.FullName == "System.Threading.Tasks.Task`1<System.Int32>"
            && ParametersMatch(target, [BoundaryCancellation]));
        var receipts = Array.FindAll(insertInstructions, instruction => instruction.Operand is MethodReference target
            && target.DeclaringType.FullName == DeliveryProducer && target.Name == "RecordProducedWork"
            && target.Parameters.Count == 0 && target.ReturnType.FullName == "System.Void");
        var acknowledgements = Array.FindAll(insertInstructions, instruction => instruction.Operand is MethodReference target
            && target.Name == "GetResult" && target.Parameters.Count == 0 && target.ReturnType.FullName == "!0"
            && target.DeclaringType.FullName == RecipientInsertAwaiter);
        if (wire.Length != 1 || receipts.Length != 1 || acknowledgements.Length != 1
            || wire.Concat(receipts).Concat(acknowledgements).Any(instruction => !Invoked(instruction)))
            offenders.Add("Shared job INSERT requires one actual SQL invocation, its exact Int32 await and one zero-argument produced-work invocation");
        else
        {
            var wireIndex = Array.IndexOf(insertInstructions, wire[0]);
            var receiptIndex = Array.IndexOf(insertInstructions, receipts[0]);
            var ackIndex = Array.IndexOf(insertInstructions, acknowledgements[0]);
            var receiptInHandler = insert.ExceptionHandlers.Any(handler => receiptIndex >= insert.Instructions.IndexOf(handler.HandlerStart)
                && (handler.HandlerEnd is null || receiptIndex < insert.Instructions.IndexOf(handler.HandlerEnd)));
            if (ackIndex <= wireIndex || receiptIndex <= ackIndex || receiptInHandler)
                offenders.Add("Shared job INSERT records produced work only after its actual await acknowledgement and outside failure handlers");
            var failedInsertHandlers = insert.ExceptionHandlers.Where(handler => handler.HandlerType == ExceptionHandlerType.Catch
                    && insert.Instructions.IndexOf(handler.TryStart) <= wireIndex
                    && insert.Instructions.IndexOf(handler.TryStart) <= ackIndex
                    && (handler.TryEnd is null || (wireIndex < insert.Instructions.IndexOf(handler.TryEnd)
                        && ackIndex < insert.Instructions.IndexOf(handler.TryEnd))))
                .Select(handler => insertInstructions.Skip(insert.Instructions.IndexOf(handler.HandlerStart))
                    .Take((handler.HandlerEnd is null ? insertInstructions.Length : insert.Instructions.IndexOf(handler.HandlerEnd))
                        - insert.Instructions.IndexOf(handler.HandlerStart)).ToArray())
                .Where(instructions => instructions.Count(instruction => Invoked(instruction) && instruction.Operand is MethodReference target
                    && target.DeclaringType.FullName == LifecycleUnit && target.Name == "MarkRollbackOnly"
                    && target.Parameters.Count == 0 && target.ReturnType.FullName == "System.Void") == 1
                    && instructions.Any(instruction => instruction.OpCode == OpCodes.Rethrow)).ToArray();
            if (failedInsertHandlers.Length != 1)
                offenders.Add("The catch protecting actual SQL issuance and acknowledgement marks its existing owner rollback-only and rethrows");
        }

        foreach (var (fieldName, expectedWriters) in new[]
                 {
                     ("_command", new[] { "Arm" }), ("_context", new[] { "Arm" }),
                     ("_connection", new[] { "BindOwner" }), ("_transaction", new[] { "BindOwner" }),
                     ("_announcement", new[] { "BindOwner" }), ("_child", new[] { "RecordChild" }),
                     ("_existing", new[] { "RecordChild" }), ("_ineligible", new[] { "RecordIneligible" }),
                     ("_observedAt", new[] { "BeginCommitBudgetRead" }), ("_remaining", new[] { "RecordCommitBudget" }),
                     ("_budgetState", new[] { "BeginCommitBudgetRead", "RecordCommitBudget", "RecordCommitBudget", "MarkCommitting" }),
                 })
        {
            var fields = participant.Fields.Where(field => field.Name == fieldName).ToArray();
            if (fields.Length != 1 || !fields[0].IsPrivate || fields[0].IsStatic)
                offenders.Add("Recipient authority retains its private owner-local field: " + fieldName);
            CheckSet(offenders, "recipient authority writers " + fieldName, fieldWrites[(JobRecipientParticipant, fieldName)],
                expectedWriters.Select(name => JobOwner(offenders, types, DeliveryApplication, JobRecipientParticipant, name)
                    + (fieldName == "_budgetState" ? ":ldflda" : ":stfld")));
        }

        var registrations = sites.Where(site => site.Target is GenericInstanceMethod generic && generic.Name == "TryAddScoped"
            && generic.GenericArguments.Count == 2 && generic.GenericArguments[0].FullName == JobRecipientParticipant
            && generic.GenericArguments[1].FullName == RecipientPostgres).Select(site => MethodId(site.Owner));
        CheckSet(offenders, "closed recipient participant registration", registrations,
            [JobOwner(offenders, types, InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.BackgroundJobContractsRegistration", "AddLearnStackBackgroundJobContracts")]);
        var complete = bodies.Single(item => item.Owner.DeclaringType.FullName == DeliveryCoordinator
            && item.Owner.Name == "ExecuteFrameAsync" && item.Body.Name == "MoveNext").Body.Body.Instructions.ToArray();
        var bind = Find(JobRecipientParticipant, "BindOwner");
        var drain = Find("LearnStack.Application.Messaging.DomainEventCollector", "DrainAsync");
        var audit = Find("LearnStack.SharedKernel.Audit.IAuditStore", "WritePendingAsync");
        var fence = Find(JobRecipientParticipant, "FenceOwnerAsync");
        var commit = Find(JobRecipientParticipant, "MarkCommitting");
        var finish = Find(LifecycleScope, "CompleteAsync");
        var ack = Find(JobRecipientParticipant, "RecordCommitted");
        var handler = Array.FindIndex(complete, instruction => instruction.Operand is MethodReference target
            && target.Name == "Invoke" && target.DeclaringType.FullName.StartsWith("System.Func`1", StringComparison.Ordinal));
        if (bind < 0 || handler <= bind || drain <= handler || audit <= drain || fence <= audit || commit <= fence || finish <= commit || ack <= finish
            || complete.Skip(commit + 1).Take(Math.Max(0, finish - commit - 1)).Any(instruction =>
                instruction.Operand is MethodReference target && target.Name == "GetAwaiter")
            || !complete.Skip(finish + 1).Take(Math.Max(0, ack - finish - 1)).Any(instruction =>
                instruction.Operand is MethodReference target && target.Name == "GetResult"))
            offenders.Add("Recipient completion must bind, drain, audit, fence, CAS, await the existing COMMIT, then record ACK");
        var providerBodies = bodies.Where(item => item.Owner.DeclaringType.FullName == RecipientPostgres).ToArray();
        var sql = providerBodies.SelectMany(item => item.Body.Body.Instructions)
            .Where(instruction => instruction.OpCode == OpCodes.Ldstr).Select(instruction => (string)instruction.Operand).ToArray();
        foreach (var prefix in new[] { "SELECT ownership_token,ownership_until,accepted_job_id", " FROM background_job_recipients WHERE ",
                     "UPDATE background_job_recipients SET accepted_job_id=@child" })
        {
            var queries = sql.Where(statement => statement.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            if (queries.Length != 1 || RecipientRoutePredicates.Any(predicate => !queries[0].Contains(predicate, StringComparison.Ordinal)))
                offenders.Add("Recipient actual SQL requires its complete immutable route: " + prefix);
        }
        foreach (var required in new[] { "SELECT id FROM background_job_recipients WHERE id=@recipient AND tenant_id=@tenant FOR UPDATE",
                     "SELECT @original_until,clock_timestamp()", "SELECT ownership_until,clock_timestamp()",
                     " AND accepted_job_id=@child AND skipped_at IS NULL AND quarantined_at IS NULL",
                     " AND ownership_token=@token AND ownership_until>clock_timestamp() AND accepted_job_id IS NULL AND skipped_at IS NULL AND quarantined_at IS NULL" })
            if (sql.Count(statement => statement == required) != 1) offenders.Add("Recipient actual SQL must retain its exact token/time/receipt fence: " + required);
        var budget = providerBodies.Single(item => item.Owner.Name == "FenceReceiptAsync" && item.Body.Name == "MoveNext").Body.Body.Instructions.ToArray();
        var beginBudget = Array.FindIndex(budget, instruction => instruction.Operand is MethodReference target
            && target.DeclaringType.FullName == JobRecipientParticipant && target.Name == "BeginCommitBudgetRead");
        var queryBudget = Array.FindIndex(budget, instruction => instruction.Operand is MethodReference target
            && target.DeclaringType.FullName == "Npgsql.NpgsqlCommand" && target.Name == "ExecuteReaderAsync");
        var recordBudget = Array.FindIndex(budget, instruction => instruction.Operand is MethodReference target
            && target.DeclaringType.FullName == JobRecipientParticipant && target.Name == "RecordCommitBudget");
        if (beginBudget < 0 || queryBudget <= beginBudget || recordBudget <= queryBudget)
            offenders.Add("Recipient lease budget starts before its wire read and retains the returned interval through cleanup");
        var history = bodies.Single(item => item.Owner.DeclaringType.FullName == RecipientStore
            && item.Owner.Name == "ResolveRecipientAsync" && item.Body.Name == "MoveNext").Body.Body.Instructions.ToArray();
        var openHistory = Array.FindIndex(history, instruction => instruction.Operand is MethodReference target
            && target.DeclaringType.FullName == RecipientStore && target.Name == "OpenAsync");
        foreach (var (type, name) in new[]
                 {
                     (RecipientSubmission, "get_Command"), (RecipientSubmission, "get_CleanupFailed"),
                     (RecipientSubmission, "get_RetainedCleanup"), (RecipientSubmission, "get_SourceOwner"),
                     ("LearnStack.SharedKernel.Jobs.JobApplicationSourceOwner", "get_CleanupConfirmed"),
                 })
            if (openHistory < 0 || !history.Take(openHistory).Any(instruction => Invoked(instruction)
                    && instruction.Operand is MethodReference target && target.DeclaringType.FullName == type && target.Name == name))
                offenders.Add("Recipient history checks original cleanup before another physical source: " + name);
        var historySql = bodies.Where(item => item.Owner.DeclaringType.FullName == RecipientStore
                && item.Owner.Name is "ResolveRecipientAsync" or "ResolveLockedRecipientAsync")
            .SelectMany(item => item.Body.Body.Instructions).Where(instruction => instruction.OpCode == OpCodes.Ldstr)
            .Select(instruction => (string)instruction.Operand).ToArray();
        if (historySql.Count(statement => statement == "SELECT id FROM background_job_recipients WHERE id=@recipient AND tenant_id=@tenant FOR UPDATE SKIP LOCKED") != 1
            || historySql.Count(statement => statement.StartsWith("id=@recipient", StringComparison.Ordinal)
                && statement.EndsWith(" AND ownership_token=@token AND ownership_until>clock_timestamp() AND accepted_job_id IS NULL AND skipped_at IS NULL AND quarantined_at IS NULL", StringComparison.Ordinal)) != 1)
            offenders.Add("Recipient durable history retains its actual locked-row and original live-token fence");
        if (historySql.Count(statement => RecipientRoutePredicates.All(predicate => statement.Contains(predicate, StringComparison.Ordinal))) != 2)
            offenders.Add("Recipient history binds every immutable route fact to its locked read and transition");
        return offenders;

        int Find(string type, string method) => Array.FindIndex(complete, instruction => instruction.Operand is MethodReference target
            && target.DeclaringType.FullName == type && target.Name == method);

        static bool Invoked(Instruction instruction) => instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt;

        void Cap(string assembly, string type, string name, string result, string[] parameters, MethodAttributes visibility,
            (string Assembly, string Type, string Method)[] owners, bool allowVirtual = false)
        {
            var declared = types[(assembly, type)];
            var methods = declared.Methods.Where(method => method.Name == name
                && (type != RecipientOccurrence || ParametersMatch(method, parameters))).ToArray();
            var expectedStatic = name is "MintRecipient" or "FromRow" or "RequireRecipient" or "ProducerOperationType";
            if (methods.Length != 1 || methods[0].ReturnType.FullName != result || !ParametersMatch(methods[0], parameters)
                || (methods[0].Attributes & MethodAttributes.MemberAccessMask) != visibility || methods[0].HasGenericParameters
                || methods[0].IsStatic != expectedStatic || methods[0].IsVirtual != allowVirtual)
                offenders.Add(type + "." + name + " requires its one exact closed signature and visibility");
            if (type == RecipientOccurrence)
            {
                var copies = declared.Methods.Where(method => method.IsConstructor && !method.IsStatic
                    && ParametersMatch(method, [RecipientOccurrence])).ToArray();
                if (declared.Methods.Count(method => method.IsConstructor && !method.IsStatic) != 2
                    || copies.Length != 1 || !copies[0].IsPrivate || copies[0].HasGenericParameters
                    || copies[0].ReturnType.FullName != "System.Void")
                    offenders.Add("Occurrence input permits only its typed constructor and exact private record-copy constructor");
            }
            CheckSet(offenders, "recipient capability " + type + "." + name, calls[(type, name)]
                    .Where(site => type != RecipientOccurrence || ParametersMatch(site.Target, parameters)).Select(site => MethodId(site.Owner)),
                owners.Select(owner => JobOwner(offenders, types, owner.Assembly, owner.Type, owner.Method)));
            foreach (var instruction in instructions[(type, name)].Where(instruction => type != RecipientOccurrence
                         || ParametersMatch((MethodReference)instruction.Operand, parameters)))
                if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt
                    && !(name == ".ctor" && instruction.OpCode == OpCodes.Newobj))
                    offenders.Add(type + "." + name + " cannot escape as a delegate");
        }
    }
}
