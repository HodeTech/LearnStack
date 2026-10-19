using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Api.Tenancy;

/// <summary>ADR-0065's process-local allowance, shared by all scoped recorders.</summary>
/// <remarks>
/// Only independently defended host tenants reach this owner. The tenant keyspace has
/// no separate admission cap; live tenants cannot exclude another tenant's audit rows.
/// Failed writes consume their admission, and suppressed requests never extend expiry.
/// </remarks>
public sealed class TenantAssertionAuditBudget(TimeProvider timeProvider)
{
    public const int PermitPerWindow = 600;
    public const int SweepBatchSize = 128;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly object _gate = new();
    private readonly Dictionary<Guid, TenantWindow> _windows = [];
    private readonly Queue<Guid> _sweep = new();

    public bool TryAcquire(Guid defendedTenant)
    {
        if (defendedTenant == Guid.Empty || defendedTenant == TenantId.PlatformSentinel.Value)
            throw new ArgumentException("An independently defended tenant is required.", nameof(defendedTenant));

        lock (_gate)
        {
            var now = _timeProvider.GetTimestamp();
            SweepExpired(now);
            if (!_windows.TryGetValue(defendedTenant, out var window))
            {
                window = new TenantWindow(now);
                _windows.Add(defendedTenant, window);
                _sweep.Enqueue(defendedTenant);
            }
            else if (Expired(window, now))
            {
                // Reset in place: this tenant already owns exactly one queue entry.
                window.StartedAt = now;
                window.Used = 0;
            }

            if (window.Used == PermitPerWindow) return false;
            window.Used++;
            return true;
        }
    }

    private bool Expired(TenantWindow window, long now) =>
        _timeProvider.GetElapsedTime(window.StartedAt, now) >= Window;

    private void SweepExpired(long now)
    {
        var inspect = Math.Min(SweepBatchSize, _sweep.Count);
        for (var index = 0; index < inspect; index++)
        {
            var tenant = _sweep.Dequeue();
            if (Expired(_windows[tenant], now)) _windows.Remove(tenant);
            else _sweep.Enqueue(tenant);
        }
    }

    private sealed class TenantWindow(long startedAt)
    {
        public long StartedAt { get; set; } = startedAt;
        public int Used { get; set; }
    }
}
