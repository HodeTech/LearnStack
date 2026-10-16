using FluentAssertions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed partial class OutboxDispatchConventionTests
{
    private const string JobParticipant = "LearnStack.Application.Jobs.BackgroundJobCompletionParticipant";
    private const string JobPostgresParticipant = "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobCompletion";
    private const string JobCommitGate = "LearnStack.Application.Jobs.BackgroundJobCommitGate";
    private const string JobPublication = "LearnStack.Application.Jobs.BackgroundJobStartPublication";
    private const string JobTrustedContext = "LearnStack.SharedKernel.Tenancy.JobTenantContext";
    private const string AsyncDiagnostics = "LearnStack.SharedKernel.Tenancy.AsyncExecutionDiagnostics";

    [Fact]
    public void Background_job_business_uses_only_the_existing_ordinary_owner_and_closed_completion_capabilities()
    {
        using var census = ProductionCensus.Read();
        JobOwnerOffenders(census).Should().BeEmpty(
            "Fix: retain one exact receipt/unit/root participant and the existing owner-only Complete boundary");
    }

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
        var sqlSites = OwnedBodies(census).Where(item => item.Owner.DeclaringType.FullName == JobPostgresParticipant)
            .SelectMany(item => item.Body.Body.Instructions).Where(instruction => instruction.OpCode == OpCodes.Ldstr
                && instruction.Operand is string sql && sql.Contains("FROM background_jobs", StringComparison.Ordinal)
                    || instruction.OpCode == OpCodes.Ldstr && instruction.Operand is string update
                    && update.StartsWith("UPDATE background_jobs", StringComparison.Ordinal)).ToArray();
        sqlSites.Should().HaveCount(3, "the real unlocked preflight, exact row lock and fresh-time UPDATE are distinct");
        foreach (var sql in sqlSites)
            AssertMutation(census, "a removed actual preflight or final-fence SQL", () =>
                ReplaceInstruction(sql, OpCodes.Ldstr, "missing job SQL fence"), JobOwnerOffenders);
    }

    private static (string Assembly, string Type)[] JobOwnerSubjects() =>
    [
        (DeliveryApplication, JobParticipant), (InfrastructureAssembly, JobPostgresParticipant),
        (DeliveryApplication, JobCommitGate), (DeliveryApplication, JobPublication),
        (KernelAssembly, JobTrustedContext), (KernelAssembly, AsyncDiagnostics),
    ];

    private static IEnumerable<MethodDefinition> JobOwnerCapabilities(ProductionCensus census)
    {
        foreach (var (assembly, type, names) in new[]
                 {
                     (DeliveryApplication, JobParticipant, new[] { ".ctor", "Arm", "RequireArmed", "RequireEntry", "BindAndPreflightAsync", "RequireRoot", "FenceOwnerAsync", "RequireCurrent" }),
                     (DeliveryApplication, JobCommitGate, new[] { ".ctor", "TryStartExecution", "TryBeginCommit", "RecordCommitted", "RecordRolledBack", "Close" }),
                     (DeliveryApplication, JobPublication, new[] { ".ctor", "Bind", "Publish", "SettleNoReceipt" }),
                     (DeliveryApplication, JobAdmission, new[] { "RequireOwnerEntry", "BindOwnerAsync", "FenceOwnerAsync" }),
                     (KernelAssembly, JobTrustedContext, new[] { ".ctor", "FromValidatedJob" }),
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
        var instructions = OwnedBodies(census).SelectMany(item => item.Body.Body.Instructions).ToArray();
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
            [(DeliveryApplication, JobParticipant, "FenceOwnerAsync"), (DeliveryApplication, JobParticipant, "FenceOwnerAsync")]);
        Cap(DeliveryApplication, JobParticipant, "FenceOwnerAsync", BoundaryTask,
            [LifecycleUnit, "System.Type", "System.Object", BoundaryCancellation], [(DeliveryApplication, JobAdmission, "FenceOwnerAsync")]);
        Cap(DeliveryApplication, JobParticipant, "RequireCurrent", "System.Void", [],
            [(DeliveryApplication, JobParticipant, "BindAndPreflightAsync"), (DeliveryApplication, JobParticipant, "BindAndPreflightAsync"),
             (DeliveryApplication, JobParticipant, "RequireRoot"),
             (DeliveryApplication, JobParticipant, "RequireEntry"),
             (InfrastructureAssembly, JobPostgresParticipant, "PreflightAsync"), (InfrastructureAssembly, JobPostgresParticipant, "PreflightAsync"),
             (InfrastructureAssembly, JobPostgresParticipant, "FenceAsync"), (InfrastructureAssembly, JobPostgresParticipant, "FenceAsync"),
             (InfrastructureAssembly, JobPostgresParticipant, "FenceAsync"), (InfrastructureAssembly, JobPostgresParticipant, "CreateCommand")]);
        Cap(DeliveryApplication, JobAdmission, "RequireOwnerEntry", JobParticipant,
            [LifecycleUnit, "LearnStack.SharedKernel.Persistence.TransactionMode", "System.Type", "LearnStack.SharedKernel.Tenancy.ITenantContext"],
            [(DeliveryApplication, DeliveryCoordinator, "ExecuteCoreAsync")]);
        Cap(DeliveryApplication, JobAdmission, "BindOwnerAsync", BoundaryTask,
            [JobParticipant, LifecycleUnit, "System.Boolean", BoundaryCancellation], [(DeliveryApplication, DeliveryCoordinator, "ExecuteFrameAsync")]);
        Cap(DeliveryApplication, JobAdmission, "FenceOwnerAsync", BoundaryTask,
            [JobParticipant, LifecycleUnit, BoundaryCancellation], [(DeliveryApplication, DeliveryCoordinator, "ExecuteFrameAsync")]);
        Cap(DeliveryApplication, JobCommitGate, ".ctor", "System.Void", [],
            [(InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.BackgroundJobExecutionControl", ".ctor")]);
        Cap(DeliveryApplication, JobPublication, ".ctor", "System.Void", [],
            [(InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.BackgroundJobExecutionControl", ".ctor"),
             (InfrastructureAssembly, JobExecutorType, "ExecuteAsync")]);
        foreach (var name in new[] { "TryStartExecution", "TryBeginCommit", "RecordCommitted", "RecordRolledBack" })
            Cap(DeliveryApplication, JobCommitGate, name, name.StartsWith("Try", StringComparison.Ordinal) ? "System.Boolean" : "System.Void", [],
                name == "TryStartExecution" ? [(InfrastructureAssembly, JobExecutorType, "ExecuteBusinessAsync")]
                : name == "RecordRolledBack" ? [(DeliveryApplication, DeliveryCoordinator, "ExecuteFrameAsync"), (DeliveryApplication, DeliveryCoordinator, "ExecuteFrameAsync")]
                : [(DeliveryApplication, DeliveryCoordinator, "ExecuteFrameAsync")]);
        Cap(DeliveryApplication, JobPublication, "Bind", "System.Void", [JobStartRequest], [(InfrastructureAssembly, JobExecutorType, "ExecutePublishedAsync")]);
        Cap(DeliveryApplication, JobPublication, "Publish", "System.Void", [JobStartRequest, JobStartReceipt], [(DeliveryApplication, JobStartRequest, "RecordCommitted")]);
        Cap(DeliveryApplication, JobPublication, "SettleNoReceipt", "System.Void", [], [(InfrastructureAssembly, JobExecutorType, "ExecutePublishedAsync")]);
        Cap(DeliveryApplication, JobStartRequest, "BindPublication", "System.Void", [JobPublication], [(DeliveryApplication, JobPublication, "Bind")]);
        Cap(DeliveryApplication, JobCommitGate, "Close", "System.Void", [],
            [(InfrastructureAssembly, JobExecutorType, "CloseBusinessGate"),
             (InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.BackgroundJobExecutionControl", "CloseExecution"),
             (InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.BackgroundJobExecutionControl", "LeaseElapsed"),
             (InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.BackgroundJobControl", "FinishBusinessAsync"),
             (InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.HangfireGenerationWork/Invocation", "HoldExecution"),
             (InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.HangfireGenerationWork/Invocation", "RequestCancellation")]);
        Cap(InfrastructureAssembly, JobExecutorType, "ExecuteBusinessAsync",
            "System.Threading.Tasks.Task`1<LearnStack.Infrastructure.Jobs.JobBusinessExecutionResult>",
            [JobStartReceipt, JobCommitGate, BoundaryCancellation], [(InfrastructureAssembly, JobBoundInvocation, "RunFrameworkAsync")]);
        Cap(InfrastructureAssembly, JobExecutorType, "ExecutePublishedAsync",
            "System.Threading.Tasks.Task`1<LearnStack.Infrastructure.Jobs.JobStartExecutionResult>",
            [JobRoutingClaim, JobPublication, "System.Func`1<System.Boolean>", BoundaryCancellation],
            [(InfrastructureAssembly, JobExecutorType, "ExecuteAsync"), (InfrastructureAssembly, JobBoundInvocation, "RunFrameworkAsync")]);
        Cap(InfrastructureAssembly, JobExecutorType, "get_ScopeFactory", "Microsoft.Extensions.DependencyInjection.IServiceScopeFactory", [],
            [(InfrastructureAssembly, JobRuntime, ".ctor")]);
        Cap(KernelAssembly, JobTrustedContext, "FromValidatedJob", JobTrustedContext,
            ["LearnStack.SharedKernel.Identifiers.TenantId", "System.Nullable`1<LearnStack.SharedKernel.Identifiers.OrganizationId>",
             "LearnStack.SharedKernel.Jobs.BackgroundJobId", "System.Nullable`1<LearnStack.SharedKernel.Jobs.BackgroundJobId>",
             "System.Nullable`1<System.Guid>", "System.Nullable`1<LearnStack.SharedKernel.Identifiers.UserId>", "System.String", "System.String"],
            [(InfrastructureAssembly, JobExecutorType, "CreateContext")]);
        Cap(KernelAssembly, JobTrustedContext, ".ctor", "System.Void",
            ["LearnStack.SharedKernel.Identifiers.TenantId", "System.Nullable`1<LearnStack.SharedKernel.Identifiers.OrganizationId>",
             "LearnStack.SharedKernel.Jobs.BackgroundJobId", "System.Nullable`1<LearnStack.SharedKernel.Jobs.BackgroundJobId>",
             "System.Nullable`1<System.Guid>", "System.Nullable`1<LearnStack.SharedKernel.Identifiers.UserId>", "System.String", "System.String"],
            [(KernelAssembly, JobTrustedContext, "FromValidatedJob")]);
        var diagnosticTypes = types[(KernelAssembly, AsyncDiagnostics)].Methods.SelectMany(method => method.HasBody
                ? method.Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Isinst).Select(instruction => ((TypeReference)instruction.Operand).FullName)
                : []);
        CheckSet(offenders, "closed asynchronous diagnostic contexts", diagnosticTypes,
            [JobTrustedContext, "LearnStack.SharedKernel.Tenancy.EventTenantContext"]);
        CheckOrder();
        CheckSql();
        return offenders;

        void Cap(string assembly, string typeName, string name, string returnType, string[] parameters,
            (string Assembly, string Type, string Method)[] owners)
        {
            var type = types[(assembly, typeName)];
            var methods = type.Methods.Where(method => method.Name == name).ToArray();
            if (methods.Length != 1 || methods[0].ReturnType.FullName != returnType || !ParametersMatch(methods[0], parameters))
                offenders.Add(typeName + "." + name + " requires its exact closed signature");
            CheckSet(offenders, typeName + "." + name + " authority callers",
                sites.Where(site => SameType(site.Target.DeclaringType, type) && site.Target.Name == name).Select(site => site.Owner),
                owners.Select(owner => JobOwner(offenders, types, owner.Assembly, owner.Type, owner.Method)));
            foreach (var instruction in instructions
                         .Where(instruction => instruction.Operand is MethodReference target
                             && SameType(target.DeclaringType, type) && target.Name == name))
                if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt
                    && !(name == ".ctor" && instruction.OpCode == OpCodes.Newobj))
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
            var gate = Find(JobCommitGate, "TryBeginCommit");
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
                             "definition_key = @definition", "profile = 'transactional'", "queue = @queue", "capacity_group IS NULL",
                             "wake_generation = @generation", "current_attempt_id = @attempt", "normal_attempts = @ordinal",
                             "ownership_token = @token", "ownership_kind = 'execution'", "phase = 'executing'", "attempt_deadline = @deadline",
                             "processed_at IS NULL", "dead_lettered_at IS NULL", "terminal_reason IS NULL",
                             "a.id = @attempt", "a.job_id = background_jobs.id", "a.stage = 'normal'", "a.ordinal = @ordinal",
                             "a.admission_token = @token", "a.admitted_at = @admitted", "a.deadline = @deadline" })
                    if (!statement.Contains(predicate, StringComparison.Ordinal)) offenders.Add("Missing exact job fence predicate: " + predicate);
            if (preflight[0].Contains("FOR UPDATE", StringComparison.Ordinal)
                || !locked[0].EndsWith("FOR UPDATE", StringComparison.Ordinal) || locked[0].Contains("clock_timestamp", StringComparison.Ordinal)
                || !update[0].Contains("SET phase = 'completed', processed_at = clock_timestamp()", StringComparison.Ordinal))
                offenders.Add("Only final completion locks the row, with fresh time evaluated by the following update");
            foreach (var statement in preflight.Concat(update))
                if (!statement.Contains("ownership_until > clock_timestamp()", StringComparison.Ordinal)
                    || !statement.Contains("attempt_deadline > clock_timestamp()", StringComparison.Ordinal))
                    offenders.Add("Job preflight and final update require strict fresh database lease/deadline fences");
        }
    }
}
