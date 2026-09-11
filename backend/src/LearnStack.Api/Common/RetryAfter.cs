using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace LearnStack.Api.Common;

/// <summary>
/// The <c>Retry-After</c> every 503 carries, set on both Problem Details paths.
/// </summary>
/// <remarks>
/// <para>
/// API Design § Status Codes requires the header on a 503. Two codes answer 503 today, over
/// both paths: <c>audit_unavailable</c> and <c>dependency_unavailable</c> as exceptions through
/// <see cref="LearnStackExceptionHandler"/>, and the idempotency store's capacity refusal —
/// <c>dependency_unavailable</c> again — as a result through
/// <see cref="ProblemDetailsActionResult"/>. None carried it, while Error Handling said
/// Packet 9 had set it (the fifth review of Packet 9). It is set where the status is decided
/// rather than where each code is minted, so a third 503 code inherits it.
/// </para>
/// <para>
/// Thirty seconds: the provider circuit breaker's default break duration
/// (<c>ResilienceOptions.CircuitBreaker.BreakDurationSeconds</c>), so a client that honours
/// the header does not return while the breaker that refused it is still open. It is a hint
/// about when a retry is worth making, not a promise the dependency is back by then.
/// </para>
/// </remarks>
internal static class RetryAfter
{
    public const int ServiceUnavailableSeconds = 30;

    public static void Apply(HttpResponse response, int? status)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (status == StatusCodes.Status503ServiceUnavailable)
        {
            response.Headers.RetryAfter = ServiceUnavailableSeconds.ToString(CultureInfo.InvariantCulture);
        }
    }
}
