using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Application.Authorization;

/// <summary>Owns one fresh policy act and its nested lifetime; declaration and source identity never replace grants.</summary>
public sealed class OperationAuthorizationCoordinator(
    IOperationPolicyCatalog policies,
    IProtectedOperationAuthorizer authorizer,
    ITenantContext context,
    IEnumerable<IAuthorizationCandidateSource> candidateSources,
    IEnumerable<ITrustedOperationSource> trustedSources)
    : OperationInvocation.MintingSource, IDisposable
{
    private static readonly Error Forbidden = new(new LocalizedMessage("lockey_forbidden"));
    private static readonly Error Unavailable = new(new LocalizedMessage("lockey_dependency_unavailable"));
    private readonly AsyncLocal<Frame?> _current = new();
    private readonly IAuthorizationCandidateSource[] _candidateSources = [.. candidateSources];
    private readonly ITrustedOperationSource[] _trustedSources = [.. trustedSources];
    private int _closed;

    public OperationInvocation? CurrentInvocation => _current.Value is { Closed: false } frame && frame.Invocation.IsActive
        ? frame.Invocation : null;

    public OperationReadWitness? CurrentReadWitness => _current.Value is { Closed: false, Decision: { } decision }
        && decision.Outcome == ProtectedOperationOutcome.Allowed && decision.IsActive ? decision.ReadWitness : null;

    public Task<TResponse> ExecuteRequestAsync<TRequest, TResponse>(TRequest request,
        Func<Task<TResponse>> next, CancellationToken cancellationToken = default)
        where TRequest : notnull where TResponse : IResultBase
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(next);
        return Volatile.Read(ref _closed) == 0 && policies.TryGet(request.GetType(), out var policy)
            ? ExecuteAsync(request, policy, next, cancellationToken)
            : Task.FromResult(Result.FailFor<TResponse>(Forbidden));
    }

    /// <summary>The HTTP adapter supplies an enumerated endpoint, then application dispatch performs a new act.</summary>
    public Task<Result<None>> CheckHttpAsync(Type requestType, object endpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestType);
        ArgumentNullException.ThrowIfNull(endpoint);
        return Volatile.Read(ref _closed) == 0 && policies.TryGet(requestType, out var policy)
            ? ExecuteAsync(endpoint, policy, () => Task.FromResult(Result.Ok(None.Value)), cancellationToken, OperationEntryKind.Http)
            : Task.FromResult(Result.Fail<None>(Forbidden));
    }

    private async Task<TResponse> ExecuteAsync<TResponse>(object operation, OperationPolicy policy,
        Func<Task<TResponse>> next, CancellationToken cancellationToken, OperationEntryKind entryKind = OperationEntryKind.Application)
        where TResponse : IResultBase
    {
        cancellationToken.ThrowIfCancellationRequested();
        AuthorizationCandidate? candidate = null;
        TrustedOperationDescriptor? trusted = null;
        Error? sourceFailure = null;
        if (!policies.TryGet(policy.RequestType, out var canonical) || !ReferenceEquals(canonical, policy))
            return Result.FailFor<TResponse>(Forbidden);

        switch (policy.Kind)
        {
            case OperationPolicyKind.PublicRead:
                if (!HasMatchingHostScope(allowAnonymous: true)
                    || policy.RequiredPermission is not { IsActive: true } permission
                    || !policies.TryGetDefinition(permission.Scope, permission.Key, out var registered)
                    || registered.Assignable || !registered.Definition.IsActive)
                    sourceFailure = TenantContextFactory.Refused;
                break;
            case OperationPolicyKind.FiniteTrusted:
                var captured = _trustedSources.Select(source => source.Capture(policy.RequestType, context))
                    .Where(descriptor => descriptor is not null).Cast<TrustedOperationDescriptor>().Take(2).ToArray();
                if (captured.Length != 1 || !captured[0].Matches(policy.RequestType, context)
                    || !policy.AllowedSources.Contains(captured[0].SourceKind))
                    sourceFailure = Forbidden;
                else trusted = captured[0];
                break;
            case OperationPolicyKind.AccountRead:
            case OperationPolicyKind.Protected:
                if (!HasProtectedContext() || (policy.Kind == OperationPolicyKind.AccountRead && !HasMatchingHostScope(allowAnonymous: false)))
                {
                    sourceFailure = TenantContextFactory.Refused;
                    break;
                }
                var candidates = _candidateSources.Select(source => source.ForValidatedScope(context.TenantId, context.OrganizationId))
                    .Where(value => value is not null).Cast<AuthorizationCandidate>().Take(2).ToArray();
                if (candidates.Length != 1 || !Matches(candidates[0])) sourceFailure = TenantContextFactory.Refused;
                else candidate = candidates[0];
                break;
            default:
                sourceFailure = Forbidden;
                break;
        }

        var invocation = ForInvocation(operation, policy, context, candidate, trusted, cancellationToken, entryKind);
        var previous = _current.Value;
        var frame = new Frame(invocation, previous);
        _current.Value = frame;
        try
        {
            if (sourceFailure is not null) return Result.FailFor<TResponse>(sourceFailure);
            if (candidate is not null)
            {
                var decision = await authorizer.AuthorizeAsync(invocation, policy, candidate, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (decision is null || !ReferenceEquals(decision.Invocation, invocation) || !decision.IsActive)
                    return Result.FailFor<TResponse>(Unavailable);
                frame.Decision = decision;
                var error = decision.Outcome switch
                {
                    ProtectedOperationOutcome.Allowed when policy.ReadScope == OperationReadScope.Tenant
                        && decision.ReadWitness is null => Unavailable,
                    ProtectedOperationOutcome.Allowed => null,
                    ProtectedOperationOutcome.NotCovered => TenantContextFactory.Refused,
                    ProtectedOperationOutcome.Forbidden => Forbidden,
                    _ => Unavailable,
                };
                if (error is not null) return Result.FailFor<TResponse>(error);
                if (policy.RequiresOrglessWrite && context.OrganizationId is not null)
                    return Result.FailFor<TResponse>(Forbidden);
            }
            if (!invocation.IsActive) return Result.FailFor<TResponse>(Unavailable);
            return await next();
        }
        finally
        {
            // Close the shared node before restoring its parent. Inherited execution
            // contexts retain the node reference and cannot revive it after this exit.
            frame.Close();
            _current.Value = previous;
        }
    }

    protected override bool IsCurrent(OperationInvocation invocation)
    {
        if (Volatile.Read(ref _closed) != 0) return false;
        var found = false;
        for (var frame = _current.Value; frame is not null; frame = frame.Parent)
        {
            if (frame.Closed) return false;
            found |= ReferenceEquals(frame.Invocation, invocation);
        }
        return found;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _closed, 1);
        GC.SuppressFinalize(this);
    }

    private bool Matches(AuthorizationCandidate candidate) => candidate.IsActive
        && candidate.Actor == context.UserId && candidate.TenantId == context.TenantId
        && candidate.OrganizationId == context.OrganizationId;

    private bool HasProtectedContext() => context.IsResolved && StronglyTypedId.IsAssigned(context.TenantId)
        && context.TenantId != TenantId.PlatformSentinel && context.UserId is { } actor
        && StronglyTypedId.IsAssigned(actor) && actor != UserId.SystemActor
        && context.Origin is TenantContextOrigin.HostAndClaim or TenantContextOrigin.ClaimAndMembership;

    private bool HasMatchingHostScope(bool allowAnonymous) => context.IsResolved
        && (allowAnonymous ? context.Origin is TenantContextOrigin.HostOnly or TenantContextOrigin.HostAndClaim
            : context.Origin == TenantContextOrigin.HostAndClaim)
        && context.HostScope is { } host && StronglyTypedId.IsAssigned(host.TenantId)
        && host.TenantId != TenantId.PlatformSentinel && host.TenantId == context.TenantId
        && (host.OrganizationId is null || (StronglyTypedId.IsAssigned(host.OrganizationId.Value)
            && host.OrganizationId == context.OrganizationId));

    private sealed class Frame(OperationInvocation invocation, Frame? parent)
    {
        private int _closed;
        internal OperationInvocation Invocation { get; } = invocation;
        internal Frame? Parent { get; } = parent;
        internal ProtectedOperationDecision? Decision { get; set; }
        internal bool Closed => Volatile.Read(ref _closed) != 0;
        internal void Close() => Interlocked.Exchange(ref _closed, 1);
    }
}
