using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Modules.Identity.Application.DataRequests;

/// <summary>
/// The canonical seek cursor of a data-request page: the last request identifier of the previous page, bound to the
/// operation, the actor and the announced group. It carries no authority, secrecy or snapshot.
/// </summary>
/// <remarks>
/// Requests are ordered newest first by their UUIDv7 identifier, which is time-ordered and unique, so the seek is
/// <c>id &lt; after</c>. A cursor minted for another operation, actor or group is refused, never reinterpreted.
/// </remarks>
public static class DataRequestCursorCodec
{
    public const int MaximumEncodedLength = 1024;
    private const int MaximumDecodedLength = 768;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonDocumentOptions Options = new() { MaxDepth = 2 };

    public static string Scope(string operation, UserId actor, TenantId tenant, OrganizationId? organization) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new object?[]
        {
            1, operation, actor.Value.ToString("D"), tenant.Value.ToString("D"), organization?.Value.ToString("D"), "request_id:desc",
        })));

    public static string Encode(string scope, Guid after) => Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
    {
        v = 1,
        scope,
        id = after.ToString("D"),
    }));

    public static bool TryDecode(string raw, string expectedScope, out Guid after)
    {
        after = Guid.Empty;
        if (string.IsNullOrEmpty(raw) || raw.Length > MaximumEncodedLength
            || raw.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
        {
            return false;
        }

        try
        {
            var bytes = Convert.FromBase64String(raw.Replace('-', '+').Replace('_', '/') + new string('=', (4 - raw.Length % 4) % 4));
            if (bytes.Length > MaximumDecodedLength || Base64Url(bytes) != raw)
            {
                return false;
            }

            using var document = JsonDocument.Parse(StrictUtf8.GetString(bytes), Options);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.EnumerateObject().Select(property => property.Name).SequenceEqual(["v", "scope", "id"])
                || root.GetProperty("v").ValueKind != JsonValueKind.Number || !root.GetProperty("v").TryGetInt32(out var version)
                || version != 1 || root.GetProperty("scope").ValueKind != JsonValueKind.String
                || root.GetProperty("id").ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var scope = root.GetProperty("scope").GetString()!;
            var text = root.GetProperty("id").GetString()!;
            if (!string.Equals(scope, expectedScope, StringComparison.Ordinal)
                || !Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty || id.Version != 7
                || !string.Equals(id.ToString("D"), text, StringComparison.Ordinal)
                || !string.Equals(Encode(scope, id), raw, StringComparison.Ordinal))
            {
                return false;
            }

            after = id;
            return true;
        }
        catch (Exception failure) when (failure is FormatException or JsonException or DecoderFallbackException
            or InvalidOperationException)
        {
            return false;
        }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
