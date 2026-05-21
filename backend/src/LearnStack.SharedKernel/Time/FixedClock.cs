namespace LearnStack.SharedKernel.Time;

/// <summary>
/// Deterministic <see cref="IClock"/> for tests. Time advances only through
/// <see cref="Advance"/> / <see cref="SetUtcNow"/> — never spontaneously.
/// </summary>
public sealed class FixedClock : IClock
{
    private DateTimeOffset _utcNow;

    public FixedClock(DateTimeOffset utcNow)
    {
        _utcNow = utcNow;
    }

    public DateTimeOffset UtcNow => _utcNow;

    public void SetUtcNow(DateTimeOffset utcNow) => _utcNow = utcNow;

    public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
}
