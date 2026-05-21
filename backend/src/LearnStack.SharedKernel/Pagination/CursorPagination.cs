namespace LearnStack.SharedKernel.Pagination;

/// <summary>
/// Cursor-pagination request. Per Standards 04 § Pagination, cursor is the
/// default for every list endpoint. The <see cref="Cursor"/> is an opaque
/// token the server minted on a previous response; the client never parses
/// it. <see cref="Limit"/> defaults to <see cref="DefaultLimit"/> and is
/// bounded by <see cref="MaxLimit"/>.
/// </summary>
public sealed record CursorPagination(string? Cursor = null, int Limit = 20)
{
    public const int DefaultLimit = 20;

    public const int MaxLimit = 100;

    /// <summary>
    /// Validates the request against the standard bounds. Returns a
    /// normalised request with <see cref="Limit"/> clamped to
    /// <see cref="MaxLimit"/>; throws when the limit is non-positive.
    /// </summary>
    public CursorPagination Normalised()
    {
        if (Limit <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Limit),
                Limit,
                $"Limit must be > 0. Got: {Limit}.");
        }

        return Limit > MaxLimit ? this with { Limit = MaxLimit } : this;
    }
}
