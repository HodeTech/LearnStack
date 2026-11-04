using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LearnStack.Api.Common;
using LearnStack.Modules.Identity.Application.Contracts.Invitations;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identity;

namespace LearnStack.Api.Authentication;

/// <summary>One strict parse retained privately; the controller dispatches this immutable capture.</summary>
internal sealed class InvitationEntryHttpInput(Guid invitationId, string code, string digest, string? ifMatch)
{
    internal Guid InvitationId { get; } = invitationId;
    [PiiSensitive] internal string Code { get; } = code;
    [PiiSensitive] internal string Digest { get; } = digest;
    internal string? IfMatch { get; } = ifMatch;
    internal bool IsCurrent(HttpContext http) => http.Request.Query.Count == 0
        && !http.Request.Headers.ContainsKey("Idempotency-Key") && !http.Request.Headers.ContainsKey("If-None-Match")
        && !http.Request.Headers.ContainsKey("If-Modified-Since") && !http.Request.Headers.ContainsKey("If-Unmodified-Since")
        && (IfMatch is null ? !http.Request.Headers.ContainsKey("If-Match")
            : http.Request.Headers.ContainsKey("If-Match") && http.Request.Headers.IfMatch.ToString() == IfMatch);

    internal static async Task<InvitationEntryHttpInput?> ReadAsync(HttpContext http, InvitationEntryOperation operation)
    {
        const int maximum = 8 * 1024;
        if (!operation.Matches(http) || http.Request.Query.Count != 0 || !AdministrationHttpInput.SupportsJsonMedia(http.Request)
            || http.Request.ContentLength > maximum || http.Request.Headers.ContainsKey("Idempotency-Key")
            || http.Request.Headers.ContainsKey("If-None-Match") || http.Request.Headers.ContainsKey("If-Modified-Since")
            || http.Request.Headers.ContainsKey("If-Unmodified-Since")
            || operation.RequestType != typeof(AcceptInvitationCommand) && http.Request.Headers.ContainsKey("If-Match")) return null;
        var bytes = new byte[maximum + 1];
        try
        {
            var count = 0;
            while (count < bytes.Length)
            {
                var read = await http.Request.Body.ReadAsync(bytes.AsMemory(count), http.RequestAborted);
                if (read == 0) break;
                count += read;
            }
            if (count > maximum) return null;
            _ = new UTF8Encoding(false, true).GetCharCount(bytes, 0, count);
            using var json = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 3 });
            if (!AdministrationHttpInput.HasFields(json.RootElement, "invitationId", "code")
                || !AdministrationHttpInput.TryText(json.RootElement, "invitationId", false, 36, out var idText)
                || !AdministrationHttpInput.TryId(idText, out var id)
                || !AdministrationHttpInput.TryText(json.RootElement, "code", false, 80, out var code)
                || !InvitationVerifier.TryDigest(id, code, out var digest)) return null;
            try { return new(id, code!, Convert.ToHexStringLower(digest), http.Request.Headers.ContainsKey("If-Match") ? http.Request.Headers.IfMatch.ToString() : null); }
            finally { CryptographicOperations.ZeroMemory(digest); }
        }
        catch (JsonException) { return null; }
        catch (DecoderFallbackException) { return null; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public override string ToString() => nameof(InvitationEntryHttpInput);
}
