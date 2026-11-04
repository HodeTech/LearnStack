using System.Diagnostics.CodeAnalysis;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.Identity;

/// <summary>One exact own-Membership tuple captured by the confined subject discovery function.</summary>
public sealed class OwnMembershipScopeWitness
{
    private readonly MintingSource _source;
    private object? _owner;

    private OwnMembershipScopeWitness(MintingSource source, Guid membershipId, UserId actor,
        TenantId tenant, OrganizationId? organization)
    {
        if (membershipId == Guid.Empty || !StronglyTypedId.IsAssigned(actor) || actor == UserId.SystemActor
            || !StronglyTypedId.IsAssigned(tenant) || tenant == TenantId.PlatformSentinel
            || (organization is { } org && !StronglyTypedId.IsAssigned(org)))
            throw new ArgumentException("Own membership scope provenance is invalid.");
        _source = source;
        MembershipId = membershipId;
        Actor = actor;
        TenantId = tenant;
        OrganizationId = organization;
    }

    public Guid MembershipId { get; }
    public UserId Actor { get; }
    public TenantId TenantId { get; }
    public OrganizationId? OrganizationId { get; }
    public bool IsActive => _source.IsCurrent;
    public bool TryBindTo(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return IsActive && Interlocked.CompareExchange(ref _owner, owner, null) is null;
    }
    public bool IsBoundTo(object owner) => IsActive && ReferenceEquals(Volatile.Read(ref _owner), owner);
    public override string ToString() => nameof(OwnMembershipScopeWitness);

#pragma warning disable CA1034 // Private construction and the compiled minter census close this provenance set.
    [SuppressMessage("Design", "CA1063:Implement IDisposable Correctly",
        Justification = "Disposal closes shared captured provenance and cannot be overridden.")]
    public abstract class MintingSource : IDisposable
#pragma warning restore CA1034
    {
        private int _closed;
        internal bool IsCurrent => Volatile.Read(ref _closed) == 0 && IsEvidenceCurrent;
        protected abstract bool IsEvidenceCurrent { get; }
        protected OwnMembershipScopeWitness Capture(Guid membershipId, UserId actor,
            TenantId tenant, OrganizationId? organization)
        {
            ObjectDisposedException.ThrowIf(!IsCurrent, this);
            return new(this, membershipId, actor, tenant, organization);
        }
        public void Dispose()
        {
            Interlocked.Exchange(ref _closed, 1);
            GC.SuppressFinalize(this);
        }
    }
}
