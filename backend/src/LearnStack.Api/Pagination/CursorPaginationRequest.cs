using System.ComponentModel.DataAnnotations;
using LearnStack.SharedKernel.Pagination;
using Microsoft.AspNetCore.Mvc;

namespace LearnStack.Api.Pagination;

/// <summary>
/// The wire shape of the cursor-pagination query string, per
/// <see href="../../../../docs/standards/04-api-design.md">Standards 04
/// § Pagination</see>. Binds <c>?cursor=…&amp;limit=…</c> and projects to the
/// kernel's <see cref="CursorPagination"/>.
/// </summary>
/// <remarks>
/// <para>
/// The kernel type exists and is not bound directly, for one measured reason.
/// <see cref="CursorPagination.Limit"/>'s <c>init</c> accessor throws on a
/// non-positive value — a deliberate kernel-level guard — and MVC records that
/// throw against the *binder's* keys, not the query parameter's. Binding
/// <c>?limit=0</c> straight onto the kernel type produced a 400 whose
/// <c>errors</c> map named <c>$</c> and <c>pagination</c>: correct status,
/// no way for the client to learn that <c>limit</c> was the problem.
/// </para>
/// <para>
/// A wire type with a nullable <see cref="int"/> and a range attribute puts the
/// failure where the client can act on it — <c>errors.limit</c> — while the
/// kernel keeps its invariant as the last line of defence rather than the
/// first.
/// </para>
/// <para>
/// The upper bound is deliberately <b>not</b> enforced here.
/// <see cref="CursorPagination"/> clamps above
/// <see cref="CursorPagination.MaxLimit"/> rather than rejecting, and that
/// decision shipped with the kernel in Packet 2. Rejecting here would give one
/// behaviour at the edge and another one layer in.
/// </para>
/// </remarks>
public sealed record CursorPaginationRequest
{
    /// <summary>
    /// Opaque continuation token minted by a previous response. Never parsed
    /// by the client, and — until something mints one — never parsed here
    /// either: its payload shape belongs to whoever produces it, and a format
    /// check written before the first producer would be a guess.
    /// </summary>
    [FromQuery(Name = "cursor")]
    public string? Cursor { get; init; }

    /// <summary>
    /// Page size. Absent means <see cref="CursorPagination.DefaultLimit"/>.
    /// </summary>
    /// <remarks>
    /// The range's upper bound is <see cref="int.MaxValue"/> on purpose — see
    /// the type's remarks. Only a non-positive limit is a client error.
    /// </remarks>
    [FromQuery(Name = "limit")]
    [Range(1, int.MaxValue, ErrorMessage = "limit must be greater than zero.")]
    public int? Limit { get; init; }

    /// <summary>
    /// Projects to the kernel type. Safe by construction: the only value this
    /// can carry past validation is positive, and the kernel clamps the top.
    /// </summary>
    public CursorPagination ToPagination() =>
        new(Cursor, Limit ?? CursorPagination.DefaultLimit);
}
