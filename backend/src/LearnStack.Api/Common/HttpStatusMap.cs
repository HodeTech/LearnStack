using System.Net;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Results;

namespace LearnStack.Api.Common;

/// <summary>
/// Maps an <see cref="Error.Code"/> (or a <see cref="LearnStackException"/>
/// subclass) to the HTTP status the API surface returns. The table mirrors
/// <see href="../../../../docs/standards/09-error-handling.md">Standards 09
/// § Result Type</see>; adding a new error code requires updating both
/// places so the contract stays in sync.
/// </summary>
public static class HttpStatusMap
{
    public static int For(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return For(error.Code);
    }

    public static int For(string code) => code switch
    {
        "validation_failed" => (int)HttpStatusCode.BadRequest,
        "unsupported_locale" => (int)HttpStatusCode.BadRequest,
        "unauthorized" => (int)HttpStatusCode.Unauthorized,
        "forbidden" => (int)HttpStatusCode.Forbidden,
        "resource_scope_violation" => (int)HttpStatusCode.Forbidden,
        "feature_disabled" => (int)HttpStatusCode.Forbidden,
        "not_found" => (int)HttpStatusCode.NotFound,
        "method_not_allowed" => (int)HttpStatusCode.MethodNotAllowed,
        "unsupported_media_type" => (int)HttpStatusCode.UnsupportedMediaType,
        "tenant_mismatch" => (int)HttpStatusCode.NotFound,
        "concurrency_conflict" => (int)HttpStatusCode.Conflict,
        "business_rule_violation" => (int)HttpStatusCode.Conflict,
        "recording_consent_required" => (int)HttpStatusCode.Conflict,
        "rate_limited" => (int)HttpStatusCode.TooManyRequests,
        "dependency_unavailable" => (int)HttpStatusCode.ServiceUnavailable,
        _ => (int)HttpStatusCode.InternalServerError,
    };

    /// <summary>
    /// The code a bodyless client error is reported under. The inverse of
    /// <see cref="For(string)"/>, for the statuses the framework can produce
    /// without ever reaching a handler — an unmatched route, a wrong method, an
    /// unsupported media type. Standards 04 § Error Responses admits one error
    /// shape; without this, three of them arrive with no body at all.
    /// </summary>
    /// <remarks>
    /// 409 maps to <c>concurrency_conflict</c> because that is the only one of
    /// its three codes a framework-level 409 could plausibly mean. The others
    /// (<c>business_rule_violation</c>, <c>recording_consent_required</c>) are
    /// carried by a handler that always supplies its own body.
    /// </remarks>
    public static string CanonicalCodeFor(int status) => status switch
    {
        (int)HttpStatusCode.BadRequest => "validation_failed",
        (int)HttpStatusCode.Unauthorized => "unauthorized",
        (int)HttpStatusCode.Forbidden => "forbidden",
        (int)HttpStatusCode.NotFound => "not_found",
        (int)HttpStatusCode.MethodNotAllowed => "method_not_allowed",
        (int)HttpStatusCode.Conflict => "concurrency_conflict",
        (int)HttpStatusCode.Gone => "api_version_sunset",
        (int)HttpStatusCode.UnsupportedMediaType => "unsupported_media_type",
        (int)HttpStatusCode.TooManyRequests => "rate_limited",
        (int)HttpStatusCode.ServiceUnavailable => "dependency_unavailable",
        _ => "internal_error",
    };

    public static int For(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            // 499 "client closed request" is an Nginx convention, not an
            // IETF code — Standards 09 § Result Type does not pin a status
            // for client disconnects. We pick 499 (rather than 408 / 503 /
            // 500) because:
            //   * IIS, Nginx, Envoy, and APISIX all emit 499 for pre-
            //     response client aborts; log dashboards and SLO calculators
            //     already treat it as "not our fault".
            //   * 408 implies a server-side timeout (we did not time out —
            //     the client left).
            //   * 5xx codes would inflate error-budget metrics and trip
            //     PagerDuty rotations for nothing.
            // L1 handler skips both Sentry capture and the response body
            // write for OperationCanceled per ADR-0032 § Sub-decision 7;
            // the status is set here for parity with the upstream proxy's
            // behaviour. If a future ADR pins a different code, change
            // this line.
            OperationCanceledException => 499,

            // Every LearnStackException carries a structured Error; the HTTP
            // status is derived from that Error.Code so the response status
            // and the Problem Details `code` field can NEVER disagree. In
            // particular `ProviderException.IsClientError` is an
            // observability concern (it gates Sentry capture in
            // ShouldCapture), NOT an HTTP-status concern: a bare provider
            // failure carries `dependency_unavailable` → 503, and an adapter
            // that wants to surface a provider 4xx as a client-actionable
            // status passes an explicit Error (e.g. validation_failed → 400).
            // Deriving from the code keeps body+status consistent for both.
            LearnStackException known => For(known.Error),
            _ => (int)HttpStatusCode.InternalServerError,
        };
    }
}
