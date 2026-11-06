using LearnStack.Api.Common;
using LearnStack.Api.Tenancy;
using LearnStack.Modules.Identity.Application.Contracts.Invitations;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Localization;

namespace LearnStack.Api.Authentication;

/// <summary>Captures only the current HTTP/native/token/offer act; an ancestor invocation cannot reuse it.</summary>
public sealed class InvitationEntrySource(IHttpContextAccessor accessor, IInvitationEntryLookup lookup,
    IdentityAuthorityQualification qualification, Func<IOperationInvocationContext> invocations) : InvitationEntryScopeWitness.MintingSource, IInvitationEntryScopeSource
{
    private HttpContext? _http;
    private Endpoint? _endpoint;
    private NativeSessionSourceFeature? _native;
    private ValidatedInvitationCredential? _credential;
    private ValidatedTenantIdentity? _identity;
    private InvitationEntryHttpInput? _input;
    private InvitationEntryOperation? _purpose;
    private GlobalIdentitySubject? _subject;
    private InvitationEntryScopeWitness? _applicationWitness;
    internal InvitationEntryObservation? Offer { get; private set; }
    internal object? Request { get; private set; }
    internal Type? EffectiveRequestType => Request?.GetType();
    internal bool IsCurrent(HttpContext http) => ReferenceEquals(_http, http) && ReferenceEquals(accessor.HttpContext, http)
        && !http.RequestAborted.IsCancellationRequested && ReferenceEquals(http.GetEndpoint(), _endpoint)
        && ReferenceEquals(http.Features.Get<InvitationEntrySource>(), this)
        && _purpose?.Matches(http) is true && ReferenceEquals(_native, http.Features.Get<NativeSessionSourceFeature>())
        && _native?.IsCurrent(http) is true && _native.InvitationOperation == _purpose
        && ReferenceEquals(_credential, http.Features.Get<ValidatedInvitationCredential>()) && _credential?.IsCurrent(http) is true
        && ReferenceEquals(_identity, http.Features.Get<TenantCredentialFeature>()?.Identity)
        && _identity is not null && _input?.IsCurrent(http) is true && Offer is not null && Request is not null;

    internal async Task<Result<InvitationEntryObservation>> PrepareAsync(HttpContext http)
    {
        if (_http is not null || InvitationEntryOperation.ForPath(http.Request.Path) is not { } purpose
            || !purpose.Matches(http) || http.Features.Get<NativeSessionSourceFeature>() is not { } native
            || native.InvitationOperation != purpose || !native.IsCurrent(http)
            || http.Features.Get<ValidatedInvitationCredential>() is not { } credential || !credential.IsCurrent(http)
            || http.Features.Get<TenantCredentialFeature>()?.Identity is not { } identity
            || http.Features.Get<HostClassification>() is not { TenantId: { } tenant } host
            || qualification.Current is not { } authority || identity.Issuer != authority.Issuer)
            return Fail("lockey_not_found");
        var input = await InvitationEntryHttpInput.ReadAsync(http, purpose);
        if (input is null) return Fail("lockey_validation_failed");
        var subject = new GlobalIdentitySubject(authority.AuthorityId, authority.Issuer, identity.Subject);
        var offer = await lookup.LookupAsync(input.InvitationId, Convert.FromHexString(input.Digest), tenant, subject, http.RequestAborted);
        if (offer is null || offer.Actor != identity.ActorUserId || offer.TenantId != tenant
            || host.OrganizationId is { } branch && branch != offer.OrganizationId
            || identity.TenantId is not null && (identity.TenantId != offer.TenantId || identity.OrganizationId != offer.OrganizationId)
            || !native.IsCurrent(http) || !credential.IsCurrent(http)) return Fail("lockey_not_found");
        object request;
        if (offer.Accepted && purpose.RequestType == typeof(InspectInvitationQuery)) return Fail("lockey_not_found");
        if (offer.Accepted)
            request = new GetOwnInvitationResultQuery(input.InvitationId, input.Code);
        else if (purpose.RequestType == typeof(AcceptInvitationCommand))
        {
            if (!ProtectedEntityTag.TryParse(input.IfMatch, out var version)) return Fail("lockey_validation_failed");
            request = new AcceptInvitationCommand(input.InvitationId, input.Code, version);
        }
        else if (purpose.RequestType == typeof(InspectInvitationQuery)) request = new InspectInvitationQuery(input.InvitationId, input.Code);
        else return Fail("lockey_not_found");
        _http = http; _endpoint = http.GetEndpoint(); _purpose = purpose; _native = native;
        _credential = credential; _identity = identity; _input = input; _subject = subject; Offer = offer; Request = request;
        http.Features.Set(this);
        http.Features.Set(new InvitationEntryRequest(request));
        return Result.Ok(offer);
    }
    internal bool MatchesInvocation(OperationInvocation invocation) => IsEvidenceCurrent(this, invocation);

    public InvitationEntryScopeWitness? ForInvocation(OperationInvocation invocation)
    {
        if (_http is null || !IsEvidenceCurrent(this, invocation)) return null;
        if (invocation.EntryKind == OperationEntryKind.Application && _applicationWitness is not null)
            return ReferenceEquals(_applicationWitness.Invocation, invocation) ? _applicationWitness : null;
        var mode = EffectiveRequestType == typeof(AcceptInvitationCommand) ? TransactionMode.ReadWrite : TransactionMode.ReadOnly;
        var witness = Capture(this, invocation, Offer!, _subject!, _input!.Digest, mode,
            _http.Features.Get<HostClassification>()!.Host);
        if (invocation.EntryKind == OperationEntryKind.Application) _applicationWitness = witness;
        return witness;
    }
    protected override bool IsEvidenceCurrent(object evidence, OperationInvocation invocation) => ReferenceEquals(evidence, this)
        && _http is not null && IsCurrent(_http) && ReferenceEquals(invocations().CurrentInvocation, invocation)
        && invocation.Policy.RequestType == EffectiveRequestType
        && invocation.Policy.Kind == OperationPolicyKind.InvitationEntry
        && (invocation.EntryKind == OperationEntryKind.Http ? ReferenceEquals(invocation.Operation, _endpoint)
            : ReferenceEquals(invocation.Operation, Request)
                && (_applicationWitness is null || ReferenceEquals(_applicationWitness.Invocation, invocation)));
    private static Result<InvitationEntryObservation> Fail(string key) => Result<InvitationEntryObservation>.Fail(new Error(LocalizedMessage.Of(key)));
}
