using System.Collections.Concurrent;
using LearnStack.SharedKernel.Time;
using Microsoft.Extensions.Options;

namespace LearnStack.Api.Tenancy;

/// <summary>
/// Counts anonymous assertion mismatches per <c>(resolved tenant, dimension)</c> and says
/// when a window has crossed its threshold.
/// </summary>
/// <remarks>
/// <para>
/// <b>In-process, never <c>ICacheService</c>, and that is a decision rather than a
/// shortcut.</b> A cache outage must not decide whether a MUST-class security event is
/// recorded, and the architecture rule
/// <c>Assertion_Budget_Does_Not_Depend_On_ICacheService</c> holds the line by both
/// reflection and a text scan
/// (<see href="../../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036
/// § Recording a rejected assertion</see>).
/// </para>
/// <para>
/// <b>Nothing is evicted inside a live window.</b> A window is reclaimed by expiring and
/// by nothing else — no size cap, no LRU. A structure that evicted under pressure would
/// hand an anonymous caller the eviction primitive: fill it against tenants of your
/// choosing, and the counter that would have caught you is gone. The key space is
/// <c>(tenant, dimension)</c>, so cardinality is bounded by the tenant count times two,
/// which is the bound ADR-0036 accepts in exchange.
/// </para>
/// <para>
/// <b>A singleton, because the window is the process's.</b> A scoped counter counts to one
/// per request and never crosses anything.
/// </para>
/// </remarks>
public sealed class TenantAssertionBurstDetector(
    IOptions<AssertionBurstOptions> options, IClock clock)
{
    private readonly ConcurrentDictionary<(Guid Tenant, TenantAssertionDimension Dimension), Window>
        _windows = new();

    private readonly AssertionBurstOptions _options =
        (options ?? throw new ArgumentNullException(nameof(options))).Value;

    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>
    /// Records one anonymous mismatch and answers whether it is the crossing.
    /// </summary>
    /// <remarks>
    /// <b>True exactly once per window</b>, on the occurrence that reaches the threshold —
    /// not on every occurrence past it. ADR-0036 says one row per
    /// <c>(resolved tenant, dimension, window)</c>, and a detector that answered true from
    /// the crossing onwards would write a row per request for the rest of the window,
    /// which is the flood the burst event exists to replace.
    /// </remarks>
    public bool RecordAndCheckCrossing(Guid resolvedTenantId, TenantAssertionDimension dimension)
    {
        var window = _windows.GetOrAdd((resolvedTenantId, dimension), _ => new Window());

        // One lock per (tenant, dimension), not one for the map. Two concurrent requests
        // against one tenant must not both read count = threshold - 1 and both cross;
        // requests against different tenants have no reason to wait for each other.
        lock (window.Gate)
        {
            var now = _clock.UtcNow;

            if (now - window.StartedAt >= _options.Window)
            {
                // Reclaimed by expiring. Reset in place rather than removed: the key is one
                // entry per (tenant, dimension) either way, and removing it opens a race
                // where a concurrent GetOrAdd revives the entry this thread is discarding.
                window.StartedAt = now;
                window.Count = 0;
                window.Recorded = false;
            }

            window.Count++;

            if (window.Recorded || window.Count < _options.Threshold)
            {
                return false;
            }

            window.Recorded = true;
            return true;
        }
    }

    private sealed class Window
    {
        public object Gate { get; } = new();

        public DateTimeOffset StartedAt { get; set; }

        public int Count { get; set; }

        public bool Recorded { get; set; }
    }
}
