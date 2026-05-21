using System.Diagnostics;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace LearnStack.Api.Common;

/// <summary>
/// Builds RFC 7807 <see cref="ProblemDetails"/> bodies from the project's
/// <see cref="Error"/> / <see cref="LearnStackException"/> hierarchy.
/// Shape pinned by Standards 09 § API Surface — every API error response
/// carries <c>code</c>, <c>messageKey</c>, <c>correlationId</c>, optional
/// <c>errors</c>.
/// </summary>
public static class ProblemDetailsFactory
{
    private const string ProblemTypePrefix = "https://errors.learnstack.dev/";

    public static ProblemDetails For(Error error, HttpContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(error);

        var problem = BuildBase(error.Code, error.Message.Key, HttpStatusMap.For(error.Code), context);

        if (error.Details is { Count: > 0 })
        {
            problem.Extensions["errors"] = ProjectDetails(error.Details);
        }

        return problem;
    }

    public static ProblemDetails For(Exception exception, HttpContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is LearnStackException known)
        {
            return For(known.Error, context);
        }

        // Unhandled / unknown — surface a stable generic shape.
        return BuildBase(
            code: "internal_error",
            messageKey: "lockey_internal_error",
            status: HttpStatusMap.For(exception),
            context: context);
    }

    private static ProblemDetails BuildBase(string code, string messageKey, int status, HttpContext? context)
    {
        var problem = new ProblemDetails
        {
            Type = ProblemTypePrefix + code,
            Title = messageKey,
            Status = status,
            Instance = context?.Request.Path.Value,
        };

        problem.Extensions["code"] = code;
        problem.Extensions["messageKey"] = messageKey;
        problem.Extensions["correlationId"] = ResolveCorrelationId(context);
        return problem;
    }

    private static string? ResolveCorrelationId(HttpContext? context)
    {
        var traceId = Activity.Current?.TraceId.ToString();
        if (!string.IsNullOrWhiteSpace(traceId))
        {
            return traceId;
        }

        return context?.TraceIdentifier;
    }

    private static Dictionary<string, IReadOnlyList<object>> ProjectDetails(
        IReadOnlyDictionary<string, IReadOnlyList<LocalizedMessage>> details)
    {
        // Per Standards 09 § Validation Errors field names use the request's
        // camelCase shape on the wire. The validator typically returns
        // PascalCase property names; lower-case the first char so the
        // payload matches what the SDK expects without losing the source
        // information.
        var projected = new Dictionary<string, IReadOnlyList<object>>(StringComparer.Ordinal);
        foreach (var (key, list) in details)
        {
            projected[ToCamelCase(key)] = list
                .Select(m => (object)new
                {
                    key = m.Key,
                    @params = m.Params,
                })
                .ToArray();
        }

        return projected;
    }

    private static string ToCamelCase(string value)
    {
        if (string.IsNullOrEmpty(value) || char.IsLower(value[0]))
        {
            return value;
        }

        return char.ToLowerInvariant(value[0]) + value[1..];
    }
}
