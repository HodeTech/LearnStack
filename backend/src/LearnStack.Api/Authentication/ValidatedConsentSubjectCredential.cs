using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;

namespace LearnStack.Api.Authentication;

/// <summary>Retains only the stock-validated credential of an exact consent entry request for its request lifetime.</summary>
internal sealed class ValidatedConsentSubjectCredential
{
    private readonly HttpContext _http;
    private readonly TenantCredentialFeature _feature;
    private readonly byte[] _headerDigest;
    private readonly PathString _path;
    private readonly string _method;
    private readonly Endpoint? _endpoint;
    private readonly CancellationToken _requestCancellation;

    private ValidatedConsentSubjectCredential(HttpContext http, TenantCredentialFeature feature,
        VerifiedProviderSubject subject, string header)
    {
        _http = http;
        _feature = feature;
        _headerDigest = SHA256.HashData(Encoding.UTF8.GetBytes(header));
        _path = http.Request.Path;
        _method = http.Request.Method;
        _endpoint = http.GetEndpoint();
        _requestCancellation = http.RequestAborted;
        Subject = subject;
    }

    internal VerifiedProviderSubject Subject { get; }

    internal static void BeginValidation(HttpContext http, string token)
    {
        if (ConsentSubjectOperation.ForPath(http.Request.Path) is { } purpose && purpose.Matches(http)
            && http.Request.Headers.Authorization.Count == 1 && http.Request.Headers.Authorization[0] is { } header)
            http.Features.Set(new PendingValidation(SHA256.HashData(Encoding.UTF8.GetBytes(header)),
                SHA256.HashData(Encoding.UTF8.GetBytes(token))));
    }

    internal static void ClearPending(HttpContext http) => http.Features.Set<PendingValidation>(null);

    internal static ValidatedConsentSubjectCredential? Capture(HttpContext http, JsonWebToken token,
        VerifiedProviderSubject subject)
    {
        var pending = http.Features.Get<PendingValidation>();
        ClearPending(http);
        if (pending is null || ConsentSubjectOperation.ForPath(http.Request.Path) is not { } purpose || !purpose.Matches(http)
            || http.Features.Get<TenantCredentialFeature>() is not { } feature
            || !ReferenceEquals(feature.VerifiedSubject, subject)
            || http.Request.Headers.Authorization.Count != 1
            || http.Request.Headers.Authorization[0] is not { } header
            || !CryptographicOperations.FixedTimeEquals(pending.HeaderDigest, SHA256.HashData(Encoding.UTF8.GetBytes(header)))
            || !CryptographicOperations.FixedTimeEquals(pending.TokenDigest, SHA256.HashData(Encoding.UTF8.GetBytes(token.EncodedToken))))
            return null;
        return new(http, feature, subject, header);
    }

    internal bool IsCurrent(HttpContext http) => ReferenceEquals(_http, http)
        && !_requestCancellation.IsCancellationRequested && http.RequestAborted == _requestCancellation
        && http.Request.Path == _path && string.Equals(http.Request.Method, _method, StringComparison.Ordinal)
        && ReferenceEquals(http.GetEndpoint(), _endpoint)
        && ReferenceEquals(http.Features.Get<ValidatedConsentSubjectCredential>(), this)
        && ReferenceEquals(http.Features.Get<TenantCredentialFeature>(), _feature)
        && ReferenceEquals(_feature.VerifiedSubject, Subject)
        && http.Request.Headers.Authorization.Count == 1
        && http.Request.Headers.Authorization[0] is { } header
        && CryptographicOperations.FixedTimeEquals(_headerDigest, SHA256.HashData(Encoding.UTF8.GetBytes(header)));

    private sealed record PendingValidation(byte[] HeaderDigest, byte[] TokenDigest)
    {
        public override string ToString() => nameof(PendingValidation);
    }

    public override string ToString() => nameof(ValidatedConsentSubjectCredential);
}
