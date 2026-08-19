using System.ComponentModel.DataAnnotations;
using LearnStack.SharedKernel.Pagination;
using LearnStack.SharedKernel.Results;
using Microsoft.AspNetCore.Mvc;

namespace LearnStack.Api.Pagination;

/// <summary>
/// The wire shape of a list endpoint's query string: cursor pagination plus
/// the <c>sort</c> and <c>q</c> parameters
/// <see href="../../../../docs/standards/04-api-design.md">Standards 04
/// § Filtering and Sorting</see> specifies.
/// </summary>
/// <remarks>
/// <para>
/// It <b>inherits</b> <see cref="CursorPaginationRequest"/> rather than
/// containing one. MVC binds a nested complex type under a prefixed name
/// (<c>?pagination.limit=</c>), which is not the query string Standards 04
/// publishes; inheritance keeps every parameter flat and lets an endpoint that
/// needs only paging take the base type unchanged.
/// </para>
/// <para>
/// Resource-specific filters — <c>?status=published&amp;level=B1</c> — are
/// deliberately absent. They are the one part of a list query that cannot be
/// generic: the names and the value sets belong to the resource. An endpoint
/// declares them as its own parameters alongside this type, and documents them
/// in OpenAPI by declaring them.
/// </para>
/// </remarks>
public record ListRequest : CursorPaginationRequest, IValidatableObject
{
    /// <summary>
    /// The query-string name, used both for binding and for the member name a
    /// validation failure is reported under. They must be the same string:
    /// the client sent <c>sort</c>, and an <c>errors</c> map keyed by the C#
    /// property name would name something the client never wrote.
    /// </summary>
    public const string SortParameterName = "sort";

    /// <summary>
    /// Ordering, most significant key first: <c>sort=-publishedAt,title</c>.
    /// A leading <c>-</c> means descending.
    /// </summary>
    [FromQuery(Name = SortParameterName)]
    public string? Sort { get; init; }

    /// <summary>
    /// Free-text search.
    /// </summary>
    /// <remarks>
    /// No length cap here. Standards 04 § Request and Response Limits states a
    /// 2 KB URL bound, but nothing in this application enforces it — the real
    /// ceiling today is Kestrel's request-line and header limits, and the
    /// gateway's once it fronts the app. Capping <c>q</c> at some other number
    /// would add a third bound that agrees with neither, so the honest move is
    /// to inherit whatever actually rejects an over-long URL and to say so.
    /// </remarks>
    [FromQuery(Name = QParameterName)]
    public string? Q { get; init; }

    /// <summary>
    /// Parses <see cref="Sort"/>. Cannot fail by the time an action can call
    /// it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured, because two plausible-sounding stories about this are both
    /// wrong. <see cref="Validate"/> flags a malformed <c>sort</c> into
    /// <c>ModelState</c>, and <c>[ApiController]</c> — which
    /// <c>VersionedRouteConvention</c> requires on every controller — returns
    /// 400 for <b>any</b> ModelState error before the action runs. So an
    /// action that reaches this method is an action whose ModelState was
    /// clean, which means <see cref="Validate"/> ran and the value parsed.
    /// </para>
    /// <para>
    /// An earlier version returned a <see cref="Result{T}"/> on the theory
    /// that MVC skips <see cref="IValidatableObject"/> once another property
    /// has failed, leaving a malformed sort to reach the action and produce a
    /// silently unsorted 200. MVC does skip it — but the action does not run
    /// either, because the other property's failure is itself a 400.
    /// <c>?limit=0&amp;sort=title,</c> answers 400 naming <c>limit</c>, never
    /// 200. The <see cref="Result{T}"/> was a failure branch no caller could
    /// reach and no test could cover.
    /// </para>
    /// </remarks>
    public SortSpecification ToSort() =>
        SortSpecification.TryParse(Sort, out var specification, out _)
            ? specification
            // Not a fallback — an assertion. If this ever throws, the
            // invariant above has broken: an action ran with an invalid
            // ModelState. Returning Empty here instead would answer 200 with
            // a page in an order the client did not ask for, which is the one
            // failure the client cannot detect. A programmer error belongs in
            // an exception per ADR-0032 § Sub-decision 4.
            : throw new InvalidOperationException(
                $"'{Sort}' reached ToSort() unparsed. ListRequest.Validate flags a "
                + "malformed sort into ModelState and [ApiController] answers 400 "
                + "before an action runs, so this is unreachable unless the "
                + "controller lost [ApiController] or validation was suppressed.");

    /// <summary>
    /// Reports a malformed <c>sort</c> against the parameter the client sent,
    /// so the 400 says <c>errors.sort</c> rather than naming a binder key.
    /// </summary>
    /// <remarks>
    /// <see cref="IValidatableObject"/> rather than a custom attribute or a
    /// model binder: MVC already runs it, the failure lands in
    /// <c>ModelState</c> under the member name, and
    /// <see cref="Common.ModelBindingProblemDetails"/> already turns
    /// <c>ModelState</c> into the one Problem Details shape. Introducing a
    /// third validation mechanism to say one thing would be the expensive way
    /// to reach the same body.
    /// </remarks>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!SortSpecification.TryParse(Sort, out _, out _))
        {
            yield return new ValidationResult(
                errorMessage: null, memberNames: [SortParameterName]);
        }
    }

    /// <summary>
    /// The query-string name for free-text search.
    /// </summary>
    public const string QParameterName = "q";
}
