using Hangfire.PostgreSql;
using Hangfire.Server;

namespace LearnStack.Infrastructure.Jobs;

// The pinned provider exposes this public obsolete seam; a package upgrade must
// qualify its replacement rather than silently changing component ownership.
#pragma warning disable CS0618
internal sealed class HangfireComponentInventory
{
    private readonly PostgreSqlStorage _storage;

    internal HangfireComponentInventory(PostgreSqlStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        if (storage.GetType() != typeof(PostgreSqlStorage)) throw new HangfireStorageRefusedException();
        _storage = storage;
        var components = storage.GetComponents().ToArray();
        var expected = new[]
        {
            "Hangfire.PostgreSql.ExpirationManager",
            "Hangfire.PostgreSql.CountersAggregator",
            "Hangfire.PostgreSql.PostgreSqlHeartbeatProcess",
        };
        if (components.Length != expected.Length
            || components.Any(component => component is null
                || component.GetType().Assembly != typeof(PostgreSqlStorage).Assembly)
            || !components.Select(component => component.GetType().FullName)
                .Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal)))
            throw new HangfireStorageRefusedException();

        Expiration = components.Single(component => component.GetType().FullName == expected[0]);
        FetchedJobHeartbeat = components.Single(component => component.GetType().FullName == expected[2]);
        Selected = Array.AsReadOnly(new[] { Expiration, FetchedJobHeartbeat });
    }

    internal IServerComponent Expiration { get; }
    internal IServerComponent FetchedJobHeartbeat { get; }
    internal IReadOnlyList<IServerComponent> Selected { get; }
    internal bool BelongsTo(PostgreSqlStorage storage) => ReferenceEquals(_storage, storage);
}
#pragma warning restore CS0618
