namespace LearnStack.SharedKernel.Random;

/// <summary>
/// Deterministic <see cref="IRandom"/> for tests. Seeded
/// <see cref="System.Random"/> reproduces the same sequence per run.
/// </summary>
public sealed class FixedRandom : IRandom
{
    private readonly System.Random _random;

    public FixedRandom(int seed)
    {
        _random = new System.Random(seed);
    }

    public int Next(int maxExclusive) => _random.Next(maxExclusive);

    public int Next(int minInclusive, int maxExclusive) =>
        _random.Next(minInclusive, maxExclusive);

    public double NextDouble() => _random.NextDouble();

    public void NextBytes(Span<byte> destination) => _random.NextBytes(destination);
}
