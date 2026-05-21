namespace LearnStack.SharedKernel.Time;

/// <summary>
/// Deterministic <see cref="IClock"/> for tests. Time advances only through
/// <see cref="Advance"/> / <see cref="SetUtcNow"/> — never spontaneously.
/// </summary>
/// <remarks>
/// All stored instants are normalised to UTC offset (<c>TimeSpan.Zero</c>):
/// the <see cref="IClock.UtcNow"/> contract promises a UTC value, so an
/// input carrying a non-zero offset is converted via
/// <see cref="DateTimeOffset.ToUniversalTime"/> at the boundary rather
/// than silently returned to the caller with the wrong offset.
/// </remarks>
public sealed class FixedClock : IClock
{
    private DateTimeOffset _utcNow;

    public FixedClock(DateTimeOffset utcNow)
    {
        _utcNow = utcNow.ToUniversalTime();
    }

    public DateTimeOffset UtcNow => _utcNow;

    public void SetUtcNow(DateTimeOffset utcNow) => _utcNow = utcNow.ToUniversalTime();

    public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta).ToUniversalTime();
}
