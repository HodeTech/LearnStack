using System.Collections.Immutable;
using LearnStack.Api.Tenancy;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Localization;

namespace LearnStack.Api.Authentication;

/// <summary>Captures one admitted credential, native request, real host and retained own scope.</summary>
public sealed class ConsentSubjectSource(IHttpContextAccessor accessor, IConsentSubjectScopeLookup lookup,
    IdentityAuthorityQualification qualification, Func<IOperationInvocationContext> invocations)
    : ConsentSubjectScopeWitness.MintingSource, IConsentSubjectScopeSource
{
    private HttpContext? _http;
    private Endpoint? _endpoint;
    private NativeSessionSourceFeature? _native;
    private ValidatedConsentSubjectCredential? _credential;
    private ValidatedTenantIdentity? _identity;
    private HostClassification? _host;
    private ConsentSubjectHttpInput? _input;
    private ConsentSubjectOperation? _purpose;
    private GlobalIdentitySubject? _subject;
    private ConsentSubjectScopeWitness? _applicationWitness;
    private object? _request;
    private ConsentSubjectRequest? _requestFeature;
    internal ConsentSubjectScopeObservation? Selected { get; private set; }
    private ConsentSubjectScopePage? _page;
    internal bool MatchesScope(HttpContext http, LearnStack.SharedKernel.Identifiers.TenantId tenant,
        LearnStack.SharedKernel.Identifiers.OrganizationId? organization) => IsCurrent(http)
        && (Selected?.TenantId ?? _host!.TenantId) == tenant
        && (Selected is null ? _host!.OrganizationId : Selected.OrganizationId) == organization;
    internal LearnStack.SharedKernel.Identifiers.UserId? Actor => _identity?.ActorUserId;
    internal object? Request => _request;
    internal async Task<bool> MatchesCapturedWithdrawalAsync(HttpContext http)
    {
        if (!IsCurrent(http) || _purpose is not { Purpose: ConsentSubjectPurpose.Withdraw } purpose
            || _input is not { Request: WithdrawOwnMembershipFieldConsentCommand original } captured) return false;
        var reread = await ConsentSubjectHttpInput.ReadAsync(http, purpose);
        return IsCurrent(http) && reread?.Request is WithdrawOwnMembershipFieldConsentCommand current
            && current == original && reread.MembershipId == captured.MembershipId;
    }
    internal ReadOwnWithdrawnFieldConsentReceiptQuery? ReceiptRequest => _request as ReadOwnWithdrawnFieldConsentReceiptQuery;
    internal bool SelectWithdrawalReceipt(HttpContext http, Guid evidenceId)
    {
        if (!IsCurrent(http) || _purpose?.Purpose != ConsentSubjectPurpose.Withdraw
            || _request is not WithdrawOwnMembershipFieldConsentCommand || evidenceId == Guid.Empty || evidenceId.Version != 7)
            return false;
        var receipt = new ReadOwnWithdrawnFieldConsentReceiptQuery(evidenceId);
        _request = receipt;
        _requestFeature = new ConsentSubjectRequest(receipt);
        http.Features.Set(_requestFeature);
        return true;
    }
    internal Type? EffectiveRequestType => Request?.GetType();
    internal bool IsCurrent(HttpContext http) => IsOpen && ReferenceEquals(_http, http) && ReferenceEquals(accessor.HttpContext, http)
        && !http.RequestAborted.IsCancellationRequested && ReferenceEquals(http.GetEndpoint(), _endpoint)
        && ReferenceEquals(http.Features.Get<ConsentSubjectSource>(), this)
        && ReferenceEquals(http.Features.Get<ConsentSubjectRequest>(), _requestFeature) && _requestFeature is not null
        && ReferenceEquals(_requestFeature.Request, _request)
        && ReferenceEquals(http.Features.Get<HostClassification>(), _host)
        && _purpose?.Matches(http) is true && ReferenceEquals(_native, http.Features.Get<NativeSessionSourceFeature>())
        && _native?.IsCurrent(http) is true && _native.ConsentOperation == _purpose
        && ReferenceEquals(_credential, http.Features.Get<ValidatedConsentSubjectCredential>()) && _credential?.IsCurrent(http) is true
        && ReferenceEquals(_identity, http.Features.Get<TenantCredentialFeature>()?.Identity) && _identity is not null
        && _input?.IsCurrent(http) is true;

    internal async Task<Result<None>> PrepareAsync(HttpContext http)
    {
        if (_http is not null || ConsentSubjectOperation.ForPath(http.Request.Path) is not { } purpose || !purpose.Matches(http)
            || http.Features.Get<NativeSessionSourceFeature>() is not { } native || native.ConsentOperation != purpose || !native.IsCurrent(http)
            || http.Features.Get<ValidatedConsentSubjectCredential>() is not { } credential || !credential.IsCurrent(http)
            || http.Features.Get<TenantCredentialFeature>()?.Identity is not { } identity
            || http.Features.Get<HostClassification>() is not { TenantId: { } tenant } host
            || qualification.Current is not { } authority || identity.Issuer != authority.Issuer)
            return Fail("lockey_not_found");
        if (identity.TenantId is { } claimed && (claimed != tenant
            || host.OrganizationId is { } branch && identity.OrganizationId != branch)) return Fail("lockey_not_found");
        var input = await ConsentSubjectHttpInput.ReadAsync(http, purpose);
        if (input is null) return Fail("lockey_validation_failed");
        var mode = purpose.Purpose == ConsentSubjectPurpose.Discover ? ConsentSubjectLookupMode.List : ConsentSubjectLookupMode.Select;
        var observed = await lookup.LookupAsync(identity.ActorUserId, tenant, host.OrganizationId, mode,
            input.MembershipId, input.After, mode == ConsentSubjectLookupMode.List ? input.Limit : 1, http.RequestAborted);
        if (observed.Count > (mode == ConsentSubjectLookupMode.List ? input.Limit : 1)
            || observed.Any(item => item.MembershipId == Guid.Empty || item.MembershipId.Version != 7
                || item.TenantId != tenant || host.OrganizationId is { } branch && item.OrganizationId != branch)
            || observed.Select(item => item.MembershipId).Distinct().Count() != observed.Count)
            return Fail("lockey_not_found");
        ConsentSubjectScopeObservation? selected = null;
        ConsentSubjectScopePage? page = null;
        if (mode == ConsentSubjectLookupMode.Select)
        {
            if (observed.Count != 1 || observed[0].MembershipId != input.MembershipId) return Fail("lockey_not_found");
            selected = observed[0];
            if (identity.TenantId is not null && (identity.TenantId != selected.TenantId || identity.OrganizationId != selected.OrganizationId)) return Fail("lockey_not_found");
        }
        else
        {
            var next = observed.Count == input.Limit && (await lookup.LookupAsync(identity.ActorUserId, tenant, host.OrganizationId,
                ConsentSubjectLookupMode.List, null, observed[^1].MembershipId, 1, http.RequestAborted)).Count != 0
                ? observed[^1].MembershipId.ToString("D") : null;
            page = new ConsentSubjectScopePage(observed.ToImmutableArray(), next);
        }
        if (!native.IsCurrent(http) || !credential.IsCurrent(http)
            || !ReferenceEquals(identity, http.Features.Get<TenantCredentialFeature>()?.Identity)
            || !ReferenceEquals(host, http.Features.Get<HostClassification>()) || !input.IsCurrent(http)) return Fail("lockey_not_found");
        _http = http; _endpoint = http.GetEndpoint(); _purpose = purpose; _native = native; _credential = credential;
        _identity = identity; _host = host; _input = input; _request = input.Request; _subject = new GlobalIdentitySubject(authority.AuthorityId, authority.Issuer, identity.Subject);
        Selected = selected; _page = page; _requestFeature = new ConsentSubjectRequest(input.Request);
        http.Features.Set(_requestFeature); http.Features.Set(this);
        return Result.Ok(None.Value);
    }

    public ConsentSubjectScopeWitness? ForInvocation(OperationInvocation invocation)
    {
        if (_http is null || !IsEvidenceCurrent(this, invocation)) return null;
        if (invocation.EntryKind == OperationEntryKind.Application && _applicationWitness is not null)
            return ReferenceEquals(_applicationWitness.Invocation, invocation) ? _applicationWitness : null;
        var witness = Capture(this, invocation, _subject!, _identity!.ActorUserId, _host!.TenantId!.Value,
            _host.OrganizationId, _host.Host, _purpose!.Purpose, Selected, _page);
        if (invocation.EntryKind == OperationEntryKind.Application) _applicationWitness = witness;
        return witness;
    }
    protected override bool IsEvidenceCurrent(object evidence, OperationInvocation invocation) => ReferenceEquals(evidence, this)
        && _http is not null && IsCurrent(_http) && ReferenceEquals(invocations().CurrentInvocation, invocation)
        && invocation.Policy.RequestType == EffectiveRequestType && invocation.Policy.Kind == OperationPolicyKind.OwnConsentEntry
        && (invocation.EntryKind == OperationEntryKind.Http ? ReferenceEquals(invocation.Operation, _endpoint)
            : ReferenceEquals(invocation.Operation, Request) && (_applicationWitness is null || ReferenceEquals(_applicationWitness.Invocation, invocation)));
    private static Result<None> Fail(string key) => Result<None>.Fail(new Error(LocalizedMessage.Of(key)));
}
