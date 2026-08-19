using System.ComponentModel.DataAnnotations;
using LearnStack.SharedKernel.Pagination;
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
    /// Free-text search. No length cap is imposed here: Standards 04 § Request
    /// and Response Limits already bounds the whole URL at 2 KB, and a second
    /// limit that disagreed with it would be the harder failure to explain.
    /// </summary>
    [FromQuery(Name = "q")]
    public string? Q { get; init; }

    /// <summary>
    /// Parses <see cref="Sort"/>. Safe to call only after validation has run —
    /// <see cref="Validate"/> is what guarantees the value parses, and
    /// <c>[ApiController]</c> is what guarantees validation ran.
    /// </summary>
    public SortSpecification ToSort() =>
        SortSpecification.TryParse(Sort, out var specification, out _)
            ? specification
            : SortSpecification.Empty;

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
}
