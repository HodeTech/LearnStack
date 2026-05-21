namespace LearnStack.SharedKernel.Pagination;

/// <summary>
/// Cursor-pagination response envelope. Surface matches Standards 04
/// § Pagination so OpenAPI generation produces a uniform shape across
/// every list endpoint.
/// </summary>
public sealed record PageInfo(
    string? NextCursor,
    string? PreviousCursor,
    bool HasNext,
    bool HasPrevious);
