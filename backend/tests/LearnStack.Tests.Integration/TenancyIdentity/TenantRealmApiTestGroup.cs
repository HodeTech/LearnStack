using LearnStack.Tests.Integration.Database;
using Xunit;

namespace LearnStack.Tests.Integration.TenancyIdentity;

// Production API/Serilog ownership is separate from the shared read-only
// provider collection. Only this collection also pays for application PostgreSQL.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TenantRealmApiTestGroup : ICollectionFixture<TenantRealmApiFixture>
{
    public const string Name = "tenant-keycloak-production-api";
}

/// <summary>One provider and matching identity database for the serialized ordinary realm proofs.</summary>
public sealed class TenantRealmApiFixture : IAsyncLifetime
{
    internal PublicReadFixture PublicDatabase { get; } = new();
    internal KeycloakFixture Provider { get; } = new();
    internal IdentityPublicReadFixture Database { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        try
        {
            await PublicDatabase.InitializeAsync();
            await Provider.InitializeAsync();
            Database = await KeycloakIdentityDatabase.CreateAsync(PublicDatabase, Provider);
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        try { if (Database is not null) await Database.DisposeAsync(); }
        finally
        {
            try { await Provider.DisposeAsync(); }
            finally { await PublicDatabase.DisposeAsync(); }
        }
    }
}
