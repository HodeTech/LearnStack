namespace LearnStack.SharedKernel.Random;

/// <summary>
/// Production <see cref="IRandom"/> backed by <see cref="System.Random.Shared"/>.
/// Thread-safe; registered as a singleton at the composition root.
/// </summary>
public sealed class SystemRandom : IRandom
{
    public int Next(int maxExclusive) => System.Random.Shared.Next(maxExclusive);

    public int Next(int minInclusive, int maxExclusive) =>
        System.Random.Shared.Next(minInclusive, maxExclusive);

    public double NextDouble() => System.Random.Shared.NextDouble();

    public void NextBytes(Span<byte> destination) => System.Random.Shared.NextBytes(destination);
}
