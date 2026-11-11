using System.Text.Json;
using LearnStack.SharedKernel.Identity;

namespace LearnStack.Infrastructure.Identity.ProviderEvents;

internal static class KeycloakEventNormalization
{
    internal static IdentityEventPage ReadPage(ReadOnlyMemory<byte> bytes, IdentityEventRead request, IdentityEventProviderOptions source)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() > request.Maximum)
                return IdentityEventPage.Unreadable(IdentityEventPageIssue.Malformed);
            return IdentityEventPage.Read(root.EnumerateArray().Select(item => ReadItem(item, request, source)));
        }
        catch (JsonException) { return IdentityEventPage.Unreadable(IdentityEventPageIssue.Malformed); }
    }

    private static IdentityEventReadItem ReadItem(JsonElement item, IdentityEventRead request, IdentityEventProviderOptions source)
    {
        try { return ReadItemCore(item, request, source); }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        { return IdentityEventReadItem.Malformed; }
    }

    private static IdentityEventReadItem ReadItemCore(JsonElement item, IdentityEventRead request, IdentityEventProviderOptions source)
    {
        if (!UniqueObject(item) || !Text(item, "type", out var code)) return IdentityEventReadItem.Malformed;
        if (!IdentityEventKindCodes.TryKind(code!, out var kind)) return IdentityEventReadItem.Unselected;
        if (!Text(item, "id", out var idText) || !Guid.TryParseExact(idText, "D", out var id) || id == Guid.Empty
            || !Text(item, "realmId", out var realm) || !StringComparer.Ordinal.Equals(realm, source.RealmId)
            || !item.TryGetProperty("time", out var time) || time.ValueKind != JsonValueKind.Number || !time.TryGetInt64(out var milliseconds)
            || milliseconds < request.From.ToUnixTimeMilliseconds() || milliseconds > request.To.ToUnixTimeMilliseconds())
            return IdentityEventReadItem.Malformed;
        IdentitySubjectKey? key = null;
        if (item.TryGetProperty("userId", out var subject) && subject.ValueKind != JsonValueKind.Null)
        {
            if (subject.ValueKind != JsonValueKind.String || subject.GetString() is not { Length: > 0 and <= 255 } value
                || value.Any(char.IsControl)) return IdentityEventReadItem.Malformed;
            try { key = IdentitySubjectKey.Compute(source.AuthorityId, value); }
            catch (ArgumentException) { return IdentityEventReadItem.Malformed; }
        }
        DateTimeOffset occurred;
        try { occurred = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds); }
        catch (ArgumentOutOfRangeException) { return IdentityEventReadItem.Malformed; }
        return IdentityEventReadItem.Observed(new(id, occurred, kind, key));
    }

    internal static IdentityEventSourcePolicy? ReadPolicy(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (!UniqueObject(root) || !root.TryGetProperty("eventsEnabled", out var enabled) || enabled.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("eventsExpiration", out var retention) || retention.ValueKind != JsonValueKind.Number || !retention.TryGetInt32(out var seconds)
                || seconds is < 604800 or > 2592000 || !root.TryGetProperty("enabledEventTypes", out var types)
                || types.ValueKind != JsonValueKind.Array || types.GetArrayLength() > 256)
                return null;
            var saved = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in types.EnumerateArray())
                if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 and <= 128 } code
                    || !saved.Add(code)) return null;
            return IdentityEventKindCodes.RequiredCodes.All(saved.Contains) ? new(seconds) : null;
        }
        catch (JsonException) { return null; }
    }

    internal static bool Text(JsonElement item, string property, out string? value)
    {
        value = null;
        if (!item.TryGetProperty(property, out var field) || field.ValueKind != JsonValueKind.String) return false;
        value = field.GetString();
        return value is not null;
    }

    internal static bool UniqueObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
            if (names.Count >= 32 || !names.Add(field.Name)) return false;
        return true;
    }
}
