using System.Net;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.SharedKernel.Audit;

public enum ClaimAssertionDimension { Tenant = 1, Organization = 2 }

/// <summary>A fixed mismatch observation under actual Membership-confirmed scope; never carries the asserted value.</summary>
public sealed class ConfirmedClaimAssertion
{
    private readonly MintingSource _source;
    private readonly ITenantContext _context;
    private readonly IPAddress? _address;

    private ConfirmedClaimAssertion(MintingSource source, ITenantContext context, ClaimAssertionDimension dimension,
        string? route, IPAddress? address, string? userAgent)
    {
        if (!context.IsResolved || context.Origin != TenantContextOrigin.ClaimAndMembership
            || !StronglyTypedId.IsAssigned(context.TenantId) || context.TenantId == TenantId.PlatformSentinel
            || context.UserId is not { } actor || !StronglyTypedId.IsAssigned(actor) || actor == UserId.SystemActor
            || context.OrganizationId is { } organization && !StronglyTypedId.IsAssigned(organization)
            || !Enum.IsDefined(dimension) || route is { Length: > 128 } || route?.Any(char.IsControl) == true)
            throw new ArgumentException("Claim assertion has no confirmed scope.");
        _source = source; _context = context;
        TenantId = context.TenantId; OrganizationId = context.OrganizationId; Actor = actor;
        Dimension = dimension; Route = route;
        CorrelationId = context.CorrelationId;
        _address = address is null ? null : new IPAddress(address.GetAddressBytes());
        UserAgent = userAgent is null ? null : new string(userAgent.Where(character => !char.IsControl(character)).Take(256).ToArray());
    }

    public TenantId TenantId { get; }
    public OrganizationId? OrganizationId { get; }
    public UserId Actor { get; }
    public ClaimAssertionDimension Dimension { get; }
    public string? CorrelationId { get; }
    public string? Route { get; }
    public IPAddress? Address => _address is null ? null : new IPAddress(_address.GetAddressBytes());
    public string? UserAgent { get; }
    public bool IsActive => _source.IsOpen && _context.IsResolved && _context.Origin == TenantContextOrigin.ClaimAndMembership
        && _context.TenantId == TenantId && _context.OrganizationId == OrganizationId && _context.UserId == Actor;
    public override string ToString() => nameof(ConfirmedClaimAssertion);

#pragma warning disable CA1034 // Only the admitted API assertion adapter owns this source capability.
    public abstract class MintingSource : IDisposable
#pragma warning restore CA1034
    {
        private int _closed;
        internal bool IsOpen => Volatile.Read(ref _closed) == 0;
        protected bool CanIssue => IsOpen;
        protected ConfirmedClaimAssertion ForAssertion(ITenantContext context, ClaimAssertionDimension dimension,
            string? route, IPAddress? address, string? userAgent)
        {
            ObjectDisposedException.ThrowIf(!IsOpen, this);
            return new(this, context, dimension, route, address, userAgent);
        }
        public void Dispose() { Interlocked.Exchange(ref _closed, 1); GC.SuppressFinalize(this); }
    }
}
