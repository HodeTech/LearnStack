namespace LearnStack.Modules.Identity.Infrastructure.Bindings;

/// <summary>
/// What the tool read from the new realm: its discovery issuer, the size of its published key set, and the users it
/// reports (ADR-0104 § 6).
/// </summary>
/// <param name="DiscoveredIssuer">The <c>issuer</c> of the new realm's discovery document.</param>
/// <param name="SigningKeyCount">How many signing keys its key set publishes.</param>
/// <param name="Complete">Whether the enumeration reached the end of the realm, not merely a page.</param>
/// <param name="Subjects">The <c>sub</c> of every user the realm reported.</param>
public sealed record NewRealmSnapshot(
    string DiscoveredIssuer, int SigningKeyCount, bool Complete, IReadOnlyCollection<string> Subjects);

/// <summary>
/// The read-only view of the new realm the continuity proof needs. The tool implements it over the realm's own
/// endpoints with an operator-supplied <c>view-users</c> credential; a test implements it over a fixed list.
/// </summary>
/// <remarks>
/// An implementation reads, and refuses by throwing <see cref="NewRealmUnavailableException"/>; it never writes and
/// never returns a partial enumeration as complete.
/// </remarks>
public interface INewRealmContinuitySource
{
    /// <summary>Reads the discovery document, the key set and every user of the realm behind <paramref name="issuer"/>.</summary>
    Task<NewRealmSnapshot> ReadAsync(string issuer, CancellationToken cancellationToken = default);
}

/// <summary>The new realm could not be read completely. A fixed message: no address, credential or body is retained.</summary>
public sealed class NewRealmUnavailableException()
    : Exception("The new realm could not be read completely.");
