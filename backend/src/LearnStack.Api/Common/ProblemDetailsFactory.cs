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
/// <remarks>
/// <see cref="ProblemDetails.Title"/> intentionally carries the
/// <c>lockey_*</c> localization key (matches the Standards 09 § API Surface
/// example). The frontend resolves the key against its i18n catalogue; the
/// wire value is stable across locales so support staff debugging in
/// Insomnia / curl can match the lockey back to the catalogue entry. A
/// future LocalizedMessage → text projector may compose a human-readable
/// Title alongside; locale negotiation from <c>Accept-Language</c> is
/// <see href="../../../../docs/roadmap/phase-04-cms-media-pages.md">Phase 04</see>'s,
/// which is what
/// <see href="../../../../docs/roadmap/phase-02d-walking-skeleton.md">Phase 02d</see>
/// names when it lists what it does not build.
/// </remarks>
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

        // Status MUST come from HttpStatusMap.For(Exception) so
        // ProviderException(IsClientError = true) returns 400 instead of
        // falling through to the carried Error.Code's 503 default. Code +
        // messageKey still come from the carried Error so the wire shape
        // stays consistent. Standards 09 § Provider Failures + ADR-0032
        // § Sub-decision 7.
        var status = HttpStatusMap.For(exception);

        if (exception is LearnStackException known)
        {
            var problem = BuildBase(known.Error.Code, known.Error.Message.Key, status, context);
            if (known.Error.Details is { Count: > 0 })
            {
                problem.Extensions["errors"] = ProjectDetails(known.Error.Details);
            }
            return problem;
        }

        // Unhandled / unknown — mint the code from the status, exactly as
        // ForStatus does for a framework error with no exception at all.
        //
        // This used to hardcode `internal_error`, which was harmless only
        // while every such exception also produced a 500. Honouring
        // BadHttpRequestException's own status broke that: an oversized upload
        // became `status: 413` with `code: "internal_error"` — a body whose
        // two halves contradict each other, which is precisely what
        // CanonicalCodeFor exists to make impossible. A 500 still yields
        // `internal_error`, because that is what CanonicalCodeFor returns for
        // it.
        var unmapped = HttpStatusMap.CanonicalCodeFor(status);
        return BuildBase(
            code: unmapped,
            messageKey: LocalizedMessage.RequiredPrefix + unmapped,
            status: status,
            context: context);
    }

    /// <summary>
    /// Builds the body for a client error the framework produced without a
    /// handler — an unmatched route, a wrong method, an unsupported media
    /// type. There is no <see cref="Error"/> to carry, so the code comes from
    /// the status and the message key from the code.
    /// </summary>
    public static ProblemDetails ForStatus(int status, HttpContext? context = null)
    {
        var code = HttpStatusMap.CanonicalCodeFor(status);
        return BuildBase(code, LocalizedMessage.RequiredPrefix + code, status, context);
    }

    private static ProblemDetails BuildBase(string code, string messageKey, int status, HttpContext? context)
    {
        var problem = new ProblemDetails
        {
            // Standards 09 § API Surface example uses the short slug
            // (e.g. /validation, not /validation_failed). Strip the
            // trailing _failed when present so the URL stays clean across
            // codes; other codes ride through unchanged.
            Type = ProblemTypePrefix + TrimFailedSuffix(code),
            Title = messageKey,
            Status = status,
            Instance = context?.Request.Path.Value,
        };

        problem.Extensions["code"] = code;
        problem.Extensions["messageKey"] = messageKey;
        problem.Extensions["correlationId"] = ResolveCorrelationId(context);
        return problem;
    }

    private static string TrimFailedSuffix(string code) =>
        code.EndsWith("_failed", StringComparison.Ordinal)
            ? code[..^"_failed".Length]
            : code;

    private static string? ResolveCorrelationId(HttpContext? context)
    {
        // Activity.Current.Id is the full W3C traceparent
        // (00-trace-span-flags) — matches the ITenantContext.CorrelationId
        // contract and what the L1 handler tags Sentry / LocalFile captures
        // with, so the Problem Details body and the captured error share one
        // handle. TraceId alone is only the 32-hex trace component.
        var traceParent = Activity.Current?.Id;
        if (!string.IsNullOrWhiteSpace(traceParent))
        {
            return traceParent;
        }

        return context?.TraceIdentifier;
    }

    private static Dictionary<string, IReadOnlyList<object>> ProjectDetails(
        IReadOnlyDictionary<string, IReadOnlyList<LocalizedMessage>> details)
    {
        // Two distinct source keys can normalize to the same camelCase key
        // (e.g. "UserId" and "UserID" both project to "userId") — merge
        // their messages instead of letting the later key win and silently
        // drop the earlier one's entries.
        var merged = new Dictionary<string, List<object>>(StringComparer.Ordinal);
        foreach (var (key, list) in details)
        {
            var camelKey = ToCamelCase(key);
            if (!merged.TryGetValue(camelKey, out var messages))
            {
                messages = [];
                merged[camelKey] = messages;
            }

            // A message with no parameters emits `{ "key": ... }` and nothing
            // else. Standards 09's canonical body shows exactly that; emitting
            // `"params": null` alongside it publishes a field whose only value
            // is "absent" and makes the SDK type it as nullable for no reason.
            messages.AddRange(list.Select(m => m.Params is { Count: > 0 }
                ? (object)new { key = m.Key, @params = m.Params }
                : new { key = m.Key }));
        }

        return merged.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<object>)kv.Value,
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Projects FluentValidation property paths to the request's camelCase
    /// shape per Standards 09 § Validation Errors. Handles nested paths
    /// (<c>Address.Street</c> → <c>address.street</c>) and acronyms via
    /// <see cref="System.Text.Json.JsonNamingPolicy.CamelCase"/>, which
    /// lowercases only the leading run of uppercase letters
    /// (<c>URLValue</c> → <c>urlValue</c>).
    /// </summary>
    private static string ToCamelCase(string propertyPath)
    {
        if (string.IsNullOrEmpty(propertyPath))
        {
            return propertyPath;
        }

        if (!propertyPath.Contains('.', StringComparison.Ordinal))
        {
            return System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(propertyPath);
        }

        var segments = propertyPath.Split('.');
        for (var i = 0; i < segments.Length; i++)
        {
            segments[i] = System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(segments[i]);
        }

        return string.Join('.', segments);
    }
}
