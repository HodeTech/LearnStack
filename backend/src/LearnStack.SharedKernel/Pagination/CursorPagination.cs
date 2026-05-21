namespace LearnStack.SharedKernel.Pagination;

/// <summary>
/// Cursor-pagination request. Per Standards 04 § Pagination, cursor is the
/// default for every list endpoint. The <see cref="Cursor"/> is an opaque
/// token the server minted on a previous response; the client never parses
/// it. <see cref="Limit"/> defaults to <see cref="DefaultLimit"/> and is
/// capped at <see cref="MaxLimit"/>.
/// </summary>
/// <remarks>
/// The <see cref="Limit"/> invariant lives in the property's <c>init</c>
/// accessor, not in the constructor, so it covers every initialisation
/// path: the constructor, object-initializer syntax
/// (<c>new CursorPagination { Limit = 0 }</c>), and the record's
/// <c>with</c> expression (<c>request with { Limit = 0 }</c>). Non-positive
/// limits throw (kernel-level programmer-error guard); above-max limits
/// are silently clamped to <see cref="MaxLimit"/>. API-layer
/// FluentValidation should still turn malformed user input into
/// <c>Result.Fail(validation_failed)</c> before the kernel sees the
/// request; the kernel guards are the last line of defense, not the first.
/// </remarks>
public sealed record CursorPagination
{
    public const int DefaultLimit = 20;

    public const int MaxLimit = 100;

    private readonly int _limit = DefaultLimit;

    public CursorPagination(string? Cursor = null, int Limit = DefaultLimit)
    {
        this.Cursor = Cursor;
        this.Limit = Limit;
    }

    public string? Cursor { get; init; }

    public int Limit
    {
        get => _limit;
        init
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    $"Limit must be > 0. Got: {value}.");
            }

            _limit = value > MaxLimit ? MaxLimit : value;
        }
    }
}
