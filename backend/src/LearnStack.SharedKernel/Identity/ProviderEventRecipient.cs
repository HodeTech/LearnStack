using System.Data.Common;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.SharedKernel.Identity;

/// <summary>One actual claimed provider target, consumed by one ordinary production transaction.</summary>
public sealed class ProviderEventRecipient : ITenantContext
{
    private readonly object _gate = new();
    private readonly CancellationToken _sourceLifetime;
    private IUnitOfWork? _unit;
    private DbTransaction? _transaction;
    private long _generation;

    private ProviderEventRecipient(Guid target, Guid @event, Guid tenant, Guid token, DateTimeOffset until,
        CancellationToken sourceLifetime)
    {
        if (target == Guid.Empty || target.Version != 7 || @event == Guid.Empty || @event.Version != 7
            || tenant == Guid.Empty || tenant == Identifiers.TenantId.PlatformSentinel.Value || token == Guid.Empty)
            throw new ArgumentException("Provider target claim is invalid.");
        TargetId = target; EventId = @event; TenantId = Identifiers.TenantId.From(tenant);
        ClaimToken = token; ClaimUntil = until; _sourceLifetime = sourceLifetime;
    }

    public Guid TargetId { get; }
    public Guid EventId { get; }
    public Guid ClaimToken { get; }
    public DateTimeOffset ClaimUntil { get; }
    public CancellationToken SourceLifetime => _sourceLifetime;
    public bool IsResolved => true;
    public TenantContextOrigin? Origin => TenantContextOrigin.ProviderEventRecipient;
    public TenantId TenantId { get; }
    public OrganizationId? OrganizationId => null;
    public UserId? UserId => Identifiers.UserId.SystemActor;
    public UserId? CausalActorUserId => null;
    public string? ModuleName => "identity";
    public string? CorrelationId => null;
    public bool IsSourceActive => !_sourceLifetime.IsCancellationRequested;

    /// <summary>Called only by the ordinary UoW's closed provider announcement branch.</summary>
    public bool TryBind(IUnitOfWork unit, DbTransaction transaction, long generation)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(transaction);
        lock (_gate)
        {
            if (_unit is not null || !IsSourceActive || generation <= 0 || unit.IsRollbackOnly
                || unit.Mode != TransactionMode.ReadWrite || !ReferenceEquals(unit.Transaction, transaction)) return false;
            _unit = unit; _transaction = transaction; _generation = generation;
            return true;
        }
    }

    public bool Matches(IUnitOfWork unit)
    {
        lock (_gate)
            return IsSourceActive && ReferenceEquals(_unit, unit) && ReferenceEquals(_transaction, unit.Transaction)
                && !unit.IsRollbackOnly && unit.Mode == TransactionMode.ReadWrite
                && unit.GetTenantContextAnnouncement(unit.Transaction) is { } announcement
                && announcement.Generation == _generation && announcement.TenantId == TenantId
                && announcement.OrganizationId is null;
    }

    public bool IsBoundTo(IUnitOfWork unit, DbTransaction transaction, long generation)
    {
        lock (_gate)
            return IsSourceActive && ReferenceEquals(_unit, unit) && ReferenceEquals(_transaction, transaction)
                && _generation == generation && !unit.IsRollbackOnly && unit.Mode == TransactionMode.ReadWrite;
    }

    public override string ToString() => nameof(ProviderEventRecipient);

#pragma warning disable CA1034 // Only the qualified intake source mints the actual returned database claim.
    public abstract class MintingSource
#pragma warning restore CA1034
    {
        protected static ProviderEventRecipient FromStoredClaim(Guid target, Guid @event, Guid tenant,
            Guid token, DateTimeOffset until, CancellationToken sourceLifetime) =>
            new(target, @event, tenant, token, until, sourceLifetime);
    }
}
