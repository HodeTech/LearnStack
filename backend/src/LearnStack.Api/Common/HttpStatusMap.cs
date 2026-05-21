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
        "tenant_mismatch" => (int)HttpStatusCode.NotFound,
        "concurrency_conflict" => (int)HttpStatusCode.Conflict,
        "business_rule_violation" => (int)HttpStatusCode.Conflict,
        "recording_consent_required" => (int)HttpStatusCode.Conflict,
        "rate_limited" => (int)HttpStatusCode.TooManyRequests,
        "dependency_unavailable" => (int)HttpStatusCode.ServiceUnavailable,
        _ => (int)HttpStatusCode.InternalServerError,
    };

    public static int For(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            OperationCanceledException => 499, // client closed request
            ProviderException pex when pex.IsClientError => (int)HttpStatusCode.BadRequest,
            ProviderException => (int)HttpStatusCode.ServiceUnavailable,
            InfrastructureException => (int)HttpStatusCode.ServiceUnavailable,
            TenantContextMissingException => (int)HttpStatusCode.NotFound,
            LearnStackException known => For(known.Error),
            _ => (int)HttpStatusCode.InternalServerError,
        };
    }
}
