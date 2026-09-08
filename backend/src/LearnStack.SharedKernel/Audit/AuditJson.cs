using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    };

    /// <summary>The JSON literal <c>null</c>, which is not the same as an absent key.</summary>
    public const string Null = "null";

    /// <summary>Renders one property value as JSON text.</summary>
    /// <remarks>
    /// <para>
    /// A value already stored as JSON — a <c>jsonb</c> column mapped to <c>string</c> —
    /// is passed through rather than re-encoded, so a document does not arrive in the
    /// diff as one long escaped string. The check is a parse, because a <c>string</c>
    /// property is far more often ordinary text than a document, and guessing from the
    /// first character would render <c>"null"</c> the user typed as the JSON <c>null</c>.
    /// </para>
    /// <para>
    /// Everything else goes through <c>JsonSerializer</c>, which is what makes a
    /// <c>Guid</c>, a <c>DateTimeOffset</c> and an <c>IPAddress</c> land as quoted
    /// strings the two readers can parse without knowing the CLR type.
    /// </para>
    /// </remarks>
    public static string Render(object? value)
    {
        if (value is null)
        {
            return Null;
        }

        if (value is string text && LooksLikeStoredJson(text))
        {
            return text;
        }

        return JsonSerializer.Serialize(value, value.GetType(), Options);
    }

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

    private static bool LooksLikeStoredJson(string text)
    {
        // Objects and arrays only. A bare JSON scalar is indistinguishable from ordinary
        // text a user typed — `null`, `true`, `42` — and treating those as stored JSON
        // would silently retype a string column's value in the diff.
        var trimmed = text.AsSpan().Trim();

        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
