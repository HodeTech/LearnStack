using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;

namespace LearnStack.Modules.Tenancy.Infrastructure.Persistence;

/// <summary>Own-navigation keyset: the tenant item precedes organizations, and changing own scope invalidates continuation.</summary>
internal static class OwnTenantNavigationCursor
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string Scope(UserId actor, TenantId tenant, OrganizationId? organization, bool tenantMembership) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new object?[]
        {
            1, OwnTenantNavigationContract.Operation, actor.Value.ToString("D"), tenant.Value.ToString("D"),
            organization?.Value.ToString("D"), tenantMembership, "tenant_then_organization_id:asc",
        })));

    internal static string Encode(string scope, OrganizationId? after) => Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
    {
        v = 1,
        scope,
        id = after?.Value.ToString("D"),
    }));

    internal static bool TryDecode(string raw, string scope, out OrganizationId? after)
    {
        after = null;
        if (string.IsNullOrEmpty(raw) || raw.Length > 1024
            || raw.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))) return false;
        try
        {
            var bytes = Convert.FromBase64String(raw.Replace('-', '+').Replace('_', '/') + new string('=', (4 - raw.Length % 4) % 4));
            if (bytes.Length > 768 || Base64Url(bytes) != raw) return false;
            using var document = JsonDocument.Parse(StrictUtf8.GetString(bytes), new JsonDocumentOptions { MaxDepth = 2 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.EnumerateObject().Select(property => property.Name).SequenceEqual(["v", "scope", "id"])
                || root.GetProperty("v").ValueKind != JsonValueKind.Number || !root.GetProperty("v").TryGetInt32(out var version) || version != 1
                || root.GetProperty("scope").ValueKind != JsonValueKind.String || root.GetProperty("scope").GetString() != scope) return false;
            var field = root.GetProperty("id");
            if (field.ValueKind != JsonValueKind.Null)
            {
                if (field.ValueKind != JsonValueKind.String) return false;
                var text = field.GetString();
                if (!Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty || id.Version != 7 || id.ToString("D") != text) return false;
                after = OrganizationId.From(id);
            }
            return Encode(scope, after) == raw;
        }
        catch (Exception failure) when (failure is FormatException or JsonException or DecoderFallbackException or InvalidOperationException)
        {
            return false;
        }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
