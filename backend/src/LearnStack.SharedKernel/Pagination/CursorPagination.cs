namespace LearnStack.SharedKernel.Pagination;

/// <summary>
/// Cursor-pagination request. Per Standards 04 § Pagination, cursor is the
/// default for every list endpoint. The <see cref="Cursor"/> is an opaque
/// token the server minted on a previous response; the client never parses
/// it. <see cref="Limit"/> defaults to <see cref="DefaultLimit"/> and is
/// bounded by <see cref="MaxLimit"/>.
/// </summary>
/// <remarks>
/// Construction is the validation point: a non-positive <see cref="Limit"/>
/// is a programmer error (kernel-level guard) and throws. API-layer
/// FluentValidation should turn malformed <em>user</em> input into
/// <c>Result.Fail(validation_failed)</c> <strong>before</strong> the
/// kernel sees the request, so the throw here only fires on coding bugs.
/// <see cref="Normalised"/> clamps above-max limits to
/// <see cref="MaxLimit"/>; it no longer throws.
/// </remarks>
public sealed record CursorPagination
{
    public const int DefaultLimit = 20;

    public const int MaxLimit = 100;

    public CursorPagination(string? Cursor = null, int Limit = DefaultLimit)
    {
        if (Limit <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Limit),
                Limit,
                $"Limit must be > 0. Got: {Limit}.");
        }

        this.Cursor = Cursor;
        this.Limit = Limit;
    }

    public string? Cursor { get; init; }

    public int Limit { get; init; }

    /// <summary>
    /// Returns the request with <see cref="Limit"/> clamped to
    /// <see cref="MaxLimit"/>. Non-throwing: invalid inputs are stopped at
    /// construction, so the only normalisation left is the upper-bound
    /// clamp.
    /// </summary>
    public CursorPagination Normalised() =>
        Limit > MaxLimit ? this with { Limit = MaxLimit } : this;
}
