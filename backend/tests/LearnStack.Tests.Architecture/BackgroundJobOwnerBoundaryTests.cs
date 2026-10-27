using FluentAssertions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed partial class OutboxDispatchConventionTests
{
    private static readonly string[] JobCapacityCompletionReleasePredicates =
    [
        "released_at=clock_timestamp()", "release_reason='completed'", "tenant_id=@tenant",
        "capacity_group=@capacity", "job_id=@id", "attempt_id=@attempt", "released_at IS NULL",
        "RETURNING tenant_id, capacity_group, job_id, attempt_id",
    ];

    private const string JobParticipant = "LearnStack.Application.Jobs.BackgroundJobCompletionParticipant";
    private const string JobPostgresParticipant = "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobCompletion";
    private const string JobCommitGate = "LearnStack.Application.Jobs.BackgroundJobCommitGate";
    private const string JobRunGate = "LearnStack.Application.Jobs.BackgroundJobRunGate";
    private const string JobCheckpointOwner = "LearnStack.Application.Jobs.BackgroundJobExternalCheckpointOwner";
    private const string JobPublication = "LearnStack.Application.Jobs.BackgroundJobStartPublication";
    private const string JobTrustedContext = "LearnStack.SharedKernel.Tenancy.JobTenantContext";
    private const string AsyncDiagnostics = "LearnStack.SharedKernel.Tenancy.AsyncExecutionDiagnostics";

    [Fact]
    public void BackgroundJob_Completion_Is_Owner_Atomic_And_Fenced()
    {
        using var census = ProductionCensus.Read();
        EvaluateDetector(census, JobOwnerOffenders).Should().BeEmpty(
            "Fix: retain one exact receipt/unit/root participant and the existing owner-only Complete boundary");
    }

    public sealed class JobOwnerMutationTests
    {

        [Fact]
        public void Background_job_owner_census_detects_actual_removals_extra_subtypes_async_calls_and_delegates()
        {
            using var census = ProductionCensus.Read();
            var application = census.Modules.Single(module => module.Assembly.Name.Name == DeliveryApplication);
            var capabilities = JobOwnerCapabilities(census).ToArray();
            capabilities.Should().NotBeEmpty("the owner census starts with actual production authority");
            foreach (var capability in capabilities)
            {
                foreach (var opcode in new[] { OpCodes.Call, OpCodes.Callvirt, OpCodes.Ldftn, OpCodes.Ldvirtftn })
                    AssertMutation(census, "an extra job owner capability through " + opcode, () =>
                        AddCallProbe(application, "AdditionalJobOwner", capability, opcode), JobOwnerOffenders);
                AssertMutation(census, "an asynchronous job owner escape", () =>
                    AddAsyncCallProbe(application, "AsyncJobOwner", capability), JobOwnerOffenders);
                AssertMutation(census, "an overload borrowing an approved owner name", () =>
                {
                    var method = AddCall(capability.DeclaringType, capability.Name, capability, OpCodes.Call);
                    return () => capability.DeclaringType.Methods.Remove(method);
                }, JobOwnerOffenders);
            }
            var actualSites = OwnedBodies(census).SelectMany(item => item.Body.Body.Instructions)
                .Where(instruction => instruction.Operand is MethodReference target && capabilities.Any(capability =>
                    SameType(target.DeclaringType, capability.DeclaringType) && target.Name == capability.Name)).ToArray();
            actualSites.Should().NotBeEmpty("the production callback and coordinator paths are live even with zero business definitions");
            foreach (var site in actualSites)
            {
                AssertMutation(census, "a deleted actual owner gate", () => ReplaceInstruction(site, OpCodes.Nop, null!), JobOwnerOffenders);
                AssertMutation(census, "a delegate escape in an approved owner", () =>
                    ReplaceInstruction(site, OpCodes.Ldftn, site.Operand), JobOwnerOffenders);
            }
            AssertMutation(census, "a second job completion implementation", () =>
            {
                var probe = AddProbe(application, "AdditionalJobCompletion");
                probe.BaseType = application.ImportReference(census.Types[(DeliveryApplication, JobParticipant)]);
                return () => application.Types.Remove(probe);
            }, JobOwnerOffenders);
            foreach (var subject in JobOwnerSubjects())
                AssertMutation(census, "a missing real job owner subject", () =>
                {
                    var type = census.Types[subject];
                    var module = type.Module;
                    module.Types.Remove(type);
                    return () => module.Types.Add(type);
                }, JobOwnerOffenders);
            var context = census.Types[(KernelAssembly, JobTrustedContext)];
            var constructor = context.Methods.Single(method => method.IsConstructor && !method.IsStatic);
            AssertMutation(census, "a publicly constructible trusted job context", () =>
                ChangeAttributes(() => constructor.Attributes, value => constructor.Attributes = value,
                    (constructor.Attributes & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Public), JobOwnerOffenders);
            var completionBody = OwnedBodies(census).Single(item => item.Owner.DeclaringType.FullName == DeliveryCoordinator
                && item.Owner.Name == "ExecuteFrameAsync" && item.Body.Name == "MoveNext").Body.Body;
            var acknowledgement = completionBody.Instructions.Single(instruction => instruction.Operand is MethodReference target
                && target.DeclaringType.FullName == JobCommitGate && target.Name == "RecordCommitted");
            AssertMutation(census, "job acknowledgement before the actual owned Complete", () =>
            {
                var index = completionBody.Instructions.IndexOf(acknowledgement);
                completionBody.Instructions.Remove(acknowledgement);
                completionBody.Instructions.Insert(0, acknowledgement);
                return () => { completionBody.Instructions.Remove(acknowledgement); completionBody.Instructions.Insert(index, acknowledgement); };
            }, JobOwnerOffenders);
            var sqlSites = OwnedBodies(census).Where(item => item.Owner.DeclaringType.FullName == JobPostgresParticipant
                    && item.Owner.Name is "PreflightAsync" or "FenceAsync")
                .SelectMany(item => item.Body.Body.Instructions).Where(instruction => instruction.OpCode == OpCodes.Ldstr
                    && instruction.Operand is string sql && sql.Contains("FROM background_jobs", StringComparison.Ordinal)
                        || instruction.OpCode == OpCodes.Ldstr && instruction.Operand is string update
                        && update.StartsWith("UPDATE background_jobs", StringComparison.Ordinal)).ToArray();
            sqlSites.Should().HaveCount(3, "the real unlocked preflight, exact row lock and fresh-time UPDATE are distinct");
            foreach (var sql in sqlSites)
                AssertMutation(census, "a removed actual preflight or final-fence SQL", () =>
                    ReplaceInstruction(sql, OpCodes.Ldstr, "missing job SQL fence"), JobOwnerOffenders);
            var releaseSql = OwnedBodies(census).Where(item => item.Owner.DeclaringType.FullName == JobPostgresParticipant)
                .SelectMany(item => item.Body.Body.Instructions).Single(instruction => instruction.OpCode == OpCodes.Ldstr
                    && instruction.Operand is string sql && sql.StartsWith("UPDATE background_job_reservations", StringComparison.Ordinal));
            AssertMutation(census, "a removed successful grouped reservation release", () =>
                ReplaceInstruction(releaseSql, OpCodes.Ldstr, "missing reservation release"), JobOwnerOffenders);
            foreach (var site in sqlSites)
                foreach (var predicate in new[] { "capacity_group IS NOT DISTINCT FROM @capacity", "r.attempt_id = @attempt", "r.released_at IS NULL" })
                    AssertMutation(census, "a grouped completion fence without " + predicate, () =>
                        ReplaceInstruction(site, OpCodes.Ldstr, ((string)site.Operand).Replace(predicate, "TRUE", StringComparison.Ordinal)),
                        JobOwnerOffenders);

            var budgetBodies = OwnedBodies(census).ToArray();
            var capture = budgetBodies.Single(item => item.Owner.DeclaringType.FullName == JobPostgresParticipant
                && item.Owner.Name == "CaptureCommitBudgetAsync" && item.Body.Name == "MoveNext").Body.Body;
            var begin = capture.Instructions.Single(instruction => instruction.Operand is MethodReference method
                && method.Name == "BeginCommitBudgetRead");
            var query = capture.Instructions.Single(instruction => instruction.Operand is MethodReference method
                && method.DeclaringType.FullName == "Npgsql.NpgsqlCommand" && method.Name == "ExecuteReaderAsync");
            AssertMutation(census, "starting the monotonic budget after the query", () =>
                MoveBudgetInstructionAfter(capture, begin, query), JobOwnerOffenders);
            foreach (var projection in capture.Instructions.Where(instruction => instruction.OpCode == OpCodes.Ldstr
                         && instruction.Operand is string sql && sql.StartsWith("SELECT ", StringComparison.Ordinal)).ToArray())
                AssertMutation(census, "a changed database-time budget projection", () =>
                    ReplaceInstruction(projection, OpCodes.Ldstr, ((string)projection.Operand).Replace("clock_timestamp()", "NULL", StringComparison.Ordinal)),
                    JobOwnerOffenders);
            var transactionBudget = capture.Instructions.Single(instruction => instruction.OpCode == OpCodes.Ldstr
                && instruction.Operand is string sql && sql.StartsWith("SELECT ownership_until,", StringComparison.Ordinal));
            AssertMutation(census, "a missing budget-local tenant predicate", () =>
                ReplaceInstruction(transactionBudget, OpCodes.Ldstr,
                    ((string)transactionBudget.Operand).Replace("tenant_id = @tenant", "TRUE", StringComparison.Ordinal)), JobOwnerOffenders);
            foreach (var site in capture.Instructions.Where(instruction => instruction.Operand is MethodReference method
                         && method.DeclaringType.FullName == "LearnStack.Infrastructure.Jobs.BackgroundJobExternalCheckpointSql"
                         && method.Name is "Identity" or "Live").ToArray())
                AssertMutation(census, "a removed Prepare budget identity/live call", () =>
                    ReplaceInstruction(site, OpCodes.Nop, null!), JobOwnerOffenders);
            foreach (var fenceName in new[] { "FenceAsync", "FencePrepareAsync", "FenceFinalizeAsync" })
            {
                var fence = budgetBodies.Single(item => item.Owner.DeclaringType.FullName == JobPostgresParticipant
                    && item.Owner.Name == fenceName && item.Body.Name == "MoveNext").Body.Body;
                var budget = fence.Instructions.Single(instruction => instruction.Operand is MethodReference method
                    && method.Name == "CaptureCommitBudgetAsync");
                var writes = fence.Instructions.Where(instruction => instruction.OpCode == OpCodes.Ldstr
                    && instruction.Operand is string sql && sql.StartsWith("UPDATE background_jobs", StringComparison.Ordinal)).ToArray();
                writes.Should().HaveCount(fenceName == "FenceFinalizeAsync" ? 2 : 1,
                    "Finalize has separate completed and terminal writes after the same captured budget");
                foreach (var write in writes)
                    AssertMutation(census, "a budget captured after the completion write", () =>
                        MoveBudgetInstructionAfter(fence, budget, write), JobOwnerOffenders);
            }
            foreach (var fieldName in new[] { "_commitBudgetStartedAt", "_commitBudgetRemaining", "_commitBudgetState" })
            {
                var field = census.Types[(DeliveryApplication, JobParticipant)].Fields.Single(field => field.Name == fieldName);
                AssertMutation(census, "a publicly writable completion budget", () =>
                    ChangeAttributes(() => field.Attributes, value => field.Attributes = value,
                        (field.Attributes & ~FieldAttributes.FieldAccessMask) | FieldAttributes.Public), JobOwnerOffenders);
                var writes = budgetBodies.Where(item => item.Owner.DeclaringType.FullName == JobParticipant)
                    .SelectMany(item => item.Body.Body.Instructions).Where(instruction => instruction.Operand is FieldReference target
                        && target.Name == fieldName && instruction.OpCode == (fieldName == "_commitBudgetState" ? OpCodes.Ldflda : OpCodes.Stfld)).ToArray();
                writes.Should().NotBeEmpty("the budget has actual publication/consumption state");
                foreach (var write in writes)
                    AssertMutation(census, "missing owner-local budget publication/consumption", () =>
                        ReplaceInstruction(write, OpCodes.Nop, null!), JobOwnerOffenders);
            }
            var participant = census.Types[(DeliveryApplication, JobParticipant)];
            var stateField = participant.Fields.Single(field => field.Name == "_commitBudgetState");
            foreach (var name in new[] { "BeginCommitBudgetRead", "RecordCommitBudget", "TryBeginCommit" })
            {
                var owner = participant.Methods.Single(method => method.Name == name);
                AssertMutation(census, "an approved method directly resetting its one-way budget state", () =>
                {
                    var reset = Instruction.Create(OpCodes.Stfld, stateField);
                    owner.Body.Instructions.Insert(0, reset);
                    return () => owner.Body.Instructions.Remove(reset);
                }, JobOwnerOffenders);
            }
            foreach (var (assembly, type, name) in new[]
                     {
                     (DeliveryApplication, JobParticipant, "BeginCommitBudgetRead"),
                     (DeliveryApplication, JobParticipant, "RecordCommitBudget"),
                     (DeliveryApplication, JobParticipant, "TryBeginCommit"),
                     (DeliveryApplication, JobAdmission, "TryBeginOwnerCommit"),
                     (InfrastructureAssembly, JobPostgresParticipant, "CaptureCommitBudgetAsync"),
                     (DeliveryApplication, JobCommitGate, "TryBeginCheckpointCommit"),
                     (DeliveryApplication, JobCommitGate, "TryBeginCheckpointCommitOwned"),
                     (DeliveryApplication, JobRunGate, "TryBeginCheckpointCommit"),
                 })
            {
                var capability = census.Types[(assembly, type)].Methods.Single(method => method.Name == name);
                AssertMutation(census, "a public owner budget capability", () =>
                    ChangeAttributes(() => capability.Attributes, value => capability.Attributes = value,
                        (capability.Attributes & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Public), JobOwnerOffenders);
            }
            var run = census.Types[(DeliveryApplication, JobRunGate)].Methods.Single(method => method.Name == "TryBeginCheckpointCommit");
            foreach (var site in run.Body.Instructions.Where(instruction => instruction.Operand is MethodReference method
                         && (method.DeclaringType.FullName == "System.Diagnostics.Stopwatch" && method.Name == "GetElapsedTime"
                             || method.DeclaringType.FullName == "System.Threading.Monitor" && method.Name is "Enter" or "Exit")).ToArray())
                AssertMutation(census, "a removed elapsed or inside-lock admission boundary", () =>
                    ReplaceInstruction(site, OpCodes.Nop, null!), JobOwnerOffenders);
            var elapsedSite = run.Body.Instructions.Single(instruction => instruction.Operand is MethodReference method
                && method.DeclaringType.FullName == "System.Diagnostics.Stopwatch" && method.Name == "GetElapsedTime");
            AssertMutation(census, "an elapsed method reference without an actual invocation", () =>
                ReplaceInstruction(elapsedSite, OpCodes.Ldftn, elapsedSite.Operand), JobOwnerOffenders);
            var monitorFinally = run.Body.ExceptionHandlers.Single(handler => handler.HandlerType == ExceptionHandlerType.Finally);
            AssertMutation(census, "a lock-finally region whose start is outside the actual body", () =>
            {
                var original = monitorFinally.HandlerStart;
                monitorFinally.HandlerStart = Instruction.Create(OpCodes.Nop);
                return () => monitorFinally.HandlerStart = original;
            }, JobOwnerOffenders);
            var checkpointOwned = census.Types[(DeliveryApplication, JobCommitGate)].Methods
                .Single(method => method.Name == "TryBeginCheckpointCommitOwned");
            AssertMutation(census, "a removed owned checkpoint helper declaration", () =>
            {
                var methods = checkpointOwned.DeclaringType.Methods;
                var index = methods.IndexOf(checkpointOwned);
                methods.Remove(checkpointOwned);
                return () => methods.Insert(index, checkpointOwned);
            }, JobOwnerOffenders);
            AssertMutation(census, "a changed owned checkpoint helper result", () =>
            {
                var original = checkpointOwned.ReturnType;
                checkpointOwned.ReturnType = application.TypeSystem.Int32;
                return () => checkpointOwned.ReturnType = original;
            }, JobOwnerOffenders);
            foreach (var parameter in checkpointOwned.Parameters)
                AssertMutation(census, "a changed owned checkpoint budget parameter", () =>
                {
                    var original = parameter.ParameterType;
                    parameter.ParameterType = application.TypeSystem.String;
                    return () => parameter.ParameterType = original;
                }, JobOwnerOffenders);
            foreach (var attribute in new[] { MethodAttributes.Static, MethodAttributes.Virtual })
                AssertMutation(census, "an open owned checkpoint helper shape", () =>
                    ChangeAttributes(() => checkpointOwned.Attributes, value => checkpointOwned.Attributes = value,
                        checkpointOwned.Attributes | attribute), JobOwnerOffenders);
            var helperCall = run.Body.Instructions.Single(instruction => instruction.Operand is MethodReference method
                && method.DeclaringType.FullName == JobCommitGate && method.Name == "TryBeginCheckpointCommitOwned");
            AssertMutation(census, "an elapsed observation after owned checkpoint admission", () =>
                MoveBudgetInstructionAfter(run.Body, elapsedSite, helperCall), JobOwnerOffenders);
            var monitorExit = run.Body.Instructions.Single(instruction => instruction.Operand is MethodReference method
                && method.DeclaringType.FullName == "System.Threading.Monitor" && method.Name == "Exit");
            AssertMutation(census, "owned checkpoint admission outside the acquired run lock", () =>
                MoveBudgetInstructionAfter(run.Body, helperCall, monitorExit), JobOwnerOffenders);
            var ownedGuardSites = checkpointOwned.Body.Instructions.Where(instruction =>
                instruction.OpCode == OpCodes.Ldflda && instruction.Operand is FieldReference field
                    && field.Name is "_issued" or "_state" or "_armCommitWindow"
                || instruction.OpCode.FlowControl == FlowControl.Cond_Branch
                || instruction.OpCode == OpCodes.Newobj && instruction.Operand is MethodReference windowConstructor
                    && windowConstructor.DeclaringType.FullName == JobCommitGate + "/ArmCommitWindow"
                || instruction.OpCode == OpCodes.Call && instruction.Operand is MethodReference exchange
                    && exchange.DeclaringType.FullName == "System.Threading.Interlocked" && exchange.Name == "CompareExchange").ToArray();
            ownedGuardSites.Should().HaveCount(9, "three owned cells, four refusal/kind branches, one original interval and one window CAS are actual production IL");
            foreach (var site in ownedGuardSites)
                AssertMutation(census, "a removed issued/Open/Arm-window obligation in the owned helper", () =>
                    ReplaceInstruction(site, OpCodes.Nop, null!), JobOwnerOffenders);
            var windowExchange = checkpointOwned.Body.Instructions.Single(instruction => instruction.Operand is MethodReference method
                && method.DeclaringType.FullName == "System.Threading.Interlocked" && method.Name == "CompareExchange");
            var finalOwnedCas = checkpointOwned.Body.Instructions.Single(instruction => instruction.Operand is MethodReference method
                && method.DeclaringType.FullName == JobCommitGate && method.Name == "TryBeginCommitOwned");
            AssertMutation(census, "an Arm window published after the owned COMMIT CAS", () =>
                MoveBudgetInstructionAfter(checkpointOwned.Body, windowExchange, finalOwnedCas), JobOwnerOffenders);
            var ownerCommit = census.Types[(DeliveryApplication, JobParticipant)].Methods.Single(method => method.Name == "TryBeginCommit");
            var guardedCas = ownerCommit.Body.Instructions.Single(instruction => instruction.Operand is MethodReference method
                && method.DeclaringType.FullName == JobCommitGate && method.Name == "TryBeginCheckpointCommit");
            var plainOwnedCas = census.Types[(DeliveryApplication, JobCommitGate)].Methods.Single(method => method.Name == "TryBeginCommitOwned");
            AssertMutation(census, "a run bypass of the owned checkpoint window", () =>
                ReplaceInstruction(helperCall, OpCodes.Call, plainOwnedCas), JobOwnerOffenders);
            var forwardedBudget = run.Body.Instructions.Take(run.Body.Instructions.IndexOf(helperCall))
                .Where(instruction => instruction.OpCode != OpCodes.Nop).TakeLast(2).ToArray();
            forwardedBudget.Should().HaveCount(2);
            foreach (var argument in forwardedBudget)
                AssertMutation(census, "a substituted interval forwarded to the owned checkpoint helper", () =>
                    ReplaceInstruction(argument, OpCodes.Ldnull, null!), JobOwnerOffenders);
            var plainCas = census.Types[(DeliveryApplication, JobCommitGate)].Methods.Single(method => method.Name == "TryBeginCommit");
            AssertMutation(census, "a direct plain CAS bypass of the owner budget", () =>
                ReplaceInstruction(guardedCas, OpCodes.Call, plainCas), JobOwnerOffenders);
        }

    }

    private static Action MoveBudgetInstructionAfter(MethodBody body, Instruction moved, Instruction anchor)
    {
        var original = body.Instructions.IndexOf(moved);
        body.Instructions.Remove(moved);
        body.Instructions.Insert(body.Instructions.IndexOf(anchor) + 1, moved);
        return () => { body.Instructions.Remove(moved); body.Instructions.Insert(original, moved); };
    }

    private static (string Assembly, string Type)[] JobOwnerSubjects() =>
    [
        (DeliveryApplication, JobParticipant), (InfrastructureAssembly, JobPostgresParticipant),
        (DeliveryApplication, JobCommitGate), (DeliveryApplication, JobRunGate), (DeliveryApplication, JobCheckpointOwner),
        (DeliveryApplication, JobPublication),
        (KernelAssembly, JobTrustedContext), (KernelAssembly, AsyncDiagnostics),
    ];

    private static IEnumerable<MethodDefinition> JobOwnerCapabilities(ProductionCensus census)
    {
        foreach (var (assembly, type, names) in new[]
                 {
                     (DeliveryApplication, JobParticipant, new[] { ".ctor", "Arm", "RequireArmed", "RequireEntry", "BindAndPreflightAsync", "RequireRoot", "FenceOwnerAsync", "RequireCurrent", "BeginCommitBudgetRead", "RecordCommitBudget", "TryBeginCommit" }),
                     (DeliveryApplication, JobCommitGate, new[] { ".ctor", "TryStartExecution", "TrySealUnusedExecution", "TryBeginCommit", "TryBeginCheckpointCommit", "TryBeginCheckpointCommitOwned", "TryBeginCommitOwned", "RecordCommitted", "RecordRolledBack", "Close" }),
                     (DeliveryApplication, JobRunGate, new[] { "TryBeginCommit", "TryBeginCheckpointCommit" }),
                     (InfrastructureAssembly, JobPostgresParticipant, new[] { "CaptureCommitBudgetAsync", "BindFinalizationRead", "PreflightFinalizeAsync", "ReadFinalizationAsync", "FenceFinalizeAsync" }),
                     (DeliveryApplication, JobPublication, new[] { ".ctor", "Bind", "Publish", "SettleNoReceipt" }),
                     (DeliveryApplication, JobAdmission, new[] { "RequireOwnerEntry", "BindOwnerAsync", "FenceOwnerAsync", "TryBeginOwnerCommit", "RequireRootParticipant" }),
                     (KernelAssembly, JobTrustedContext, new[] { ".ctor", "FromValidatedJob", "ForRecoveryRead", "ForRecipient" }),
                     (DeliveryApplication, JobStartRequest, new[] { "BindPublication" }),
                     (InfrastructureAssembly, JobExecutorType, new[] { "ExecuteBusinessAsync", "ExecutePublishedAsync", "get_ScopeFactory" }),
                 })
            foreach (var name in names)
                foreach (var method in census.Types[(assembly, type)].Methods.Where(method => method.Name == name))
                    yield return method;
    }

    private static SortedSet<string> JobOwnerOffenders(ProductionCensus census)
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        var types = census.Types;
        foreach (var subject in JobOwnerSubjects())
            if (!types.ContainsKey(subject)) offenders.Add("Missing business owner subject: " + subject.Type);
        if (offenders.Count != 0) return offenders;
        var sites = CallSites(census).ToArray();
        RecordDetachedTargets(offenders, sites);
        if (offenders.Count != 0) return offenders;
        var sitesByTargetName = sites.ToLookup(site => site.Target.Name, StringComparer.Ordinal);
        var bodies = OwnedBodies(census).ToArray();
        // Build once from this evaluation's live graph; each seam keeps its
        // exact declaring-type and opcode checks inside the named bucket.
        var instructionsByTargetName = bodies.SelectMany(item => item.Body.Body.Instructions)
            .Where(instruction => instruction.Operand is MethodReference)
            .Select(instruction => (Instruction: instruction, Target: (MethodReference)instruction.Operand))
            .ToLookup(site => site.Target.Name, StringComparer.Ordinal);
        var participant = types[(DeliveryApplication, JobParticipant)];
        var postgres = types[(InfrastructureAssembly, JobPostgresParticipant)];
        if (!participant.IsAbstract || !postgres.IsSealed) offenders.Add("Job completion has one closed abstract/implementation pair");
        CheckSet(offenders, "job completion implementations", types.Values.Where(type => Derives(type, participant, types)).Select(TypeId), [TypeId(postgres)]);
        CheckSet(offenders, "closed job participant registration", sites.Where(site => site.Target is GenericInstanceMethod generic
                && generic.Name == "TryAddScoped" && generic.GenericArguments.Count == 2
                && generic.GenericArguments[0].FullName == JobParticipant && generic.GenericArguments[1].FullName == JobPostgresParticipant)
            .Select(site => site.Owner), [JobOwner(offenders, types, InfrastructureAssembly,
                "LearnStack.Infrastructure.Jobs.BackgroundJobContractsRegistration", "AddLearnStackBackgroundJobContracts")]);
        foreach (var type in new[] { types[(DeliveryApplication, JobCommitGate)], types[(DeliveryApplication, JobPublication)], types[(KernelAssembly, JobTrustedContext)] })
            if (!type.IsSealed) offenders.Add(type.FullName + " must remain sealed");
        var context = types[(KernelAssembly, JobTrustedContext)];
        if (context.Methods.Count(method => method.IsConstructor && !method.IsStatic) != 1
            || context.Methods.Any(method => method.IsConstructor && !method.IsStatic && !method.IsPrivate)
            || context.Fields.Any(field => !field.IsStatic && !field.IsInitOnly)
            || context.Properties.Any(property => property.SetMethod is not null))
            offenders.Add("Trusted job attribution is privately constructed and immutable");
        Cap(DeliveryApplication, JobParticipant, ".ctor", "System.Void", [LifecycleUnit, "LearnStack.SharedKernel.Tenancy.ITenantContextAccessor"],
            [(InfrastructureAssembly, JobPostgresParticipant, ".ctor")]);
        Cap(DeliveryApplication, JobParticipant, "Arm", "System.Void", [JobStartReceipt, JobCommitGate],
            [(InfrastructureAssembly, JobExecutorType, "ExecuteBusinessAsync")]);
        Cap(DeliveryApplication, JobParticipant, "RequireArmed", "System.Void", [JobStartReceipt, LifecycleUnit],
            [(DeliveryApplication, JobAdmission, "Enter")]);
        Cap(DeliveryApplication, JobParticipant, "RequireEntry", "System.Void", [LifecycleUnit, "System.Boolean", "LearnStack.SharedKernel.Persistence.TransactionMode"],
            [(DeliveryApplication, JobAdmission, "RequireOwnerEntry")]);
        Cap(DeliveryApplication, JobParticipant, "BindAndPreflightAsync", BoundaryTask,
            [LifecycleUnit, "System.Boolean", "System.Type", "System.Object", BoundaryCancellation],
            [(DeliveryApplication, JobAdmission, "BindOwnerAsync")]);
        Cap(DeliveryApplication, JobParticipant, "RequireRoot", "System.Void", [LifecycleUnit, "System.Type", "System.Object"],
            [(DeliveryApplication, JobParticipant, "FenceOwnerAsync"), (DeliveryApplication, JobParticipant, "FenceOwnerAsync"),
             (DeliveryApplication, JobParticipant, "TryBeginCommit")]);
        Cap(DeliveryApplication, JobParticipant, "FenceOwnerAsync", BoundaryTask,
            [LifecycleUnit, "System.Type", "System.Object", "LearnStack.SharedKernel.Results.IResultBase", BoundaryCancellation],
            [(DeliveryApplication, JobAdmission, "FenceOwnerAsync")]);
        Cap(DeliveryApplication, JobParticipant, "RequireCurrent", "System.Void", [],
            [(DeliveryApplication, JobParticipant, "BindAndPreflightAsync"), (DeliveryApplication, JobParticipant, "BindAndPreflightAsync"),
             (DeliveryApplication, JobParticipant, "RequireRoot"),
             (DeliveryApplication, JobParticipant, "RequireEntry"),
             (InfrastructureAssembly, JobPostgresParticipant, "PreflightAsync"), (InfrastructureAssembly, JobPostgresParticipant, "PreflightAsync"),
             (InfrastructureAssembly, JobPostgresParticipant, "FenceAsync"), (InfrastructureAssembly, JobPostgresParticipant, "FenceAsync"),
             (InfrastructureAssembly, JobPostgresParticipant, "FenceAsync"), (InfrastructureAssembly, JobPostgresParticipant, "FenceAsync"),
             (InfrastructureAssembly, JobPostgresParticipant, "FenceAsync"), (InfrastructureAssembly, JobPostgresParticipant, "CreateCommand"),
             (DeliveryApplication, JobParticipant, "BeginCommitBudgetRead"), (DeliveryApplication, JobParticipant, "RecordCommitBudget"),
             (InfrastructureAssembly, JobPostgresParticipant, "PreflightPrepareAsync"), (InfrastructureAssembly, JobPostgresParticipant, "PreflightPrepareAsync"),
             (InfrastructureAssembly, JobPostgresParticipant, "FencePrepareAsync"), (InfrastructureAssembly, JobPostgresParticipant, "FencePrepareAsync"),
             (InfrastructureAssembly, JobPostgresParticipant, "FencePrepareAsync"), (InfrastructureAssembly, JobPostgresParticipant, "FencePrepareAsync"),
             (InfrastructureAssembly, JobPostgresParticipant, "FencePrepareAsync"), (InfrastructureAssembly, JobPostgresParticipant, "FencePrepareAsync"),
             (InfrastructureAssembly, JobPostgresParticipant, "PrepareDefinition"), (InfrastructureAssembly, JobPostgresParticipant, "CaptureCommitBudgetAsync"),
             (InfrastructureAssembly, JobPostgresParticipant, "ReadFinalizationAsync"),
             (InfrastructureAssembly, JobPostgresParticipant, "ReadFinalizationAsync"),
             (InfrastructureAssembly, JobPostgresParticipant, "ReadFinalizationAsync"),
             (InfrastructureAssembly, JobPostgresParticipant, "ReadFinalizationAsync"),
             (InfrastructureAssembly, JobPostgresParticipant, "FenceFinalizeAsync"),
             (InfrastructureAssembly, JobPostgresParticipant, "FenceFinalizeAsync"),
             (InfrastructureAssembly, JobPostgresParticipant, "FenceFinalizeAsync"),
             (InfrastructureAssembly, JobPostgresParticipant, "FenceFinalizeAsync"),
             (InfrastructureAssembly, JobPostgresParticipant, "FenceFinalizeAsync"),
             (InfrastructureAssembly, JobPostgresParticipant, "FenceFinalizeAsync"),
             (InfrastructureAssembly, JobPostgresParticipant + "/FinalizationTerminalMint", "Mint"),
             (InfrastructureAssembly, JobPostgresParticipant + "/FinalizationTerminalMint", "Mint")]);
        Cap(DeliveryApplication, JobAdmission, "RequireOwnerEntry", JobParticipant,
            [LifecycleUnit, "LearnStack.SharedKernel.Persistence.TransactionMode", "System.Type", "LearnStack.SharedKernel.Tenancy.ITenantContext"],
            [(DeliveryApplication, DeliveryCoordinator, "ExecuteCoreAsync")]);
        Cap(DeliveryApplication, JobAdmission, "BindOwnerAsync", BoundaryTask,
            [JobParticipant, LifecycleUnit, "System.Boolean", BoundaryCancellation], [(DeliveryApplication, DeliveryCoordinator, "ExecuteFrameAsync")]);
        Cap(DeliveryApplication, JobAdmission, "FenceOwnerAsync", BoundaryTask,
            [JobParticipant, LifecycleUnit, "LearnStack.SharedKernel.Results.IResultBase", BoundaryCancellation],
            [(DeliveryApplication, DeliveryCoordinator, "ExecuteFrameAsync")]);
        Cap(DeliveryApplication, JobCommitGate, ".ctor", "System.Void", [JobRunGate, "LearnStack.Application.Jobs.BackgroundJobCheckpointKind"],
            [(DeliveryApplication, JobRunGate, "TryOpenBoundary")]);
        Cap(DeliveryApplication, JobPublication, ".ctor", "System.Void", [],
            [(InfrastructureAssembly, JobExecutorType, "ExecuteAsync")],
            [JobOwner(offenders, types, InfrastructureAssembly, OrchestrationExecution, ".ctor",
                [OrchestrationControl, OrchestrationInvocation, JobRoutingClaim, JobRunGate + "/RuntimeStop", "System.TimeProvider"])]);
        Cap(DeliveryApplication, JobCommitGate, "TryStartExecution", "System.Boolean", [],
            [(InfrastructureAssembly, JobExecutorType, "ExecuteBusinessAsync")]);
        Cap(DeliveryApplication, JobCommitGate, "TryBeginCommit", "System.Boolean", [],
            [(DeliveryApplication, JobStartRequest, "MarkCommitting"), (DeliveryApplication, CapacityCommand, "MarkCommitting")]);
        Cap(DeliveryApplication, JobCommitGate, "RecordCommitted", "System.Void", [],
            [(DeliveryApplication, DeliveryCoordinator, "ExecuteFrameAsync"), (DeliveryApplication, JobStartRequest, "RecordCommitted"),
             (DeliveryApplication, CapacityCommand, "RecordCommitted"), (DeliveryApplication, JobCheckpointOwner, "RecordCommitted"),
             (DeliveryApplication, JobRecipientParticipant, "RecordCommitted")]);
        Cap(DeliveryApplication, JobCommitGate, "RecordRolledBack", "System.Void", [],
            [(DeliveryApplication, DeliveryCoordinator, "ExecuteFrameAsync"), (DeliveryApplication, DeliveryCoordinator, "ExecuteFrameAsync"),
             (DeliveryApplication, JobStartRequest, "RecordRolledBack"), (DeliveryApplication, CapacityCommand, "RecordRolledBack"),
             (DeliveryApplication, JobCheckpointOwner, "RecordRolledBack"), (DeliveryApplication, JobRecipientParticipant, "RecordRolledBack")]);
        Cap(DeliveryApplication, JobParticipant, "BeginCommitBudgetRead", "System.Void", [],
            [(InfrastructureAssembly, JobPostgresParticipant, "CaptureCommitBudgetAsync")]);
        Cap(DeliveryApplication, JobParticipant, "RecordCommitBudget", "System.Void", ["System.Data.Common.DbDataReader"],
            [(InfrastructureAssembly, JobPostgresParticipant, "CaptureCommitBudgetAsync")]);
        Cap(DeliveryApplication, JobParticipant, "TryBeginCommit", "System.Boolean", [LifecycleUnit, "System.Type", "System.Object"],
            [(DeliveryApplication, JobAdmission, "TryBeginOwnerCommit")]);
        Cap(DeliveryApplication, JobAdmission, "TryBeginOwnerCommit", "System.Boolean", [JobParticipant, LifecycleUnit],
            [(DeliveryApplication, DeliveryCoordinator, "ExecuteFrameAsync")]);
        Cap(DeliveryApplication, JobAdmission, "RequireRootParticipant", JobAdmission + "/RequestFrame", [JobParticipant, LifecycleUnit],
            [(DeliveryApplication, JobAdmission, "BindOwnerAsync"), (DeliveryApplication, JobAdmission, "BindOwnerAsync"),
             (DeliveryApplication, JobAdmission, "FenceOwnerAsync"), (DeliveryApplication, JobAdmission, "FenceOwnerAsync"),
             (DeliveryApplication, JobAdmission, "TryBeginOwnerCommit")]);
        Cap(InfrastructureAssembly, JobPostgresParticipant, "CaptureCommitBudgetAsync", BoundaryTask, [BoundaryCancellation],
            [(InfrastructureAssembly, JobPostgresParticipant, "FenceAsync"), (InfrastructureAssembly, JobPostgresParticipant, "FencePrepareAsync"),
             (InfrastructureAssembly, JobPostgresParticipant, "FenceFinalizeAsync")]);
        Cap(InfrastructureAssembly, JobPostgresParticipant, "BindFinalizationRead", "LearnStack.Application.Jobs.BackgroundJobExternalSnapshot",
            [JobStartReceipt, "LearnStack.Infrastructure.Jobs.JobExternalReadHandle"], [(InfrastructureAssembly, JobExecutorType, "ExecuteBusinessAsync")]);
        Cap(InfrastructureAssembly, JobPostgresParticipant, "PreflightFinalizeAsync", BoundaryTask,
            [BoundaryCancellation], [(InfrastructureAssembly, JobPostgresParticipant, "PreflightAsync")]);
        Cap(InfrastructureAssembly, JobPostgresParticipant, "ReadFinalizationAsync",
            "System.Threading.Tasks.Task`1<LearnStack.Application.Jobs.BackgroundJobExternalSnapshot>", [BoundaryCancellation],
            [(InfrastructureAssembly, JobPostgresParticipant, "PreflightFinalizeAsync"), (InfrastructureAssembly, JobPostgresParticipant, "FenceFinalizeAsync")]);
        Cap(InfrastructureAssembly, JobPostgresParticipant, "FenceFinalizeAsync", BoundaryTask,
            ["LearnStack.SharedKernel.Results.IResultBase", BoundaryCancellation], [(InfrastructureAssembly, JobPostgresParticipant, "FenceAsync")]);
        Cap(DeliveryApplication, JobCommitGate, "TryBeginCheckpointCommit", "System.Boolean", ["System.Int64", "System.TimeSpan"],
            [(DeliveryApplication, JobParticipant, "TryBeginCommit"), (DeliveryApplication, JobCheckpointOwner, "MarkCommitting"),
             (DeliveryApplication, JobRecipientParticipant, "MarkCommitting")]);
        Cap(DeliveryApplication, JobRunGate, "TryBeginCheckpointCommit", "System.Boolean", [JobCommitGate, "System.Int64", "System.TimeSpan"],
            [(DeliveryApplication, JobCommitGate, "TryBeginCheckpointCommit")]);
        Cap(DeliveryApplication, JobRunGate, "TryBeginCommit", "System.Boolean", [JobCommitGate],
            [(DeliveryApplication, JobCommitGate, "TryBeginCommit")]);
        Cap(DeliveryApplication, JobCommitGate, "TryBeginCheckpointCommitOwned", "System.Boolean", ["System.Int64", "System.TimeSpan"],
            [(DeliveryApplication, JobRunGate, "TryBeginCheckpointCommit")]);
        Cap(DeliveryApplication, JobCommitGate, "TryBeginCommitOwned", "System.Boolean", [],
            [(DeliveryApplication, JobRunGate, "TryBeginCommit"), (DeliveryApplication, JobCommitGate, "TryBeginCheckpointCommitOwned")]);
        Cap(DeliveryApplication, JobCommitGate, "TrySealUnusedExecution", "System.Boolean", [],
            [(InfrastructureAssembly, OrchestrationInvocation, "PrepareCapacityCleanupCoreAsync")]);
        Cap(DeliveryApplication, JobPublication, "Bind", "System.Void", [JobStartRequest], [(InfrastructureAssembly, JobExecutorType, "ExecutePublishedAsync")]);
        Cap(DeliveryApplication, JobPublication, "Publish", "System.Void", [JobStartRequest, JobStartReceipt], [(DeliveryApplication, JobStartRequest, "RecordCommitted")]);
        Cap(DeliveryApplication, JobPublication, "SettleNoReceipt", "System.Void", [], [(InfrastructureAssembly, JobExecutorType, "ExecutePublishedAsync")]);
        Cap(DeliveryApplication, JobStartRequest, "BindPublication", "System.Void", [JobPublication], [(DeliveryApplication, JobPublication, "Bind")]);
        Cap(DeliveryApplication, JobCommitGate, "Close", "System.Void", [],
            [(DeliveryApplication, JobCommitGate, "TrySealUnusedExecution"),
             (InfrastructureAssembly, JobExecutorType, "CloseBusinessGate"),
             (InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.BackgroundJobControl", "FinishBusinessAsync"),
             (InfrastructureAssembly, JobExecutorType, "ExecuteRecipientAsync"),
             (InfrastructureAssembly, JobExecutorType, "ExecuteRecipientAsync"),
             (InfrastructureAssembly, JobExecutorType, "ExecuteRecipientAsync"),
             (InfrastructureAssembly, JobExecutorType, "StopRecipient"),
             (InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.BackgroundJobRecipientOperation", "RequestStop"),
             (InfrastructureAssembly, OrchestrationInvocation, "PrepareCapacityCleanupCoreAsync")]);
        Cap(InfrastructureAssembly, JobExecutorType, "ExecuteBusinessAsync",
            "System.Threading.Tasks.Task`1<LearnStack.Infrastructure.Jobs.JobBusinessExecutionResult>",
            [JobStartReceipt, JobCommitGate, BoundaryCancellation], [(InfrastructureAssembly, JobBoundInvocation, "RunFrameworkAsync"),
             (InfrastructureAssembly, OrchestrationExecution, "ExecuteExternalNormalAsync")]);
        Cap(InfrastructureAssembly, JobExecutorType, "ExecutePublishedAsync",
            "System.Threading.Tasks.Task`1<LearnStack.Infrastructure.Jobs.JobStartExecutionResult>",
            [JobRoutingClaim, JobPublication, JobCommitGate, "System.Func`1<System.Boolean>", BoundaryCancellation],
            [(InfrastructureAssembly, JobExecutorType, "ExecuteAsync"), (InfrastructureAssembly, JobBoundInvocation, "RunFrameworkAsync")]);
        Cap(InfrastructureAssembly, JobExecutorType, "get_ScopeFactory", "Microsoft.Extensions.DependencyInjection.IServiceScopeFactory", [],
            [(InfrastructureAssembly, OrchestrationControl, ".ctor")]);
        Cap(KernelAssembly, JobTrustedContext, "FromValidatedJob", JobTrustedContext,
            ["LearnStack.SharedKernel.Identifiers.TenantId", "System.Nullable`1<LearnStack.SharedKernel.Identifiers.OrganizationId>",
             "LearnStack.SharedKernel.Jobs.BackgroundJobId", "System.Nullable`1<LearnStack.SharedKernel.Jobs.BackgroundJobId>",
             "System.Nullable`1<System.Guid>", "System.Nullable`1<LearnStack.SharedKernel.Identifiers.UserId>", "System.String", "System.String"],
            [(InfrastructureAssembly, JobExecutorType, "CreateContext")]);
        Cap(KernelAssembly, JobTrustedContext, ".ctor", "System.Void",
            ["LearnStack.SharedKernel.Identifiers.TenantId", "System.Nullable`1<LearnStack.SharedKernel.Identifiers.OrganizationId>",
             "System.Nullable`1<LearnStack.SharedKernel.Jobs.BackgroundJobId>", "System.Nullable`1<LearnStack.SharedKernel.Jobs.BackgroundJobId>",
             "System.Nullable`1<System.Guid>", "System.Nullable`1<LearnStack.SharedKernel.Identifiers.UserId>", "System.String", "System.String"],
            [(KernelAssembly, JobTrustedContext, "FromValidatedJob"), (KernelAssembly, JobTrustedContext, "ForRecoveryRead"),
             (KernelAssembly, JobTrustedContext, "ForRecipient")]);
        Cap(KernelAssembly, JobTrustedContext, "ForRecipient", JobTrustedContext,
            ["LearnStack.SharedKernel.Identifiers.TenantId", "System.Nullable`1<LearnStack.SharedKernel.Identifiers.OrganizationId>",
             "System.String", "System.String"], [(InfrastructureAssembly, JobExecutorType, "ExecuteRecipientAsync")]);
        Cap(KernelAssembly, JobTrustedContext, "ForRecoveryRead", JobTrustedContext,
            ["LearnStack.SharedKernel.Identifiers.TenantId", "System.Nullable`1<LearnStack.SharedKernel.Identifiers.OrganizationId>",
             "LearnStack.SharedKernel.Jobs.BackgroundJobId", "System.String"],
            [(InfrastructureAssembly, JobExecutorType, "ReadExternalStateCoreAsync")]);
        var diagnosticTypes = types[(KernelAssembly, AsyncDiagnostics)].Methods.SelectMany(method => method.HasBody
                ? method.Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Isinst).Select(instruction => ((TypeReference)instruction.Operand).FullName)
                : []);
        CheckSet(offenders, "closed asynchronous diagnostic contexts", diagnosticTypes,
            [JobTrustedContext, "LearnStack.SharedKernel.Tenancy.EventTenantContext"]);
        CheckOrder();
        CheckSql();
        CheckCommitBudget();
        return offenders;

        void Cap(string assembly, string typeName, string name, string returnType, string[] parameters,
            (string Assembly, string Type, string Method)[] owners, string[]? exactOwners = null)
        {
            var type = types[(assembly, typeName)];
            var methods = type.Methods.Where(method => method.Name == name).ToArray();
            if (methods.Length != 1 || methods[0].ReturnType.FullName != returnType || !ParametersMatch(methods[0], parameters))
                offenders.Add(typeName + "." + name + " requires its exact closed signature");
            CheckSet(offenders, typeName + "." + name + " authority callers",
                sitesByTargetName[name].Where(site => SameType(site.Target.DeclaringType, type)).Select(site => site.Owner),
                owners.Select(owner => JobOwner(offenders, types, owner.Assembly, owner.Type, owner.Method)).Concat(exactOwners ?? []));
            foreach (var site in instructionsByTargetName[name].Where(site => SameType(site.Target.DeclaringType, type)))
                if (site.Instruction.OpCode != OpCodes.Call && site.Instruction.OpCode != OpCodes.Callvirt
                    && !(name == ".ctor" && site.Instruction.OpCode == OpCodes.Newobj))
                    offenders.Add(typeName + "." + name + " cannot escape as a delegate or constructor");
        }

        void CheckOrder()
        {
            var body = OwnedBodies(census).Where(item => item.Owner.DeclaringType.FullName == DeliveryCoordinator
                && item.Owner.Name == "ExecuteFrameAsync" && item.Body.Name == "MoveNext").SelectMany(item => item.Body.Body.Instructions).ToArray();
            int Find(string type, string method) => Array.FindIndex(body, instruction => instruction.Operand is MethodReference target
                && target.DeclaringType.FullName == type && target.Name == method);
            var drain = Find("LearnStack.Application.Messaging.DomainEventCollector", "DrainAsync");
            var audit = Find("LearnStack.SharedKernel.Audit.IAuditStore", "WritePendingAsync");
            var fence = Find(JobAdmission, "FenceOwnerAsync");
            var gate = Find(JobAdmission, "TryBeginOwnerCommit");
            var complete = Find(LifecycleScope, "CompleteAsync");
            var acknowledged = Find(JobCommitGate, "RecordCommitted");
            var bind = Find(JobAdmission, "BindOwnerAsync");
            var handler = Array.FindIndex(body, instruction => instruction.Operand is MethodReference target
                && target.Name == "Invoke" && target.DeclaringType.FullName.StartsWith("System.Func`1", StringComparison.Ordinal));
            if (drain < 0 || audit <= drain || fence <= audit || gate <= fence || complete <= gate || acknowledged <= complete
                || bind < 0 || handler <= bind
                || body.Skip(gate + 1).Take(Math.Max(0, complete - gate - 1)).Any(instruction =>
                    instruction.Operand is MethodReference target && target.Name == "GetAwaiter")
                || !body.Skip(complete + 1).Take(Math.Max(0, acknowledged - complete - 1)).Any(instruction =>
                    instruction.Operand is MethodReference target && target.Name == "GetResult"
                    && target.DeclaringType.FullName.StartsWith("System.Runtime.CompilerServices.TaskAwaiter", StringComparison.Ordinal)))
                offenders.Add("Job completion must drain, write MUST audit, fence, CAS, await existing Complete, then record ACK");
        }

        void CheckCommitBudget()
        {
            foreach (var (assembly, type, name, visibility, isStatic) in new[]
                     {
                         (DeliveryApplication, JobParticipant, "BeginCommitBudgetRead", MethodAttributes.Family, false),
                         (DeliveryApplication, JobParticipant, "RecordCommitBudget", MethodAttributes.Family, false),
                         (DeliveryApplication, JobParticipant, "TryBeginCommit", MethodAttributes.Assembly, false),
                         (DeliveryApplication, JobAdmission, "TryBeginOwnerCommit", MethodAttributes.Assembly, true),
                         (InfrastructureAssembly, JobPostgresParticipant, "CaptureCommitBudgetAsync", MethodAttributes.Private, false),
                         (DeliveryApplication, JobCommitGate, "TryBeginCheckpointCommit", MethodAttributes.Assembly, false),
                         (DeliveryApplication, JobCommitGate, "TryBeginCheckpointCommitOwned", MethodAttributes.Assembly, false),
                         (DeliveryApplication, JobRunGate, "TryBeginCheckpointCommit", MethodAttributes.Assembly, false),
                     })
            {
                var matches = types[(assembly, type)].Methods.Where(method => method.Name == name).ToArray();
                if (matches.Length != 1)
                {
                    offenders.Add(type + "." + name + " requires one closed budget declaration");
                    continue;
                }
                var method = matches[0];
                if ((method.Attributes & MethodAttributes.MemberAccessMask) != visibility
                    || method.IsStatic != isStatic || method.IsVirtual || method.HasGenericParameters)
                    offenders.Add(type + "." + name + " requires its closed nonvirtual visibility");
            }
            foreach (var (fieldName, writerNames) in new[]
                     {
                         ("_commitBudgetStartedAt", new[] { "BeginCommitBudgetRead" }),
                         ("_commitBudgetRemaining", new[] { "RecordCommitBudget" }),
                         ("_commitBudgetState", new[] { "BeginCommitBudgetRead", "RecordCommitBudget", "TryBeginCommit" }),
                     })
            {
                var fields = participant.Fields.Where(field => field.Name == fieldName).ToArray();
                if (fields.Length != 1 || !fields[0].IsPrivate || fields[0].IsStatic)
                    offenders.Add("Missing private owner-local budget field: " + fieldName);
                var accesses = census.FieldInstructions(fieldName)
                    .Where(item => SameType(item.Target.DeclaringType, participant)).ToArray();
                var writes = accesses.Where(item => item.Instruction.OpCode ==
                        (fieldName == "_commitBudgetState" ? OpCodes.Ldflda : OpCodes.Stfld))
                    .Select(item => item.Owner.Name).ToArray();
                CheckSet(offenders, "required one-way budget writes for " + fieldName, writes,
                    fieldName == "_commitBudgetState"
                        ? ["BeginCommitBudgetRead", "BeginCommitBudgetRead", "RecordCommitBudget", "RecordCommitBudget", "TryBeginCommit"]
                        : writerNames);
                foreach (var item in accesses.Where(item => item.Instruction.OpCode == OpCodes.Stfld
                                                       || item.Instruction.OpCode == OpCodes.Ldflda))
                    if (fieldName == "_commitBudgetState" && item.Instruction.OpCode == OpCodes.Stfld
                        || item.Owner.DeclaringType.FullName != JobParticipant
                        || !writerNames.Contains(item.Owner.Name, StringComparer.Ordinal))
                        offenders.Add("A different method cannot rewrite the original completion budget: " + MethodId(item.Owner));
            }
            var capture = bodies.Where(item => item.Owner.DeclaringType.FullName == JobPostgresParticipant
                && item.Owner.Name == "CaptureCommitBudgetAsync").SelectMany(item => item.Body.Body.Instructions).ToArray();
            var projections = capture.Where(instruction => instruction.OpCode == OpCodes.Ldstr
                && instruction.Operand is string sql && sql.StartsWith("SELECT ", StringComparison.Ordinal))
                .Select(instruction => (string)instruction.Operand).ToArray();
            var transactionalPreflight = bodies.Where(item => item.Owner.DeclaringType.FullName == JobPostgresParticipant
                    && item.Owner.Name == "PreflightAsync").SelectMany(item => item.Body.Body.Instructions)
                .Where(instruction => instruction.OpCode == OpCodes.Ldstr && instruction.Operand is string text
                    && text.StartsWith("SELECT ", StringComparison.Ordinal)).Select(instruction => (string)instruction.Operand).ToArray();
            const string from = " FROM background_jobs WHERE ";
            if (transactionalPreflight.Length != 1 || !transactionalPreflight[0].Contains(from, StringComparison.Ordinal)
                || !projections.Contains("SELECT ownership_until,attempt_deadline,clock_timestamp()"
                    + transactionalPreflight[0][transactionalPreflight[0].IndexOf(from, StringComparison.Ordinal)..], StringComparer.Ordinal))
                offenders.Add("The budget preserves every validated transactional identity/live predicate");
            CheckSet(offenders, "Prepare and Finalize budget identity/live calls", capture.Where(instruction =>
                        instruction.Operand is MethodReference method && method.DeclaringType.FullName
                            == "LearnStack.Infrastructure.Jobs.BackgroundJobExternalCheckpointSql" && method.Name is "Identity" or "Live")
                    .Select(instruction => ((MethodReference)instruction.Operand).Name + ":" + instruction.OpCode.Name), ["Identity:call", "Live:call", "Identity:call", "Live:call"]);
            if (projections.Length != 3
                || !projections.Any(sql => sql.StartsWith("SELECT ownership_until,attempt_deadline,clock_timestamp() FROM background_jobs WHERE ", StringComparison.Ordinal))
                || projections.Count(sql => sql == "SELECT j.ownership_until,j.attempt_deadline,clock_timestamp() FROM background_jobs j WHERE ") != 2
                || !capture.Any(instruction => instruction.OpCode == OpCodes.Ldstr
                    && instruction.Operand is string text && text == " AND j.phase='preparing' AND j.plan IS NULL")
                || !capture.Any(instruction => instruction.OpCode == OpCodes.Ldstr
                    && instruction.Operand is string text && text == " AND j.phase='finalizing'"))
                offenders.Add("The completion budget requires all three exact post-lock database-time projections");
            int At(string type, string name) => Array.FindIndex(capture, instruction => instruction.Operand is MethodReference method
                && method.DeclaringType.FullName == type && method.Name == name);
            var begin = At(JobParticipant, "BeginCommitBudgetRead");
            var query = At("Npgsql.NpgsqlCommand", "ExecuteReaderAsync");
            var read = At("System.Data.Common.DbDataReader", "ReadAsync");
            var record = At(JobParticipant, "RecordCommitBudget");
            if (begin < 0 || query <= begin || read <= query || record <= read)
                offenders.Add("Completion budget starts before the actual query and records only its returned row: "
                    + $"begin={begin}, query={query}, read={read}, record={record}; "
                    + string.Join("; ", capture.Where(instruction => instruction.Operand is MethodReference method
                        && method.Name is "BeginCommitBudgetRead" or "ExecuteReaderAsync" or "ReadAsync" or "RecordCommitBudget")
                        .Select(instruction => ((MethodReference)instruction.Operand).FullName)));
            foreach (var fenceName in new[] { "FenceAsync", "FencePrepareAsync", "FenceFinalizeAsync" })
            {
                var fenceBody = bodies.Where(item => item.Owner.DeclaringType.FullName == JobPostgresParticipant
                    && item.Owner.Name == fenceName).SelectMany(item => item.Body.Body.Instructions).ToArray();
                var locked = Array.FindIndex(fenceBody, instruction => instruction.Operand is MethodReference method
                    && method.DeclaringType.FullName == "System.Data.Common.DbCommand" && method.Name == "ExecuteScalarAsync");
                var budget = Array.FindIndex(fenceBody, instruction => instruction.Operand is MethodReference method
                    && method.DeclaringType.FullName == JobPostgresParticipant && method.Name == "CaptureCommitBudgetAsync");
                var write = Array.FindIndex(fenceBody, instruction => instruction.OpCode == OpCodes.Ldstr
                    && instruction.Operand is string sql && sql.StartsWith("UPDATE background_jobs", StringComparison.Ordinal));
                if (locked < 0 || budget <= locked || write <= budget)
                    offenders.Add(fenceName + $" must lock, capture live time, then write completion: lock={locked}, capture={budget}, write={write}; "
                        + string.Join("; ", fenceBody.Where(instruction => instruction.Operand is MethodReference method
                            && method.Name is "ExecuteScalarAsync" or "CaptureCommitBudgetAsync")
                            .Select(instruction => ((MethodReference)instruction.Operand).FullName)));
            }
            var budgeted = types[(DeliveryApplication, JobRunGate)].Methods.Where(method => method.Name == "TryBeginCheckpointCommit").ToArray();
            if (budgeted.Length != 1 || !budgeted[0].HasBody) return; // The exact capability check already reports this.
            var runBody = budgeted[0].Body;
            var elapsed = runBody.Instructions.Where(instruction => instruction.Operand is MethodReference method
                && method.DeclaringType.FullName == "System.Diagnostics.Stopwatch" && method.Name == "GetElapsedTime"
                && ParametersMatch(method, ["System.Int64"]) && instruction.OpCode == OpCodes.Call).ToArray();
            var cas = runBody.Instructions.Where(instruction => instruction.Operand is MethodReference method
                && method.DeclaringType.FullName == JobCommitGate && method.Name == "TryBeginCheckpointCommitOwned"
                && ParametersMatch(method, ["System.Int64", "System.TimeSpan"])
                && instruction.OpCode is { } opcode && (opcode == OpCodes.Call || opcode == OpCodes.Callvirt)).ToArray();
            var entered = runBody.Instructions.Where(instruction => instruction.Operand is MethodReference method
                && method.DeclaringType.FullName == "System.Threading.Monitor" && method.Name == "Enter"
                && instruction.OpCode == OpCodes.Call).ToArray();
            var exited = runBody.Instructions.Where(instruction => instruction.Operand is MethodReference method
                && method.DeclaringType.FullName == "System.Threading.Monitor" && method.Name == "Exit"
                && instruction.OpCode == OpCodes.Call).ToArray();
            if (elapsed.Length != 1 || cas.Length != 1 || entered.Length != 1 || exited.Length != 1)
            {
                offenders.Add("The budgeted CAS requires one elapsed observation and actual run monitor ownership");
                return;
            }
            var elapsedIndex = runBody.Instructions.IndexOf(elapsed[0]);
            var casIndex = runBody.Instructions.IndexOf(cas[0]);
            var forwarded = runBody.Instructions.Take(casIndex).Where(instruction => instruction.OpCode != OpCodes.Nop).TakeLast(3).ToArray();
            if (forwarded.Length != 3 || forwarded[0].OpCode != OpCodes.Ldarg_1
                || forwarded[1].OpCode != OpCodes.Ldarg_2 || forwarded[2].OpCode != OpCodes.Ldarg_3)
                offenders.Add("The locked run forwards its exact boundary and observed interval to the owned helper");
            if (runBody.Instructions.IndexOf(entered[0]) >= elapsedIndex || elapsedIndex >= casIndex
                || !runBody.ExceptionHandlers.Any(handler => handler.HandlerType == ExceptionHandlerType.Finally
                    && runBody.Instructions.Contains(handler.TryStart) && runBody.Instructions.Contains(handler.TryEnd)
                    && runBody.Instructions.Contains(handler.HandlerStart)
                    && (handler.HandlerEnd is null || runBody.Instructions.Contains(handler.HandlerEnd))
                    && runBody.Instructions.IndexOf(handler.TryStart) <= elapsedIndex
                    && runBody.Instructions.IndexOf(handler.TryEnd) > casIndex
                    && runBody.Instructions.IndexOf(handler.HandlerStart) <= runBody.Instructions.IndexOf(exited[0])
                    && (handler.HandlerEnd is null || runBody.Instructions.IndexOf(handler.HandlerEnd) > runBody.Instructions.IndexOf(exited[0]))))
                offenders.Add("Elapsed observation and owned checkpoint admission must share the same acquired run lock");

            var ownedMethods = types[(DeliveryApplication, JobCommitGate)].Methods
                .Where(method => method.Name == "TryBeginCheckpointCommitOwned").ToArray();
            if (ownedMethods.Length != 1 || !ownedMethods[0].HasBody)
            {
                offenders.Add("The exact owned checkpoint helper requires its real synchronous body");
                return;
            }
            var owned = ownedMethods[0].Body.Instructions.ToArray();
            var issued = FieldAddress("_issued");
            var state = FieldAddress("_state");
            var window = FieldAddress("_armCommitWindow");
            var reads = owned.Select((instruction, index) => (instruction, index)).Where(item =>
                item.instruction.OpCode == OpCodes.Call && item.instruction.Operand is MethodReference method
                && method.DeclaringType.FullName == "System.Threading.Volatile" && method.Name == "Read"
                && ParametersMatch(method, ["System.Int32&"])).Select(item => item.index).ToArray();
            var kind = CallIndex(JobCommitGate, "get_Kind", OpCodes.Call);
            var constructed = CallIndex(JobCommitGate + "/ArmCommitWindow", ".ctor", OpCodes.Newobj);
            var windowCas = owned.Select((instruction, index) => (instruction, index)).Where(item =>
                item.instruction.OpCode == OpCodes.Call && item.instruction.Operand is GenericInstanceMethod method
                && method.DeclaringType.FullName == "System.Threading.Interlocked" && method.Name == "CompareExchange"
                && method.GenericArguments.Count == 1 && method.GenericArguments[0].FullName == JobCommitGate + "/ArmCommitWindow")
                .Select(item => item.index).ToArray();
            var finalCas = CallIndex(JobCommitGate, "TryBeginCommitOwned", OpCodes.Call);
            var branches = owned.Select((instruction, index) => (instruction, index))
                .Where(item => item.instruction.OpCode.FlowControl == FlowControl.Cond_Branch).Select(item => item.index).ToArray();
            if (issued < 0 || state < 0 || window < 0 || reads.Length != 2 || kind < 0 || constructed < 0
                || windowCas.Length != 1 || finalCas < 0 || branches.Length != 4
                || owned.Count(instruction => instruction.Operand is MethodReference) != 6)
            {
                offenders.Add("The owned helper requires issued/Open checks, one Arm window CAS, and the existing COMMIT CAS");
                return;
            }
            var windowArguments = owned.Take(constructed).Where(instruction => instruction.OpCode != OpCodes.Nop).TakeLast(2).ToArray();
            if (!(issued < reads[0] && reads[0] < state && state < reads[1] && reads[1] < kind
                    && kind < window && window < constructed && constructed < windowCas[0] && windowCas[0] < finalCas)
                || !branches.Any(index => reads[0] < index && index < state)
                || !branches.Any(index => reads[1] < index && index < kind)
                || !branches.Any(index => kind < index && index < window)
                || !branches.Any(index => windowCas[0] < index && index < finalCas)
                || !owned.Skip(reads[1] + 1).Take(kind - reads[1] - 1).Any(instruction => instruction.OpCode == OpCodes.Ret)
                || !owned.Skip(windowCas[0] + 1).Take(finalCas - windowCas[0] - 1).Any(instruction => instruction.OpCode == OpCodes.Ret)
                || windowArguments.Length != 2 || windowArguments[0].OpCode != OpCodes.Ldarg_1 || windowArguments[1].OpCode != OpCodes.Ldarg_2)
                offenders.Add("Issued/Open refusal and exact Arm-window publication must precede the owned COMMIT CAS");

            int FieldAddress(string name)
            {
                var matches = owned.Select((instruction, index) => (instruction, index)).Where(item =>
                    item.instruction.OpCode == OpCodes.Ldflda && item.instruction.Operand is FieldReference field
                    && field.DeclaringType.FullName == JobCommitGate && field.Name == name).Select(item => item.index).ToArray();
                return matches.Length == 1 ? matches[0] : -1;
            }
            int CallIndex(string type, string name, OpCode opcode)
            {
                var matches = owned.Select((instruction, index) => (instruction, index)).Where(item =>
                    item.instruction.OpCode == opcode && item.instruction.Operand is MethodReference method
                    && method.DeclaringType.FullName == type && method.Name == name).Select(item => item.index).ToArray();
                return matches.Length == 1 ? matches[0] : -1;
            }
        }

        void CheckSql()
        {
            var sql = OwnedBodies(census).Where(item => item.Owner.DeclaringType.FullName == JobPostgresParticipant)
                .SelectMany(item => item.Body.Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Ldstr
                    && instruction.Operand is string).Select(instruction => (item.Owner.Name, Text: (string)instruction.Operand))).ToArray();
            var preflight = sql.Where(item => item.Name == "PreflightAsync" && item.Text.StartsWith("SELECT ", StringComparison.Ordinal)).Select(item => item.Text).ToArray();
            var locked = sql.Where(item => item.Name == "FenceAsync" && item.Text.StartsWith("SELECT TRUE FROM background_jobs", StringComparison.Ordinal)).Select(item => item.Text).ToArray();
            var update = sql.Where(item => item.Name == "FenceAsync" && item.Text.StartsWith("UPDATE background_jobs", StringComparison.Ordinal)).Select(item => item.Text).ToArray();
            if (preflight.Length != 1 || locked.Length != 1 || update.Length != 1)
            { offenders.Add("Job completion requires its three distinct production SQL statements"); return; }
            foreach (var statement in preflight.Concat(locked).Concat(update))
                foreach (var predicate in new[] { "tenant_id = @tenant", "organization_id IS NOT DISTINCT FROM @organization",
                             "definition_key = @definition", "profile = 'transactional'", "queue = @queue", "capacity_group IS NOT DISTINCT FROM @capacity",
                             "wake_generation = @generation", "current_attempt_id = @attempt", "normal_attempts = @ordinal",
                             "ownership_token = @token", "ownership_kind = 'execution'", "phase = 'executing'", "attempt_deadline = @deadline",
                             "processed_at IS NULL", "dead_lettered_at IS NULL", "terminal_reason IS NULL",
                             "a.id = @attempt", "a.job_id = background_jobs.id", "a.stage = 'normal'", "a.ordinal = @ordinal",
                             "a.admission_token = @token", "a.admitted_at = @admitted", "a.deadline = @deadline",
                             "@capacity::text IS NULL OR EXISTS", "r.tenant_id = @tenant", "r.capacity_group = @capacity",
                             "r.job_id = background_jobs.id", "r.attempt_id = @attempt", "r.released_at IS NULL" })
                    if (!statement.Contains(predicate, StringComparison.Ordinal)) offenders.Add("Missing exact job fence predicate: " + predicate);
            if (preflight[0].Contains("FOR UPDATE", StringComparison.Ordinal)
                || !locked[0].EndsWith("FOR UPDATE", StringComparison.Ordinal) || locked[0].Contains("clock_timestamp", StringComparison.Ordinal)
                || !update[0].Contains("SET phase = 'completed', processed_at = clock_timestamp()", StringComparison.Ordinal))
                offenders.Add("Only final completion locks the row, with fresh time evaluated by the following update");
            foreach (var statement in preflight.Concat(update))
                if (!statement.Contains("ownership_until > clock_timestamp()", StringComparison.Ordinal)
                    || !statement.Contains("attempt_deadline > clock_timestamp()", StringComparison.Ordinal))
                    offenders.Add("Job preflight and final update require strict fresh database lease/deadline fences");
            var releases = sql.Where(item => item.Name == "FenceAsync" && item.Text.StartsWith("UPDATE background_job_reservations", StringComparison.Ordinal))
                .Select(item => item.Text).ToArray();
            if (releases.Length != 1 || JobCapacityCompletionReleasePredicates
                .Any(predicate => !releases[0].Contains(predicate, StringComparison.Ordinal)))
                offenders.Add("Grouped completion releases exactly its existing reservation on the fenced business owner");
        }
    }
}
