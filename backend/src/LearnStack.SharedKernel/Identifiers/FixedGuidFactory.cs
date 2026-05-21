namespace LearnStack.SharedKernel.Identifiers;

/// <summary>
/// Deterministic <see cref="IGuidFactory"/> for tests. Returns the supplied
/// sequence in order; throws <see cref="InvalidOperationException"/> once
/// the sequence is exhausted so tests fail loud rather than silently
/// reusing a default value.
/// </summary>
public sealed class FixedGuidFactory : IGuidFactory
{
    private readonly Queue<Guid> _sequence;

    public FixedGuidFactory(params Guid[] sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        _sequence = new Queue<Guid>(sequence);
    }

    public Guid NewUuidV7() => Dequeue();

    public Guid NewUuidV4() => Dequeue();

    private Guid Dequeue()
    {
        if (_sequence.Count == 0)
        {
            throw new InvalidOperationException(
                "FixedGuidFactory sequence exhausted. Construct with enough GUIDs " +
                "for the test, or switch to a different fixture.");
        }

        return _sequence.Dequeue();
    }
}
