namespace LearnStack.Api.Tenancy;

/// <summary>
/// When an anonymous run of rejected assertions becomes an audited burst, per
/// <see href="../../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036
/// § Recording a rejected assertion</see>.
/// </summary>
/// <remarks>
/// <para>
/// <b>An anonymous mismatch is not itself an audited operation; the burst is.</b> That is
/// the precedent [Audit Coverage](../../../../docs/standards/18-audit-coverage.md)'s
/// baseline already sets with "login failure burst beyond rate limit" rather than every
/// failed login — an anonymous caller can generate mismatches at will, so a row per
/// occurrence would let that caller choose how much a tenant's audit log grows.
/// </para>
/// <para>
/// <b>Per instance, and that errs toward more auditing.</b> The counters are in-process
/// (see <see cref="TenantAssertionBurstDetector"/>), so a deployment of N instances can
/// emit up to N rows per window. Stated rather than hidden: the alternative is shared
/// state, and a cache outage must not decide whether a MUST-class security event is
/// recorded.
/// </para>
/// </remarks>
public sealed class AssertionBurstOptions
{
    public const string SectionName = "Tenancy:AssertionBurst";

    /// <summary>
    /// How many anonymous mismatches against one <c>(tenant, dimension)</c> make a burst.
    /// </summary>
    /// <remarks>
    /// Ten, because the failure this exists to catch is a run and the failure it must not
    /// report is a mistake. A misconfigured BFF sending one stale header retries a handful
    /// of times and stops; a caller enumerating tenant identifiers does not. One is not a
    /// signal and a thousand is the same signal a hundred times.
    /// </remarks>
    public int Threshold { get; init; } = 10;

    /// <summary>The window a run has to happen inside.</summary>
    /// <remarks>
    /// Five minutes: long enough that a slow enumeration still crosses, short enough that
    /// an operator reading the row is reading about something that is happening rather
    /// than something that happened this morning. A window is reclaimed by expiring, never
    /// by pressure — see <see cref="TenantAssertionBurstDetector"/>.
    /// </remarks>
    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(5);
}
