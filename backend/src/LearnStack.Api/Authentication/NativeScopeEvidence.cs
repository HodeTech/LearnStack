using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Api.Authentication;

/// <summary>The exact source tuple bound to the existing stored actor, without altering provider identity facts.</summary>
internal sealed class NativeScopeEvidence
{
    private readonly HttpContext _request;
    private readonly NativeSessionSourceFeature _source;
    private readonly VerifiedProviderSubject _subject;
    private readonly ValidatedTenantIdentity _identity;

    private NativeScopeEvidence(HttpContext request, NativeSessionSourceFeature source,
        VerifiedProviderSubject subject, ValidatedTenantIdentity identity)
    {
        _request = request;
        _source = source;
        _subject = subject;
        _identity = identity;
    }

    internal TenantId Tenant => _source.Tenant!.Value;
    internal OrganizationId? Organization => _source.Organization;
    internal UserId Actor => _identity.ActorUserId;

    internal static NativeScopeEvidence? TryMint(HttpContext request, ValidatedTenantIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(identity);
        var source = request.Features.Get<NativeSessionSourceFeature>();
        var feature = request.Features.Get<TenantCredentialFeature>();
        if (source is not { GlobalOperation: null, Tenant: not null }
            || feature?.VerifiedSubject is not { } subject || !source.IsCurrent(request)
            || !ReferenceEquals(feature.Identity, identity)
            || identity.Issuer != subject.Issuer || identity.Subject != subject.Subject
            || identity.TenantId != subject.TenantId || identity.OrganizationId != subject.OrganizationId
            || (subject.ClaimedActor is { } actor && actor != identity.ActorUserId)
            || !StronglyTypedId.IsAssigned(identity.ActorUserId) || identity.ActorUserId == UserId.SystemActor)
            return null;
        return new(request, source, subject, identity);
    }

    internal bool IsCurrent(HttpContext request) =>
        ReferenceEquals(_request, request) && _source.IsCurrent(request)
        && ReferenceEquals(request.Features.Get<NativeScopeEvidence>(), this)
        && request.Features.Get<TenantCredentialFeature>() is { } feature
        && ReferenceEquals(feature.VerifiedSubject, _subject) && ReferenceEquals(feature.Identity, _identity);

    public override string ToString() => nameof(NativeScopeEvidence);
}
