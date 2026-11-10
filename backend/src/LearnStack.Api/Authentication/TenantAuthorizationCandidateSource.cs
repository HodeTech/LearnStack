using LearnStack.Api.Tenancy;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;

namespace LearnStack.Api.Authentication;

/// <summary>Mints only the exact claim/host candidate after quota-admitted existing-binding normalization.</summary>
internal sealed class TenantAuthorizationCandidateSource(IHttpContextAccessor accessor)
    : AuthorizationCandidate.MintingSource, IAuthorizationCandidateSource
{
    AuthorizationCandidate? IAuthorizationCandidateSource.ForValidatedScope(TenantId tenant, OrganizationId? organization) =>
        ForValidatedScope(tenant, organization);

    internal AuthorizationCandidate? ForValidatedScope(TenantId tenant, OrganizationId? organization)
    {
        var http = accessor.HttpContext;
        var native = http?.Features.Get<NativeScopeEvidence>();
        var invitation = http?.Features.Get<InvitationEntrySource>();
        var consent = http?.Features.Get<ConsentSubjectSource>();
        if (http is null || http.RequestAborted.IsCancellationRequested
            || http.Features.Get<HostClassification>() is not { } host
            || http.Features.Get<TenantCredentialFeature>() is not { VerifiedSubject: { } subject, Identity: { } identity }
            || http.Features.Get<SubjectAdmissionDecision>() is not { IsAcquired: true } decision
            || http.Features.Get<TenantSubjectAdmissionProvenance>() is not { } provenance
            || !provenance.Matches(subject) || !MatchesIdentity(identity, subject)
            || (http.Features.Get<NativeSessionSourceFeature>() is not null
                && (consent is not null ? !consent.IsCurrent(http) : invitation is null ? native is null || !native.IsCurrent(http) : !invitation.IsCurrent(http)))
            || (consent is not null ? !consent.MatchesScope(http, tenant, organization) : invitation is not null ? invitation.Offer!.TenantId != tenant || invitation.Offer.OrganizationId != organization
                : native is null
                ? identity.TenantId != tenant || organization != (identity.OrganizationId ?? host.OrganizationId)
                : native.Tenant != tenant || native.Organization != organization)
            || (host.TenantId is { } hostTenant && hostTenant != tenant)
            || (host.OrganizationId is { } hostOrganization && hostOrganization != organization))
            return null;

        return ForCandidate(AuthorizationSourceKind.Http, identity.ActorUserId, tenant, organization,
            new CredentialWitness(http, host, subject, identity, decision, provenance, native, invitation, consent));
    }

    protected override bool IsEvidenceCurrent(object? evidence) => evidence is CredentialWitness witness
        && ReferenceEquals(accessor.HttpContext, witness.Http)
        && !witness.Http.RequestAborted.IsCancellationRequested
        && ReferenceEquals(witness.Http.Features.Get<HostClassification>(), witness.Host)
        && witness.Http.Features.Get<TenantCredentialFeature>() is { } feature
        && ReferenceEquals(feature.Identity, witness.Identity)
        && ReferenceEquals(feature.VerifiedSubject, witness.Subject)
        && ReferenceEquals(witness.Http.Features.Get<SubjectAdmissionDecision>(), witness.Decision)
        && ReferenceEquals(witness.Http.Features.Get<TenantSubjectAdmissionProvenance>(), witness.Provenance)
        && witness.Provenance.Matches(witness.Subject)
        && (witness.Consent is not null ? ReferenceEquals(witness.Http.Features.Get<ConsentSubjectSource>(), witness.Consent)
            && witness.Consent.IsCurrent(witness.Http) : witness.Invitation is not null ? ReferenceEquals(witness.Http.Features.Get<InvitationEntrySource>(), witness.Invitation)
            && witness.Invitation.IsCurrent(witness.Http) : witness.Native is null
            ? witness.Http.Features.Get<NativeSessionSourceFeature>() is null
                && witness.Http.Features.Get<NativeScopeEvidence>() is null
            : ReferenceEquals(witness.Http.Features.Get<NativeScopeEvidence>(), witness.Native)
                && witness.Native.IsCurrent(witness.Http));

    private static bool MatchesIdentity(ValidatedTenantIdentity identity, VerifiedProviderSubject subject) =>
        identity.Issuer == subject.Issuer && identity.Subject == subject.Subject
        && (subject.ClaimedActor is null || subject.ClaimedActor == identity.ActorUserId)
        && identity.TenantId == subject.TenantId && identity.OrganizationId == subject.OrganizationId
        && StronglyTypedId.IsAssigned(identity.ActorUserId) && identity.ActorUserId != UserId.SystemActor;

    private sealed class CredentialWitness(HttpContext http, HostClassification host, VerifiedProviderSubject subject,
        ValidatedTenantIdentity identity, SubjectAdmissionDecision decision,
        TenantSubjectAdmissionProvenance provenance, NativeScopeEvidence? native, InvitationEntrySource? invitation, ConsentSubjectSource? consent)
    {
        internal HttpContext Http { get; } = http;
        internal HostClassification Host { get; } = host;
        internal VerifiedProviderSubject Subject { get; } = subject;
        internal ValidatedTenantIdentity Identity { get; } = identity;
        internal SubjectAdmissionDecision Decision { get; } = decision;
        internal TenantSubjectAdmissionProvenance Provenance { get; } = provenance;
        internal NativeScopeEvidence? Native { get; } = native;
        internal InvitationEntrySource? Invitation { get; } = invitation;
        internal ConsentSubjectSource? Consent { get; } = consent;

        public override string ToString() => nameof(CredentialWitness);
    }
}
