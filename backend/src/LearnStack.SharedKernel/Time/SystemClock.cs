namespace LearnStack.SharedKernel.Time;

/// <summary>
/// Production <see cref="IClock"/> implementation backed by the BCL system
/// clock. Registered as a singleton at the composition root.
/// </summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
