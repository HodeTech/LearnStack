using System.Collections.Immutable;
using LearnStack.Api.Tenancy;
using LearnStack.Modules.Identity.Application.Contracts.DataRequests;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;

namespace LearnStack.Api.Authentication;

/// <summary>
/// Captures one admitted credential, native request, real host and retained own data-request group (ADR-0106 § 9):
/// the sole minter of <see cref="OwnDataRequestScopeWitness"/>, modelled exactly on <see cref="ConsentSubjectSource"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where its authority comes from.</b> The actor is the one completed binding normalization produced from a
/// stock-validated credential, so a Closing, Closed or unknown binding never reaches it, and the system actor never
/// does. The host is the independently classified real host. The group is the one the confined definer proved for
/// that actor under that host ceiling: on an organization host only that organization's group, on a tenant host any
/// group of that tenant where the actor holds or held a Membership or has a request. Nothing the caller sends selects
/// a tenant or widens the host.
/// </para>
/// <para>
/// <b>What it never supplies.</b> No Membership, no ordinary native session, no tenant-wide read and no other
/// operation: its witness is current only for an <see cref="OperationPolicyKind.OwnDataRequestEntry"/> invocation of
/// the exact request type it captured, on the exact endpoint it captured.
/// </para>
/// </remarks>
public sealed class OwnDataRequestSubjectSource(IHttpContextAccessor accessor, IOwnDataRequestScopeLookup lookup,
    IdentityAuthorityQualification qualification, Func<IOperationInvocationContext> invocations)
    : OwnDataRequestScopeWitness.MintingSource, IOwnDataRequestScopeSource
{
    private HttpContext? _http;
    private Endpoint? _endpoint;
    private NativeSessionSourceFeature? _native;
    private ValidatedOwnDataRequestCredential? _credential;
    private ValidatedTenantIdentity? _identity;
    private HostClassification? _host;
    private OwnDataRequestHttpInput? _input;
    private OwnDataRequestOperation? _purpose;
    private GlobalIdentitySubject? _subject;
    private OwnDataRequestScopeWitness? _applicationWitness;
    private object? _request;
    private OwnDataRequestEntryRequest? _requestFeature;
    private OwnDataRequestScopePage? _page;

    /// <summary>The proved group; <see langword="null"/> only for discovery, which acts in the host's own scope.</summary>
    internal DataRequestScopeGroup? Selected { get; private set; }

    internal bool MatchesScope(HttpContext http, TenantId tenant, OrganizationId? organization) => IsCurrent(http)
        && (Selected?.TenantId ?? _host!.TenantId) == tenant
        && (Selected is null ? _host!.OrganizationId : Selected.OrganizationId) == organization;

    internal UserId? Actor => _identity?.ActorUserId;
    internal object? Request => _request;
    internal Type? EffectiveRequestType => Request?.GetType();

    /// <summary>The keyed purposes' input, read again from the current request, still equals the captured one.</summary>
    internal async Task<bool> MatchesCapturedMutationAsync(HttpContext http)
    {
        if (!IsCurrent(http) || _purpose is not { Purpose: OwnDataRequestPurpose.Create or OwnDataRequestPurpose.Cancel } purpose
            || _input is not { Request: CreateRetainedDataExportCommand or CancelRetainedDataRequestCommand } captured) return false;
        var reread = await OwnDataRequestHttpInput.ReadAsync(http, purpose);
        return IsCurrent(http) && reread is not null && reread.Request.GetType() == captured.Request.GetType()
            && reread.Request.Equals(captured.Request) && reread.OrganizationId == captured.OrganizationId;
    }

    /// <summary>
    /// A keyed replay discloses the stored receipt of the original request only through a fresh receipt read in this
    /// same witness's group: the captured mutation becomes the receipt query, never another operation.
    /// </summary>
    internal bool SelectMutationReceipt(HttpContext http, Guid requestId)
    {
        if (!IsCurrent(http) || _purpose?.Purpose is not (OwnDataRequestPurpose.Create or OwnDataRequestPurpose.Cancel)
            || _request is not (CreateRetainedDataExportCommand or CancelRetainedDataRequestCommand)
            || requestId == Guid.Empty || requestId.Version != 7)
            return false;
        var receipt = new ReadRetainedDataRequestReceiptQuery(requestId);
        _request = receipt;
        _requestFeature = new OwnDataRequestEntryRequest(receipt);
        http.Features.Set(_requestFeature);
        return true;
    }

    /// <summary>
    /// Whether this source is the current retained entry for <paramref name="actor"/> in exactly this group, on a
    /// download purpose: the one condition under which a ticket is bound to, or read through, this request's credential.
    /// </summary>
    internal ValidatedOwnDataRequestCredential? DownloadCredential(HttpContext http, UserId actor, TenantId tenant, OrganizationId? organization) =>
        IsCurrent(http) && _purpose?.Purpose is OwnDataRequestPurpose.Ticket or OwnDataRequestPurpose.Chunk
        && Selected is { } group && group.TenantId == tenant && group.OrganizationId == organization
        && _identity!.ActorUserId == actor ? _credential : null;

    internal bool IsCurrent(HttpContext http) => IsOpen && ReferenceEquals(_http, http) && ReferenceEquals(accessor.HttpContext, http)
        && !http.RequestAborted.IsCancellationRequested && ReferenceEquals(http.GetEndpoint(), _endpoint)
        && ReferenceEquals(http.Features.Get<OwnDataRequestSubjectSource>(), this)
        && ReferenceEquals(http.Features.Get<OwnDataRequestEntryRequest>(), _requestFeature) && _requestFeature is not null
        && ReferenceEquals(_requestFeature.Request, _request)
        && ReferenceEquals(http.Features.Get<HostClassification>(), _host)
        && _purpose?.Matches(http) is true && ReferenceEquals(_native, http.Features.Get<NativeSessionSourceFeature>())
        && _native?.IsCurrent(http) is true && _native.DataRequestOperation == _purpose
        && ReferenceEquals(_credential, http.Features.Get<ValidatedOwnDataRequestCredential>()) && _credential?.IsCurrent(http) is true
        && ReferenceEquals(_identity, http.Features.Get<TenantCredentialFeature>()?.Identity) && _identity is not null
        && _input?.IsCurrent(http) is true;

    internal async Task<Result<None>> PrepareAsync(HttpContext http)
    {
        if (_http is not null || OwnDataRequestOperation.ForPath(http.Request.Path) is not { } purpose || !purpose.Matches(http)
            || http.Features.Get<NativeSessionSourceFeature>() is not { } native || native.DataRequestOperation != purpose || !native.IsCurrent(http)
            || http.Features.Get<ValidatedOwnDataRequestCredential>() is not { } credential || !credential.IsCurrent(http)
            || http.Features.Get<TenantCredentialFeature>()?.Identity is not { } identity
            || identity.ActorUserId == UserId.SystemActor || !StronglyTypedId.IsAssigned(identity.ActorUserId)
            || http.Features.Get<HostClassification>() is not { TenantId: { } tenant } host
            || qualification.Current is not { } authority || identity.Issuer != authority.Issuer)
            return Fail("lockey_not_found");
        if (identity.TenantId is { } claimed && (claimed != tenant
            || host.OrganizationId is { } branch && identity.OrganizationId != branch)) return Fail("lockey_not_found");
        var input = await OwnDataRequestHttpInput.ReadAsync(http, purpose);
        if (input is null) return Fail("lockey_validation_failed");
        DataRequestScopeGroup? selected = null;
        OwnDataRequestScopePage? page = null;
        if (purpose.Purpose == OwnDataRequestPurpose.List)
        {
            var after = input.After is { } cursor ? new DataRequestScopeGroup(tenant, cursor.OrganizationId) : null;
            if (after is not null && host.OrganizationId is { } ceiling && after.OrganizationId != ceiling) return Fail("lockey_validation_failed");
            var observed = await lookup.LookupAsync(identity.ActorUserId, tenant, host.OrganizationId, OwnDataRequestLookupMode.List,
                null, after, input.Limit, http.RequestAborted);
            if (!WithinHost(observed, tenant, host.OrganizationId, input.Limit)) return Fail("lockey_not_found");
            var next = observed.Count == input.Limit && (await lookup.LookupAsync(identity.ActorUserId, tenant, host.OrganizationId,
                OwnDataRequestLookupMode.List, null, observed[^1], 1, http.RequestAborted)).Count != 0
                ? OwnDataRequestHttpInput.KeyFor(observed[^1]) : null;
            page = new OwnDataRequestScopePage(observed.ToImmutableArray(), next);
        }
        else
        {
            if (host.OrganizationId is { } hostBranch && input.OrganizationId != hostBranch) return Fail("lockey_not_found");
            var requested = new DataRequestScopeGroup(tenant, input.OrganizationId);
            var observed = await lookup.LookupAsync(identity.ActorUserId, tenant, host.OrganizationId, OwnDataRequestLookupMode.Select,
                requested, null, 1, http.RequestAborted);
            if (!WithinHost(observed, tenant, host.OrganizationId, 1) || observed.Count != 1 || observed[0] != requested)
                return Fail("lockey_not_found");
            selected = observed[0];
            if (identity.TenantId is not null && (identity.TenantId != selected.TenantId || identity.OrganizationId != selected.OrganizationId))
                return Fail("lockey_not_found");
        }
        if (!native.IsCurrent(http) || !credential.IsCurrent(http)
            || !ReferenceEquals(identity, http.Features.Get<TenantCredentialFeature>()?.Identity)
            || !ReferenceEquals(host, http.Features.Get<HostClassification>()) || !input.IsCurrent(http)) return Fail("lockey_not_found");
        _http = http; _endpoint = http.GetEndpoint(); _purpose = purpose; _native = native; _credential = credential;
        _identity = identity; _host = host; _input = input; _request = input.Request;
        _subject = new GlobalIdentitySubject(authority.AuthorityId, authority.Issuer, identity.Subject);
        Selected = selected; _page = page; _requestFeature = new OwnDataRequestEntryRequest(input.Request);
        http.Features.Set(_requestFeature); http.Features.Set(this);
        return Result.Ok(None.Value);
    }

    public OwnDataRequestScopeWitness? ForInvocation(OperationInvocation invocation)
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
        && invocation.Policy.RequestType == EffectiveRequestType && invocation.Policy.Kind == OperationPolicyKind.OwnDataRequestEntry
        && (invocation.EntryKind == OperationEntryKind.Http ? ReferenceEquals(invocation.Operation, _endpoint)
            : ReferenceEquals(invocation.Operation, Request) && (_applicationWitness is null || ReferenceEquals(_applicationWitness.Invocation, invocation)));

    /// <summary>Re-checks every observed group against the same host ceiling and bound, so a drifted lookup never widens.</summary>
    private static bool WithinHost(IReadOnlyList<DataRequestScopeGroup> observed, TenantId tenant, OrganizationId? hostOrganization, int limit) =>
        observed.Count <= limit && observed.Distinct().Count() == observed.Count
        && observed.All(group => group is not null && group.TenantId == tenant
            && (hostOrganization is not { } branch || group.OrganizationId == branch));

    private static Result<None> Fail(string key) => Result<None>.Fail(new Error(LocalizedMessage.Of(key)));
}
