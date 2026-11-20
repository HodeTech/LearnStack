namespace LearnStack.SharedKernel.Identity;

/// <summary>
/// What the provider shows about one broker whose owner marker names a tenant and a connection: nonsecret facts only,
/// never a configuration value, display name, issuer or secret (ADR-0099 Amendment 10).
/// </summary>
/// <param name="Owner">The tenant and connection the marker names, with the alias derived from the connection.</param>
/// <param name="Readable">False when the representation cannot be read as the protocol's admitted shape.</param>
/// <param name="Conforms">True when a readable broker's members and configuration equal the fixed profile exactly.</param>
/// <param name="Enabled">Whether the provider offers the broker as a sign-in option.</param>
/// <param name="Protocol">The brokered protocol of a readable broker; null when it is not readable.</param>
public sealed record FederationBrokerFacts(
    FederationBrokerOwner Owner, bool Readable, bool Conforms, bool Enabled, FederationBrokerProtocol? Protocol)
{
    public override string ToString() => nameof(FederationBrokerFacts);
}

/// <summary>How a listing or an inspection ended at the provider.</summary>
public enum FederationOperatorStatus
{
    /// <summary>The provider answered.</summary>
    Ok = 1,

    /// <summary>The provider could not be reached or answered outside the admitted shape; nothing is known.</summary>
    Unavailable = 2,
}

/// <summary>
/// One page of the realm's identity-provider instances, classified by marker and shape.
/// </summary>
/// <param name="Status">Whether the provider answered.</param>
/// <param name="Owned">The brokers whose marker parses to an owner whose alias is the derived one.</param>
/// <param name="Ignored">The count of listed instances with no marker, a foreign marker or an alias that is not LearnStack's.</param>
/// <param name="Scanned">How many instances the provider returned, which the caller adds to the offset.</param>
public sealed record FederationBrokerPage(
    FederationOperatorStatus Status, IReadOnlyList<FederationBrokerFacts> Owned, int Ignored, int Scanned)
{
    /// <summary>The page of a provider that could not answer.</summary>
    public static FederationBrokerPage Unavailable { get; } = new(FederationOperatorStatus.Unavailable, [], 0, 0);

    public override string ToString() => nameof(FederationBrokerPage);
}

/// <summary>What one alias is at the provider.</summary>
public enum FederationInspectionStatus
{
    /// <summary>No instance has the alias.</summary>
    Absent = 1,

    /// <summary>An instance has the alias and its marker does not name a tenant and connection of LearnStack's.</summary>
    NotOwned = 2,

    /// <summary>The marker names a tenant and a connection, and the alias is the connection's derived one.</summary>
    Owned = 3,

    /// <summary>The provider could not be reached or answered outside the admitted shape.</summary>
    Unavailable = 4,
}

/// <summary>The inspection of one alias, with the mapper count of an owned readable broker.</summary>
/// <param name="Status">What the alias is.</param>
/// <param name="Facts">The facts of an owned broker; null otherwise.</param>
/// <param name="MapperCount">The mappers on an owned readable broker; zero otherwise.</param>
public sealed record FederationBrokerInspection(FederationInspectionStatus Status, FederationBrokerFacts? Facts, int MapperCount)
{
    public static FederationBrokerInspection Unavailable { get; } = new(FederationInspectionStatus.Unavailable, null, 0);

    public static FederationBrokerInspection Absent { get; } = new(FederationInspectionStatus.Absent, null, 0);

    public static FederationBrokerInspection NotOwned { get; } = new(FederationInspectionStatus.NotOwned, null, 0);

    public override string ToString() => nameof(FederationBrokerInspection);
}

/// <summary>How an operator remove or disable ended; no raw response crosses the port.</summary>
public enum FederationOperatorOutcome
{
    /// <summary>The provider's change was made and, for a remove, confirmed.</summary>
    Applied = 1,

    /// <summary>The broker was already absent or already disabled; nothing was sent.</summary>
    AlreadyApplied,

    /// <summary>The provider definitively refused the change; nothing changed.</summary>
    Refused,

    /// <summary>The instance's marker does not name the owner the caller proved; nothing was touched.</summary>
    NotOwned,

    /// <summary>The provider could not be reached before any change was sent; nothing changed.</summary>
    Unavailable,

    /// <summary>A change was sent and its outcome is unknown: the next report shows the broker present or gone.</summary>
    Uncertain,
}

/// <summary>
/// ADR-0099 Amendment 10's operator port, beside the tenant provider port and never the tenant runner's:
/// a paged listing of the realm's identity-provider instances, an inspection of one alias, a remove and a
/// marker-proven disable. It holds the same admin client and adds no realm authority. Only the operator service and
/// the Infrastructure adapter hold it.
/// </summary>
public interface IFederationOperator
{
    /// <summary>Lists one page of the realm's instances in the provider's order, from <paramref name="offset"/>.</summary>
    Task<FederationBrokerPage> ListAsync(int offset, int limit, CancellationToken cancellationToken = default);

    /// <summary>Reads one alias: whether it is absent, foreign to LearnStack or owned, and its nonsecret facts.</summary>
    Task<FederationBrokerInspection> InspectAsync(string brokerAlias, CancellationToken cancellationToken = default);

    /// <summary>Counts the mappers on an owned readable broker; null when the provider could not answer.</summary>
    Task<int?> CountMappersAsync(FederationBrokerOwner owner, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the instance after proving its marker names this owner, whether or not it can be read, and confirms
    /// that it is gone. The provider deletes the federated links with it. When <paramref name="requireDisabledUnreadable"/>
    /// is set, the instance must still read as unreadable and be disabled when the delete is about to be sent, proved again
    /// at that moment, or the call is <see cref="FederationOperatorOutcome.Refused"/> and nothing is sent: that is the
    /// condition under which the owner removes a broker a live connection row still names (ADR-0099 Amendment 12).
    /// </summary>
    Task<FederationOperatorOutcome> RemoveAsync(
        FederationBrokerOwner owner, bool requireDisabledUnreadable = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets <c>enabled</c> to false at the provider only, after proving the marker: the one provider write that sends
    /// back the representation the provider returned, with nothing else changed.
    /// </summary>
    Task<FederationOperatorOutcome> DisableAsync(FederationBrokerOwner owner, CancellationToken cancellationToken = default);
}

/// <summary>The fail-closed default while the federation admin client is not configured: nothing is sent.</summary>
public sealed class UnconfiguredFederationOperator : IFederationOperator
{
    public Task<FederationBrokerPage> ListAsync(int offset, int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult(FederationBrokerPage.Unavailable);

    public Task<FederationBrokerInspection> InspectAsync(string brokerAlias, CancellationToken cancellationToken = default) =>
        Task.FromResult(FederationBrokerInspection.Unavailable);

    public Task<int?> CountMappersAsync(FederationBrokerOwner owner, CancellationToken cancellationToken = default) =>
        Task.FromResult<int?>(null);

    public Task<FederationOperatorOutcome> RemoveAsync(
        FederationBrokerOwner owner, bool requireDisabledUnreadable = false, CancellationToken cancellationToken = default) =>
        Task.FromResult(FederationOperatorOutcome.Unavailable);

    public Task<FederationOperatorOutcome> DisableAsync(FederationBrokerOwner owner, CancellationToken cancellationToken = default) =>
        Task.FromResult(FederationOperatorOutcome.Unavailable);
}
