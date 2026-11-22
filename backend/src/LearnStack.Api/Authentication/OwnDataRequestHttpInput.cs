using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LearnStack.Api.Common;
using LearnStack.Api.Idempotency;
using LearnStack.Modules.Identity.Application.Contracts.DataRequests;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;

namespace LearnStack.Api.Authentication;

/// <summary>
/// Strict request capture for the retained own data-request entry, selected before authorization and model binding
/// (ADR-0106 § 9), as <see cref="ConsentSubjectHttpInput"/> is for the consent subject.
/// </summary>
/// <remarks>
/// <para>
/// <b>The group is a selection, never authority.</b> Every purpose but discovery names exactly one group in the one
/// <c>group</c> query value: the literal <c>tenant</c> for the tenant-wide group or the organization's canonical
/// UUIDv7. The confined definer must then prove that group for the verified subject on the real host.
/// </para>
/// <para>
/// <b>Closed shapes.</b> Only the declared query keys, each once; only the declared body fields, each once; an
/// <c>Idempotency-Key</c> only on creation and cancellation; <c>If-Match</c> only, and always, on cancellation; no
/// other conditional header anywhere. The body is buffered, bounded and cleared once parsed, because the chunk read
/// carries a ticket in it.
/// </para>
/// </remarks>
internal sealed class OwnDataRequestHttpInput
{
    internal const string TenantWideGroup = "tenant";
    private const int MaximumBody = 4096;

    private readonly string _query;
    private readonly string? _ifMatch;

    private OwnDataRequestHttpInput(object request, bool selectsGroup, OrganizationId? organization, DataRequestScopeGroupCursor? after,
        int limit, string query, string? ifMatch)
    {
        Request = request; SelectsGroup = selectsGroup; OrganizationId = organization; After = after; Limit = limit;
        _query = query; _ifMatch = ifMatch;
    }

    internal object Request { get; }

    /// <summary>Whether a group was selected; only discovery selects none.</summary>
    internal bool SelectsGroup { get; }

    /// <summary>The selected group's organization, or <see langword="null"/> for the tenant-wide group.</summary>
    internal OrganizationId? OrganizationId { get; }

    /// <summary>Discovery's cursor: the last group of the previous page.</summary>
    internal DataRequestScopeGroupCursor? After { get; }

    internal int Limit { get; }

    internal bool IsCurrent(HttpContext http) => (http.Request.QueryString.Value ?? "") == _query
        && HeadersAdmit(http.Request, PurposeOf(Request))
        && string.Equals(IfMatch(http.Request), _ifMatch, StringComparison.Ordinal);

    internal static async Task<OwnDataRequestHttpInput?> ReadAsync(HttpContext http, OwnDataRequestOperation operation)
    {
        var purpose = operation.Purpose;
        if (!HeadersAdmit(http.Request, purpose)) return null;
        long version = 0;
        if (purpose == OwnDataRequestPurpose.Cancel && !ProtectedEntityTag.TryRead(http.Request, out version)) return null;
        string? cursor = null; string? group = null; var limit = 20;
        foreach (var (key, values) in http.Request.Query)
        {
            if (values.Count != 1 || string.IsNullOrEmpty(values[0])) return null;
            var value = values[0]!;
            if (key == "group" && purpose != OwnDataRequestPurpose.List && value.Length <= 36) group = value;
            else if (key == "cursor" && purpose is OwnDataRequestPurpose.List or OwnDataRequestPurpose.Read
                && value.Length <= (purpose == OwnDataRequestPurpose.List ? 36 : DataRequestCursorLimit)) cursor = value;
            else if (key == "limit" && purpose is OwnDataRequestPurpose.List or OwnDataRequestPurpose.Read
                && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                && parsed is >= 1 and <= OwnDataRequestScopeWitness.MaximumPage
                && parsed.ToString(CultureInfo.InvariantCulture) == value) limit = parsed;
            else return null;
        }

        OrganizationId? organization = null;
        if (purpose != OwnDataRequestPurpose.List)
        {
            if (!TryGroup(group, out organization)) return null;
        }

        DataRequestScopeGroupCursor? after = null;
        if (purpose == OwnDataRequestPurpose.List && cursor is not null)
        {
            if (!TryGroup(cursor, out var anchor)) return null;
            after = new DataRequestScopeGroupCursor(anchor);
        }

        object request;
        if (purpose is OwnDataRequestPurpose.List or OwnDataRequestPurpose.Read)
        {
            if (!await AdministrationHttpInput.ReadHasNoBodyAsync(http)) return null;
            request = purpose == OwnDataRequestPurpose.List
                ? new ListRetainedDataRequestScopesQuery(cursor, limit)
                : new ListRetainedDataRequestsQuery(organization, cursor, limit);
        }
        else
        {
            if (!AdministrationHttpInput.SupportsJsonMedia(http.Request)) return null;
            var bytes = await ReadBodyAsync(http);
            try
            {
                _ = new UTF8Encoding(false, true).GetCharCount(bytes);
                using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 2 });
                var body = json.RootElement;
                var parsed = purpose switch
                {
                    OwnDataRequestPurpose.Create => AdministrationHttpInput.HasFields(body)
                        ? new CreateRetainedDataExportCommand(organization) : null,
                    OwnDataRequestPurpose.Cancel => TryRequestId(body, out var cancelled, "requestId")
                        ? new CancelRetainedDataRequestCommand(organization, cancelled, version) : null,
                    OwnDataRequestPurpose.Ticket => TryRequestId(body, out var ticketed, "requestId")
                        ? new MintRetainedDataExportTicketCommand(organization, ticketed) : null,
                    OwnDataRequestPurpose.Chunk => TryChunk(body, organization),
                    _ => (object?)null,
                };
                if (parsed is null) return null;
                request = parsed;
            }
            catch (JsonException) { return null; }
            catch (DecoderFallbackException) { return null; }
            catch (InvalidOperationException) { return null; }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }

        return new(request, purpose != OwnDataRequestPurpose.List, organization, after, limit,
            http.Request.QueryString.Value ?? "", IfMatch(http.Request));
    }

    /// <summary>The canonical text of a group key: <c>tenant</c>, or the organization's canonical UUID.</summary>
    internal static string KeyFor(DataRequestScopeGroup group) =>
        group.OrganizationId is { } organization ? organization.Value.ToString("D") : TenantWideGroup;

    private const int DataRequestCursorLimit = 1024;

    private static bool TryGroup(string? text, out OrganizationId? organization)
    {
        organization = null;
        if (text is null) return false;
        if (string.Equals(text, TenantWideGroup, StringComparison.Ordinal)) return true;
        if (!AdministrationHttpInput.TryId(text, out var id)) return false;
        organization = LearnStack.SharedKernel.Identifiers.OrganizationId.From(id);
        return true;
    }

    private static bool TryRequestId(JsonElement body, out Guid id, params string[] fields)
    {
        id = Guid.Empty;
        return AdministrationHttpInput.HasFields(body, fields)
            && AdministrationHttpInput.TryText(body, "requestId", false, 36, out var text) && AdministrationHttpInput.TryId(text, out id);
    }

    private static ReadRetainedDataExportChunkCommand? TryChunk(JsonElement body, OrganizationId? organization)
    {
        if (!TryRequestId(body, out var id, "requestId", "ticket", "index")
            || !AdministrationHttpInput.TryText(body, "ticket", false, 64, out var ticket) || ticket is null
            || body.GetProperty("index") is not { ValueKind: JsonValueKind.Number } index
            || !index.TryGetInt32(out var position) || position < 0
            || !string.Equals(index.GetRawText(), position.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            return null;
        return new ReadRetainedDataExportChunkCommand(organization, id, ticket, position);
    }

    private static OwnDataRequestPurpose PurposeOf(object request) => request switch
    {
        ListRetainedDataRequestScopesQuery => OwnDataRequestPurpose.List,
        ListRetainedDataRequestsQuery => OwnDataRequestPurpose.Read,
        CreateRetainedDataExportCommand => OwnDataRequestPurpose.Create,
        CancelRetainedDataRequestCommand => OwnDataRequestPurpose.Cancel,
        MintRetainedDataExportTicketCommand => OwnDataRequestPurpose.Ticket,
        ReadRetainedDataExportChunkCommand => OwnDataRequestPurpose.Chunk,
        _ => 0,
    };

    /// <summary>A key only where the operation is keyed, If-Match only (and always) on cancellation, no other precondition.</summary>
    private static bool HeadersAdmit(HttpRequest request, OwnDataRequestPurpose purpose) => Enum.IsDefined(purpose)
        && !request.Headers.ContainsKey("If-None-Match") && !request.Headers.ContainsKey("If-Modified-Since")
        && !request.Headers.ContainsKey("If-Unmodified-Since")
        && (purpose == OwnDataRequestPurpose.Cancel ? request.Headers.IfMatch.Count == 1 : !request.Headers.ContainsKey("If-Match"))
        && (purpose is OwnDataRequestPurpose.Create or OwnDataRequestPurpose.Cancel
            || !request.Headers.ContainsKey(IdempotentAttribute.HeaderName));

    private static string? IfMatch(HttpRequest request) => request.Headers.IfMatch.Count == 1 ? request.Headers.IfMatch[0] : null;

    private static async Task<byte[]> ReadBodyAsync(HttpContext http)
    {
        if (http.Request.ContentLength > MaximumBody) throw TooLarge();
        http.Request.EnableBuffering();
        http.Request.Body.Position = 0;
        var buffer = new byte[MaximumBody + 1];
        try
        {
            var count = 0;
            while (count < buffer.Length)
            {
                var read = await http.Request.Body.ReadAsync(buffer.AsMemory(count), http.RequestAborted);
                if (read == 0) break;
                count += read;
            }
            if (count > MaximumBody) throw TooLarge();
            return buffer[..count];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            http.Request.Body.Position = 0;
        }
    }

    private static BadHttpRequestException TooLarge() => new("Data request entry body exceeds its limit.", StatusCodes.Status413PayloadTooLarge);

    public override string ToString() => nameof(OwnDataRequestHttpInput);
}

/// <summary>The last group of a discovery page; <see langword="null"/> organization is the tenant-wide group.</summary>
internal sealed record DataRequestScopeGroupCursor(OrganizationId? OrganizationId);
