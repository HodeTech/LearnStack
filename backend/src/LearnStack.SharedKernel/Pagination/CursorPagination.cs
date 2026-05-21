namespace LearnStack.SharedKernel.Pagination;

/// <summary>
/// Cursor-pagination request. Per Standards 04 § Pagination, cursor is the
/// default for every list endpoint. The <see cref="Cursor"/> is an opaque
/// token the server minted on a previous response; the client never parses
/// it. <see cref="Limit"/> defaults to <see cref="DefaultLimit"/> and is
/// capped at <see cref="MaxLimit"/> by the constructor.
/// </summary>
/// <remarks>
/// Construction enforces every invariant: non-positive limits throw
/// (kernel-level programmer-error guard), above-max limits are silently
/// clamped to <see cref="MaxLimit"/>. There is no normalisation step a
/// caller can forget — once a <see cref="CursorPagination"/> exists, its
/// <see cref="Limit"/> is always in <c>[1, MaxLimit]</c>. API-layer
/// FluentValidation should still turn malformed user input into
/// <c>Result.Fail(validation_failed)</c> before the kernel sees the
/// request; the ctor guards are the last line of defense, not the first.
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
        this.Limit = Limit > MaxLimit ? MaxLimit : Limit;
    }

    public string? Cursor { get; init; }

    public int Limit { get; init; }
}
