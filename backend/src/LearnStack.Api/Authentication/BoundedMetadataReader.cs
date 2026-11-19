using System.Text;
using OpenTelemetry;

namespace LearnStack.Api.Authentication;

/// <summary>
/// Reads one identity-provider metadata document under a deadline and a byte cap, so a hostile
/// or broken issuer endpoint can neither hold a request nor fill memory.
/// </summary>
/// <remarks>
/// Both bearer schemes' stock configuration managers consume documents through this one
/// reader: the tenant scheme and the operator scheme (ADR-0101 § 2) each pass their own
/// client, timeout, cap and refusal text, and nothing else differs.
/// </remarks>
internal static class BoundedMetadataReader
{
    internal static async Task<string> ReadAsync(
        HttpClient client,
        Uri uri,
        TimeSpan timeout,
        int maximumBytes,
        string refusal,
        CancellationToken cancel)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(timeout);
        // Suppress metadata HTTP spans. The composition root separately projects
        // native HTTP/DNS metrics, which do not honor this request-local scope.
        using var suppression = SuppressInstrumentationScope.Begin();
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > maximumBytes)
        {
            throw new IOException(refusal);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var remaining = maximumBytes - (int)body.Length;
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining + 1)), deadline.Token);
            if (read == 0)
            {
                return Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length);
            }

            if (read > remaining)
            {
                throw new IOException(refusal);
            }

            body.Write(buffer, 0, read);
        }
    }
}
