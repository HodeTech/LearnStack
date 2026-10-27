using LearnStack.SharedKernel.Identifiers;
using System.Diagnostics.CodeAnalysis;

namespace LearnStack.SharedKernel.Permissions;

public enum AuthorizationSourceKind { Http = 1, Seed = 2, IntegrationEvent = 3, BackgroundJob = 4 }

/// <summary>A question from an enumerated trusted owner, never authority supplied by a UserId alone.</summary>
public sealed class AuthorizationCandidate
{
    private readonly MintingSource _source;
    private readonly object? _evidence;

    private AuthorizationCandidate(MintingSource source, AuthorizationSourceKind kind,
        UserId actor, TenantId tenant, OrganizationId? organization, object? evidence)
    {
        if (!Enum.IsDefined(kind) || !StronglyTypedId.IsAssigned(actor) || actor == UserId.SystemActor
            || actor.Value.Version != 7 || !StronglyTypedId.IsAssigned(tenant) || tenant == TenantId.PlatformSentinel
            || (organization is { } org && !StronglyTypedId.IsAssigned(org)))
            throw new ArgumentException("Authorization candidate provenance is invalid.");
        _source = source;
        _evidence = evidence;
        SourceKind = kind;
        Actor = actor;
        TenantId = tenant;
        OrganizationId = organization;
    }

    public AuthorizationSourceKind SourceKind { get; }
    public UserId Actor { get; }
    public TenantId TenantId { get; }
    public OrganizationId? OrganizationId { get; }
    public bool IsActive => _source.IsActive && _source.MatchesEvidence(_evidence);

    public override string ToString() => nameof(AuthorizationCandidate);

#pragma warning disable CA1034 // Private construction and the compiled minter census close the trusted source set.
    [SuppressMessage("Design", "CA1063:Implement IDisposable Correctly",
        Justification = "Disposal only closes the shared provenance lifetime; derived sources cannot replace that authority transition.")]
    public abstract class MintingSource : IDisposable
#pragma warning restore CA1034
    {
        private int _closed;
        internal bool IsActive => Volatile.Read(ref _closed) == 0;
        internal bool MatchesEvidence(object? evidence) => IsEvidenceCurrent(evidence);

        protected virtual bool IsEvidenceCurrent(object? evidence) => true;

        protected AuthorizationCandidate ForCandidate(AuthorizationSourceKind kind,
            UserId actor, TenantId tenant, OrganizationId? organization, object? evidence = null)
        {
            ObjectDisposedException.ThrowIf(!IsActive, this);
            return new(this, kind, actor, tenant, organization, evidence);
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _closed, 1);
            GC.SuppressFinalize(this);
        }
    }
}
