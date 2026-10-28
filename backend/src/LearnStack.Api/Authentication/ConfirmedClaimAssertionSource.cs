using System.Net;
using LearnStack.Api.Tenancy;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Http.Features;

namespace LearnStack.Api.Authentication;

/// <summary>Staged adapter for successful claim-only resolution; consumes no host allowance and retains no assertion value.</summary>
internal sealed class ConfirmedClaimAssertionSource(IHttpContextAccessor accessor,
    TenantAuthorizationCandidateSource candidates, OperationHttpClassifier classifier)
    : ConfirmedClaimAssertion.MintingSource
{
    private readonly Dictionary<ClaimAssertionDimension, ConfirmedClaimAssertion> _captured = [];
    private HttpContext? _request;

    internal ConfirmedClaimAssertion? Capture(ITenantContext context, ClaimAssertionDimension dimension)
    {
        var http = accessor.HttpContext;
        if (!CanIssue || http is null || (_request is not null && !ReferenceEquals(_request, http))
            || http.RequestAborted.IsCancellationRequested || !context.IsResolved
            || context.Origin != TenantContextOrigin.ClaimAndMembership) return null;
        try
        {
            if (candidates.ForValidatedScope(context.TenantId, context.OrganizationId) is not { IsActive: true } candidate
                || candidate.Actor != context.UserId) return null;
        }
        catch (ObjectDisposedException) { return null; }
        if (_captured.TryGetValue(dimension, out var prior)) return prior.IsActive ? prior : null;
        var route = classifier.Classify(http.GetEndpoint(), out _) == HttpOperationClassification.Application
            ? http.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName : null;
        IPAddress? address = null;
        if (http.Features.Get<AnonymousVisitor>() is { Refuse: false } visitor
            && IPAddress.TryParse(visitor.VisitorKey, out var admitted)) address = admitted;
        address ??= http.Features.Get<IHttpConnectionFeature>()?.RemoteIpAddress;
        var agent = http.Request.Headers.UserAgent;
        var captured = ForAssertion(context, dimension, route, address, agent.Count == 0 ? null : agent.ToString());
        _request = http;
        _captured.Add(dimension, captured);
        return captured;
    }
}
