using System.Text.Json;

namespace LearnStack.Api.Common;

internal static class EmptyNativeJsonBody
{
    internal static async Task<bool> ReadAsync(HttpContext http)
    {
        if (http.Request.ContentLength is > 512 || !http.Request.HasJsonContentType()) return false;
        var bytes = new byte[513];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await http.Request.Body.ReadAsync(bytes.AsMemory(count), http.RequestAborted);
            if (read == 0) break;
            count += read;
        }
        if (count is 0 or > 512) return false;
        try
        {
            using var json = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 2 });
            return json.RootElement.ValueKind == JsonValueKind.Object && !json.RootElement.EnumerateObject().Any();
        }
        catch (JsonException) { return false; }
    }
}
