using System.Diagnostics;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;

namespace LearnStack.SharedKernel.Tenancy;

/// <summary>Validated asynchronous attribution. Execution authority still requires the consumed start receipt.</summary>
public sealed class JobTenantContext : ITenantContext
{
    private JobTenantContext(TenantId tenantId, OrganizationId? organizationId, BackgroundJobId jobId,
        BackgroundJobId? parentJobId, Guid? incomingEventId, UserId? causalActorUserId,
        string traceparent, string moduleName)
    {
        TenantId = tenantId;
        OrganizationId = organizationId;
        JobId = jobId;
        ParentJobId = parentJobId;
        IncomingEventId = incomingEventId;
        CausalActorUserId = causalActorUserId;
        CorrelationId = traceparent;
        ModuleName = moduleName;
    }

    public bool IsResolved => true;
    public TenantContextOrigin? Origin => TenantContextOrigin.Ambient;
    public TenantId TenantId { get; }
    public OrganizationId? OrganizationId { get; }
    public UserId? UserId => Identifiers.UserId.SystemActor;
    public UserId? CausalActorUserId { get; }
    public string? CorrelationId { get; }
    public string? ModuleName { get; }
    public BackgroundJobId JobId { get; }
    public BackgroundJobId? ParentJobId { get; }
    public Guid? IncomingEventId { get; }

    /// <summary>Only the job executor calls this closed factory in production.</summary>
    public static JobTenantContext FromValidatedJob(TenantId tenantId, OrganizationId? organizationId,
        BackgroundJobId jobId, BackgroundJobId? parentJobId, Guid? incomingEventId,
        UserId? causalActorUserId, string traceparent, string moduleName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        if (!tenantId.IsInitialized() || tenantId.Value == Guid.Empty || tenantId == Identifiers.TenantId.PlatformSentinel
            || organizationId is { } organization && (!organization.IsInitialized() || organization.Value == Guid.Empty)
            || !jobId.IsInitialized() || jobId.Value.Version != 7
            || parentJobId is { } parent && (!parent.IsInitialized() || parent.Value.Version != 7 || parent == jobId)
            || incomingEventId == Guid.Empty
            || causalActorUserId is { } actor && (!actor.IsInitialized() || actor.Value == Guid.Empty)
            || !ActivityContext.TryParse(traceparent, null, out var trace)
            || traceparent != $"00-{trace.TraceId}-{trace.SpanId}-{(byte)trace.TraceFlags:x2}"
            || moduleName.Any(character => character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9'))
            || moduleName[0] is not (>= 'a' and <= 'z'))
            throw new ArgumentException("Job attribution requires valid immutable scope, identities and trace.");
        return new(tenantId, organizationId, jobId, parentJobId, incomingEventId, causalActorUserId,
            traceparent, moduleName);
    }
}
