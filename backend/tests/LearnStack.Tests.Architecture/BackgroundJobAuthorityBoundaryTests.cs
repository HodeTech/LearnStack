using FluentAssertions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed partial class OutboxDispatchConventionTests
{
    private const string JobAdmission = "LearnStack.Application.Jobs.BackgroundJobExecutionAdmission";
    private const string JobTenantBehavior = "LearnStack.Application.Pipeline.TenantContextBehavior`2";
    private const string JobTransactionBehavior = "LearnStack.Application.Pipeline.TransactionBehavior`2";
    private const string PrivilegedSource = "LearnStack.Infrastructure.Persistence.PrivilegedDataSource";
    private const string JobBridge = "LearnStack.Infrastructure.Jobs.BackgroundJobBridge";

    /// <summary>ADR-0047 ordinary execution authority and Step 3's live framework admission.</summary>
    [Fact]
    public void BackgroundJob_Ordinary_Execution_Has_No_Platform_Authority()
    {
        using var census = ProductionCensus.Read();
        EvaluateDetector(census, JobAuthorityOffenders).Should().BeEmpty(
            "Fix: retain the independent sticky admission, exact pipeline gate and guards at every alternate/privileged entry");
        EvaluateDetector(census, JobStepThreeOffenders).Should().BeEmpty(
            "Fix: retain the exact producer, opaque routing capability and acknowledged app-role start receipt path");
        CheckJobFrameworkMetadata();
    }

    public sealed class JobAuthorityCensusMutationTests
    {

        /// <summary>ADR-0047: prove the same production predicate detects removal and additional activation.</summary>
        [Fact]
        public void Background_job_authority_census_detects_extra_activation_and_removed_live_guards()
        {
            using var census = ProductionCensus.Read();
            var admission = census.Types[(DeliveryApplication, JobAdmission)];
            var enter = admission.Methods.Single(method => method.Name == "Enter");
            var application = census.Modules.Single(module => module.Assembly.Name.Name == DeliveryApplication);
            foreach (var opcode in new[] { OpCodes.Call, OpCodes.Callvirt, OpCodes.Ldftn, OpCodes.Ldvirtftn })
                AssertMutation(census, "an unauthorized admission activator through " + opcode, () =>
                    AddCallProbe(application, "AdditionalJobActivator", enter, opcode), JobAuthorityOffenders);
            AssertMutation(census, "an async admission activator", () =>
                AddAsyncCallProbe(application, "AsyncJobActivator", enter), JobAuthorityOffenders);
            string? expectedDetached = null;
            AssertMutation(census, "an unrelated detached production method target", () =>
            {
                var probe = AddProbe(application, "DetachedAuthorityTarget");
                var detached = new MethodDefinition("UnrelatedDetachedAuthorityTarget",
                    MethodAttributes.Private | MethodAttributes.Static, application.TypeSystem.Void);
                probe.Methods.Add(detached);
                var caller = new MethodDefinition("Run",
                    MethodAttributes.Public | MethodAttributes.Static, application.TypeSystem.Void);
                caller.Body.Instructions.Add(Instruction.Create(OpCodes.Call, detached));
                caller.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                probe.Methods.Add(caller);
                expectedDetached = MethodId(caller) + ": detached production method target UnrelatedDetachedAuthorityTarget";
                probe.Methods.Remove(detached);
                return () => application.Types.Remove(probe);
            }, current =>
            {
                var found = JobAuthorityOffenders(current);
                if (current.Types.ContainsKey((DeliveryApplication, "ReviewProbe.DetachedAuthorityTarget")))
                    found.Should().Contain(expectedDetached!,
                        "the unrelated raw detached target must retain its exact owner-qualified diagnostic");
                return found;
            });
            var guards = OwnedBodies(census).SelectMany(item => item.Body.Body.Instructions)
                .Where(instruction => instruction.Operand is MethodReference target && target.DeclaringType.FullName == JobAdmission
                    && target.Name is "TryEnterRequest" or "RefusePrivilegedEntry" or "RequireContext" or "ModeFor").ToArray();
            guards.Should().NotBeEmpty("the real runtime gates must be nonempty subjects before dirty controls run");
            foreach (var guard in guards)
                AssertMutation(census, "a missing actual job authority gate", () =>
                    ReplaceInstruction(guard, OpCodes.Ldstr, "missing job authority gate"), JobAuthorityOffenders);
            AssertJobStepThreeControls(census);
        }

    }

    private static SortedSet<string> JobAuthorityOffenders(ProductionCensus census)
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        var types = census.Types;
        foreach (var subject in new[] { (DeliveryApplication, JobAdmission), (InfrastructureAssembly, JobStartHandler), (InfrastructureAssembly, PrivilegedSource),
                     (DeliveryApplication, JobTenantBehavior), (DeliveryApplication, JobTransactionBehavior),
                     (InfrastructureAssembly, JobBridge), (InfrastructureAssembly, JobBridgeStore),
                     (InfrastructureAssembly, Store), (InfrastructureAssembly, DeliveryStore), (InfrastructureAssembly, JobRuntime) })
            if (!types.ContainsKey(subject)) offenders.Add("Missing job authority subject: " + subject.Item2);
        if (offenders.Count != 0) return offenders;
        var admission = types[(DeliveryApplication, JobAdmission)];
        var enter = admission.Methods.Where(method => method.Name == "Enter").ToArray();
        if (enter.Length != 1 || !ParametersMatch(enter[0], [JobStartReceipt, LifecycleUnit,
                "LearnStack.Application.Jobs.BackgroundJobCompletionParticipant"]))
            offenders.Add("Admission activation must consume its one confirmed start receipt contract");
        var snapshot = admission.NestedTypes.SingleOrDefault(type => type.Name == "Admission");
        if (snapshot is null || !snapshot.IsSealed || snapshot.Fields.Any(field => !field.IsInitOnly)
            || snapshot.Fields.Any(field => field.FieldType.FullName is "LearnStack.SharedKernel.Jobs.IBackgroundJobRequestCatalog"
                or "LearnStack.SharedKernel.Audit.IAuditCatalog"))
            offenders.Add("Admission must retain a sealed immutable snapshot without mutable catalogue authority");
        var identities = new Dictionary<MethodDefinition, string>(ReferenceEqualityComparer.Instance);
        var allSites = OwnedCallSites(census).ToArray();
        var detached = new SortedSet<string>(StringComparer.Ordinal);
        RecordDetachedTargets(detached, allSites);
        if (detached.Count != 0)
        {
            offenders.UnionWith(detached);
            return offenders;
        }
        var sites = allSites.Where(site =>
            (site.Target.Name is "Enter" or "TryEnterRequest" or "ModeFor" or "RequireContext" or "RefusePrivilegedEntry")
            && site.Target.DeclaringType.FullName == JobAdmission).ToArray();
        CheckSet(offenders, "ordinary job admission activators", sites.Where(site => site.Target.Name == "Enter").Select(site => Identity(site.Owner)),
            [Owner(InfrastructureAssembly, JobExecutorType, "ExecuteBusinessAsync")]);
        CheckSet(offenders, "job request gate", sites.Where(site => site.Target.Name == "TryEnterRequest").Select(site => Identity(site.Owner)),
            [Owner(DeliveryApplication, JobTenantBehavior, "Handle")]);
        CheckSet(offenders, "closed framework transaction mode", sites.Where(site => site.Target.Name == "ModeFor").Select(site => Identity(site.Owner)),
            [Owner(DeliveryApplication, JobTransactionBehavior, "Handle")]);
        var revalidation = AdmissionRevalidationMethod(census, offenders);
        CheckSet(offenders, "job context owner gates", sites.Where(site => site.Target.Name == "RequireContext").Select(site => Identity(site.Owner)),
            [Owner(DeliveryApplication, DeliveryCoordinator, "ExecuteCoreAsync"), Owner(DeliveryApplication, DeliveryCoordinator, "ExecuteFrameAsync"),
             Owner(DeliveryApplication, JobAdmission, "RequireOwnerEntry"), Owner(DeliveryApplication, JobAdmission, "RequireOwnerEntry"),
             Owner(DeliveryApplication, JobAdmission, "RequireOwnerEntry"),
             Owner(DeliveryApplication, JobAdmission, "IsOperationEvidenceCurrent"),
             Owner(DeliveryApplication, JobAdmission, "RequireOwnerEntry"),
             Owner(InfrastructureAssembly, JobExecutorType, "ExecuteBusinessAsync"), Owner(InfrastructureAssembly, JobExecutorType, "ExecuteBusinessAsync"),
             Owner(InfrastructureAssembly, JobExecutorType, "ExecuteCapacityCleanupAsync"),
             Owner(InfrastructureAssembly, JobExecutorType, "ReadExternalStateCoreAsync"),
             Owner(InfrastructureAssembly, JobExecutorType, "WriteExternalCheckpointAsync"),
             Owner(InfrastructureAssembly, JobExecutorType, "ExecuteRecipientAsync"),
             revalidation is null ? "missing exact admission revalidation" : MethodId(revalidation)]);
        CheckSet(offenders, "privileged/alternate entry guards", sites.Where(site => site.Target.Name == "RefusePrivilegedEntry").Select(site => Identity(site.Owner)),
            [Owner(InfrastructureAssembly, PrivilegedSource, "OpenConnection"), Owner(InfrastructureAssembly, PrivilegedSource, "OpenConnectionAsync"),
             Owner(InfrastructureAssembly, Store, "OpenConnectionAsync"), Owner(InfrastructureAssembly, DeliveryStore, "OpenConnectionAsync"),
             Owner(InfrastructureAssembly, BoundaryPlatform, "EnterAsync"), Owner(InfrastructureAssembly, BoundaryTransport, "PublishAsync"),
             Owner(InfrastructureAssembly, DeliveryExecutorType, "ExecuteAsync"), Owner(InfrastructureAssembly, DeliveryExecutorType, "ExecuteControlledAsync"),
             Owner(InfrastructureAssembly, JobRoutingStore, "TryClaimAsync"), Owner(InfrastructureAssembly, JobRoutingStore, "TryClaimOwnedAsync"),
             Owner(InfrastructureAssembly, JobExecutorType, "ExecutePublishedAsync"), Owner(InfrastructureAssembly, JobExecutorType, "ExecuteBusinessAsync"),
             Owner(InfrastructureAssembly, JobExecutorType, "ExecuteCapacityCleanupAsync"),
             Owner(InfrastructureAssembly, JobExecutorType, "ReadExternalStateCoreAsync"),
             Owner(InfrastructureAssembly, JobExecutorType, "WriteExternalCheckpointAsync"),
             Owner(InfrastructureAssembly, JobExecutorType, "ExecuteRecipientAsync"),
             Owner(InfrastructureAssembly, JobExecutorType, "InvokeExternalAsync"),
             Owner(InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.BackgroundJobControl", "TryBegin"),
             Owner(InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.BackgroundJobControl", "TryBeginRecovery"),
             Owner(InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.BackgroundJobControl", "CanRoute"),
             Owner(InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.BackgroundJobControl", "TryBeginExternalStatus"),
             Owner(InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobControlStore", "RenewBatchAsync"),
             Owner(InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobControlStore", "FinishKnownRollbackAsync"),
             Owner(InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobControlStore", "RequestInvalidEnvelopeAsync"),
             Owner(InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobControlStore", "RegisterRecurrenceAsync"),
             Owner(InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobControlStore", "MaterializeRecurrenceAsync"),
             Owner(InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobControlStore", "MaterializeRecipientPageAsync"),
             Owner(InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobControlStore", "TryClaimRecipientAsync"),
             Owner(InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobControlStore", "ResolveRecipientAsync"),
             Owner(InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobControlStore", "RunRecoveryQuantumAsync"),
             Owner(InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobControlStore", "ProbeCommitAsync"),
             Owner(InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobControlStore", "DeferCapacityAsync"),
             Owner(InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobControlStore", "AcquireCapacityRecoveryAsync"),
             Owner(InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobControlStore", "FinishCapacityRecoveryAsync"),
             Owner(InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobControlStore", "AcquireExternalRecoveryAsync"),
             Owner(InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobControlStore", "ProbeExternalRecoveryAsync"),
             Owner(InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobControlStore", "ProbeExternalCheckpointAsync"),
             Owner(InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobControlStore", "RetryKnownExternalAsync"),
             Owner(InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobControlStore", "FinishExternalUnresolvedAsync"),
             Owner(InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobControlStore", "RequestNoPlanExternalTerminalAsync"),
             Owner(InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobControlStore", "AcquireExternalTerminalNoStartAsync"),
             Owner(InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobControlStore", "ProbeExternalTerminalNoStartAcquisitionAsync"),
             Owner(InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobControlStore", "FinishExternalFinalizeAsync"),
             Owner(InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobControlStore", "ReadDurableObservationAsync"),
             Owner(InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobTerminalStore", "TryClaimTerminalAsync"),
             Owner(InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobTerminalStore", "TryTerminalizeAsync"),
             Owner(InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobTerminalStore", "BackoffKnownAuditRollbackAsync"),
             Owner(InfrastructureAssembly, JobRuntime, "BindMessagingLifetime"), Owner(InfrastructureAssembly, JobRuntime, "StartAsync"),
             Owner(InfrastructureAssembly, JobRuntime, "RequestStop"), Owner(InfrastructureAssembly, JobRuntime, "StopAsync"),
             RequireBoundaryMethod(offenders, types[(InfrastructureAssembly, JobBridge)], "RunQuantumAsync", BoundaryBooleanTask,
                 MethodAttributes.Public, BoundaryCancellation),
             RequireBoundaryMethod(offenders, types[(InfrastructureAssembly, JobBridge)], "StopAsync", BoundaryTask,
                 MethodAttributes.Public, BoundaryCancellation),
             RequireBoundaryMethod(offenders, types[(InfrastructureAssembly, JobBridgeStore)], "TryClaimAsync",
                 "System.Threading.Tasks.Task`1<LearnStack.Infrastructure.Jobs.BackgroundJobBridgeClaim>", MethodAttributes.Assembly,
                 "System.Collections.Generic.IReadOnlyCollection`1<LearnStack.SharedKernel.Jobs.BackgroundJobId>", BoundaryCancellation),
             RequireBoundaryMethod(offenders, types[(InfrastructureAssembly, JobBridgeStore)], "UpdateAsync", BoundaryBooleanTask,
                 MethodAttributes.Private, "LearnStack.Infrastructure.Jobs.BackgroundJobBridgeClaim", "System.String",
                 "System.Nullable`1<LearnStack.Infrastructure.Jobs.BackgroundJobBridgeDeferral>", BoundaryCancellation),
             Owner(DeliveryApplication, DeliveryCoordinator, "PrepareDeliveryOwnerAsync"), Owner(DeliveryApplication, DeliveryCoordinator, "ExecutePreparedDeliveryAsync"),
             Owner(DeliveryApplication, DeliveryCoordinator, "PrepareGlobalIdentityOwnerAsync"),
             Owner(DeliveryApplication, DeliveryCoordinator, "ExecutePreparedGlobalIdentityAsync")]);
        return offenders;

        string Identity(MethodDefinition method)
        {
            if (identities.TryGetValue(method, out var identity)) return identity;
            identity = MethodId(method);
            identities.Add(method, identity);
            return identity;
        }

        string Owner(string assembly, string typeName, string name)
        {
            if (!types.TryGetValue((assembly, typeName), out var type))
            { offenders.Add("Missing guarded entry: " + typeName); return "missing:" + typeName; }
            var methods = type.Methods.Where(method => method.Name == name).ToArray();
            if (methods.Length != 1) { offenders.Add("Guarded entry must have one exact method: " + typeName + "." + name); return "missing:" + name; }
            return MethodId(methods[0]);
        }
    }
}
