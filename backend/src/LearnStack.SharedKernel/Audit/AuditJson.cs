using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using LearnStack.SharedKernel.Domain;

namespace LearnStack.SharedKernel.Audit;

/// <summary>
/// The JSON the capture writes: how a value is rendered, and what happens when the
/// rendering is too large to store.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every value slot in a captured change holds JSON text, not a rendered value.</b>
/// <c>42</c> and <c>"42"</c> are different values, a CLR <c>null</c> is the JSON
/// <c>null</c> rather than an absent key, and the redaction sentinel therefore enters
/// quoted. A slot holding a rendered value would make the <c>changes</c> column
/// unparseable by the two readers that consume it — the Phase 06 diff viewer and each
/// module's <c>IUserReferenceLocator</c>.
/// </para>
/// <para>
/// <b>Here rather than beside the interceptor</b>, because the shape it writes is a
/// contract on the <c>changes</c>, <c>before_state</c> and <c>after_state</c> columns
/// rather than a detail of how they are filled —
/// <see cref="CapturedEntityChange"/>'s own remarks describe it, and a reader that has
/// to recognise an elision record should be reading the same type the writer used
/// instead of re-deriving the shape from prose. It names no EF Core type for the same
/// reason every other member of this namespace does not.
/// </para>
/// <para>
/// <b>The elision preserves the column's JSON type</b>
/// (<see href="../../../../docs/decisions/0044-audit-write-path.md">ADR-0044 § 8</see>).
/// <c>changes</c> is an array on both sides of the cap, so an elided <c>changes</c> is a
/// one-element array; <c>before_state</c> and <c>after_state</c> are objects on both
/// sides, so theirs is the object form. A reader that has to branch on the shape before
/// it can tell what it is looking at is a reader that gets it wrong once.
/// </para>
/// </remarks>
public static class AuditJson
{
    /// <summary>
    /// The cap each of <c>before_state</c>, <c>after_state</c> and <c>changes</c> is
    /// bounded by.
    /// </summary>
    /// <remarks>
    /// Packet 8's <see cref="JsonValue.MaxRowBytes"/> — 256 KiB — reused rather than
    /// redeclared. A second constant would be a second number to keep equal, and this
    /// one is already the size the corpus calls a row.
    /// </remarks>
    public const int MaxBytes = JsonValue.MaxRowBytes;

    private static readonly JsonSerializerOptions Options = new()
    {
        // No indentation and no cycle handling: the values reaching here are scalars and
        // strings read off a ChangeTracker entry, never object graphs.
        WriteIndented = false,

        // Letters as letters. The default encoder writes every non-ASCII character as a
        // six-byte \uXXXX escape, and the cap below is measured on this text — so a
        // display name in Turkish or Japanese reached it three to six times sooner than
        // its value did and was elided, while jsonb stores the decoded character either
        // way (the fifth review of Packet 9). UnicodeRanges.All still escapes what the
        // default does beyond that: the HTML-sensitive characters and the controls.
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    /// <summary>The JSON literal <c>null</c>, which is not the same as an absent key.</summary>
    public const string Null = "null";

    /// <summary>Renders one property value as JSON text.</summary>
    /// <remarks>
    /// <para>
    /// <b>The passthrough is decided by the column, never by the value's content.</b> A
    /// value stored in a <c>jsonb</c> column is emitted verbatim, so a document does not
    /// arrive in the
    /// diff as one long escaped string; everything else is serialised, so a
    /// <c>varchar(200)</c> display name that happens to read as <c>[1,2,3]</c> stays the
    /// three-character string it is. Deciding from the value instead was wrong twice over
    /// — it retyped ordinary text whose content parsed, contradicting this file's own rule
    /// that <c>42</c> and <c>"42"</c> are different values, and it emitted text
    /// <c>jsonb</c> refuses: <c>System.Text.Json</c> accepts the <c>\u0000</c> escape and
    /// unbounded numeric exponents and PostgreSQL does not, so a display name containing
    /// either failed the <c>INSERT</c> — inside the business transaction, on the path
    /// whose whole job is that the record survives. Measured on PostgreSQL 18.6:
    /// <c>22P05 unsupported Unicode escape sequence</c>.
    /// </para>
    /// <para>
    /// <b>An enum renders as its name</b>, not its ordinal. The three closed-set columns
    /// beside these store the name, and a snapshot that stored <c>1</c> where the row
    /// stores <c>Denied</c> would make the two halves of the same record disagree — and
    /// silently change meaning the day a member is inserted into the middle of the enum.
    /// </para>
    /// <para>
    /// Everything else goes through <c>JsonSerializer</c>, which is what makes a
    /// <c>Guid</c>, a <c>DateTimeOffset</c>, an <c>IPAddress</c> and a Vogen identifier
    /// land as quoted strings the two readers can parse without knowing the CLR type.
    /// </para>
    /// </remarks>
    /// <param name="value">The property's current or original value.</param>
    /// <param name="storedAsJson">
    /// Whether the property's column is <c>jsonb</c>. The caller reads it from the model,
    /// which is the only place the answer is knowable.
    /// <para>
    /// It gates the passthrough <b>together with the value being text</b>, and that pairing
    /// is deliberate rather than a leftover value sniff. The caller hands over the value
    /// the column holds — through the property's converter — so for a <c>jsonb</c> column
    /// that is the document as a <c>string</c>. A <c>jsonb</c> column whose provider value
    /// is something else is a mapping this repository does not have (Npgsql's native
    /// <c>JsonDocument</c> support would be one), and serialising it is the safe answer
    /// rather than emitting <c>ToString()</c> into a <c>jsonb</c> parameter.
    /// </para>
    /// </param>
    public static string Render(object? value, bool storedAsJson = false)
    {
        if (value is null)
        {
            return Null;
        }

        if (storedAsJson && value is string document)
        {
            // Well-formedness is checked by PARSING, because that is what tells an escape
            // from the six characters that spell one, and because the numeric bounds
            // jsonb enforces are invisible in the text.
            return JsonValue.IsWellFormed(document) ? document : Unstorable(document.Length);
        }

        if (value is Enum member)
        {
            return JsonSerializer.Serialize(member.ToString(), Options);
        }

        // The VALUE, not the text it serialises to. `JsonSerializer` renders a NUL as the
        // six-character escape \u0000, which is storable text and which PostgreSQL's
        // jsonb input function then refuses while parsing — so a check on the output
        // passes and the INSERT still fails.
        if (value is string text && !JsonValue.IsStorableText(text))
        {
            return Unstorable(text.Length);
        }

        return JsonSerializer.Serialize(value, value.GetType(), Options);
    }

    /// <summary>
    /// The marker that stands in for a value PostgreSQL cannot hold.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>NUL</c> or an unpaired surrogate cannot be stored in a PostgreSQL <c>text</c>
    /// column at all, and cannot be parsed by the <c>jsonb</c> input function even
    /// escaped — measured: <c>22P05 unsupported Unicode escape sequence</c>. So a value
    /// carrying one is a value <b>no column holds</b>: the business write carrying it
    /// fails too. What must not happen is the AUDIT write failing with it, because the
    /// audit write is what records that the business write failed. Before this marker
    /// existed, a rolled-back write's reconcile row carried the captured value and the
    /// standalone <c>INSERT</c> raised <c>22P05</c> — the record of the failure destroyed
    /// by the same character that caused it.
    /// </para>
    /// <para>
    /// An object rather than a truncated string, on the size cap's precedent: never a
    /// silent substitution. The slot it sits in is already polymorphic — it mirrors
    /// whatever the column holds, a number or a string or a document — so a marker
    /// changes no reader's contract, unlike the two column-level shapes
    /// <see cref="CapObject"/> and <see cref="CapArray"/> keep stable.
    /// </para>
    /// </remarks>
    private static string Unstorable(int length) =>
        $"{{\"_unstorable\":true,\"reason\":\"nul-or-unpaired-surrogate\",\"chars\":{length}}}";

    /// <summary>Renders a string that is already known to be JSON text.</summary>
    public static string Quote(string value) => JsonSerializer.Serialize(value, Options);

    /// <summary>
    /// Returns <paramref name="json"/> if it fits, or the object-shaped elision record.
    /// </summary>
    public static string CapObject(string json) =>
        Fits(json) ? json : Elision(json);

    /// <summary>
    /// Returns <paramref name="json"/> if it fits, or the elision record wrapped in a
    /// one-element array, so <c>changes</c> is an array on both sides of the cap.
    /// </summary>
    public static string CapArray(string json) =>
        Fits(json) ? json : "[" + Elision(json) + "]";

    private static bool Fits(string json) => Encoding.UTF8.GetByteCount(json) <= MaxBytes;

    /// <summary>
    /// <c>{"_elided": true, "bytes": n, "sha256": "…"}</c> — never an empty object and
    /// never a silent truncation.
    /// </summary>
    /// <remarks>
    /// The digest is what makes the record useful rather than merely honest: two rows
    /// elided from the same payload carry the same hash, so a reader can tell a repeated
    /// value from a changed one without the value.
    /// </remarks>
    private static string Elision(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));

        return $"{{\"_elided\":true,\"bytes\":{bytes.Length},\"sha256\":\"{digest}\"}}";
    }

}
