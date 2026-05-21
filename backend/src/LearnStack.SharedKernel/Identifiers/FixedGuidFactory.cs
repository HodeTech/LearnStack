namespace LearnStack.SharedKernel.Identifiers;

/// <summary>
/// Deterministic <see cref="IGuidFactory"/> for tests. Returns the supplied
/// sequence in order; throws <see cref="InvalidOperationException"/> once
/// the sequence is exhausted so tests fail loud rather than silently
/// reusing a default value.
/// </summary>
/// <remarks>
/// <see cref="NewUuidV7"/> and <see cref="NewUuidV4"/> draw from the
/// <em>same</em> queue — the fixture does not synthesise version-7 / -4
/// shapes from the supplied <see cref="Guid"/>s. Callers that need to
/// assert <c>guid.Version == 7</c> (or 4) seed the queue with
/// version-appropriate values (e.g. <c>Guid.CreateVersion7()</c> minted
/// at test setup) so the fixture's output is the exact <see cref="Guid"/>
/// the test passes in. This keeps the fixture trivial; the production
/// <see cref="SystemGuidFactory"/> is where the version contract is
/// enforced.
/// </remarks>
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
