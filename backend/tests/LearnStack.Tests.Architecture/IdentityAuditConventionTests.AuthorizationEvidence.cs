using FluentAssertions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;
using EvidenceCensus = LearnStack.Tests.Architecture.TrustedOperationSourceConventionTests.Census;

namespace LearnStack.Tests.Architecture;

public sealed partial class IdentityAuditConventionTests
{
    private const string Occurrence = "LearnStack.SharedKernel.Audit.AuthorizationEvidenceOccurrence";
    private const string AuditContext = "LearnStack.SharedKernel.Audit.AuthorizationAuditContext";
    private const string Transport = "LearnStack.SharedKernel.Audit.OperationTransportProvenance";
    private const string Assertion = "LearnStack.SharedKernel.Audit.ConfirmedClaimAssertion";
    private const string AssertionSource = "LearnStack.Api.Authentication.ConfirmedClaimAssertionSource";
    private const string CentralEvidence = "LearnStack.Application.Audit.AuthorizationEvidenceCoordinator";
    private const string HttpTransport = "LearnStack.Api.Authentication.HttpAuthorizationTransportSource";
    private const string TransportPort = "LearnStack.SharedKernel.Audit.IAuthorizationTransportSource";
    private const string InvocationContext = "LearnStack.SharedKernel.Permissions.IOperationInvocationContext";
    private const string PolicyOwner = "LearnStack.Application.Authorization.OperationAuthorizationCoordinator";
    private const string RoleAuthorization = "LearnStack.Modules.Identity.Application.Authorization.RoleGrantAuthorization";
    private const string RoleHandler = "LearnStack.Modules.Identity.Application.Roles.RoleGrantCommandHandlerBase";
    private const string StateCapture = "LearnStack.Infrastructure.Audit.AuditStateCapture";
    private const string FrameBindings = "System.Collections.Generic.Dictionary`2<System.Int32,LearnStack.SharedKernel.Permissions.OperationInvocation>";
    private const string OrdinaryAuditOwner = "LearnStack.Application.Audit.AuditExecutionCoordinator";
    private static readonly string[] AuthorizationPrimaryKeyFilter = ["23505", "public", "audit_log", "audit_log_pkey"];
    private const string SeedTenancyGuard = "LearnStack.Modules.Tenancy.Application.Seeding.SeedTenantWriteGuard";
    private const string SeedCustomizationGuard = "LearnStack.Modules.Customization.Application.Seeding.SeedCustomizationWriteGuard";
    private const string SeedEducationGuard = "LearnStack.Modules.Education.Application.Seeding.SeedEducationWriteGuard";
    private const string SeedOriginHandler = "LearnStack.Modules.Tenancy.Application.Seeding.AdoptSeedAdministrationOriginCommandHandler";
    private const string FixedPrerequisiteSource = "LearnStack.Tools.Seeder.FixedSeedPrerequisiteSource";
    private const string FixedCompletionSource = "LearnStack.Tools.Seeder.FixedSeedCompletionSource";
    private const string SeedStageReader = "LearnStack.Modules.Identity.Infrastructure.Persistence.SeedIdentityStageReader";
    // These reads borrow the current invocation; they mint no decision or audit
    // evidence. Keep the two fixed purposes and complete-writer guards explicit,
    // including the one generated local lifetime check, rather than admitting a namespace.
    private static readonly (string Owner, string Method, int Count)[] CurrentInvocationBorrowers =
    [
        (CentralEvidence, "Snapshot", 1),
        (RoleAuthorization, "Matches", 1),
        (RoleHandler, "ExecuteAsync", 1),
        (FixedPrerequisiteSource, "Capture", 1),
        (FixedPrerequisiteSource, "HasInvocation", 1),
        (FixedCompletionSource, "Capture", 1),
        (FixedCompletionSource, "HasInvocation", 1),
        (SeedTenancyGuard, "AdmitAsync", 1),
        (SeedTenancyGuard, "RequireCurrent", 1),
        (SeedTenancyGuard, "AllowsIdentityContinuationAsync", 1),
        (SeedTenancyGuard, "AllowsContentContinuationAsync", 1),
        (SeedTenancyGuard, "<HasPristinePrerequisitesAsync>g__Current|13_0", 1),
        (SeedOriginHandler, "HasCurrentSeedInvocation", 1),
        (SeedCustomizationGuard, "PrepareAsync", 3),
        (SeedCustomizationGuard, "IsCurrent", 1),
        (SeedCustomizationGuard, "IsCompleteAsync", 3),
        (SeedEducationGuard, "PrepareAsync", 2),
        (SeedEducationGuard, "IsCurrent", 1),
        (SeedStageReader, "HasNoRoleHistoryAsync", 2),
    ];

    [Fact]
    public void Authorization_Evidence_Uses_Trusted_Bounded_Provenance()
    {
        using var census = EvidenceCensus.Read();
        EvidenceProvenanceOffenders(census).Should().BeEmpty("only the central owner composes fixed events from live typed source and decision evidence");
        foreach (var name in new[] { Occurrence, AuditContext, Transport, Assertion })
        {
            EvidenceDirty(census, EvidenceProvenanceOffenders, () =>
            {
                var constructor = census.Type(name).Methods.Single(method => method.IsConstructor);
                var attributes = constructor.Attributes;
                constructor.IsPublic = true;
                return () => constructor.Attributes = attributes;
            });
            EvidenceDirty(census, EvidenceProvenanceOffenders, () =>
            {
                var field = census.Type(name).Fields.First(item => item.IsInitOnly);
                field.IsInitOnly = false;
                return () => field.IsInitOnly = true;
            });
            EvidenceDirty(census, EvidenceProvenanceOffenders, () =>
            {
                var module = census.Type(CentralEvidence).Module;
                var extra = new TypeDefinition("EvidenceControl", "Extra" + census.Type(name).Name + "Minter", TypeAttributes.NotPublic,
                    module.ImportReference(census.Type(name + "/MintingSource")));
                module.Types.Add(extra);
                return () => module.Types.Remove(extra);
            });
        }
        foreach (var (type, method) in new[] { (Occurrence + "/MintingSource", "ForOccurrence"),
                     (AuditContext + "/MintingSource", "ForAuditContext"), (Transport + "/MintingSource", "ForTransport"),
                     (Assertion + "/MintingSource", "ForAssertion"),
                     ("LearnStack.SharedKernel.Audit.IAuditStore", "WriteAuthorizationPendingAsync"),
                     ("LearnStack.SharedKernel.Audit.IAuditStore", "WriteAuthorizationStandaloneAsync"),
                     ("LearnStack.SharedKernel.Audit.IAuditStateCapture", "AttachAuthorization"),
                     (CentralEvidence, "BeginExecution"), (CentralEvidence, "Observe"),
                     (CentralEvidence, "RecordPolicyRefusal"), (CentralEvidence, "CaptureRefusalObservation"), (CentralEvidence, "RecordRefusalResult"),
                     ("LearnStack.Api.Tenancy.IClaimContextAssertionRecorder", "RecordAsync"),
                     (InvocationContext, "get_CurrentInvocation"),
                     (CentralEvidence, "BeforeSuccessAsync") })
            foreach (var opcode in new[] { OpCodes.Call, OpCodes.Ldftn }) EvidenceDirty(census, EvidenceProvenanceOffenders,
                () => EvidenceExtraCall(census, census.Method(type, method), opcode));
        foreach (var opcode in new[] { OpCodes.Callvirt, OpCodes.Ldvirtftn })
            EvidenceDirty(census, EvidenceProvenanceOffenders,
                () => EvidenceExtraCall(census, census.Method(InvocationContext, "get_CurrentInvocation"), opcode));
        foreach (var (owner, method, _) in CurrentInvocationBorrowers)
        {
            var borrowed = EvidenceCalls(census.Body(owner, method), InvocationContext, "get_CurrentInvocation").First();
            EvidenceDirty(census, EvidenceProvenanceOffenders, () => EvidenceReplace(borrowed, OpCodes.Nop, null));
        }
        var guardedPreparation = census.Body(SeedCustomizationGuard, "PrepareAsync");
        var retainedRead = EvidenceCalls(guardedPreparation, InvocationContext, "get_CurrentInvocation").First();
        foreach (var opcode in new[] { OpCodes.Call, OpCodes.Ldftn, OpCodes.Ldvirtftn })
            EvidenceDirty(census, EvidenceProvenanceOffenders,
                () => EvidenceReplace(retainedRead, opcode, retainedRead.Operand));
        EvidenceDirty(census, EvidenceProvenanceOffenders, () =>
        {
            var duplicate = Instruction.Create(OpCodes.Callvirt, (MethodReference)retainedRead.Operand);
            guardedPreparation.Body.Instructions.Add(duplicate);
            return () => guardedPreparation.Body.Instructions.Remove(duplicate);
        });
        foreach (var (type, method, target, name) in new[]
        {
            (Occurrence, ".ctor", Occurrence, "CopyAddress"), (Occurrence, "get_Draft", Occurrence, "CopyAddress"),
            (Transport, "Matches", Transport + "/MintingSource", "get_IsOpen"),
            (Transport, "Matches", "LearnStack.SharedKernel.Permissions.OperationInvocation", "get_IsActive"),
            (Transport + "/MintingSource", "Dispose", "System.Threading.Interlocked", "Exchange"),
            (Assertion, "get_IsActive", Assertion + "/MintingSource", "get_IsOpen"),
            (Assertion + "/MintingSource", "Dispose", "System.Threading.Interlocked", "Exchange"),
            (AssertionSource, "Capture", "LearnStack.Api.Authentication.TenantAuthorizationCandidateSource", "ForValidatedScope"),
            (CentralEvidence, "Snapshot", InvocationContext, "get_CurrentInvocation"),
            (CentralEvidence, "RecordObservedDenial", "LearnStack.SharedKernel.Identifiers.IGuidFactory", "NewUuidV7"),
            (CentralEvidence, "RecordObservedDenial", "LearnStack.SharedKernel.Time.IClock", "get_UtcNow"),
            (HttpTransport, "Capture", "LearnStack.Api.Authentication.OperationHttpClassifier", "Classify"),
            (EvidenceStore, "HasAuthorizationEvidenceAsync", EvidenceStore, "Bind"),
            (StateCapture, "AttachAuthorization", FrameBindings, "TryGetValue"),
            (StateCapture, "AttachAuthorization", FrameBindings, "TryAdd"),
            (StateCapture, "Clear", FrameBindings, "Clear"),
            (OrdinaryAuditOwner, "ExecuteDeclaredAsync", CentralEvidence, "ReconcileBeforeCaptureClearAsync"),
            (CentralEvidence, "BeforeOwnerCompletionAsync", "LearnStack.SharedKernel.Persistence.IUnitOfWork", "MarkRollbackOnly"),
            (CentralEvidence, "BeforeOwnerCompletionAsync", CentralEvidence + "/CommitBatch", "get_Generation"),
            (CentralEvidence, "RecordOwnerOutcome", "LearnStack.SharedKernel.Persistence.IUnitOfWork", "get_HasActiveTransaction"),
        }) EvidenceDirty(census, EvidenceProvenanceOffenders, () => EvidenceRemoveCall(census.Body(type, method), target, name));
        var verification = census.Body(EvidenceStore, "HasAuthorizationEvidenceAsync");
        var sql = verification.Body.Instructions.Single(instruction => instruction.Operand is string text && text.Contains("SELECT 1 FROM public.audit_log", StringComparison.Ordinal));
        foreach (var (before, after) in new[]
        {
            ("AND metadata IS NOT DISTINCT FROM @metadata", string.Empty),
            ("WHERE id = @id", "WHERE TRUE"),
            ("AND timestamp = @timestamp", string.Empty),
            ("WHERE id = @id", "WHERE id IS NOT DISTINCT FROM @id"),
            ("AND timestamp = @timestamp", "AND timestamp IS NOT DISTINCT FROM @timestamp"),
        }) EvidenceDirty(census, EvidenceProvenanceOffenders, () => EvidenceReplace(sql, OpCodes.Ldstr,
            ((string)sql.Operand).Replace(before, after, StringComparison.Ordinal)));
        var standalone = census.Body(EvidenceStore, "WriteAuthorizationStandaloneAsync");
        var key = standalone.Body.Instructions.Single(instruction => instruction.Operand as string == "audit_log_pkey");
        EvidenceDirty(census, EvidenceProvenanceOffenders, () => EvidenceReplace(key, OpCodes.Ldstr, "unrelated_unique_key"));
        foreach (var contract in new[] { InvocationContext, "LearnStack.SharedKernel.Audit.IAuthorizationRefusalReporter",
                     "LearnStack.Api.Tenancy.IClaimContextAssertionRecorder" })
            EvidenceDirty(census, EvidenceProvenanceOffenders, () =>
        {
            var type = census.Type(PolicyOwner);
            var extra = new TypeDefinition("EvidenceControl", "UnownedInvocationContext", TypeAttributes.NotPublic, type.Module.TypeSystem.Object);
            extra.Interfaces.Add(new InterfaceImplementation(type.Module.ImportReference(census.Type(contract))));
            type.Module.Types.Add(extra);
            return () => type.Module.Types.Remove(extra);
        });
        var auditOwner = census.Body(OrdinaryAuditOwner, "ExecuteDeclaredAsync");
        var reconcile = EvidenceCalls(auditOwner, CentralEvidence, "ReconcileBeforeCaptureClearAsync").Single();
        var clear = EvidenceCalls(auditOwner, "LearnStack.SharedKernel.Audit.IAuditStateCapture", "Clear").Single();
        EvidenceDirty(census, EvidenceProvenanceOffenders, () => EvidenceReplace(auditOwner.Body.Instructions
            .SkipWhile(instruction => instruction != reconcile).TakeWhile(instruction => instruction != clear).First(EvidenceAwaitAck), OpCodes.Nop, null));
        var captureBinding = census.Body(StateCapture, "AttachAuthorization");
        var comparison = EvidenceFrameIdentityComparisons(captureBinding).Single();
        EvidenceDirty(census, EvidenceProvenanceOffenders, () => EvidenceReplace(comparison, OpCodes.Nop, null));
        EvidenceDirty(census, EvidenceProvenanceOffenders, () => EvidenceReplace(comparison.Previous.Previous.Previous, OpCodes.Ldloc_0, null));
        EvidenceDirty(census, EvidenceProvenanceOffenders, () =>
        {
            var undo = EvidenceReplace(comparison, OpCodes.Nop, null);
            var decoy = Instruction.Create(OpCodes.Beq_S, captureBinding.Body.Instructions[^1]);
            captureBinding.Body.Instructions.Add(decoy);
            return () => { captureBinding.Body.Instructions.Remove(decoy); undo(); };
        });
        var generationGuard = EvidencePendingGenerationGuards(census.Body(CentralEvidence, "BeforeOwnerCompletionAsync")).Single();
        EvidenceDirty(census, EvidenceProvenanceOffenders, () => EvidenceReplace(generationGuard.TransactionMismatch, OpCodes.Nop, null));
        EvidenceDirty(census, EvidenceProvenanceOffenders, () => EvidenceReplace(generationGuard.GenerationEqual, OpCodes.Nop, null));
        var prematureAck = EvidencePrematureAcknowledgementGuards(census.Body(CentralEvidence, "RecordOwnerOutcome")).Single();
        EvidenceDirty(census, EvidenceProvenanceOffenders, () => EvidenceReplace(prematureAck, OpCodes.Nop, null));
        EvidenceObservationDirtyControls(census);
    }

    private static List<string> EvidenceProvenanceOffenders(EvidenceCensus census)
    {
        var errors = new List<string>();
        foreach (var name in new[] { Occurrence, AuditContext, Transport, Assertion })
        {
            var type = census.Type(name);
            if (!type.IsSealed || type.Methods.Count(method => method.IsConstructor) != 1
                || !type.Methods.Single(method => method.IsConstructor).IsPrivate
                || type.Properties.Any(property => property.SetMethod is not null)
                || type.Fields.Any(field => !field.IsPrivate || !field.IsInitOnly || field.IsStatic))
                errors.Add("Authorization evidence has private immutable construction: " + name);
        }
        EvidenceDerivers(Occurrence + "/MintingSource", [AuditContext + "/MintingSource"]);
        EvidenceDerivers(AuditContext + "/MintingSource", [CentralEvidence]);
        EvidenceDerivers(Transport + "/MintingSource", [HttpTransport]);
        EvidenceDerivers(Assertion + "/MintingSource", [AssertionSource]);
        EvidenceImplementers(census, errors, TransportPort, [HttpTransport]);
        EvidenceImplementers(census, errors, InvocationContext, [PolicyOwner]);
        EvidenceImplementers(census, errors, "LearnStack.SharedKernel.Audit.IAuthorizationRefusalReporter", [CentralEvidence]);
        EvidenceCallers(census, errors, Occurrence, ".ctor", [(Occurrence + "/MintingSource", "ForOccurrence", 1)]);
        EvidenceCallers(census, errors, AuditContext, ".ctor", [(AuditContext + "/MintingSource", "ForAuditContext", 1)]);
        EvidenceCallers(census, errors, Transport, ".ctor", [(Transport + "/MintingSource", "ForTransport", 1)]);
        EvidenceCallers(census, errors, Assertion, ".ctor", [(Assertion + "/MintingSource", "ForAssertion", 1)]);
        EvidenceCallers(census, errors, Assertion + "/MintingSource", "ForAssertion", [(AssertionSource, "Capture", 1)]);
        EvidenceCallers(census, errors, AssertionSource, "Capture", [("LearnStack.Api.Authentication.ClaimContextAssertionRecorder", "RecordAsync", 1)]);
        EvidenceCallers(census, errors, CentralEvidence, "RecordConfirmedAssertionAsync", [("LearnStack.Api.Authentication.ClaimContextAssertionRecorder", "RecordAsync", 1)]);
        EvidenceImplementers(census, errors, "LearnStack.Api.Tenancy.IClaimContextAssertionRecorder", ["LearnStack.Api.Authentication.ClaimContextAssertionRecorder"]);
        EvidenceCallers(census, errors, "LearnStack.Api.Tenancy.IClaimContextAssertionRecorder", "RecordAsync",
            [("LearnStack.Api.Tenancy.TenantAssertionMiddleware", "InvokeAsync", 1)]);
        EvidenceCallers(census, errors, Occurrence + "/MintingSource", "ForOccurrence", [(CentralEvidence, "RecordObservedDenial", 1),
            (CentralEvidence, "RecordConfirmedAssertionAsync", 1)]);
        EvidenceCallers(census, errors, AuditContext + "/MintingSource", "ForAuditContext", [(CentralEvidence, "Snapshot", 1)]);
        EvidenceCallers(census, errors, Transport + "/MintingSource", "ForTransport", [(HttpTransport, "Capture", 1)]);
        EvidenceCallers(census, errors, TransportPort, "Capture", [(CentralEvidence, "Snapshot", 1)]);
        EvidenceCallers(census, errors, "LearnStack.SharedKernel.Audit.IAuditStore", "WriteAuthorizationPendingAsync", [(CentralEvidence, "BeforeOwnerCompletionAsync", 1)]);
        EvidenceCallers(census, errors, "LearnStack.SharedKernel.Audit.IAuditStore", "WriteAuthorizationStandaloneAsync",
            [(CentralEvidence, "AcknowledgeStandaloneAsync", 1), (CentralEvidence, "ReconcileBeforeCaptureClearAsync", 1), (CentralEvidence, "RecordConfirmedAssertionAsync", 1)]);
        EvidenceCallers(census, errors, CentralEvidence, "ReconcileBeforeCaptureClearAsync",
            [(CentralEvidence, "EndExecutionAsync", 1), (OrdinaryAuditOwner, "ExecuteDeclaredAsync", 1)]);
        EvidenceCallers(census, errors, CentralEvidence + "/CommitBatch", ".ctor", [(CentralEvidence, "BeforeOwnerCompletionAsync", 1)]);
        EvidenceCallers(census, errors, CentralEvidence, "BeforeOwnerCompletionAsync", [("LearnStack.Application.Persistence.TransactionExecutionCoordinator", "ExecuteFrameAsync", 1)]);
        EvidenceCallers(census, errors, CentralEvidence, "RecordOwnerOutcome", [("LearnStack.Application.Persistence.TransactionExecutionCoordinator", "ExecuteFrameAsync", 3)]);
        EvidenceCallers(census, errors, CentralEvidence, "BeginExecution", [(PolicyOwner, "ExecuteAsync", 1),
            (OrdinaryAuditOwner, "ExecuteDeclaredAsync", 1), ("LearnStack.Application.Persistence.TransactionExecutionCoordinator", "ExecuteFrameAsync", 1)]);
        EvidenceCallers(census, errors, CentralEvidence, "Observe", [(PolicyOwner, "ExecuteAsync", 2), (CentralEvidence, "ReportRecheck", 1)]);
        EvidenceCallers(census, errors, CentralEvidence, "RecordPolicyRefusal", [(PolicyOwner, "ExecuteAsync", 3)]);
        EvidenceCallers(census, errors, CentralEvidence, "BeforeSuccessAsync", [(PolicyOwner, "ExecuteAsync", 1), (OrdinaryAuditOwner, "ExecuteDeclaredAsync", 1)]);
        EvidenceCallers(census, errors, CentralEvidence, "Snapshot", [(CentralEvidence, "Observe", 1), (CentralEvidence, "RecordDenial", 1)]);
        EvidenceCallers(census, errors, CentralEvidence, "RecordDenial", [(CentralEvidence, "ReportResourceRefusal", 1), (CentralEvidence, "RecordPolicyRefusal", 1)]);
        EvidenceCallers(census, errors, "LearnStack.SharedKernel.Audit.IAuditStateCapture", "AttachAuthorization",
            [(CentralEvidence, "Observe", 1), (CentralEvidence, "RecordObservedDenial", 1)]);
        EvidenceCallers(census, errors, InvocationContext, "get_CurrentInvocation", CurrentInvocationBorrowers);
        if (census.Methods.SelectMany(method => method.Body.Instructions).Any(instruction =>
                instruction.Operand is MethodReference target && target.DeclaringType.FullName == InvocationContext
                && target.Name == "get_CurrentInvocation" && instruction.OpCode.Code != Code.Callvirt))
            errors.Add("Current invocation borrowers use the exact interface callvirt, never a direct call or method alias");
        EvidenceCallers(census, errors, InvocationContext, "get_CurrentDecision", [(RoleAuthorization, "Matches", 1), (RoleHandler, "ExecuteAsync", 1),
            ("LearnStack.Modules.Identity.Application.Bindings.AdmitNativeSessionScopeCommandHandler", "Handle", 1)]);
        EvidenceCallers(census, errors, "LearnStack.SharedKernel.Audit.IAuthorizationRefusalReporter", "ReportRecheck", [(RoleAuthorization, "ReportRecheck", 1)]);
        EvidenceCallers(census, errors, "LearnStack.SharedKernel.Audit.IAuthorizationRefusalReporter", "ReportResourceRefusal", [(RoleAuthorization, "ReportRefusal", 1)]);
        foreach (var (type, method, target, name) in new[]
        {
            (Occurrence, ".ctor", Occurrence, "CopyAddress"), (Occurrence, "get_Draft", Occurrence, "CopyAddress"),
            (Transport, "Matches", Transport + "/MintingSource", "get_IsOpen"),
            (Transport, "Matches", "LearnStack.SharedKernel.Permissions.OperationInvocation", "get_IsActive"),
            (Transport + "/MintingSource", "Dispose", "System.Threading.Interlocked", "Exchange"),
            (Assertion, "get_IsActive", Assertion + "/MintingSource", "get_IsOpen"),
            (Assertion + "/MintingSource", "Dispose", "System.Threading.Interlocked", "Exchange"),
            (AssertionSource, "Capture", "LearnStack.Api.Authentication.TenantAuthorizationCandidateSource", "ForValidatedScope"),
            (CentralEvidence, "RecordObservedDenial", "LearnStack.SharedKernel.Identifiers.IGuidFactory", "NewUuidV7"),
            (CentralEvidence, "RecordObservedDenial", "LearnStack.SharedKernel.Time.IClock", "get_UtcNow"),
            (HttpTransport, "Capture", "LearnStack.Api.Authentication.OperationHttpClassifier", "Classify"),
            (EvidenceStore, "HasAuthorizationEvidenceAsync", EvidenceStore, "Bind"),
            (StateCapture, "AttachAuthorization", FrameBindings, "TryGetValue"),
            (StateCapture, "AttachAuthorization", FrameBindings, "TryAdd"),
            (StateCapture, "Clear", FrameBindings, "Clear"),
            (OrdinaryAuditOwner, "ExecuteDeclaredAsync", CentralEvidence, "ReconcileBeforeCaptureClearAsync"),
            (CentralEvidence, "BeforeOwnerCompletionAsync", "LearnStack.SharedKernel.Persistence.IUnitOfWork", "MarkRollbackOnly"),
            (CentralEvidence, "BeforeOwnerCompletionAsync", CentralEvidence + "/CommitBatch", "get_Generation"),
            (CentralEvidence, "RecordOwnerOutcome", "LearnStack.SharedKernel.Persistence.IUnitOfWork", "get_HasActiveTransaction"),
        }) EvidenceRequire(census, errors, type, method, target, name);
        if (EvidencePendingGenerationGuards(census.Body(CentralEvidence, "BeforeOwnerCompletionAsync")).Count() != 1)
            errors.Add("Each pending write is acknowledged before checking the exact captured transaction and fresh announcement generation; mismatch poisons that owner");
        if (EvidencePrematureAcknowledgementGuards(census.Body(CentralEvidence, "RecordOwnerOutcome")).Count() != 1)
            errors.Add("Committed evidence is never acknowledged while that same captured physical unit remains active");
        EvidenceObservationOffenders(census, errors);
        if (EvidenceFrameIdentityComparisons(census.Body(StateCapture, "AttachAuthorization")).Count() != 1)
            errors.Add("Only the existing frame's actual stored invocation may refresh the same resource snapshot");
        var auditOwner = census.Body(OrdinaryAuditOwner, "ExecuteDeclaredAsync");
        var reconciliation = EvidenceCalls(auditOwner, CentralEvidence, "ReconcileBeforeCaptureClearAsync").ToArray();
        var clear = EvidenceCalls(auditOwner, "LearnStack.SharedKernel.Audit.IAuditStateCapture", "Clear").ToArray();
        if (reconciliation.Length != 1 || clear.Length != 1 || !EvidenceAwaited(auditOwner, reconciliation[0], clear[0]))
            errors.Add("Actual authorization reconciliation acknowledges or records loss before ordinary capture clears the uncertain business state");
        var frameBindings = census.Type(StateCapture).Fields.SingleOrDefault(field => field.Name == "_authorizationFrames");
        if (frameBindings is not { IsPrivate: true, IsInitOnly: true, IsStatic: false } || frameBindings.FieldType.FullName != FrameBindings
            || census.Methods.SelectMany(method => method.Body.Instructions.Select(instruction => (Method: method, Instruction: instruction)))
                .Any(item => item.Instruction.Operand is FieldReference field && field.DeclaringType.FullName == StateCapture && field.Name == "_authorizationFrames"
                    && (item.Instruction.OpCode == OpCodes.Stfld ? census.Owner(item.Method) != StateCapture + "::.ctor"
                        : item.Instruction.OpCode != OpCodes.Ldfld || !(census.Owner(item.Method) == StateCapture + "::AttachAuthorization"
                            || census.Owner(item.Method) == StateCapture + "::Clear"))))
            errors.Add("Only the actual resource frame binds one invocation, refreshes that same act and clears its private binding map");
        var sql = census.Body(EvidenceStore, "HasAuthorizationEvidenceAsync").Body.Instructions.Select(instruction => instruction.Operand)
            .OfType<string>().SingleOrDefault(text => text.Contains("SELECT 1 FROM public.audit_log", StringComparison.Ordinal));
        var fields = new[] { "id", "tenant_id", "organization_id", "actor_user_id", "actor_email", "module", "operation", "operation_type", "operation_class",
            "entity_type", "entity_id", "outcome", "error_key", "reason", "before_state", "after_state", "changes", "correlation_id", "ip_address", "user_agent", "timestamp", "metadata" };
        var expectedSql = "SELECT EXISTS ( SELECT 1 FROM public.audit_log WHERE "
            + string.Join(" AND ", fields.Select(field => field + (field is "id" or "timestamp" ? " = @" : " IS NOT DISTINCT FROM @") + field)) + ")";
        if (sql is null || SourceText.WithoutWhitespace(sql) != SourceText.WithoutWhitespace(expectedSql))
            errors.Add("A duplicate exactly matches both indexed nonnullable keys and all twenty nullable-safe persisted values under its scoped fresh transaction");
        var standalone = census.Body(EvidenceStore, "WriteAuthorizationStandaloneAsync");
        var filter = standalone.Body.Instructions.Select(instruction => instruction.Operand).OfType<string>().ToArray();
        if (AuthorizationPrimaryKeyFilter.Any(value => filter.Count(actual => actual == value) != 1))
            errors.Add("Only the expected public.audit_log primary-key duplicate enters exact verification");
        foreach (var name in new[] { "WriteAuthorizationPendingAsync", "WriteAuthorizationStandaloneAsync", "InsertAuthorizationEvidenceAsync", "HasAuthorizationEvidenceAsync" })
            if (census.Body(EvidenceStore, name).Body.Instructions.Any(instruction => instruction.Operand is MethodReference target
                && (target.DeclaringType.FullName is "System.DateTime" or "System.DateTimeOffset" && target.Name == "get_UtcNow"
                    || target.DeclaringType.FullName == "LearnStack.SharedKernel.Identifiers.IGuidFactory")))
                errors.Add("The store cannot replace the occurrence's frozen identity or decision time: " + name);
        return errors;

        void EvidenceDerivers(string baseType, string[] expected)
        {
            if (!EvidenceSame(census.Types.Where(type => type.BaseType?.FullName == baseType).Select(type => type.FullName), expected))
                errors.Add("Authorization evidence minters are closed: " + baseType);
        }
    }

    // Release Roslyn lowers this ReferenceEquals to a reference beq. Bind its
    // operands to the actual dictionary out-local and context.Invocation, and
    // require both lookup-miss and identity-match to enter the same TryAdd path.
    // An unrelated equality instruction supplies no frame ownership evidence.
    private static IEnumerable<Instruction> EvidenceFrameIdentityComparisons(MethodDefinition body)
    {
        foreach (var comparison in body.Body.Instructions.Where(instruction => instruction.OpCode.Code is Code.Beq or Code.Beq_S))
        {
            if (comparison.Previous is not { } invocation || !EvidenceIsCall(invocation, AuditContext, "get_Invocation")
                || invocation.Previous?.OpCode != OpCodes.Ldarg_1
                || invocation.Previous.Previous is not { } owner || EvidenceLocal(owner) is not { } ownerIndex
                || owner.Previous is not { OpCode.Code: Code.Brfalse or Code.Brfalse_S } miss
                || miss.Previous is not { } lookup || !EvidenceIsCall(lookup, FrameBindings, "TryGetValue")
                || lookup.Previous is not { OpCode.Code: Code.Ldloca or Code.Ldloca_S } captured
                || EvidenceLocal(captured) != ownerIndex
                || lookup.Previous.Previous is not { } key || EvidenceLocal(key) is not { } keyIndex
                || lookup.Previous.Previous.Previous is not { OpCode.Code: Code.Ldfld, Operand: FieldReference dictionary }
                || dictionary.DeclaringType.FullName != StateCapture || dictionary.Name != "_authorizationFrames"
                || lookup.Previous.Previous.Previous.Previous?.OpCode != OpCodes.Ldarg_0
                || !body.Body.Instructions.Any(instruction => EvidenceIsCall(instruction, StateCapture, "get_CurrentFrame")
                    && instruction.Next is { } frameStore && EvidenceStoredLocal(frameStore) == keyIndex)
                || comparison.Operand is not Instruction target || miss.Operand != target
                || comparison.Next?.OpCode != OpCodes.Ret || comparison.Next.Next != target
                || target.OpCode != OpCodes.Ldarg_0
                || target.Next is not { OpCode.Code: Code.Ldfld, Operand: FieldReference addDictionary }
                || addDictionary.FullName != dictionary.FullName
                || target.Next.Next is not { } addKey || EvidenceLocal(addKey) != keyIndex
                || addKey.Next?.OpCode != OpCodes.Ldarg_1 || addKey.Next.Next is not { } addInvocation
                || !EvidenceIsCall(addInvocation, AuditContext, "get_Invocation")
                || addInvocation.Next is not { } add || !EvidenceIsCall(add, FrameBindings, "TryAdd")) continue;
            yield return comparison;
        }
    }

    private static int? EvidenceLocal(Instruction instruction) => instruction.OpCode.Code switch
    {
        Code.Ldloc_0 => 0,
        Code.Ldloc_1 => 1,
        Code.Ldloc_2 => 2,
        Code.Ldloc_3 => 3,
        Code.Ldloc or Code.Ldloc_S or Code.Ldloca or Code.Ldloca_S when instruction.Operand is VariableDefinition variable => variable.Index,
        _ => null,
    };

    private static int? EvidenceStoredLocal(Instruction instruction) => instruction.OpCode.Code switch
    {
        Code.Stloc_0 => 0,
        Code.Stloc_1 => 1,
        Code.Stloc_2 => 2,
        Code.Stloc_3 => 3,
        Code.Stloc or Code.Stloc_S when instruction.Operand is VariableDefinition variable => variable.Index,
        _ => null,
    };
}
