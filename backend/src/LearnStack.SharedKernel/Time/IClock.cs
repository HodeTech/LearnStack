namespace LearnStack.SharedKernel.Time;

/// <summary>
/// Wall-clock abstraction for domain and application code. Per Standards 02
/// § Time, no production code reads <c>DateTime.UtcNow</c> /
/// <c>DateTimeOffset.UtcNow</c> directly — every timestamp flows through
/// <see cref="IClock"/> so tests can pin time deterministically.
/// </summary>
public interface IClock
{
    /// <summary>
    /// Current UTC instant. Persisted timestamps are always UTC; conversion
    /// to a presentation timezone happens at the surface boundary only.
    /// </summary>
    DateTimeOffset UtcNow { get; }
}
