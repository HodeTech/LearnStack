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
/// What a <c>jsonb</c> column refuses that <c>System.Text.Json</c> accepts.
/// </summary>
public enum JsonStorageFault
{
    /// <summary>
    /// Text carrying <c>U+0000</c> or an unpaired surrogate.
    /// </summary>
    /// <remarks>
    /// Measured on PostgreSQL 18.6: <c>{"a":"\u0000"}</c> is <c>22P05</c>
    /// ("unsupported Unicode escape sequence — cannot be converted to text"),
    /// because a PostgreSQL <c>text</c> cannot hold a NUL; an unpaired surrogate,
    /// high or low, in a value or in a member name, is <c>22P02</c> ("Unicode low
    /// surrogate must follow a high surrogate"). A correctly paired surrogate is
    /// accepted, and so is the six-character literal <c>\u0000</c> written with an
    /// escaped backslash — which is why this reads the parsed value rather than
    /// scanning the text.
    /// </remarks>
    Text,

    /// <summary>
    /// A number outside PostgreSQL <c>numeric</c>'s range.
    /// </summary>
    /// <remarks>
    /// Measured on the same server: <c>numeric</c> holds 131,072 digits before the
    /// decimal point and 16,383 after, and one more of either is <c>22003</c>
    /// ("value overflows numeric format"). <c>1e131071</c> stores and
    /// <c>1e131072</c> does not; <c>1e-16383</c> stores and <c>1e-16384</c> does
    /// not. Nine characters of JSON are enough to reach it.
    /// </remarks>
    Number,
}

/// <summary>
/// Guards a value on its way into a <c>jsonb</c> column.
/// </summary>
/// <remarks>
/// <para>
/// PostgreSQL rejects malformed JSON on the insert with <c>22P02</c>, three
/// layers from the call that produced it and naming neither the property nor the
/// aggregate. Parsing here is one pass over a value the caller already holds, and
/// it turns that into an <c>ArgumentException</c> at the call site — the same
/// reason <c>TenantDomain</c> runs the host through <c>EffectiveHost.Normalize</c>
/// rather than waiting for its CHECK.
/// </para>
/// <para>
/// <b>Well-formed is not the same as storable, and the gap is measured.</b>
/// <c>JsonDocument.Parse</c> accepts three documents this column rejects — see
/// <see cref="JsonStorageFault"/> for each and its SQLSTATE. A guard that stopped
/// at parsing therefore let exactly the failure it exists to prevent through: the
/// caller got a 500 from the insert instead of a refusal naming the field.
/// </para>
/// </remarks>
public static class JsonValue
{
    /// <summary>
    /// The serialised size of one customization row, in bytes of UTF-8.
    /// </summary>
    /// <remarks>
    /// <see href="../../../../docs/architecture/32-tenant-customization-model.md">§
    /// 8.4</see>'s declared limit, named here because more than one writer needs it
    /// and the schema gate is not the only one: a taxonomy band's <c>metadata</c> is
    /// a customization row too, and the HTTP body limit — which was the only thing
    /// bounding it — does not cover the seeder, the Hub adapter, or the bulk
    /// importer <see href="../../../../docs/roadmap/phase-04-cms-media-pages.md">Phase
    /// 04</see> brings.
    /// </remarks>
    public const int MaxRowBytes = 256 * 1024;

    /// <summary>Digits PostgreSQL <c>numeric</c> holds before the decimal point.</summary>
    private const int MaxWholeDigits = 131_072;

    /// <summary>Digits it holds after.</summary>
    private const int MaxFractionDigits = 16_383;

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
            using var document = System.Text.Json.JsonDocument.Parse(value);
            return !Unstorable(document.RootElement).Any();
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>Whether a value is JSON a <c>jsonb</c> column takes, and small enough to store.</summary>
    /// <remarks>
    /// The pair of <see cref="IsWellFormed"/> for the columns § 8.4 caps. Kept
    /// separate because not every <c>jsonb</c> column is a customization row.
    /// </remarks>
    public static bool IsStorableRow(string value) =>
        IsWellFormed(value)
        && System.Text.Encoding.UTF8.GetByteCount(value) <= MaxRowBytes;

    /// <summary>
    /// Refuses a customization row that is not storable JSON, or is too large.
    /// </summary>
    /// <remarks>
    /// A separate member rather than a cap inside <see cref="EnsureWellFormed"/>:
    /// that guard also runs on a tenant setting's value and on
    /// <c>LocalizedText.FromJson</c>, which reads a column back — capping there
    /// would refuse a stored value on the way out.
    /// </remarks>
    public static void EnsureStorableRow(string value, string parameterName)
    {
        EnsureWellFormed(value, parameterName);

        var bytes = System.Text.Encoding.UTF8.GetByteCount(value);

        if (bytes > MaxRowBytes)
        {
            throw new ArgumentException(
                $"The value is {bytes} bytes of UTF-8; a customization row holds {MaxRowBytes}.",
                parameterName);
        }
    }

    public static void EnsureWellFormed(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        System.Text.Json.JsonDocument document;

        // The exception carries the parser's own message, which names the offset —
        // so this parses rather than calling IsWellFormed and losing it.
        try
        {
            document = System.Text.Json.JsonDocument.Parse(value);
        }
        catch (System.Text.Json.JsonException exception)
        {
            throw new ArgumentException(
                $"The value is not well-formed JSON and the column is jsonb: {exception.Message}",
                parameterName,
                exception);
        }

        using (document)
        {
            foreach (var (location, fault) in Unstorable(document.RootElement))
            {
                throw new ArgumentException(
                    $"The value at '{location}' is well-formed JSON that a jsonb column "
                    + $"rejects ({fault}); see JsonStorageFault for the measurement.",
                    parameterName);
            }
        }
    }

    /// <summary>
    /// Every position in <paramref name="element"/> a <c>jsonb</c> column refuses.
    /// </summary>
    /// <remarks>
    /// Public because the tenant-authored write path needs all of them at once and
    /// as a <b>400 naming the JSON pointer</b> rather than as the
    /// <see cref="ArgumentException"/> the aggregate factories want — the schema
    /// gate reports every position, and this guard is the backstop behind it. One
    /// implementation, because two copies of "what jsonb takes" would answer
    /// differently and only one of them would be the database.
    /// </remarks>
    public static IEnumerable<(string Location, JsonStorageFault Fault)> Unstorable(
        System.Text.Json.JsonElement element) =>
        Unstorable(element, string.Empty);

    /// <summary>
    /// Appends one RFC 6901 § 4 token to a JSON pointer.
    /// </summary>
    /// <remarks>
    /// Named here rather than in each walker so the locations this guard reports
    /// and the ones the schema gate reports are the same grammar — a client renders
    /// both from one <c>Details</c> map.
    /// </remarks>
    public static string Locate(string parent, string token)
    {
        ArgumentNullException.ThrowIfNull(token);

        return parent + "/" + token.Replace("~", "~0", StringComparison.Ordinal)
            .Replace("/", "~1", StringComparison.Ordinal);
    }

    private static IEnumerable<(string Location, JsonStorageFault Fault)> Unstorable(
        System.Text.Json.JsonElement element, string location)
    {
        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    // The OBJECT's location for a bad member name, not the member's.
                    // A pointer built from a name carrying a NUL would put that NUL
                    // into a Problem Details key, an audit row and a log line — three
                    // sinks, to say something the parent already says.
                    if (!TryDecode(property, out var name))
                    {
                        yield return (location, JsonStorageFault.Text);

                        // The member's own value is not walked: its pointer would
                        // have to be built from the name that cannot be decoded, and
                        // the document is refused either way.
                        continue;
                    }

                    foreach (var fault in Unstorable(property.Value, Locate(location, name)))
                    {
                        yield return fault;
                    }
                }

                break;

            case System.Text.Json.JsonValueKind.Array:
                var index = 0;

                foreach (var item in element.EnumerateArray())
                {
                    var at = Locate(location, index.ToString(System.Globalization.CultureInfo.InvariantCulture));

                    foreach (var fault in Unstorable(item, at))
                    {
                        yield return fault;
                    }

                    index++;
                }

                break;

            case System.Text.Json.JsonValueKind.String:
                if (!IsStorableString(element))
                {
                    yield return (location, JsonStorageFault.Text);
                }

                break;

            case System.Text.Json.JsonValueKind.Number:
                if (!IsStorableNumber(element.GetRawText()))
                {
                    yield return (location, JsonStorageFault.Number);
                }

                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Whether a string value survives the trip into a PostgreSQL <c>text</c>.
    /// </summary>
    /// <remarks>
    /// <b>Reading it can throw, which is itself the answer.</b> Measured:
    /// <c>JsonElement.GetString()</c> raises <c>InvalidOperationException</c>
    /// ("Cannot read incomplete UTF-16 JSON text as string with missing low
    /// surrogate") for an unpaired escape rather than returning one — so the first
    /// version of this guard turned a document PostgreSQL refuses into an exception
    /// escaping a port whose contract says tenant input never throws. Catching it
    /// is precise: on a <c>String</c> element that is the only reason it raises.
    /// </remarks>
    private static bool IsStorableString(System.Text.Json.JsonElement element)
    {
        try
        {
            return IsStorableText(element.GetString() ?? string.Empty);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <inheritdoc cref="IsStorableString"/>
    private static bool TryDecode(System.Text.Json.JsonProperty property, out string name)
    {
        try
        {
            name = property.Name;
        }
        catch (InvalidOperationException)
        {
            name = string.Empty;
            return false;
        }

        return IsStorableText(name);
    }

    /// <summary>
    /// Whether every character survives the trip into a PostgreSQL <c>text</c>.
    /// </summary>
    /// <remarks>
    /// Reads the parsed string rather than the document text, which is what tells
    /// an escape from the six characters that spell one: <c>"\\u0000"</c> is a
    /// backslash followed by <c>u0000</c> and stores, measured.
    /// </remarks>
    internal static bool IsStorableText(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];

            if (character == '\0')
            {
                return false;
            }

            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= text.Length || !char.IsLowSurrogate(text[index + 1]))
                {
                    return false;
                }

                index++;
                continue;
            }

            if (char.IsLowSurrogate(character))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether a JSON number token fits PostgreSQL <c>numeric</c>.
    /// </summary>
    /// <remarks>
    /// Counted on the token rather than converted, because the values that overflow
    /// are the ones no CLR numeric type can hold either: <c>1e1000000</c> is nine
    /// characters and 1,000,001 digits. The exponent moves the decimal point, so
    /// the whole part is the integer digits plus it and the fraction is the
    /// fractional digits minus it — which is exactly where the measured boundary
    /// sits, at <c>1e131071</c> stored and <c>1e131072</c> refused.
    /// </remarks>
    internal static bool IsStorableNumber(ReadOnlySpan<char> token)
    {
        var at = 0;

        if (at < token.Length && (token[at] == '-' || token[at] == '+'))
        {
            at++;
        }

        var start = at;

        while (at < token.Length && char.IsAsciiDigit(token[at]))
        {
            at++;
        }

        var integer = token[start..at];
        var fraction = ReadOnlySpan<char>.Empty;

        if (at < token.Length && token[at] == '.')
        {
            at++;
            start = at;

            while (at < token.Length && char.IsAsciiDigit(token[at]))
            {
                at++;
            }

            fraction = token[start..at];
        }

        long exponent = 0;

        if (at < token.Length && (token[at] is 'e' or 'E'))
        {
            at++;
            var negative = at < token.Length && token[at] == '-';

            if (at < token.Length && (token[at] == '-' || token[at] == '+'))
            {
                at++;
            }

            // A token whose exponent does not fit a long is out of range in
            // whichever direction it points; there is no need to know which.
            if (!long.TryParse(token[at..], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var digits))
            {
                return false;
            }

            exponent = negative ? -digits : digits;
        }

        var leading = 0;

        while (leading < integer.Length && integer[leading] == '0')
        {
            leading++;
        }

        // Trailing zeros in the fraction are NOT trimmed: `1.0` has scale 1 in
        // PostgreSQL, and the cap is on the stored scale rather than on the value.
        return integer.Length - leading + exponent <= MaxWholeDigits
            && fraction.Length - exponent <= MaxFractionDigits;
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

    /// <remarks>
    /// Anchored with <c>\z</c> and not <c>$</c>. In .NET <c>$</c> matches at the end
    /// of the input <b>or immediately before a final newline</b>, so
    /// <c>"card\n"</c> was url-safe — measured — and that value reaches a URL
    /// segment, a cache-key component and an <c>x-taxonomy</c> reference.
    /// </remarks>
    [System.Text.RegularExpressions.GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*\z")]
    private static partial System.Text.RegularExpressions.Regex Pattern();
}
