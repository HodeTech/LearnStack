using System.Globalization;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using LearnStack.Api.Common;
using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using LearnStack.SharedKernel.Identity;

namespace LearnStack.Api.Authentication;

/// <summary>Strict request capture, selected before authorization and model binding.</summary>
internal sealed class ConsentSubjectHttpInput(object request, Guid? membershipId, Guid? after, int limit, string query)
{
    internal object Request { get; } = request;
    internal Guid? MembershipId { get; } = membershipId;
    internal Guid? After { get; } = after;
    internal int Limit { get; } = limit;
    internal bool IsCurrent(HttpContext http) => (http.Request.QueryString.Value ?? "") == query
        && NoConditionalHeaders(http.Request) && (Request is WithdrawOwnMembershipFieldConsentCommand
            || !http.Request.Headers.ContainsKey("Idempotency-Key"));

    private static bool NoConditionalHeaders(HttpRequest request) => !request.Headers.ContainsKey("If-Match")
        && !request.Headers.ContainsKey("If-None-Match") && !request.Headers.ContainsKey("If-Modified-Since")
        && !request.Headers.ContainsKey("If-Unmodified-Since");

    internal static async Task<ConsentSubjectHttpInput?> ReadAsync(HttpContext http, ConsentSubjectOperation operation)
    {
        if (!NoConditionalHeaders(http.Request) || (operation.Purpose != ConsentSubjectPurpose.Withdraw
            && http.Request.Headers.ContainsKey("Idempotency-Key"))) return null;
        string? cursor = null; Guid? member = null; var limit = 20;
        foreach (var (key, values) in http.Request.Query)
        {
            if (values.Count != 1 || string.IsNullOrEmpty(values[0])) return null;
            var value = values[0]!;
            if (key == "membershipId" && operation.Purpose != ConsentSubjectPurpose.Discover && AdministrationHttpInput.TryId(value, out var id)) member = id;
            else if (key == "cursor" && operation.Purpose != ConsentSubjectPurpose.Withdraw && value.Length <= 256) cursor = value;
            else if (key == "limit" && operation.Purpose != ConsentSubjectPurpose.Withdraw && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed is >= 1 and <= 20 && parsed.ToString(CultureInfo.InvariantCulture) == value) limit = parsed;
            else return null;
        }
        if (operation.Purpose != ConsentSubjectPurpose.Discover && member is null) return null;
        Guid? after = null;
        if (operation.Purpose == ConsentSubjectPurpose.Discover && cursor is not null)
        {
            if (!AdministrationHttpInput.TryId(cursor, out var id)) return null;
            after = id;
        }
        object request;
        if (operation.Purpose == ConsentSubjectPurpose.Withdraw)
        {
            if (!AdministrationHttpInput.SupportsJsonMedia(http.Request)) return null;
            var bytes = await ReadWithdrawalBodyAsync(http);
            try
            {
                _ = new UTF8Encoding(false, true).GetCharCount(bytes);
                using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 3 });
                var body = json.RootElement;
                if (!AdministrationHttpInput.HasFields(body, "evidenceId", "expectedSequence", "expectedEvidenceId", "confirmed")
                    || !AdministrationHttpInput.TryText(body, "evidenceId", false, 36, out var evidenceText) || !AdministrationHttpInput.TryId(evidenceText, out var evidence)
                    || !AdministrationHttpInput.TryText(body, "expectedEvidenceId", false, 36, out var expectedText) || !AdministrationHttpInput.TryId(expectedText, out var expected)
                    || !body.GetProperty("expectedSequence").TryGetInt64(out var sequence) || sequence is < 1 or > 9007199254740991L
                    || body.GetProperty("confirmed").ValueKind != JsonValueKind.True) return null;
                request = new WithdrawOwnMembershipFieldConsentCommand(evidence, sequence, expected, true);
            }
            catch (JsonException) { return null; }
            catch (DecoderFallbackException) { return null; }
            catch (InvalidOperationException) { return null; }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        else
        {
            if (!await AdministrationHttpInput.ReadHasNoBodyAsync(http)) return null;
            request = operation.Purpose == ConsentSubjectPurpose.Discover
                ? new ListOwnFieldConsentScopesQuery(cursor, limit)
                : new ListOwnMembershipFieldConsentsQuery(member!.Value, cursor, limit);
        }
        return new(request, member, after, limit, http.Request.QueryString.Value ?? "");
    }
    private static async Task<byte[]> ReadWithdrawalBodyAsync(HttpContext http)
    {
        const int maximum = 4096;
        if (http.Request.ContentLength > maximum) throw TooLarge();
        http.Request.EnableBuffering();
        http.Request.Body.Position = 0;
        var buffer = new byte[maximum + 1];
        try
        {
            var count = 0;
            while (count < buffer.Length)
            {
                var read = await http.Request.Body.ReadAsync(buffer.AsMemory(count), http.RequestAborted);
                if (read == 0) break;
                count += read;
            }
            if (count > maximum) throw TooLarge();
            return buffer[..count];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            http.Request.Body.Position = 0;
        }
    }
    private static BadHttpRequestException TooLarge() => new("Consent entry request body exceeds its limit.", StatusCodes.Status413PayloadTooLarge);
    public override string ToString() => nameof(ConsentSubjectHttpInput);
}
