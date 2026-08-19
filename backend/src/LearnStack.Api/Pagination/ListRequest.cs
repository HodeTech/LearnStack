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
    /// Parses <see cref="Sort"/>, or reports why it could not.
    /// </summary>
    /// <remarks>
    /// It returns a <see cref="Result{T}"/> rather than falling back to
    /// <see cref="SortSpecification.Empty"/>. Failing open looked safe —
    /// validation runs first, so the failure path is unreachable — but
    /// "unreachable" there rests on <c>[ApiController]</c> being present and on
    /// MVC having run <see cref="IValidatableObject"/>, and MVC skips the
    /// latter once any property has already failed. An endpoint would then
    /// answer <b>200 with a silently unsorted page</b>: the worst available
    /// outcome, because the client cannot tell.
    /// </remarks>
    public Result<SortSpecification> ToSort() =>
        SortSpecification.TryParse(Sort, out var specification, out var offending)
            ? Result<SortSpecification>.Ok(specification)
            : Result<SortSpecification>.Fail(SortSpecification.MalformedSortError(offending));

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
