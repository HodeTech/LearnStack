using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LearnStack.Modules.Education.Application.Abstractions;
using LearnStack.Modules.Education.Domain;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Modules.Education.Application.PublicReads;

/// <summary>Resource-local seek protocol, not authentication, secrecy or snapshot authority.</summary>
public static class PublicCursorCodec
{
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'";
    private const int MaxEncodedLength = 1024;
    private const int MaxDecodedLength = 768;
    private static readonly JsonDocumentOptions Options = new() { MaxDepth = 4 };
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string Scope(TenantId tenant, OrganizationId? organization, string hostDigest, string locale, bool outline) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new object?[]
        {
            1, outline ? "course-outline" : "catalog", hostDigest, tenant.Value.ToString("D"),
            organization?.Value.ToString("D"), locale, outline ? "sort:id:asc" : "created_at:id:asc",
        })));

    public static string EncodeCatalog(string scope, CatalogContinuation anchor) => Encode(new
    {
        v = 1,
        scope,
        createdAt = anchor.CreatedAt.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture),
        id = anchor.Id.Value.ToString("D"),
    });
    public static string EncodeOutline(string scope, OutlineContinuation anchor) => Encode(new
    {
        v = 1,
        scope,
        parent = anchor.Parent.Value.ToString("D"),
        sort = anchor.Sort,
        id = anchor.Id.Value.ToString("D"),
    });

    public static bool TryCatalog(string raw, string? expectedScope, out CatalogContinuation? anchor)
    {
        anchor = null;
        using var document = Decode(raw);
        try
        {
            if (document is null || !Envelope(document.RootElement, ["v", "scope", "createdAt", "id"], expectedScope)
                || !Uuid(document.RootElement, "id", out var id)
                || !String(document.RootElement, "createdAt", out var timestamp)
                || !DateTimeOffset.TryParseExact(timestamp, TimestampFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var createdAt)) return false;
            anchor = new CatalogContinuation(createdAt, CourseId.From(id));
            return true;
        }
        catch (InvalidOperationException) { return false; } // JSON strings are transcoded lazily, including escaped surrogates.
    }
    public static bool TryOutline(string raw, string? expectedScope, out OutlineContinuation? anchor)
    {
        anchor = null;
        using var document = Decode(raw);
        try
        {
            if (document is null || !Envelope(document.RootElement, ["v", "scope", "parent", "sort", "id"], expectedScope)
                || !Uuid(document.RootElement, "id", out var id) || !Uuid(document.RootElement, "parent", out var parent)
                || document.RootElement.GetProperty("sort").ValueKind != JsonValueKind.Number
                || !document.RootElement.GetProperty("sort").TryGetInt32(out var sort) || sort < 0) return false;
            anchor = new OutlineContinuation(CourseId.From(parent), sort, LessonId.From(id));
            return true;
        }
        catch (InvalidOperationException) { return false; }
    }

    private static string Encode<T>(T payload) => Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static JsonDocument? Decode(string raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.Length > MaxEncodedLength
            || raw.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))) return null;
        try
        {
            var bytes = Convert.FromBase64String(raw.Replace('-', '+').Replace('_', '/') + new string('=', (4 - raw.Length % 4) % 4));
            if (bytes.Length > MaxDecodedLength || Base64Url(bytes) != raw) return null;
            return JsonDocument.Parse(StrictUtf8.GetString(bytes), Options);
        }
        catch (Exception exception) when (exception is FormatException or JsonException or DecoderFallbackException) { return null; }
    }
    private static bool Envelope(JsonElement root, string[] fields, string? expectedScope)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        var names = root.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Length != fields.Length || names.Distinct(StringComparer.Ordinal).Count() != names.Length
            || names.Except(fields, StringComparer.Ordinal).Any()
            || root.GetProperty("v").ValueKind != JsonValueKind.Number
            || !root.GetProperty("v").TryGetInt32(out var version) || version != 1
            || !String(root, "scope", out var scope) || scope.Length != 64
            || scope.Any(character => !char.IsAsciiHexDigit(character) || char.IsAsciiLetterUpper(character))) return false;
        return expectedScope is null || scope == expectedScope;
    }
    private static bool String(JsonElement root, string name, out string value)
    {
        var member = root.GetProperty(name);
        value = member.ValueKind == JsonValueKind.String ? member.GetString()! : "";
        return member.ValueKind == JsonValueKind.String;
    }
    private static bool Uuid(JsonElement root, string name, out Guid value)
    {
        value = Guid.Empty;
        return String(root, name, out var text) && Guid.TryParseExact(text, "D", out value)
            && value != Guid.Empty && value.ToString("D") == text;
    }
}
