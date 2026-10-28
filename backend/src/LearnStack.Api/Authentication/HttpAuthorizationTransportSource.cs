using System.Net;
using LearnStack.Api.Tenancy;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Permissions;
using Microsoft.AspNetCore.Http.Features;

namespace LearnStack.Api.Authentication;

/// <summary>Reuses admitted visitor metadata and fixed endpoint identity; never reparses forwarded headers.</summary>
internal sealed class HttpAuthorizationTransportSource(IHttpContextAccessor accessor, OperationHttpClassifier classifier)
    : OperationTransportProvenance.MintingSource, IAuthorizationTransportSource
{
    public OperationTransportProvenance? Capture(OperationInvocation invocation)
    {
        var http = accessor.HttpContext;
        if (http is null || http.RequestAborted.IsCancellationRequested
            || invocation.Candidate is not { IsActive: true, SourceKind: AuthorizationSourceKind.Http }
            || !invocation.IsActive || http.Features.Get<TenantCredentialFeature>()?.Identity is not { } identity
            || identity.ActorUserId != invocation.Actor || !MatchesScope(http, identity, invocation)
            || classifier.Classify(http.GetEndpoint(), out _) != HttpOperationClassification.Application
            || http.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName is not { } route)
            return null;
        IPAddress? address = null;
        if (http.Features.Get<AnonymousVisitor>() is { Refuse: false } visitor
            && IPAddress.TryParse(visitor.VisitorKey, out var admitted)) address = admitted;
        address ??= http.Features.Get<IHttpConnectionFeature>()?.RemoteIpAddress;
        var agent = http.Request.Headers.UserAgent;
        return ForTransport(invocation, route, address, agent.Count == 0 ? null : agent.ToString());
    }

    private static bool MatchesScope(HttpContext http, ValidatedTenantIdentity identity, OperationInvocation invocation) =>
        http.Features.Get<NativeScopeEvidence>() is { } native
            ? native.IsCurrent(http) && native.Actor == invocation.Actor && native.Tenant == invocation.TenantId
                && native.Organization == invocation.OrganizationId
            : identity.TenantId == invocation.TenantId;
}
