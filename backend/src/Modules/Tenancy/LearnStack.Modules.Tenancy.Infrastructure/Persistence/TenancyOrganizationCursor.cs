using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Modules.Tenancy.Infrastructure.Persistence;

/// <summary>Bounded canonical resource-local seek data; the cursor grants no authority or snapshot.</summary>
internal static class TenancyOrganizationCursor
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonDocumentOptions Options = new() { MaxDepth = 2 };

    internal static string Scope(TenantId tenant, UserId actor, OrganizationId? organization) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new object?[]
        {
            1, "tenancy.organization.list", tenant.Value.ToString("D"), actor.Value.ToString("D"),
            organization?.Value.ToString("D"), "organization_id:asc",
        })));

    internal static string Encode(string scope, OrganizationId id) => Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
    {
        v = 1,
        scope,
        id = id.Value.ToString("D"),
    }));

    internal static bool TryDecode(string raw, string expectedScope, out OrganizationId? after)
    {
        after = null;
        if (string.IsNullOrEmpty(raw) || raw.Length > 1024
            || raw.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))) return false;
        try
        {
            var bytes = Convert.FromBase64String(raw.Replace('-', '+').Replace('_', '/') + new string('=', (4 - raw.Length % 4) % 4));
            if (bytes.Length > 768 || Base64Url(bytes) != raw) return false;
            using var document = JsonDocument.Parse(StrictUtf8.GetString(bytes), Options);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.EnumerateObject().Select(property => property.Name).SequenceEqual(["v", "scope", "id"])
                || root.GetProperty("v").ValueKind != JsonValueKind.Number || !root.GetProperty("v").TryGetInt32(out var version) || version != 1
                || root.GetProperty("scope").ValueKind != JsonValueKind.String || root.GetProperty("scope").GetString() != expectedScope
                || root.GetProperty("id").ValueKind != JsonValueKind.String) return false;
            var text = root.GetProperty("id").GetString();
            if (!Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty || id.Version != 7 || id.ToString("D") != text) return false;
            var anchor = OrganizationId.From(id);
            if (Encode(expectedScope, anchor) != raw) return false;
            after = anchor;
            return true;
        }
        catch (Exception failure) when (failure is FormatException or JsonException or DecoderFallbackException or InvalidOperationException)
        {
            return false;
        }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
