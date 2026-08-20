using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace LearnStack.Api.Common;

/// <summary>
/// Bounds the request body at the size
/// <see href="../../../../docs/standards/04-api-design.md">Standards 04
/// § Request and Response Limits</see> publishes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Middleware, not <c>KestrelServerLimits.MaxRequestBodySize</c>, and not
/// <c>[RequestSizeLimit]</c>.</b> Both of those are server features, and the
/// integration suite runs on <c>TestServer</c>, which implements neither:
/// measured, an action carrying <c>[RequestSizeLimit(1024)]</c> accepts a
/// 5000-byte body under <c>WebApplicationFactory</c> and logs
/// "This server does not support the IHttpMaxRequestBodySizeFeature". A bound
/// that cannot be asserted is a bound that can be deleted without anything
/// going red, which is how a published limit becomes fiction — the condition
/// this packet found the whole limits table in.
/// </para>
/// <para>
/// Kestrel's own limit is still set, to the same number, at the composition
/// root. This one is authoritative and testable; that one tears the connection
/// down before the bytes are buffered. Two bounds, one number, in that order —
/// not two numbers competing.
/// </para>
/// <para>
/// A declared <c>Content-Length</c> over the limit is refused without reading
/// anything. A request that declares nothing — <c>Transfer-Encoding:
/// chunked</c> — is counted as it is read, because a body with no declared
/// length slips any guard that only inspects the header.
/// </para>
/// </remarks>
public static class RequestBodyLimit
{
    /// <summary>
    /// Standards 04 § Request and Response Limits: "Request body (JSON) — 1 MB".
    /// Binary, so the number in the table and the number in the code are the
    /// same quantity rather than 4.9% apart.
    /// </summary>
    public const long MaxBytes = 1024 * 1024;

    /// <summary>
    /// The outer Kestrel bound, deliberately <b>above</b> <see cref="MaxBytes"/>.
    /// </summary>
    /// <remarks>
    /// The two do not count the same quantity: this middleware counts decoded
    /// payload bytes, and Kestrel counts raw bytes off the wire — chunk headers,
    /// per-chunk CRLFs and the terminating chunk included. Setting them equal
    /// makes Kestrel strictly tighter for a chunked body, so the bound that
    /// fires is the one that counts the quantity the standard does not publish.
    /// Measured against real Kestrel with both set to 1 MiB: a 1 MiB payload in
    /// 64 KiB chunks is 413, and so is a <b>762 KB</b> payload sent in 16-byte
    /// chunks, because its framing alone crosses the line.
    /// <para>
    /// Four times is headroom, not a proof. Chunked framing costs a fixed
    /// overhead per chunk, so a sufficiently pathological chunking still crosses
    /// Kestrel's line first: at one byte per chunk the wire cost is six bytes per
    /// payload byte, and a payload over roughly <b>683 KiB</b> is refused by
    /// Kestrel rather than by the middleware. That case is accepted rather than
    /// chased. The client still gets a 413 — the right status, without the
    /// Problem Details body — and no legitimate client frames a 683 KiB payload
    /// one byte at a time; one that does is a denial-of-service shape in its own
    /// right, which is exactly what a backstop is for. Raising the multiplier
    /// only moves the threshold; nothing finite removes it.
    /// </para>
    /// </remarks>
    public const long KestrelBackstopBytes = MaxBytes * 4;

    /// <summary>
    /// Refuses an oversized body with <b>413</b>.
    /// </summary>
    /// <remarks>
    /// Registered <b>below</b> <c>MapLearnStackClientErrors</c>, so the status
    /// set here acquires the one Problem Details shape on the way out rather
    /// than growing a second writer beside it — the same route the rate
    /// limiter's 429 takes. Registered <b>after</b> the rate limiter, so a
    /// client flooding oversized bodies is told it is being rate limited, which
    /// is the more useful of the two answers and the cheaper one to produce.
    /// </remarks>
    public static WebApplication UseLearnStackRequestBodyLimit(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.Use(async (context, next) =>
        {
            if (context.Request.ContentLength is > MaxBytes)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }

            if (context.Request.ContentLength is null)
            {
                context.Request.Body = new CountedStream(context.Request.Body, MaxBytes);
            }

            await next(context).ConfigureAwait(false);
        });

        return app;
    }

    /// <summary>
    /// A read-only pass-through that refuses to hand out more than
    /// <paramref name="limit"/> bytes.
    /// </summary>
    /// <remarks>
    /// Throws <see cref="BadHttpRequestException"/> with 413 rather than
    /// truncating, because a truncated body reaches the deserializer as a
    /// malformed one and the client is told its JSON is invalid when the real
    /// answer is that it was too big. That is also the exception Kestrel itself
    /// throws for this condition, so <c>HttpStatusMap.For(Exception)</c> already
    /// carries it to the right status.
    /// </remarks>
    private sealed class CountedStream(Stream inner, long limit) : Stream
    {
        private long _read;

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Count(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

        public override async Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Count(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken)
                .ConfigureAwait(false));

        /// <summary>
        /// The legacy APM pair, bridged to the async path.
        /// </summary>
        /// <remarks>
        /// <see cref="Stream"/>'s default <c>BeginRead</c> invokes the
        /// <b>synchronous</b> <see cref="Read(byte[], int, int)"/> on a pool
        /// thread, and ASP.NET refuses synchronous reads of a request body —
        /// measured, a five-byte chunked body through
        /// <c>Task.Factory.FromAsync(req.Body.BeginRead, …)</c> threw
        /// "Synchronous operations are disallowed" and became a 500. Wrapping
        /// this type must not take an API away from a caller that had it.
        /// </remarks>
        public override IAsyncResult BeginRead(
            byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
        {
            var completion = new TaskCompletionSource<int>(
                state, TaskCreationOptions.RunContinuationsAsynchronously);

            _ = Complete(completion, callback, ReadAsync(buffer, offset, count, default));

            return completion.Task;

            static async Task Complete(
                TaskCompletionSource<int> completion, AsyncCallback? callback, Task<int> read)
            {
                try
                {
                    completion.TrySetResult(await read.ConfigureAwait(false));
                }
                catch (OperationCanceledException)
                {
                    completion.TrySetCanceled();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }

                callback?.Invoke(completion.Task);
            }
        }

        public override int EndRead(IAsyncResult asyncResult)
        {
            ArgumentNullException.ThrowIfNull(asyncResult);
            return ((Task<int>)asyncResult).GetAwaiter().GetResult();
        }

        public override void Flush() => inner.Flush();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private int Count(int read)
        {
            _read += read;

            if (_read > limit)
            {
                throw new BadHttpRequestException(
                    $"Request body exceeds the {limit}-byte limit.",
                    StatusCodes.Status413PayloadTooLarge);
            }

            return read;
        }
    }
}
