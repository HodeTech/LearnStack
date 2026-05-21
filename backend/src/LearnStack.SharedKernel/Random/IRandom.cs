using System.Diagnostics.CodeAnalysis;

namespace LearnStack.SharedKernel.Random;

/// <summary>
/// Randomness abstraction for domain and application code. Production code
/// never instantiates <see cref="System.Random"/> directly so tests can pin
/// the sequence deterministically per Standards 02 § Time. Cryptographic
/// randomness uses <c>System.Security.Cryptography.RandomNumberGenerator</c>
/// and is out of scope for this abstraction.
/// </summary>
/// <remarks>
/// CA1716 (<c>Next</c> is a VB reserved keyword) is suppressed: LearnStack
/// is C#-only per ADR-0032 and the <c>Next</c> name matches the BCL
/// <see cref="System.Random"/> surface a future maintainer expects.
/// </remarks>
[SuppressMessage(
    "Naming",
    "CA1716:Identifiers should not match keywords",
    Justification = "Mirrors System.Random.Next; LearnStack is C#-only per ADR-0032; no VB consumer affected.")]
public interface IRandom
{
    /// <summary>
    /// Returns a non-negative random integer less than <paramref name="maxExclusive"/>.
    /// </summary>
    int Next(int maxExclusive);

    /// <summary>
    /// Returns a random integer in <c>[minInclusive, maxExclusive)</c>.
    /// </summary>
    int Next(int minInclusive, int maxExclusive);

    /// <summary>
    /// Returns a random double in <c>[0.0, 1.0)</c>.
    /// </summary>
    double NextDouble();

    /// <summary>
    /// Fills the destination span with random bytes.
    /// </summary>
    void NextBytes(Span<byte> destination);
}
