using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Pagination;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using Microsoft.Net.Http.Headers;

namespace LearnStack.Api.Common;

/// <summary>Closed wire parsing before JSON model binding can discard duplicate or missing fields.</summary>
internal static class AdministrationHttpInput
{
    internal const int MaxBodyBytes = 16 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static ProblemDetailsActionResult Invalid() => new(new Error(LocalizedMessage.Of("lockey_validation_failed")));

    internal static bool TryId(string? text, out Guid id) => Guid.TryParseExact(text, "D", out id)
        && id.Version == 7 && id != Guid.Empty && id != UserId.SystemActor.Value && id != TenantId.PlatformSentinel.Value
        && string.Equals(text, id.ToString("D"), StringComparison.Ordinal) && text![19] is '8' or '9' or 'a' or 'b';

    internal static async Task<byte[]> ReadBodyBytesAsync(HttpContext http)
    {
        if (http.Request.ContentLength > MaxBodyBytes) throw TooLarge();
        http.Request.EnableBuffering();
        http.Request.Body.Position = 0;
        var bytes = new byte[MaxBodyBytes + 1];
        var count = 0;
        try
        {
            while (count < bytes.Length)
            {
                var read = await http.Request.Body.ReadAsync(bytes.AsMemory(count), http.RequestAborted);
                if (read == 0) break;
                count += read;
            }
            if (count > MaxBodyBytes) throw TooLarge();
            return bytes[..count];
        }
        finally { http.Request.Body.Position = 0; }
    }

    internal static async Task<JsonDocument?> ReadObjectAsync(HttpContext http, params string[] fields)
    {
        if (http.Request.Query.Count != 0 || !MediaTypeHeaderValue.TryParse(http.Request.ContentType, out var contentType)
            || !string.Equals(contentType.MediaType.Value, "application/json", StringComparison.OrdinalIgnoreCase)
            || contentType.Charset.HasValue && !string.Equals(HeaderUtilities.RemoveQuotes(contentType.Charset).Value,
                "utf-8", StringComparison.OrdinalIgnoreCase)) return null;
        var bytes = await ReadBodyBytesAsync(http);
        JsonDocument? document = null;
        try
        {
            _ = StrictUtf8.GetCharCount(bytes);
            document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 5 });
            if (HasFields(document.RootElement, fields)) return document;
        }
        catch (JsonException) { }
        catch (DecoderFallbackException) { }
        catch (InvalidOperationException) { }
        document?.Dispose();
        return null;
    }

    internal static bool HasFields(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var remaining = fields.ToHashSet(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!remaining.Remove(property.Name)) return false;
        return remaining.Count == 0;
    }

    internal static bool TryText(JsonElement value, string name, bool nullable, int maximum, out string? text)
    {
        text = null;
        if (!value.TryGetProperty(name, out var item)) return false;
        if (item.ValueKind == JsonValueKind.Null) return nullable;
        if (item.ValueKind != JsonValueKind.String) return false;
        return TryString(item, maximum, out text);
    }

    internal static bool TryRoles(JsonElement body, out ImmutableArray<Guid> roles)
    {
        roles = [];
        var value = body.GetProperty("roleIds");
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 8) return false;
        var builder = ImmutableArray.CreateBuilder<Guid>();
        foreach (var item in value.EnumerateArray())
        {
            if (!TryString(item, 36, out var text) || !TryId(text, out var id) || builder.Contains(id)) return false;
            builder.Add(id);
        }
        roles = builder.ToImmutable();
        return true;
    }

    internal static bool TryGrants(JsonElement body, out ImmutableArray<PermissionDefinition> grants)
    {
        grants = [];
        var value = body.GetProperty("grants");
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 8) return false;
        var builder = ImmutableArray.CreateBuilder<PermissionDefinition>();
        foreach (var item in value.EnumerateArray())
        {
            if (!HasFields(item, "scope", "key") || !TryText(item, "scope", false, 12, out var scope)
                || !TryText(item, "key", false, 128, out var key) || scope is not ("Tenant" or "Organization")) return false;
            PermissionDefinition pair;
            try { pair = new PermissionDefinition(scope == "Tenant" ? PermissionScope.Tenant : PermissionScope.Organization, key!); }
            catch (ArgumentException) { return false; }
            if (builder.Contains(pair)) return false;
            builder.Add(pair);
        }
        grants = builder.ToImmutable();
        return true;
    }

    internal static async Task<bool> ReadHasNoBodyAsync(HttpContext http) =>
        (await ReadBodyBytesAsync(http)).Length == 0;

    internal static bool TryPage(HttpRequest request, out string? cursor, out int limit)
    {
        cursor = null;
        limit = CursorPagination.DefaultLimit;
        foreach (var (name, values) in request.Query)
        {
            if (name is not ("cursor" or "limit") || values.Count != 1 || string.IsNullOrWhiteSpace(values[0])) return false;
            var text = values[0]!;
            if (name == "cursor")
            {
                if (text.Length > 1024) return false;
                cursor = text;
            }
            else if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out limit)
                || limit <= 0 || limit.ToString(CultureInfo.InvariantCulture) != text) return false;
        }
        limit = Math.Min(limit, CursorPagination.MaxLimit);
        return true;
    }

    internal static string Version(long value) => value >= 0 ? value.ToString(CultureInfo.InvariantCulture)
        : throw new InvalidOperationException("The administration result version is invalid.");

    private static bool TryString(JsonElement item, int maximum, out string? value)
    {
        value = null;
        if (item.ValueKind != JsonValueKind.String) return false;
        try { value = item.GetString(); }
        catch (InvalidOperationException) { return false; }
        return value is not null && value.Length <= maximum && JsonValue.IsStorableText(value);
    }

    private static BadHttpRequestException TooLarge() => new("Administration request body exceeds its limit.", StatusCodes.Status413PayloadTooLarge);
}
