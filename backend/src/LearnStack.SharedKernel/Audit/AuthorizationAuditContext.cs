using System.Collections.Immutable;
using System.Net;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.SharedKernel.Audit;

/// <summary>The central composer's bounded immutable decision and source context, never handler metadata.</summary>
public sealed class AuthorizationAuditContext
{
    private readonly IPAddress? _address;

    private AuthorizationAuditContext(OperationInvocation invocation, ProtectedOperationDecision? decision,
        OperationTransportProvenance? transport)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var target = invocation.TenantId ?? (invocation.TrustedSource is { SourceKind: AuthorizationSourceKind.Seed }
            && invocation.Operation is IProvisionsTenant provisioning ? provisioning.ProvisioningTenantId : (TenantId?)null);
        if (!invocation.IsActive || target is not { } tenant || !StronglyTypedId.IsAssigned(tenant)
            || tenant == TenantId.PlatformSentinel
            || invocation.Actor is { } actor && !StronglyTypedId.IsAssigned(actor)
            || invocation.Actor is null && invocation.TrustedSource is not { SourceKind: AuthorizationSourceKind.Seed }
            || invocation.Candidate is { } candidate && invocation.Actor != candidate.Actor
            || (decision is not null && (!ReferenceEquals(decision.Invocation, invocation) || !decision.IsActive))
            || (invocation.Candidate is null) == (invocation.TrustedSource is null)
            || (transport is not null && !transport.Matches(invocation)))
            throw new ArgumentException("Authorization audit context has no live source.");
        TenantId = tenant;
        Invocation = invocation;
        OrganizationId = invocation.OrganizationId;
        Actor = invocation.Actor;
        PolicyId = invocation.Policy.Id;
        SourceKind = invocation.Candidate?.SourceKind ?? invocation.TrustedSource!.SourceKind;
        MembershipIds = decision?.MembershipIds ?? [];
        RoleIds = decision?.RoleIds ?? [];
        Route = transport?.Route;
        _address = transport?.Address;
        UserAgent = transport?.UserAgent;
        if (invocation.TrustedSource is not null)
        {
            CausationId = invocation.Context switch
            {
                EventTenantContext delivery => delivery.EventId,
                JobTenantContext job => job.IncomingEventId,
                _ => null,
            };
            CausalActor = invocation.Context.CausalActorUserId;
            JobId = (invocation.Context as JobTenantContext)?.JobId;
            ParentJobId = (invocation.Context as JobTenantContext)?.ParentJobId;
        }
    }

    public TenantId TenantId { get; }
    /// <summary>Capture-frame attachment identity; it is not serialized into persisted metadata.</summary>
    public OperationInvocation Invocation { get; }
    public OrganizationId? OrganizationId { get; }
    public UserId? Actor { get; }
    public string PolicyId { get; }
    public AuthorizationSourceKind SourceKind { get; }
    public ImmutableArray<Guid> MembershipIds { get; }
    public ImmutableArray<Guid> RoleIds { get; }
    public string? Route { get; }
    public IPAddress? Address => _address is null ? null : new IPAddress(_address.GetAddressBytes());
    public string? UserAgent { get; }
    public Guid? CausationId { get; }
    public UserId? CausalActor { get; }
    public BackgroundJobId? JobId { get; }
    public BackgroundJobId? ParentJobId { get; }
    public override string ToString() => nameof(AuthorizationAuditContext);

    /// <summary>Closed versioned shape; no arbitrary dictionary, raw route or grant graph.</summary>
    public string ToJson()
    {
        var fields = new List<string>
        {
            "\"version\":1", "\"policy\":" + AuditJson.Quote(PolicyId),
            "\"source\":" + AuditJson.Quote(SourceKind switch
            {
                AuthorizationSourceKind.Http => "http", AuthorizationSourceKind.Seed => "seed",
                AuthorizationSourceKind.IntegrationEvent => "event", AuthorizationSourceKind.BackgroundJob => "job",
                _ => throw new InvalidOperationException("Authorization source is unknown."),
            }),
            "\"memberships\":[" + string.Join(',', MembershipIds.Select(id => AuditJson.Quote(id.ToString("D")))) + "]",
            "\"roles\":[" + string.Join(',', RoleIds.Select(id => AuditJson.Quote(id.ToString("D")))) + "]",
        };
        if (Route is not null) fields.Add("\"route\":" + AuditJson.Quote(Route));
        if (CausationId is { } causation) fields.Add("\"causation_id\":" + AuditJson.Quote(causation.ToString("D")));
        if (CausalActor is { } causal) fields.Add("\"causal_actor_user_id\":" + AuditJson.Quote(causal.Value.ToString("D")));
        if (JobId is { } job) fields.Add("\"job_id\":" + AuditJson.Quote(job.Value.ToString("D")));
        if (ParentJobId is { } parent) fields.Add("\"parent_job_id\":" + AuditJson.Quote(parent.Value.ToString("D")));
        return "{" + string.Join(',', fields) + "}";
    }

#pragma warning disable CA1034 // Only the central evidence owner composes source and decision context.
    public abstract class MintingSource : AuthorizationEvidenceOccurrence.MintingSource
#pragma warning restore CA1034
    {
        protected static AuthorizationAuditContext ForAuditContext(OperationInvocation invocation,
            ProtectedOperationDecision? decision, OperationTransportProvenance? transport) => new(invocation, decision, transport);
    }
}
