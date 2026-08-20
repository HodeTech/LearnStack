using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Api.Common;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace LearnStack.Tests.Integration;

/// <summary>
/// The request-body bound, per
/// <see href="../../../docs/standards/04-api-design.md">Standards 04
/// § Request and Response Limits</see>.
/// </summary>
/// <remarks>
/// These run under <c>TestServer</c>, which implements neither
/// <c>IHttpMaxRequestBodySizeFeature</c> nor
/// <c>IHttpRequestBodySizeFeature</c> — measured, an action carrying
/// <c>[RequestSizeLimit(1024)]</c> accepts a 5000-byte body there and logs that
/// the server does not support the feature. That is precisely why the
/// authoritative bound is middleware: a limit only Kestrel enforces is a limit
/// this suite cannot assert, and one nothing asserts is one a refactor can
/// delete in silence.
/// </remarks>
public sealed class RequestBodyLimitHttpTests : IDisposable
{
    private readonly ProbeHostFixture _host = new(typeof(BodyProbeController));

    [Fact]
    public async Task A_Body_Under_The_Limit_Is_Accepted()
    {
        using var client = _host.CreateClient();

        var response = await client.PostAsync(
            Path, Json(1024));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_Body_At_The_Limit_Is_Accepted()
    {
        using var client = _host.CreateClient();

        // The bound is "more than", not "at least" — an exactly-1 MiB body is
        // inside a 1 MiB limit, and an off-by-one here rejects a request the
        // published table promises to accept.
        var response = await client.PostAsync(
            Path, Exactly(RequestBodyLimit.MaxBytes));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_Declared_Oversized_Body_Is_Refused_Without_Being_Read()
    {
        using var client = _host.CreateClient();
        BodyProbeController.Reset();

        var response = await client.PostAsync(
            Path, Json(RequestBodyLimit.MaxBytes + 1));

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        BodyProbeController.Invocations.Should().Be(0,
            "a Content-Length over the limit is refused before anything reads the body");
    }

    [Fact]
    public async Task An_Undeclared_Oversized_Body_Is_Refused_As_It_Is_Read()
    {
        // Transfer-Encoding: chunked arrives with no Content-Length, so a guard
        // that only inspects the header lets the whole thing through. This is
        // the case the counting stream exists for.
        using var client = _host.CreateClient();
        BodyProbeController.Reset();

        var response = await PostUndeclaredAsync(client, RequestBodyLimit.MaxBytes + 4096);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task An_Undeclared_Body_Under_The_Limit_Still_Reaches_The_Action()
    {
        // The counting stream must be a pass-through, not a filter: wrapping the
        // body must not change what the action reads.
        using var client = _host.CreateClient();
        BodyProbeController.Reset();

        var response = await PostUndeclaredAsync(client, 4096);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("read").GetInt32().Should().Be(4096,
                "every byte the client sent must reach the action");
    }

    [Fact]
    public async Task The_413_Carries_The_One_Error_Shape()
    {
        // Standards 04 § Error Responses admits exactly one shape. The limit
        // middleware sets a status and writes nothing, so this asserts that
        // MapLearnStackClientErrors — registered above it — still supplies the
        // body, rather than a second writer being needed here.
        using var client = _host.CreateClient();

        var response = await client.PostAsync(
            Path, Json(RequestBodyLimit.MaxBytes + 1));

        response.Content.Headers.ContentType!.ToString()
            .Should().Be(ProblemDetailsMediaType.Value);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("code").GetString().Should().Be("payload_too_large");
        problem.GetProperty("messageKey").GetString().Should().Be("lockey_payload_too_large");
        problem.GetProperty("status").GetInt32().Should().Be(413);
        problem.TryGetProperty("correlationId", out _).Should().BeTrue();
    }

    public void Dispose() => _host.Dispose();

    private static Uri Path => new("/api/v1/bodyprobe", UriKind.Relative);

    private static StringContent Json(long bytes) =>
        new("\"" + new string('x', (int)bytes - 2) + "\"", Encoding.UTF8, "application/json");

    private static StringContent Exactly(long bytes) => Json(bytes);

    /// <summary>
    /// Posts a body the request declares no length for, so the counting stream —
    /// not the header check — is what has to catch it.
    /// </summary>
    /// <remarks>
    /// Both halves are required, and each was measured. Setting
    /// <c>Headers.ContentLength = null</c> on a <see cref="StreamContent"/> does
    /// not work: it clears the assigned value and the getter falls back to
    /// <c>TryComputeLength()</c>, which succeeds for a seekable stream — the
    /// first version of this class did that, and every one of its requests
    /// arrived with a <c>Content-Length</c>, so <c>CountedStream</c> was never
    /// constructed and deleting it left all six tests green. An
    /// <see cref="HttpContent"/> that refuses to compute a length is still not
    /// enough on its own under <c>TestServer</c>; the request must also ask for
    /// chunked. Together they produce what the middleware sees as
    /// <c>ContentLength=null, Transfer-Encoding: chunked</c>.
    /// </remarks>
    private static Task<HttpResponseMessage> PostUndeclaredAsync(HttpClient client, long bytes)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Path)
        {
            Content = new UndeclaredContent(bytes),
        };
        request.Headers.TransferEncodingChunked = true;

        return client.SendAsync(request);
    }

    /// <summary>Content that genuinely refuses to declare a length.</summary>
    private sealed class UndeclaredContent(long bytes) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(
            Stream stream, TransportContext? context)
        {
            var buffer = new byte[8192];
            for (var remaining = bytes; remaining > 0;)
            {
                var take = (int)Math.Min(buffer.Length, remaining);
                await stream.WriteAsync(buffer.AsMemory(0, take));
                remaining -= take;
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}

/// <summary>Reads its body to the end and reports how many bytes it saw.</summary>
public sealed class BodyProbeController : ApiControllerBase, ITestOnlyController
{
    private static int _invocations;

    public static int Invocations => _invocations;

    public static void Reset() => Interlocked.Exchange(ref _invocations, 0);

    [HttpPost]
    public async Task<IActionResult> Post()
    {
        Interlocked.Increment(ref _invocations);

        var read = 0;
        var buffer = new byte[8192];
        int n;
        while ((n = await Request.Body.ReadAsync(buffer, HttpContext.RequestAborted)) > 0)
        {
            read += n;
        }

        return Ok(new { read });
    }
}
