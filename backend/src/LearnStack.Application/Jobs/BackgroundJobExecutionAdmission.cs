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
public sealed class BackgroundJobExecutionAdmission(
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
    public IDisposable Enter(ConfirmedStartReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var definitionKey = receipt.Envelope.DefinitionKey;
        var tenantId = receipt.Envelope.TenantId;
        var organizationId = receipt.Envelope.OrganizationId;
        var stage = receipt.Stage;
        if (CurrentFrame.Value is not null || !ValidTenant(tenantId)
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
        var frame = new Frame(admitted, receipt: receipt);
        CurrentFrame.Value = frame;
        return new Scope(frame);
    }

    /// <summary>Checks the positive request set before MediatR constructs its handler.</summary>
    public static bool PermitsRequest(Type requestType, ITenantContext context)
    {
        ArgumentNullException.ThrowIfNull(requestType);
        ArgumentNullException.ThrowIfNull(context);
        var frame = CurrentFrame.Value;
        if (frame is null) return !IsFramework(requestType);
        if (frame.Claim is { } claim)
        {
            if (!IsLive(frame) || !frame.Admission.Requests.TryGetValue(requestType, out var framework)
                || !framework.AuditPermits || CurrentRequest.Value is not null) return false;
            return frame.Envelope is null
                ? requestType == typeof(ReadBackgroundJobEnvelopeQuery) && !context.IsResolved
                : requestType == typeof(AdmitBackgroundJobAttemptCommand) && MatchesRoute(claim, context);
        }
        if (!IsLive(frame) || !MatchesContext(frame.Admission, context)
            || typeof(IProvisionsTenant).IsAssignableFrom(requestType)
            || requestType.IsDefined(typeof(AllowsUnresolvedTenantContextAttribute), inherit: false)
            || requestType.IsDefined(typeof(PublicSurfaceAttribute), inherit: false)) return false;

        var admitted = frame.Admission;
        if (!admitted.Requests.TryGetValue(requestType, out var request) || !request.AuditPermits) return false;
        return request.Classification switch
        {
            BackgroundJobRequestClassification.Ordinary => request.Scope == admitted.Scope
                && (HasOrdinaryParent(frame) ? admitted.NestedRequestTypes.Contains(requestType) : admitted.RootRequestType == requestType),
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
        var invocation = new RequestFrame(frame, CurrentRequest.Value,
            frame.Admission.Requests[requestType].Classification == BackgroundJobRequestClassification.Ordinary);
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
        if (CurrentFrame.Value is { } frame
            && (!IsLive(frame) || provisioning is not null
                || (frame.Claim is not null ? !PermitsFrameworkContext(frame, context)
                    : !MatchesContext(frame.Admission, context)))) throw Denied();
    }

    /// <summary>Refuses privileged or alternate asynchronous entry even after accessor replacement.</summary>
    public static void RefusePrivilegedEntry()
    {
        if (CurrentFrame.Value is not null) throw Denied();
    }

    /// <summary>Closed framework pre-read mapping, independent of ambient job admission.</summary>
    public static TransactionMode ModeFor(Type requestType, IBackgroundJobRequestCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(requestType);
        ArgumentNullException.ThrowIfNull(catalog);
        return requestType.IsDefined(typeof(PublicSurfaceAttribute), inherit: false)
            || requestType == typeof(ReadBackgroundJobEnvelopeQuery)
            ? TransactionMode.ReadOnly : TransactionMode.ReadWrite;
    }

    /// <summary>Starts the opaque routing capability before the app-role pre-read.</summary>
    public IDisposable EnterFramework(BackgroundJobRoutingClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (CurrentFrame.Value is not null || !ValidTenant(claim.TenantId)
            || !ValidOrganization(claim.OrganizationId)) throw Denied();
        var metadata = requests.All.ToFrozenDictionary(request => request.RequestType, request => Snapshot(request, audit));
        foreach (var required in BackgroundJobFrameworkRequests.All)
            if (!metadata.TryGetValue(required.RequestType, out var found) || !found.AuditPermits
                || found.Classification != required.Classification) throw Denied();
        claim.EnterOnce();
        var admitted = new Admission(claim.TenantId, claim.OrganizationId,
            claim.OrganizationId is null ? BackgroundJobScope.TenantWide : BackgroundJobScope.Organization,
            typeof(ReadBackgroundJobEnvelopeQuery), Array.Empty<Type>().ToFrozenSet(), metadata);
        var frame = new Frame(admitted, claim);
        CurrentFrame.Value = frame;
        return new Scope(frame);
    }

    /// <summary>Only JobExecutor advances its live framework scope after validated app-role reading.</summary>
    public static void InstallValidatedEnvelope(BackgroundJobRoutingClaim claim, BackgroundJobEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        RequireRead(claim);
        if (CurrentRequest.Value is not null || envelope.JobId != claim.JobId || envelope.TenantId != claim.TenantId
            || envelope.OrganizationId != claim.OrganizationId || envelope.DefinitionKey != claim.DefinitionKey
            || envelope.Queue != claim.Queue || envelope.OriginalDueAt != claim.OriginalDueAt) throw Denied();
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
            || frame.Envelope is null) throw Denied();
    }

    /// <summary>A legacy direct owner cannot borrow the currently active framework request.</summary>
    internal static void RequireOperation(Type? operationType)
    {
        if (CurrentFrame.Value is { Claim: not null } frame
            && (!IsLive(frame) || CurrentRequest.Value is null
                || operationType != (frame.Envelope is null ? typeof(ReadBackgroundJobEnvelopeQuery)
                    : typeof(AdmitBackgroundJobAttemptCommand)))) throw Denied();
    }

    internal static ITenantContext AnnouncementContext(ITenantContext context, Type? operationType)
    {
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

    private static bool IsFramework(Type requestType) => requestType == typeof(ReadBackgroundJobEnvelopeQuery)
        || requestType == typeof(AdmitBackgroundJobAttemptCommand);
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
    private sealed class Frame(Admission admission, BackgroundJobRoutingClaim? claim = null, ConfirmedStartReceipt? receipt = null)
    {
        private int _closed;
        public Admission Admission { get; } = admission;
        public BackgroundJobRoutingClaim? Claim { get; } = claim;
        public ConfirmedStartReceipt? Receipt { get; } = receipt;
        public BackgroundJobEnvelope? Envelope { get; set; }
        public bool IsOpen => Volatile.Read(ref _closed) == 0;
        public void Close() => Interlocked.Exchange(ref _closed, 1);
    }

    private sealed class RequestFrame(Frame execution, RequestFrame? parent, bool isOrdinary)
    {
        private int _closed;
        public Frame Execution { get; } = execution;
        public RequestFrame? Parent { get; } = parent;
        public bool IsOrdinary { get; } = isOrdinary;
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
