using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;

namespace LearnStack.SharedKernel.Errors;

/// <summary>
/// Thrown when a command reaches PostgreSQL on a transaction no sanctioned setter
/// announced the tenant on.
/// </summary>
/// <remarks>
/// <para>
/// A <b>programmer error</b>, not a business refusal, which is why it is an exception
/// and why it carries <c>internal_error</c>. Row Level Security already makes the state
/// safe — with <c>app.tenant_id</c> unset every policy predicate is <c>NULL</c>, so a
/// tenant-owned read returns zero rows and a write is refused — but safe and silent, and
/// an empty result set arriving from production is an outage. This is the diagnostic
/// above that, never the boundary itself.
/// </para>
/// <para>
/// <b>It used to carry <c>tenant_mismatch</c>, and that was wrong in a way only Packet 7
/// could reveal.</b> Nothing threw it before the guard existed. That code maps to
/// <c>404</c>, so a wiring bug would have reached a client byte-identical to the
/// deliberate refusal an unresolvable host gets — the one response this packet spent
/// two steps making indistinguishable on purpose. A server fault hiding inside the
/// anti-oracle 404 is invisible in monitoring and unactionable in a bug report.
/// <c>internal_error</c> maps to <c>500</c>, which is what a fault is.
/// </para>
/// </remarks>
public sealed class TenantContextMissingException : LearnStackException
{
    private static readonly Error DefaultError = new(
        new LocalizedMessage("lockey_internal_error"));

    public TenantContextMissingException(string message, Exception? innerException = null)
        : base(DefaultError, message, innerException)
    {
    }
}
