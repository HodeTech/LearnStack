using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.Domain;

// Guards that turn a database-layer rejection into an ArgumentException at the call
// site. They live here rather than in a module because every module's aggregates need
// them and `ModuleDomain_DoesNotDependOn_OtherModuleDomain` forbids the second module
// from borrowing the first's copy — which would mean two spellings of, for instance,
// what canonical case a locale tag has.

/// <summary>
/// Guards a value against the length its column holds.
/// </summary>
/// <remarks>
/// The database rejects a longer value with <c>22001</c>, which names neither the
/// property nor the aggregate and arrives three layers from the call that produced
/// it. The numbers here are the ones the EF configurations map; asserting them at
/// the factory is what makes the failure say which field is wrong.
/// </remarks>
public static class MappedLength
{
    /// <summary>
    /// The width every schema in the repository maps for a human-facing display name.
    /// </summary>
    /// <remarks>
    /// Public for the same reason as <see cref="UrlSlug.MaxLength"/>: the validator
    /// refuses at this bound and the factories throw at it, and one number is what keeps
    /// the two answers the same.
    /// </remarks>
    public const int DisplayName = 200;

    public static void EnsureAtMost(string value, int maximum, string parameterName)
    {
        if (value.Length > maximum)
        {
            throw new ArgumentException(
                $"The value is {value.Length} characters; the column holds {maximum}.",
                parameterName);
        }
    }
}

/// <summary>
/// Guards a value on its way into a <c>jsonb</c> column.
/// </summary>
/// <remarks>
/// PostgreSQL rejects malformed JSON on the insert with <c>22P02</c>, three
/// layers from the call that produced it and naming neither the property nor the
/// aggregate. Parsing here is one pass over a value the caller already holds, and
/// it turns that into an <c>ArgumentException</c> at the call site — the same
/// reason <c>TenantDomain</c> runs the host through <c>EffectiveHost.Normalize</c>
/// rather than waiting for its CHECK.
/// </remarks>
public static class JsonValue
{
    /// <summary>
    /// Whether <paramref name="value"/> is JSON a <c>jsonb</c> column will take.
    /// </summary>
    /// <remarks>
    /// The predicate half of the pair <see cref="UrlSlug"/> already has, and for the
    /// same reason: a validator owes the caller a refusal, and reaching
    /// <see cref="EnsureWellFormed"/> for that answer means catching an
    /// <see cref="ArgumentException"/> to decide whether to report one.
    /// </remarks>
    public static bool IsWellFormed(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            using var _ = System.Text.Json.JsonDocument.Parse(value);
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    public static void EnsureWellFormed(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        // The exception carries the parser's own message, which names the offset —
        // so this parses rather than calling IsWellFormed and losing it.
        try
        {
            using var _ = System.Text.Json.JsonDocument.Parse(value);
        }
        catch (System.Text.Json.JsonException exception)
        {
            throw new ArgumentException(
                $"The value is not well-formed JSON and the column is jsonb: {exception.Message}",
                parameterName,
                exception);
        }
    }
}

/// <summary>
/// Guards a tenant-owned row's owning identifier.
/// </summary>
/// <remarks>
/// <c>IsInitialized()</c> alone is not enough: <c>TenantId.From(Guid.Empty)</c>
/// reports initialized, and a nil-uuid tenant then inserts and satisfies its own
/// policy whenever <c>app.tenant_id</c> holds the same nil. No ADR reserves the
/// nil uuid — the platform sentinel is deliberately unfixed until Packet 9 — so
/// this is refused at the factory rather than left to collide with whatever that
/// packet chooses.
/// </remarks>
public static class TenantOwnership
{
    public static void EnsureRealTenant(TenantId tenantId, string message, string parameterName)
    {
        if (!tenantId.IsInitialized() || tenantId.Value == Guid.Empty)
        {
            throw new ArgumentException(message, parameterName);
        }
    }
}

/// <summary>
/// Guards a slug against the shape its column's consumers assume.
/// </summary>
/// <remarks>
/// <para>
/// Lowercase alphanumeric with single interior hyphens. The shape was adopted for
/// hostnames — a tenant slug appears in one and an organization slug is documented
/// as a DNS label — and neither factory looked at the characters, so a slug with a
/// slash or an uppercase letter reached a hostname unchallenged.
/// </para>
/// <para>
/// It now guards things that are not hostnames: a customization key and a taxonomy
/// item key reuse it because they reach a URL segment and a cache-key component,
/// and because they are part of a primary key where two spellings would be two
/// rows. Those carry their own width (<c>CustomizationKey.MaxLength</c>); this
/// class owns the character shape, not the length.
/// </para>
/// </remarks>
public static partial class UrlSlug
{
    /// <summary>
    /// The width every slug column in the Tenancy schema maps — a DNS label.
    /// A key that reuses this SHAPE without being a hostname declares its own
    /// width; see <c>CustomizationKey.MaxLength</c>.
    /// </summary>
    /// <remarks>
    /// Named rather than written at each call site because two layers read it: the
    /// factories below, which throw, and <c>ProvisionTenantCommandValidator</c>, which
    /// refuses. A number in both places is a number that drifts in one of them, and the
    /// drift is invisible until a caller sends a 64-character slug and gets whichever
    /// answer the two disagree on.
    /// </remarks>
    public const int MaxLength = 63;

    /// <summary>Whether <paramref name="value"/> is a URL-safe slug.</summary>
    /// <remarks>
    /// The predicate form exists so a validator can refuse the same shape this class
    /// throws on. Application code cannot use the throwing form: an
    /// <c>ArgumentException</c> escaping a handler has no entry in <c>HttpStatusMap</c>
    /// and becomes a 500, which is the wrong answer for a caller who mistyped a slug —
    /// and one that arrives only after the transaction was opened and the tenant
    /// announced.
    /// </remarks>
    public static bool IsUrlSafe(string value) => Pattern().IsMatch(value);

    public static void EnsureUrlSafe(string value, string parameterName)
    {
        if (!IsUrlSafe(value))
        {
            throw new ArgumentException(
                $"'{value}' is not a URL-safe slug: lowercase letters, digits and single "
                + "interior hyphens only.",
                parameterName);
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial System.Text.RegularExpressions.Regex Pattern();
}
