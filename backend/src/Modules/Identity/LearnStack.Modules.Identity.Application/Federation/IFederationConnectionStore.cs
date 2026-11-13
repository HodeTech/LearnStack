using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Persistence;

namespace LearnStack.Modules.Identity.Application.Federation;

/// <summary>
/// The current tenant's connection registry. Every read selects through <see cref="FederationConnectionReads.Live"/>,
/// so a Deleted tombstone (ADR-0099 Amendment 1) is never returned as the live connection.
/// </summary>
public interface IFederationConnectionStore : IAggregateWriteStore<FederationConnection, FederationConnectionId>
{
    /// <summary>The live connection, tracked and row-locked under the Identity tenant fence, for one domain step.</summary>
    Task<FederationConnection?> FindLiveLockedAsync(CancellationToken cancellationToken);

    /// <summary>The live connection without tracking, for a read.</summary>
    Task<FederationConnection?> ReadLiveAsync(CancellationToken cancellationToken);
}

/// <summary>The single tombstone filter every federation read uses (Step 1 review S7).</summary>
public static class FederationConnectionReads
{
    /// <summary>
    /// The same predicate for the two raw SQL reads (the store's row lock and the public sign-in reader), which cannot
    /// use <see cref="Live"/>. An architecture convention pins this text and requires both readers to interpolate it.
    /// </summary>
    public const string LiveSql = "state <> 'Deleted' AND deleted_at IS NULL";

    public static IQueryable<FederationConnection> Live(this IQueryable<FederationConnection> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Where(connection => connection.State != FederationConnectionState.Deleted && connection.DeletedAt == null);
    }
}

/// <summary>The host tenant's Active option for the anonymous sign-in read; LearnStack's database only.</summary>
public sealed record PublicSignInOption(string Alias, string DisplayName, FederationBrokerProtocol Protocol);

/// <summary>The public read's narrow reader: only an Active, live connection, never a tombstone or another state.</summary>
public interface IPublicSignInReader
{
    Task<PublicSignInOption?> ReadActiveAsync(CancellationToken cancellationToken);
}
