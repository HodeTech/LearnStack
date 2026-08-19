using LearnStack.Api.Common;
using LearnStack.SharedKernel.Idempotency;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace LearnStack.Api.Idempotency;

/// <summary>
/// Marks an action that requires an <c>Idempotency-Key</c> header, per
/// <see href="../../../../docs/standards/04-api-design.md">Standards 04
/// § Idempotency</see>.
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

    public bool IsReusable => false;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        return ActivatorUtilities.CreateInstance<IdempotencyFilter>(serviceProvider);
    }
}

/// <summary>
/// Claims the key, replays a completed response, refuses a concurrent one, and
/// records the result.
/// </summary>
/// <remarks>
/// A <b>resource</b> filter, not an action filter. An action filter runs before
/// the result is executed, so at that point the response body does not exist
/// yet and "store the response" would mean re-serialising the
/// <see cref="IActionResult"/> and hoping the second rendering matches the
/// first. A resource filter wraps result execution, so the bytes captured are
/// the bytes the client received.
/// </remarks>
internal sealed class IdempotencyFilter(
    IIdempotencyStore store,
    ITenantContext tenantContext) : IAsyncResourceFilter
{
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

        var (claim, stored) = await store
            .TryClaimAsync(tenantId, key, cancellationToken).ConfigureAwait(false);

        if (claim == IdempotencyClaim.Completed && stored is not null)
        {
            await ReplayAsync(context.HttpContext, stored, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (claim == IdempotencyClaim.InFlight)
        {
            // The first attempt has not finished. Answering 409 rather than
            // waiting keeps the server from holding a connection open for work
            // it cannot speed up, and tells the client the honest thing: ask
            // again.
            context.Result = new ProblemDetailsActionResult(
                new Error(new LocalizedMessage("lockey_concurrency_conflict")));
            return;
        }

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
            await store.AbandonAsync(tenantId, key, cancellationToken).ConfigureAwait(false);
            throw;
        }

        response.Body = original;
        buffer.Position = 0;
        await buffer.CopyToAsync(original, cancellationToken).ConfigureAwait(false);

        if (executed.Exception is not null && !executed.ExceptionHandled)
        {
            // Release rather than record. Storing a failure would make every
            // retry replay it for the retention window, turning one transient
            // fault into a day of them.
            await store.AbandonAsync(tenantId, key, cancellationToken).ConfigureAwait(false);
            return;
        }

        // A 5xx is not an outcome worth replaying either — the operation may or
        // may not have happened, and pinning the answer for 24 hours removes
        // the client's only way to find out.
        if (response.StatusCode >= 500)
        {
            await store.AbandonAsync(tenantId, key, cancellationToken).ConfigureAwait(false);
            return;
        }

        await store.CompleteAsync(
            tenantId,
            key,
            new IdempotentResponse(response.StatusCode, response.ContentType, buffer.ToArray()),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes the stored response, marked as a replay.</summary>
    private static async Task ReplayAsync(
        HttpContext context, IdempotentResponse stored, CancellationToken cancellationToken)
    {
        context.Response.StatusCode = stored.StatusCode;
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
}
