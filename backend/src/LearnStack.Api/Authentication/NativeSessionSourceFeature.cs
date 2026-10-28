using LearnStack.Api.Tenancy;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Time;
using System.Security.Cryptography;
using System.Text;

namespace LearnStack.Api.Authentication;

/// <summary>One quota-admitted source/candidate tuple, distinct from all provider and normalized identity facts.</summary>
internal sealed class NativeSessionSourceFeature
{
    private readonly HttpContext _request;
    private readonly HostClassification _host;
    private readonly VerifiedProviderSubject _subject;
    private readonly SubjectAdmissionDecision _decision;
    private readonly TenantSubjectAdmissionProvenance _provenance;
    private readonly PathString _path;
    private readonly string _method;
    private readonly EffectiveHostAccessor _hosts;
    private readonly NativeSessionSourcePolicy _policy;
    private readonly IClock _clock;
    private readonly byte[] _credentialDigest;

    internal NativeSessionSourceFeature(HttpContext request, HostClassification host,
        VerifiedProviderSubject subject, SubjectAdmissionDecision decision,
        TenantSubjectAdmissionProvenance provenance, TenantId? tenant, OrganizationId? organization,
        GlobalSelfOperation? operation, EffectiveHostAccessor hosts, NativeSessionSourcePolicy policy, IClock clock)
    {
        _request = request;
        _host = host;
        _subject = subject;
        _decision = decision;
        _provenance = provenance;
        _path = request.Request.Path;
        _method = request.Request.Method;
        _hosts = hosts;
        _policy = policy;
        _clock = clock;
        _credentialDigest = SHA256.HashData(Encoding.UTF8.GetBytes(
            request.Request.Headers[NativeSessionSourcePolicy.SecretHeader][0]!));
        Tenant = tenant;
        Organization = organization;
        GlobalOperation = operation;
    }

    internal TenantId? Tenant { get; }
    internal OrganizationId? Organization { get; }
    internal GlobalSelfOperation? GlobalOperation { get; }

    internal bool IsCurrent(HttpContext request) =>
        ReferenceEquals(_request, request) && !request.RequestAborted.IsCancellationRequested
        && request.Request.Path == _path && string.Equals(request.Request.Method, _method, StringComparison.Ordinal)
        && ReferenceEquals(request.Features.Get<NativeSessionSourceFeature>(), this)
        && ReferenceEquals(request.Features.Get<HostClassification>(), _host)
        && ReferenceEquals(request.Features.Get<TenantCredentialFeature>()?.VerifiedSubject, _subject)
        && ReferenceEquals(request.Features.Get<SubjectAdmissionDecision>(), _decision)
        && ReferenceEquals(request.Features.Get<TenantSubjectAdmissionProvenance>(), _provenance)
        && _decision.IsAcquired && _provenance.Matches(_subject)
        && _hosts.IsTrustedHop(request) && string.Equals(_hosts.For(request), _host.Host, StringComparison.Ordinal)
        && _policy.Matches(request.Request.Headers[NativeSessionSourcePolicy.SecretHeader], _clock.UtcNow)
        && CryptographicOperations.FixedTimeEquals(_credentialDigest, SHA256.HashData(Encoding.UTF8.GetBytes(
            request.Request.Headers[NativeSessionSourcePolicy.SecretHeader][0]!)))
        && NativeSessionSourcePolicy.HasOnlyKnownNativeHeaders(request.Request.Headers)
        && MatchesSelector(request, NativeSessionSourcePolicy.TenantHeader, Tenant?.Value)
        && MatchesSelector(request, NativeSessionSourcePolicy.OrganizationHeader, Organization?.Value)
        && ReferenceEquals(GlobalSelfOperation.ForPath(request.Request.Path), GlobalOperation);

    public override string ToString() => nameof(NativeSessionSourceFeature);

    private static bool MatchesSelector(HttpContext request, string header, Guid? expected)
    {
        var values = request.Request.Headers[header];
        return expected is null ? !request.Request.Headers.ContainsKey(header)
            : values.Count == 1 && string.Equals(values[0], expected.Value.ToString("D"), StringComparison.Ordinal);
    }
}
