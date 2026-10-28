using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Modules.Identity.Application.Memberships;

/// <summary>Canonical resource-local seek protocol; it carries no authentication, secrecy or snapshot authority.</summary>
public static class MembershipDirectoryCursorCodec
{
    public const int MaximumEncodedLength = 1024;
    public const int MaximumDecodedLength = 768;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonDocumentOptions Options = new() { MaxDepth = 2 };

    public static string Scope(UserId actor, TenantId tenant, OrganizationId? organization) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new object?[]
        {
            1, IdentityOperationPolicySource.MembershipDirectoryOperation, actor.Value.ToString("D"),
            tenant.Value.ToString("D"), organization?.Value.ToString("D"), "membership_id:asc",
        })));

    public static string Encode(string scope, MembershipId after) => Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
    {
        v = 1,
        scope,
        id = after.Value.ToString("D"),
    }));

    public static bool TryDecode(string raw, string? expectedScope, out MembershipId? after)
    {
        after = null;
        if (string.IsNullOrEmpty(raw) || raw.Length > MaximumEncodedLength
            || raw.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))) return false;
        try
        {
            var bytes = Convert.FromBase64String(raw.Replace('-', '+').Replace('_', '/') + new string('=', (4 - raw.Length % 4) % 4));
            if (bytes.Length > MaximumDecodedLength || Base64Url(bytes) != raw) return false;
            using var document = JsonDocument.Parse(StrictUtf8.GetString(bytes), Options);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.EnumerateObject().Select(property => property.Name).SequenceEqual(["v", "scope", "id"])
                || root.GetProperty("v").ValueKind != JsonValueKind.Number || !root.GetProperty("v").TryGetInt32(out var version) || version != 1
                || root.GetProperty("scope").ValueKind != JsonValueKind.String || root.GetProperty("id").ValueKind != JsonValueKind.String)
                return false;
            var scope = root.GetProperty("scope").GetString()!;
            var text = root.GetProperty("id").GetString()!;
            if (scope.Length != 64 || scope.Any(character => !char.IsAsciiHexDigit(character) || char.IsAsciiLetterUpper(character))
                || (expectedScope is not null && scope != expectedScope)
                || !Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty || id.Version != 7 || id.ToString("D") != text)
                return false;
            var anchor = MembershipId.From(id);
            // Re-encoding also rejects alternate number spellings, whitespace and escaped canonical fields.
            if (Encode(scope, anchor) != raw) return false;
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
