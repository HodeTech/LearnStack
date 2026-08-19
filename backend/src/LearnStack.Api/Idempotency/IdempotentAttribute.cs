using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using LearnStack.Api.Common;
using LearnStack.SharedKernel.Idempotency;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace LearnStack.Api.Idempotency;

/// <summary>
/// Marks an action that requires an <c>Idempotency-Key</c> header, per
/// <see href="../../../../docs/standards/04-api-design.md">Standards 04
/// § Idempotency</see> and
/// <see href="../../../../docs/decisions/0037-idempotency-key-contract.md">ADR-0037</see>.
/// </summary>
/// <remarks>
/// <para>
/// Standards 04 requires it for payment operations, webhook processing,
/// notification sending and recording start/stop, and encourages it for
/// enrollment creation and invitation sending. None of those endpoints exists
/// yet — this ships the mechanism so the first one is a one-attribute change
/// rather than a design.
/// </para>
/// <para>
/// An attribute rather than blanket middleware, because "which operations have
/// external side effects" is knowledge the endpoint has and the pipeline does
/// not. Requiring the header everywhere would make every caller carry one for
/// operations that never needed it; requiring it nowhere is where we started.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, Inherited = true)]
public sealed class IdempotentAttribute : Attribute, IFilterFactory
{
    /// <summary>The header a client supplies its key in.</summary>
    public const string HeaderName = "Idempotency-Key";

    /// <summary>
    /// The <c>errors</c> map key a key failure is reported under.
    /// </summary>
    /// <remarks>
    /// Not the literal header name. <c>ProblemDetailsFactory</c> camelCases
    /// every <c>errors</c> key, and camelCasing <c>Idempotency-Key</c> produces
    /// <c>idempotency-Key</c> — a spelling that is neither the header nor
    /// anything else. The camelCase form is what the map holds throughout
    /// (<c>limit</c>, <c>sort</c>), so this is the one that survives the
    /// projection unchanged and reads as the same thing.
    /// </remarks>
    public const string ErrorsKey = "idempotencyKey";

    /// <summary>
    /// Set on a replayed response. A client retrying after a timeout otherwise
    /// cannot tell whether its second call did the work or collected the first
    /// one's answer.
    /// </summary>
    public const string ReplayedHeaderName = "Idempotency-Replayed";

    /// <summary>
    /// Bounds on the client-chosen key. Standards 04's example is a ULID; the
    /// range accepts that and a UUID without pinning either, and the character
    /// class keeps a key that reaches a log or a database column from carrying
    /// anything but printable ASCII.
    /// </summary>
    public const int MinKeyLength = 8;

    public const int MaxKeyLength = 128;

    /// <summary>
    /// The largest response body that is stored for replay.
    /// </summary>
    /// <remarks>
    /// A stored response is held for the whole retention window, so the cap is
    /// what keeps the store's entry ceiling from being a memory ceiling only in
    /// name. An <c>[Idempotent]</c> endpoint answers with the outcome of an
    /// operation — an identifier, a receipt, a status — and 256 KiB is far more
    /// than any of those. Exceeding it is a server-side mistake, so it is logged
    /// as one rather than silently truncated.
    /// </remarks>
    public const int MaxStoredResponseBytes = 256 * 1024;

    public bool IsReusable => false;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        return ActivatorUtilities.CreateInstance<IdempotencyFilter>(serviceProvider);
    }
}

/// <summary>
/// Claims the key, replays a completed response, refuses a concurrent or
/// mismatched one, and records the result.
/// </summary>
/// <remarks>
/// A <b>resource</b> filter, not an action filter. An action filter runs before
/// the result is executed, so at that point the response body does not exist
/// yet and "store the response" would mean re-serialising the
/// <see cref="IActionResult"/> and hoping the second rendering matches the
/// first. A resource filter wraps result execution, so the bytes captured are
/// the bytes the client received.
/// </remarks>
internal sealed partial class IdempotencyFilter(
    IIdempotencyStore store,
    ITenantContext tenantContext,
    ILogger<IdempotencyFilter> logger) : IAsyncResourceFilter
{
    /// <summary>
    /// Response headers that are never replayed.
    /// </summary>
    /// <remarks>
    /// Everything here describes <b>this</b> exchange rather than the operation's
    /// outcome: the framing headers are recomputed for the new body,
    /// <c>Set-Cookie</c> is bound to the first attempt's session rather than to
    /// the work it did, and the correlation id belongs to the request that is
    /// asking now — replaying the old one would point a support engineer at the
    /// wrong trace. Everything else is reproduced, because a <c>201</c> without
    /// its <c>Location</c> is not the same answer.
    /// </remarks>
    private static readonly HashSet<string> NonReplayableHeaders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Content-Length",
            "Transfer-Encoding",
            "Connection",
            "Keep-Alive",
            "Upgrade",
            "Trailer",
            "Date",
            "Server",
            "Set-Cookie",
            CorrelationHeaderMiddleware.HeaderName,
            IdempotentAttribute.ReplayedHeaderName,
        };

    /// <summary>
    /// Statuses that are released rather than recorded, because they describe a
    /// condition rather than an outcome: the operation may or may not have run,
    /// and pinning the answer for the retention window removes the client's only
    /// way to find out. 5xx is handled separately, on the same reasoning.
    /// </summary>
    private static readonly HashSet<int> RetryableStatuses =
    [
        StatusCodes.Status408RequestTimeout,
        425, // Too Early (RFC 8470); StatusCodes has no constant for it.
        StatusCodes.Status429TooManyRequests,
    ];

    public async Task OnResourceExecutionAsync(
        ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (!TryReadKey(context.HttpContext, out var key))
        {
            context.Result = new ProblemDetailsActionResult(new Error(
                new LocalizedMessage("lockey_validation_failed"),
                new Dictionary<string, IReadOnlyList<LocalizedMessage>>(StringComparer.Ordinal)
                {
                    [IdempotentAttribute.ErrorsKey] =
                        [new LocalizedMessage("lockey_idempotency_key_invalid")],
                }));
            return;
        }

        // No tenant, no key space. The key is client-chosen, so a flat space
        // would let one tenant's retry collect another tenant's response body.
        // A request with no resolved tenant is already refused downstream;
        // refusing here means the store is never asked a question it cannot
        // scope.
        if (!tenantContext.IsResolved)
        {
            context.Result = new StatusCodeResult(StatusCodes.Status404NotFound);
            return;
        }

        var tenantId = tenantContext.TenantId;
        var cancellationToken = context.HttpContext.RequestAborted;
        var fingerprint = await ComputeFingerprintAsync(context.HttpContext, cancellationToken)
            .ConfigureAwait(false);

        var claim = await store
            .TryClaimAsync(tenantId, key, fingerprint, cancellationToken).ConfigureAwait(false);

        switch (claim.Outcome)
        {
            case IdempotencyClaim.Completed when claim.Stored is not null:
                await ReplayAsync(context.HttpContext, claim.Stored, cancellationToken)
                    .ConfigureAwait(false);
                return;

            case IdempotencyClaim.InFlight:
                // The first attempt has not finished. Answering rather than
                // waiting keeps the server from holding a connection open for
                // work it cannot speed up, and tells the client the honest
                // thing: ask again, with this same key.
                context.Result = new ProblemDetailsActionResult(
                    new Error(new LocalizedMessage("lockey_request_in_progress")));
                return;

            case IdempotencyClaim.Mismatched:
                // Same key, different request. Replaying would answer a question
                // the client did not ask; running would defeat the key. Both are
                // silent failures, so neither is chosen for it.
                context.Result = new ProblemDetailsActionResult(new Error(
                    new LocalizedMessage("lockey_idempotency_key_reuse"),
                    new Dictionary<string, IReadOnlyList<LocalizedMessage>>(StringComparer.Ordinal)
                    {
                        [IdempotentAttribute.ErrorsKey] =
                            [new LocalizedMessage("lockey_idempotency_key_reuse")],
                    }));
                return;

            default:
                break;
        }

        await RunAndRecordAsync(context, next, tenantId, key, claim.Token, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RunAndRecordAsync(
        ResourceExecutingContext context,
        ResourceExecutionDelegate next,
        Guid tenantId,
        string key,
        Guid token,
        CancellationToken cancellationToken)
    {
        var response = context.HttpContext.Response;
        var original = response.Body;
        using var buffer = new MemoryStream();
        response.Body = buffer;

        ResourceExecutedContext executed;
        try
        {
            executed = await next().ConfigureAwait(false);
        }
        catch
        {
            response.Body = original;
            await AbandonAsync(tenantId, key, token).ConfigureAwait(false);
            throw;
        }

        response.Body = original;

        // Record BEFORE delivering, and with a token that does not follow the
        // connection. A client that disconnects at exactly the moment its
        // operation completes is the case idempotency exists for: it will retry.
        // Recording after the copy — or under RequestAborted — loses the answer
        // precisely then, and the retry re-runs the work.
        if (ShouldRecord(executed, response, buffer.Length))
        {
            await store.CompleteAsync(
                tenantId,
                key,
                token,
                new IdempotentResponse(
                    response.StatusCode,
                    response.ContentType,
                    CaptureHeaders(response),
                    buffer.ToArray()),
                CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            await AbandonAsync(tenantId, key, token).ConfigureAwait(false);
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(original, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether this outcome is worth replaying for the retention window.</summary>
    private bool ShouldRecord(ResourceExecutedContext executed, HttpResponse response, long bodyLength)
    {
        // Release rather than record. Storing a failure would make every retry
        // replay it for the retention window, turning one transient fault into a
        // day of them.
        if (executed.Exception is not null && !executed.ExceptionHandled)
        {
            return false;
        }

        // A 5xx is not an outcome worth replaying either — the operation may or
        // may not have happened, and pinning the answer removes the client's
        // only way to find out.
        if (response.StatusCode >= 500 || RetryableStatuses.Contains(response.StatusCode))
        {
            return false;
        }

        if (bodyLength > IdempotentAttribute.MaxStoredResponseBytes)
        {
            ResponseTooLargeToReplay(logger, bodyLength, IdempotentAttribute.MaxStoredResponseBytes);
            return false;
        }

        return true;
    }

    private Task AbandonAsync(Guid tenantId, string key, Guid token) =>
        store.AbandonAsync(tenantId, key, token, CancellationToken.None);

    /// <summary>
    /// Digests everything that must match for a replay to be the same answer:
    /// who is asking, what they are asking of, and with what.
    /// </summary>
    /// <remarks>
    /// The key alone is not enough. It is chosen by the client, so two users in
    /// one tenant can pick the same one — and without the principal in the
    /// digest the second would be handed the first one's response body. The
    /// method, path and query bound the key to one endpoint; the body catches
    /// the classic client bug of reusing a key after editing the payload, which
    /// would otherwise be answered "that succeeded" about the wrong amount.
    /// </remarks>
    private async Task<string> ComputeFingerprintAsync(
        HttpContext context, CancellationToken cancellationToken)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        Append(digest, tenantContext.UserId?.ToString() ?? string.Empty);
        Append(digest, context.Request.Method);
        Append(digest, context.Request.Path.Value ?? string.Empty);
        Append(digest, context.Request.QueryString.Value ?? string.Empty);

        // Buffered so model binding can still read it. The resource filter runs
        // before binding, so this is the one place the body can be read without
        // taking it away from the action.
        context.Request.EnableBuffering();

        var rented = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            int read;
            while ((read = await context.Request.Body
                       .ReadAsync(rented.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                digest.AppendData(rented.AsSpan(0, read));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        context.Request.Body.Position = 0;
        return Convert.ToHexStringLower(digest.GetHashAndReset());

        // A separator no component can contain, so that ("ab", "c") and
        // ("a", "bc") are different requests rather than the same digest.
        static void Append(IncrementalHash target, string value)
        {
            target.AppendData(Encoding.UTF8.GetBytes(value));
            target.AppendData([0x1F]);
        }
    }

    /// <summary>Snapshots the response headers that describe the outcome.</summary>
    private static Dictionary<string, IReadOnlyList<string>> CaptureHeaders(
        HttpResponse response)
    {
        var captured = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, values) in response.Headers)
        {
            if (NonReplayableHeaders.Contains(name))
            {
                continue;
            }

            captured[name] = values.Where(value => value is not null).ToArray()!;
        }

        return captured;
    }

    /// <summary>Writes the stored response, marked as a replay.</summary>
    private static async Task ReplayAsync(
        HttpContext context, IdempotentResponse stored, CancellationToken cancellationToken)
    {
        context.Response.StatusCode = stored.StatusCode;

        foreach (var (name, values) in stored.Headers)
        {
            context.Response.Headers[name] = new StringValues([.. values]);
        }

        context.Response.Headers[IdempotentAttribute.ReplayedHeaderName] = "true";

        if (stored.ContentType is not null)
        {
            context.Response.ContentType = stored.ContentType;
        }

        if (stored.Body.Length > 0)
        {
            await context.Response.Body
                .WriteAsync(stored.Body, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool TryReadKey(HttpContext context, out string key)
    {
        key = string.Empty;
        var raw = context.Request.Headers[IdempotentAttribute.HeaderName];

        // Repeated is refused rather than resolved by first-or-last, for the
        // same reason every other header on this surface is.
        if (raw.Count != 1 || string.IsNullOrWhiteSpace(raw[0]))
        {
            return false;
        }

        var candidate = raw[0]!;
        if (candidate.Length is < IdempotentAttribute.MinKeyLength or > IdempotentAttribute.MaxKeyLength)
        {
            return false;
        }

        foreach (var character in candidate)
        {
            // Printable ASCII, no space. The key becomes a dictionary key today
            // and a database column in Packet 6; neither should ever hold a
            // control character a client chose.
            if (character is < '!' or > '~')
            {
                return false;
            }
        }

        key = candidate;
        return true;
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Error,
        Message = "An [Idempotent] endpoint answered with {ByteCount} bytes, over the {Limit}-byte "
            + "replay cap; the key was released and a retry will re-run the operation.")]
    private static partial void ResponseTooLargeToReplay(ILogger logger, long byteCount, int limit);
}
