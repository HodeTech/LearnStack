using System.Collections.Frozen;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Application.Jobs;

/// <summary>Independent, immutable ordinary-job authority retained through actual cleanup.</summary>
public sealed partial class BackgroundJobExecutionAdmission(
    IBackgroundJobDefinitionCatalog definitions,
    IBackgroundJobRequestCatalog requests,
    IAuditCatalog audit)
{
    private static readonly AsyncLocal<Frame?> CurrentFrame = new();
    private static readonly AsyncLocal<RequestFrame?> CurrentRequest = new();

    /// <summary>The ordinary ceiling's standard authorization refusal.</summary>
    public static Error Refused { get; } = new(new LocalizedMessage("lockey_forbidden"));

    /// <summary>
    /// Activates one registered definition. Only the job executor may call this in
    /// production; neither request types nor a caller-selected scope are accepted.
    /// </summary>
    public IDisposable Enter(ConfirmedStartReceipt receipt, IUnitOfWork unit, BackgroundJobCompletionParticipant completion)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(completion);
        completion.RequireArmed(receipt, unit);
        var definitionKey = receipt.Envelope.DefinitionKey;
        var tenantId = receipt.Envelope.TenantId;
        var organizationId = receipt.Envelope.OrganizationId;
        var stage = receipt.Stage;
        if (JobApplicationSourceOwner.IsExternal || CurrentFrame.Value is not null || !ValidTenant(tenantId)
            || !ValidOrganization(organizationId)
            || !definitions.TryGet(definitionKey, out var definition)
            || definition.Readiness != BackgroundJobDefinitionReadiness.Active
            || !MatchesScope(definition.Scope, organizationId))
        {
            throw Denied();
        }

        var root = stage switch
        {
            BackgroundJobExecutionStage.Transactional when definition.Profile == BackgroundJobExecutionProfile.Transactional =>
                definition.ProfileRegistration.RootRequestType,
            BackgroundJobExecutionStage.Prepare when definition.Profile == BackgroundJobExecutionProfile.External =>
                definition.ProfileRegistration.RootRequestType,
            BackgroundJobExecutionStage.Finalize when definition.Profile == BackgroundJobExecutionProfile.External =>
                definition.ProfileRegistration.FinalizeRequestType,
            _ => null,
        };
        if (root is null) throw Denied();
        var metadata = requests.All.ToFrozenDictionary(request => request.RequestType, request => Snapshot(request, audit));
        var admitted = new Admission(tenantId, organizationId, definition.Scope, root,
            definition.NestedRequestTypes.Except(definition.RootRequestTypes).ToFrozenSet(), metadata);
        receipt.Consume();
        var frame = new Frame(admitted, receipt: receipt, unit: unit, completion: completion);
        CurrentFrame.Value = frame;
        return new Scope(frame);
    }

    /// <summary>Checks the positive request set before MediatR constructs its handler.</summary>
    public static bool PermitsRequest(Type requestType, ITenantContext context)
    {
        ArgumentNullException.ThrowIfNull(requestType);
        ArgumentNullException.ThrowIfNull(context);
        if (JobApplicationSourceOwner.IsExternal) return false;
        var frame = CurrentFrame.Value;
        if (frame is null) return !IsFramework(requestType);
        if (frame.Recipient is { } recipient)
            return IsLive(frame) && CurrentRequest.Value is null && !frame.RootClaimed
                && requestType == typeof(SubmitBackgroundJobRecipientCommand) && !recipient.Gate.IsClosed
                && recipient.Completion.PermitsContext(context) && MatchesContext(frame.Admission, context)
                && ReferenceEquals(recipient.SourceOwner, JobApplicationSourceOwner.Current)
                && frame.Admission.Requests.TryGetValue(requestType, out var submission) && submission.AuditPermits;
        if (frame.ExternalCheckpoint is { } external)
            return IsLive(frame) && CurrentRequest.Value is null && requestType == external.RequestType
                && !external.Authority.Run.IsClosed && external.Gate?.IsClosed != true
                && MatchesContext(frame.Admission, context)
                && ReferenceEquals(external.Authority.SourceOwner, JobApplicationSourceOwner.Current)
                && frame.Admission.Requests.TryGetValue(requestType, out var externalRequest)
                && externalRequest.AuditPermits;
        if (frame.CapacityCleanup is { } cleanup)
            return IsLive(frame) && CurrentRequest.Value is null
                && requestType == typeof(RecordBackgroundJobCapacityCleanupCommand)
                && MatchesContext(frame.Admission, context)
                && ReferenceEquals(cleanup.SourceOwner, JobApplicationSourceOwner.Current)
                && frame.Admission.Requests.TryGetValue(requestType, out var checkpoint) && checkpoint.AuditPermits;
        if (frame.Claim is { } claim)
        {
            if (!IsLive(frame) || !frame.Admission.Requests.TryGetValue(requestType, out var framework)
                || !framework.AuditPermits || CurrentRequest.Value is not null) return false;
            return frame.Envelope is null
                ? requestType == typeof(ReadBackgroundJobEnvelopeQuery) && !context.IsResolved
                : requestType == typeof(AdmitBackgroundJobAttemptCommand) && MatchesRoute(claim, context);
        }
        if (requestType == typeof(RecordBackgroundJobCapacityCleanupCommand)
            || !IsLive(frame) || !MatchesContext(frame.Admission, context)
            || frame.Completion?.PermitsContext(context) != true
            || typeof(IProvisionsTenant).IsAssignableFrom(requestType)
            || requestType.IsDefined(typeof(AllowsUnresolvedTenantContextAttribute), inherit: false)
            || requestType.IsDefined(typeof(PublicSurfaceAttribute), inherit: false)) return false;

        var admitted = frame.Admission;
        if (!admitted.Requests.TryGetValue(requestType, out var request) || !request.AuditPermits) return false;
        return request.Classification switch
        {
            BackgroundJobRequestClassification.Ordinary => request.Scope == admitted.Scope
                && (HasOrdinaryParent(frame) ? admitted.NestedRequestTypes.Contains(requestType)
                    : !frame.RootClaimed && admitted.RootRequestType == requestType),
            BackgroundJobRequestClassification.FrameworkReadOnly or BackgroundJobRequestClassification.FrameworkReadWrite =>
                request.Scope is null,
            _ => false,
        };
    }

    /// <summary>Retains root/nested position until the admitted request's actual completion.</summary>
    public static bool TryEnterRequest(Type requestType, ITenantContext context, out IDisposable? scope)
    {
        scope = null;
        if (!PermitsRequest(requestType, context)) return false;
        if (CurrentFrame.Value is not { } frame) return true;
        if (frame.Recipient is not null && !frame.TryClaimRoot()) return false;
        var ordinary = frame.Admission.Requests[requestType].Classification == BackgroundJobRequestClassification.Ordinary;
        var root = ordinary && !HasOrdinaryParent(frame);
        if (root && !frame.TryClaimRoot()) return false;
        var invocation = new RequestFrame(frame, CurrentRequest.Value, requestType, ordinary, root);
        CurrentRequest.Value = invocation;
        scope = new RequestScope(invocation);
        return true;
    }

    private static bool HasOrdinaryParent(Frame frame)
    {
        for (var request = CurrentRequest.Value; request is not null; request = request.Parent)
            if (ReferenceEquals(request.Execution, frame) && request.IsOrdinary) return true;
        return false;
    }

    private static bool IsLive(Frame frame)
    {
        if (!frame.IsOpen) return false;
        for (var request = CurrentRequest.Value; request is not null; request = request.Parent)
            if (!ReferenceEquals(request.Execution, frame) || !request.IsOpen) return false;
        return true;
    }

    private static RequestSnapshot Snapshot(BackgroundJobRequestDescriptor request, IAuditCatalog catalog)
    {
        var registered = catalog.TryGet(request.RequestType, out var classification);
        var allowed = registered && !classification.Entries.Any(entry => entry.OperationType == OperationType.PlatformAdmin)
            && (request.Classification == BackgroundJobRequestClassification.Ordinary
                || (classification.WritesNoRow && classification.Entries.Count == 0));
        return new(request.Classification, request.Scope, allowed);
    }

    /// <summary>Prevents direct owner entry from provisioning or retargeting a job.</summary>
    public static void RequireContext(ITenantContext context, IProvisionsTenant? provisioning = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (JobApplicationSourceOwner.IsExternal) throw Denied();
        if (CurrentFrame.Value is { } frame
            && (!IsLive(frame) || provisioning is not null
                || (frame.Recipient is not null ? !MatchesContext(frame.Admission, context) || !frame.Recipient.Completion.PermitsContext(context)
                    : frame.CapacityCleanup is not null || frame.ExternalCheckpoint is not null ? !MatchesContext(frame.Admission, context)
                    : frame.Claim is not null ? !PermitsFrameworkContext(frame, context)
                    : !MatchesContext(frame.Admission, context) || frame.Completion?.PermitsContext(context) != true))) throw Denied();
    }

    /// <summary>Refuses privileged or alternate asynchronous entry even after accessor replacement.</summary>
    public static void RefusePrivilegedEntry()
    {
        if (JobApplicationSourceOwner.IsExternal || CurrentFrame.Value is not null) throw Denied();
    }

    /// <summary>
    /// Denial-only scope for provider construction and calls. Inherited tasks stay
    /// refused even after the original flow restores its ambient state. This
    /// scope confers no execution, checkpoint, tenant or transaction authority.
    /// </summary>
    public static IDisposable EnterExternal()
    {
        if (JobApplicationSourceOwner.IsExternal || CurrentFrame.Value is not null || CurrentRequest.Value is not null)
            throw Denied();
        return JobApplicationSourceOwner.EnterExternal();
    }

    /// <summary>Closed framework pre-read mapping, independent of ambient job admission.</summary>
    public static TransactionMode ModeFor(Type requestType, IBackgroundJobRequestCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(requestType);
        ArgumentNullException.ThrowIfNull(catalog);
        return requestType.IsDefined(typeof(PublicSurfaceAttribute), inherit: false)
            || requestType == typeof(ReadBackgroundJobEnvelopeQuery)
            || requestType == typeof(ReadBackgroundJobExternalStateQuery)
            ? TransactionMode.ReadOnly : TransactionMode.ReadWrite;
    }

    /// <summary>Starts the opaque routing capability before the app-role pre-read.</summary>
    public IDisposable EnterFramework(BackgroundJobRoutingClaim claim, Func<bool>? startGate = null)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (startGate?.Invoke() == false || JobApplicationSourceOwner.IsExternal || CurrentFrame.Value is not null || !ValidTenant(claim.TenantId)
            || !ValidOrganization(claim.OrganizationId)) throw Denied();
        var metadata = requests.All.ToFrozenDictionary(request => request.RequestType, request => Snapshot(request, audit));
        foreach (var required in BackgroundJobFrameworkRequests.All)
            if (!metadata.TryGetValue(required.RequestType, out var found) || !found.AuditPermits
                || found.Classification != required.Classification) throw Denied();
        claim.EnterOnce();
        var admitted = new Admission(claim.TenantId, claim.OrganizationId,
            claim.OrganizationId is null ? BackgroundJobScope.TenantWide : BackgroundJobScope.Organization,
            typeof(ReadBackgroundJobEnvelopeQuery), Array.Empty<Type>().ToFrozenSet(), metadata);
        var frame = new Frame(admitted, claim, startGate: startGate);
        CurrentFrame.Value = frame;
        return new Scope(frame);
    }

    /// <summary>Admits exactly one cleanup checkpoint, without consuming a business receipt.</summary>
    public IDisposable EnterCapacityCleanup(RecordBackgroundJobCapacityCleanupCommand command, IUnitOfWork unit)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(unit);
        if (JobApplicationSourceOwner.IsExternal || CurrentFrame.Value is not null || CurrentRequest.Value is not null
            || !ReferenceEquals(command.SourceOwner, JobApplicationSourceOwner.Current)) throw Denied();
        command.SourceOwner.RequireFreshScopeReady();
        var metadata = requests.All.ToFrozenDictionary(request => request.RequestType, request => Snapshot(request, audit));
        if (!metadata.TryGetValue(typeof(RecordBackgroundJobCapacityCleanupCommand), out var checkpoint)
            || !checkpoint.AuditPermits || checkpoint.Classification != BackgroundJobRequestClassification.FrameworkReadWrite)
            throw Denied();
        var recovery = command.Recovery;
        var admitted = new Admission(recovery.TenantId, recovery.OrganizationId,
            recovery.OrganizationId is null ? BackgroundJobScope.TenantWide : BackgroundJobScope.Organization,
            typeof(RecordBackgroundJobCapacityCleanupCommand), Array.Empty<Type>().ToFrozenSet(), metadata);
        var frame = new Frame(admitted, unit: unit, capacityCleanup: command);
        CurrentFrame.Value = frame;
        return new Scope(frame);
    }

    public static void RequireCapacityCleanup(RecordBackgroundJobCapacityCleanupCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (CurrentFrame.Value is not { } frame || !IsLive(frame)
            || !ReferenceEquals(frame.CapacityCleanup, command)
            || !ReferenceEquals(command.SourceOwner, JobApplicationSourceOwner.Current)
            || CurrentRequest.Value is not { IsOrdinary: false } request
            || !ReferenceEquals(request.Execution, frame)
            || request.RequestType != typeof(RecordBackgroundJobCapacityCleanupCommand)) throw Denied();
    }

    /// <summary>Only the executor admits this exact immutable recipient and one framework request.</summary>
    public IDisposable EnterRecipient(SubmitBackgroundJobRecipientCommand command, IUnitOfWork unit)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Completion.RequireArmed(command, unit);
        var claim = command.Claim;
        if (JobApplicationSourceOwner.IsExternal || CurrentFrame.Value is not null || CurrentRequest.Value is not null
            || command.Gate.IsClosed || !claim.HasLiveLease
            || !ReferenceEquals(command.SourceOwner, JobApplicationSourceOwner.Current)
            || !definitions.TryGet(claim.DefinitionKey, out var definition)
            || definition.Readiness != BackgroundJobDefinitionReadiness.Active
            || definition.ParameterType != typeof(BackgroundJobOccurrenceParameters)
            || !MatchesScope(definition.Scope, claim.OrganizationId)
            || definition.ProfileRegistration.RootRequestType.Assembly != claim.DeclarationType.Assembly
            || !claim.RecurrenceKey.StartsWith(definition.OwnerModule + ".", StringComparison.Ordinal)) throw Denied();
        command.SourceOwner.RequireFreshScopeReady();
        var metadata = requests.All.ToFrozenDictionary(request => request.RequestType, request => Snapshot(request, audit));
        if (!metadata.TryGetValue(typeof(SubmitBackgroundJobRecipientCommand), out var request)
            || !request.AuditPermits || request.Classification != BackgroundJobRequestClassification.FrameworkReadWrite)
            throw Denied();
        claim.EnterOnce();
        var admitted = new Admission(claim.TenantId, claim.OrganizationId, definition.Scope,
            typeof(SubmitBackgroundJobRecipientCommand), Array.Empty<Type>().ToFrozenSet(), metadata);
        var frame = new Frame(admitted, unit: unit, recipient: command);
        CurrentFrame.Value = frame;
        return new Scope(frame);
    }

    public static void RequireRecipient(SubmitBackgroundJobRecipientCommand command)
    {
        if (JobApplicationSourceOwner.IsExternal || CurrentFrame.Value is not { Recipient: { } recipient } frame
            || !IsLive(frame) || !ReferenceEquals(recipient, command) || command.Gate.IsClosed
            || !ReferenceEquals(command.SourceOwner, JobApplicationSourceOwner.Current)
            || CurrentRequest.Value is not { IsOrdinary: false } request
            || !ReferenceEquals(request.Execution, frame) || request.RequestType != typeof(SubmitBackgroundJobRecipientCommand)) throw Denied();
    }

    public IDisposable EnterExternalCheckpoint(ReadBackgroundJobExternalStateQuery request, IUnitOfWork unit) =>
        EnterExternalCheckpoint(request.Owner, unit);
    public IDisposable EnterExternalCheckpoint(RecordBackgroundJobArmCommand request, IUnitOfWork unit) =>
        EnterExternalCheckpoint(request.Owner, unit);
    public IDisposable EnterExternalCheckpoint(RecordBackgroundJobOutcomeCommand request, IUnitOfWork unit) =>
        EnterExternalCheckpoint(request.Owner, unit);

    private Scope EnterExternalCheckpoint(BackgroundJobExternalCheckpointOwner checkpoint, IUnitOfWork unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (JobApplicationSourceOwner.IsExternal || CurrentFrame.Value is not null || CurrentRequest.Value is not null
            || checkpoint.Authority.Run.IsClosed || checkpoint.Gate?.IsClosed == true
            || !ReferenceEquals(checkpoint.Authority.SourceOwner, JobApplicationSourceOwner.Current)) throw Denied();
        checkpoint.Authority.SourceOwner.RequireFreshScopeReady();
        var metadata = requests.All.ToFrozenDictionary(request => request.RequestType, request => Snapshot(request, audit));
        if (!metadata.TryGetValue(checkpoint.RequestType, out var request) || !request.AuditPermits
            || request.Classification != (checkpoint.IsReadOnly ? BackgroundJobRequestClassification.FrameworkReadOnly
                : BackgroundJobRequestClassification.FrameworkReadWrite)) throw Denied();
        var envelope = checkpoint.Authority;
        var admitted = new Admission(envelope.TenantId, envelope.OrganizationId,
            envelope.OrganizationId is null ? BackgroundJobScope.TenantWide : BackgroundJobScope.Organization,
            checkpoint.RequestType, Array.Empty<Type>().ToFrozenSet(), metadata);
        var frame = new Frame(admitted, unit: unit, externalCheckpoint: checkpoint);
        CurrentFrame.Value = frame;
        return new Scope(frame);
    }

    internal static void RequireExternalCheckpoint(BackgroundJobExternalCheckpointOwner checkpoint)
    {
        if (JobApplicationSourceOwner.IsExternal || CurrentFrame.Value is not { } frame || !IsLive(frame)
            || !ReferenceEquals(frame.ExternalCheckpoint, checkpoint) || checkpoint.Authority.Run.IsClosed
            || checkpoint.Gate?.IsClosed == true
            || !ReferenceEquals(checkpoint.Authority.SourceOwner, JobApplicationSourceOwner.Current)
            || CurrentRequest.Value is not { IsOrdinary: false } request
            || !ReferenceEquals(request.Execution, frame) || request.RequestType != checkpoint.RequestType) throw Denied();
    }

    /// <summary>Only JobExecutor advances its live framework scope after validated app-role reading.</summary>
    public static void InstallValidatedEnvelope(BackgroundJobRoutingClaim claim, BackgroundJobEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        RequireRead(claim);
        if (CurrentRequest.Value is not null || envelope.JobId != claim.JobId || envelope.TenantId != claim.TenantId
            || envelope.OrganizationId != claim.OrganizationId || envelope.DefinitionKey != claim.DefinitionKey
            || envelope.Queue != claim.Queue || envelope.OriginalDueAt != claim.OriginalDueAt
            || envelope.Profile != (claim.Profile == BackgroundJobExecutionProfile.Transactional ? "transactional" : "external")) throw Denied();
        CurrentFrame.Value!.Envelope = envelope;
    }

    public static void RequireRead(BackgroundJobRoutingClaim claim)
    {
        if (CurrentFrame.Value is not { } frame || !IsLive(frame)
            || !ReferenceEquals(frame.Claim, claim) || frame.Envelope is not null) throw Denied();
    }

    public static void RequireStart(AdmitBackgroundJobAttemptCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (CurrentFrame.Value is not { } frame || !IsLive(frame)
            || !ReferenceEquals(frame.Claim, command.Claim) || !ReferenceEquals(frame.Envelope, command.Envelope)
            || frame.Envelope is null || frame.StartGate?.Invoke() == false) throw Denied();
    }

    /// <summary>A legacy direct owner cannot borrow the currently active framework request.</summary>
    internal static void RequireOperation(Type? operationType)
    {
        if (JobApplicationSourceOwner.IsExternal) throw Denied();
        if (CurrentFrame.Value is not { } frame) return;
        if (!IsLive(frame) || CurrentRequest.Value is not { } request
            || operationType is null || request.RequestType != operationType) throw Denied();
        if (frame.CapacityCleanup is not null && operationType != typeof(RecordBackgroundJobCapacityCleanupCommand)) throw Denied();
        if (frame.ExternalCheckpoint is { } checkpoint && operationType != checkpoint.RequestType) throw Denied();
        if (frame.Recipient is not null && operationType != typeof(SubmitBackgroundJobRecipientCommand)) throw Denied();
        if (frame.Claim is not null
            && operationType != (frame.Envelope is null ? typeof(ReadBackgroundJobEnvelopeQuery)
                : typeof(AdmitBackgroundJobAttemptCommand))) throw Denied();
    }

    /// <summary>Closes direct and fresh-scope escapes before a second transaction can begin.</summary>
    internal static BackgroundJobCompletionParticipant? RequireOwnerEntry(IUnitOfWork unit, TransactionMode mode,
        Type? operationType, ITenantContext context)
    {
        RequireOperation(operationType);
        if (CurrentFrame.Value is { Recipient: { } recipient } recipientFrame)
        {
            RequireRecipient(recipient);
            RequireContext(context);
            if (!ReferenceEquals(recipientFrame.Unit, unit) || unit.HasActiveTransaction || mode != TransactionMode.ReadWrite)
                throw Denied();
            recipient.SourceOwner.RequireFreshScopeReady();
            return null;
        }
        if (CurrentFrame.Value is { ExternalCheckpoint: { } external } externalFrame)
        {
            RequireExternalCheckpoint(external);
            RequireContext(context);
            if (!ReferenceEquals(externalFrame.Unit, unit) || unit.HasActiveTransaction
                || mode != (external.IsReadOnly ? TransactionMode.ReadOnly : TransactionMode.ReadWrite)) throw Denied();
            external.Authority.SourceOwner.RequireFreshScopeReady();
            return null;
        }
        if (CurrentFrame.Value is { CapacityCleanup: { } cleanup } checkpoint)
        {
            RequireCapacityCleanup(cleanup);
            RequireContext(context);
            if (!ReferenceEquals(checkpoint.Unit, unit) || mode != TransactionMode.ReadWrite
                || unit.HasActiveTransaction) throw Denied();
            cleanup.SourceOwner.RequireFreshScopeReady();
            return null;
        }
        if (CurrentFrame.Value is not { Receipt: not null } frame) return null;
        RequireContext(context);
        if (!ReferenceEquals(frame.Unit, unit) || frame.Completion is not { } completion
            || CurrentRequest.Value is not { } request || !ReferenceEquals(request.Execution, frame)) throw Denied();
        completion.RequireEntry(unit, request.IsRoot, mode);
        return request.IsRoot ? completion : null;
    }

    internal static async Task BindOwnerAsync(BackgroundJobCompletionParticipant completion, IUnitOfWork unit,
        bool owner, CancellationToken cancellationToken)
    {
        var request = RequireRootParticipant(completion, unit);
        await completion.BindAndPreflightAsync(unit, owner, request.RequestType, request, cancellationToken).ConfigureAwait(false);
        _ = RequireRootParticipant(completion, unit);
    }

    internal static async Task FenceOwnerAsync(BackgroundJobCompletionParticipant completion, IUnitOfWork unit,
        LearnStack.SharedKernel.Results.IResultBase response, CancellationToken cancellationToken)
    {
        var request = RequireRootParticipant(completion, unit);
        await completion.FenceOwnerAsync(unit, request.RequestType, request, response, cancellationToken).ConfigureAwait(false);
        _ = RequireRootParticipant(completion, unit);
    }

    internal static bool TryBeginOwnerCommit(BackgroundJobCompletionParticipant completion, IUnitOfWork unit)
    {
        var request = RequireRootParticipant(completion, unit);
        return completion.TryBeginCommit(unit, request.RequestType, request);
    }

    private static RequestFrame RequireRootParticipant(BackgroundJobCompletionParticipant completion, IUnitOfWork unit)
    {
        if (CurrentFrame.Value is not { Receipt: not null } frame || !IsLive(frame)
            || !ReferenceEquals(frame.Unit, unit) || !ReferenceEquals(frame.Completion, completion)
            || CurrentRequest.Value is not { IsRoot: true } request
            || !ReferenceEquals(request.Execution, frame) || request.RequestType != frame.Admission.RootRequestType)
            throw Denied();
        return request;
    }

    internal static ITenantContext AnnouncementContext(ITenantContext context, Type? operationType)
    {
        if (JobApplicationSourceOwner.IsExternal) throw Denied();
        if (CurrentFrame.Value is { Claim: { } claim, Envelope: null } frame)
        {
            if (operationType != typeof(ReadBackgroundJobEnvelopeQuery) || !PermitsFrameworkContext(frame, context)) throw Denied();
            return new RoutingContext(claim);
        }
        return context;
    }

    public static bool PermitsUnresolvedRead(Type requestType, ITenantContext context) =>
        requestType == typeof(ReadBackgroundJobEnvelopeQuery) && !context.IsResolved
        && CurrentFrame.Value is { Claim: not null, Envelope: null } frame && IsLive(frame);

    internal static BackgroundJobId? ParentJobId => CurrentFrame.Value?.Receipt?.Envelope.JobId;
    internal static Guid? IncomingEventId => CurrentFrame.Value?.Receipt?.Envelope.IncomingEventId;

    internal static Type? ProducerOperationType(Type? operationType)
    {
        if (CurrentFrame.Value is not { Recipient: { } recipient }) return operationType;
        RequireRecipient(recipient);
        if (operationType != typeof(SubmitBackgroundJobRecipientCommand)) throw Denied();
        return recipient.Claim.DeclarationType;
    }

    private static bool IsFramework(Type requestType) => requestType == typeof(ReadBackgroundJobEnvelopeQuery)
        || requestType == typeof(AdmitBackgroundJobAttemptCommand)
        || requestType == typeof(RecordBackgroundJobCapacityCleanupCommand)
        || requestType == typeof(ReadBackgroundJobExternalStateQuery)
        || requestType == typeof(RecordBackgroundJobArmCommand)
        || requestType == typeof(RecordBackgroundJobOutcomeCommand)
        || requestType == typeof(SubmitBackgroundJobRecipientCommand);
    private static bool PermitsFrameworkContext(Frame frame, ITenantContext context) =>
        frame.Envelope is null ? !context.IsResolved && CurrentRequest.Value is { IsOrdinary: false }
            : MatchesRoute(frame.Claim!, context) && CurrentRequest.Value is { IsOrdinary: false };
    private static bool MatchesRoute(BackgroundJobRoutingClaim claim, ITenantContext context) =>
        context.IsResolved && context.Origin == TenantContextOrigin.Ambient && context.HostScope is null
        && context.UserId == UserId.SystemActor && context.TenantId == claim.TenantId
        && context.OrganizationId == claim.OrganizationId;

    private sealed class RoutingContext(BackgroundJobRoutingClaim claim) : ITenantContext
    {
        public bool IsResolved => true;
        public TenantId TenantId => claim.TenantId;
        public OrganizationId? OrganizationId => claim.OrganizationId;
        public UserId? UserId => null;
        public string? CorrelationId => null;
        public string? ModuleName => null;
    }

    private static bool MatchesContext(Admission admitted, ITenantContext context) =>
        context.IsResolved && context.Origin == TenantContextOrigin.Ambient && context.HostScope is null
        && context.UserId == UserId.SystemActor && context.TenantId == admitted.TenantId
        && context.OrganizationId == admitted.OrganizationId;

    private static bool ValidTenant(TenantId tenant) =>
        tenant.IsInitialized() && tenant.Value != Guid.Empty && tenant != TenantId.PlatformSentinel;

    private static bool ValidOrganization(OrganizationId? organization) =>
        organization is null || (organization.Value.IsInitialized() && organization.Value.Value != Guid.Empty);

    private static bool MatchesScope(BackgroundJobScope scope, OrganizationId? organization) => scope switch
    {
        BackgroundJobScope.TenantWide => organization is null,
        BackgroundJobScope.Organization => organization is not null,
        _ => false,
    };

    private static InvalidOperationException Denied() => new("Ordinary background-job authority refuses this operation.");

    private sealed record Admission(TenantId TenantId, OrganizationId? OrganizationId, BackgroundJobScope Scope,
        Type RootRequestType, FrozenSet<Type> NestedRequestTypes, FrozenDictionary<Type, RequestSnapshot> Requests);

    private sealed record RequestSnapshot(BackgroundJobRequestClassification Classification, BackgroundJobScope? Scope, bool AuditPermits);

    // Lifecycle revocation is separate from the immutable authority snapshot. A task
    // retaining a closed frame stays refused after its owner's execution flow restores.
    private sealed class Frame(Admission admission, BackgroundJobRoutingClaim? claim = null, ConfirmedStartReceipt? receipt = null,
        Func<bool>? startGate = null, IUnitOfWork? unit = null, BackgroundJobCompletionParticipant? completion = null,
        RecordBackgroundJobCapacityCleanupCommand? capacityCleanup = null,
        BackgroundJobExternalCheckpointOwner? externalCheckpoint = null,
        SubmitBackgroundJobRecipientCommand? recipient = null)
    {
        private int _closed;
        private int _rootClaimed;
        public Admission Admission { get; } = admission;
        public BackgroundJobRoutingClaim? Claim { get; } = claim;
        public ConfirmedStartReceipt? Receipt { get; } = receipt;
        public Func<bool>? StartGate { get; } = startGate;
        public IUnitOfWork? Unit { get; } = unit;
        public BackgroundJobCompletionParticipant? Completion { get; } = completion;
        public RecordBackgroundJobCapacityCleanupCommand? CapacityCleanup { get; } = capacityCleanup;
        public BackgroundJobExternalCheckpointOwner? ExternalCheckpoint { get; } = externalCheckpoint;
        public SubmitBackgroundJobRecipientCommand? Recipient { get; } = recipient;
        public bool RootClaimed => Volatile.Read(ref _rootClaimed) != 0;
        public bool TryClaimRoot() => Interlocked.CompareExchange(ref _rootClaimed, 1, 0) == 0;
        public BackgroundJobEnvelope? Envelope { get; set; }
        public bool IsOpen => Volatile.Read(ref _closed) == 0;
        public void Close() => Interlocked.Exchange(ref _closed, 1);
    }

    private sealed class RequestFrame(Frame execution, RequestFrame? parent, Type requestType, bool isOrdinary, bool isRoot)
    {
        private int _closed;
        public Frame Execution { get; } = execution;
        public RequestFrame? Parent { get; } = parent;
        public Type RequestType { get; } = requestType;
        public bool IsOrdinary { get; } = isOrdinary;
        public bool IsRoot { get; } = isRoot;
        public bool IsOpen => Volatile.Read(ref _closed) == 0;
        public void Close() => Interlocked.Exchange(ref _closed, 1);
    }

    private sealed class RequestScope(RequestFrame request) : IDisposable
    {
        public void Dispose()
        {
            request.Close();
            if (ReferenceEquals(CurrentRequest.Value, request)) CurrentRequest.Value = request.Parent;
        }
    }

    private sealed class Scope(Frame frame) : IDisposable
    {
        public void Dispose()
        {
            frame.Close();
            if (ReferenceEquals(CurrentFrame.Value, frame)) CurrentFrame.Value = null;
        }
    }
}
