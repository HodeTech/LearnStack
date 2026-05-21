using System.Diagnostics.CodeAnalysis;

namespace LearnStack.SharedKernel.Pagination;

/// <summary>
/// Cursor-paginated response. The <see cref="Items"/> are the page
/// payload; <see cref="PageInfo"/> carries the next/previous opaque
/// cursors plus the boolean hints the client uses to render
/// pagination controls.
/// </summary>
/// <remarks>
/// CA1000 (do not declare static members on generic types) is intentionally
/// suppressed for <see cref="Empty"/>: <c>Page&lt;T&gt;.Empty</c> mirrors
/// the canonical factory pattern (<c>Array.Empty&lt;T&gt;</c>,
/// <c>ReadOnlyCollection&lt;T&gt;.Empty</c>) and removes per-call-site
/// boilerplate. The alternative (<c>Page.Empty&lt;T&gt;()</c> helper)
/// forces callers to repeat <c>T</c>.
/// </remarks>
[SuppressMessage(
    "Design",
    "CA1000:Do not declare static members on generic types",
    Justification = "Canonical empty-instance pattern (mirrors Array.Empty<T>); Result<T>.Ok/Fail uses the same lineage.")]
public sealed record Page<T>(IReadOnlyList<T> Items, PageInfo PageInfo)
{
    public static Page<T> Empty { get; } = new(
        Array.Empty<T>(),
        new PageInfo(null, null, HasNext: false, HasPrevious: false));
}
