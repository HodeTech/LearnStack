using LearnStack.Tests.Integration.Database;
using System.Net;
using FluentAssertions;
using Xunit;

namespace LearnStack.Tests.Integration.TenancyIdentity;

// Production API/Serilog ownership is separate from the shared read-only
// provider collection. Only this collection also pays for application PostgreSQL.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TenantRealmApiTestGroup : ICollectionFixture<TenantRealmApiFixture>
{
    public const string Name = "tenant-keycloak-production-api";
}

/// <summary>Isolated legacy scoped-bearer compatibility provider; native/BFF collections use the committed web graph.</summary>
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
            await SelectLegacyScopedBearerFixtureAsync();
            Database = await KeycloakIdentityDatabase.CreateAsync(PublicDatabase, Provider);
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    private async Task SelectLegacyScopedBearerFixtureAsync()
    {
        // The production API's validated legacy profile fixes azp=learnstack-web.
        // Only this separately owned provider restores its historical default
        // attachment so direct-bearer scope/mismatch negatives remain real.
        using var admin = await Provider.CreateAdminClientAsync();
        var scopes = await admin.GetAsync("client-scopes");
        var native = scopes.EnumerateArray().Single(scope => scope.GetProperty("name").GetString() == "learnstack-native-session");
        var legacy = scopes.EnumerateArray().Single(scope => scope.GetProperty("name").GetString() == "learnstack-tenant-api");
        var web = await admin.ClientAsync("learnstack-web");
        var path = "clients/" + web.GetProperty("id").GetString();
        var defaults = await admin.GetAsync(path + "/default-client-scopes");
        defaults.EnumerateArray().Select(scope => scope.GetProperty("name").GetString())
            .Should().Contain("learnstack-native-session").And.NotContain("learnstack-tenant-api");
        (await admin.DeleteAsync(path + "/default-client-scopes/" + native.GetProperty("id").GetString())).Should().Be(HttpStatusCode.NoContent);
        (await admin.PutAsync(path + "/default-client-scopes/" + legacy.GetProperty("id").GetString(), new { })).Should().Be(HttpStatusCode.NoContent);
        defaults = await admin.GetAsync(path + "/default-client-scopes");
        defaults.EnumerateArray().Select(scope => scope.GetProperty("name").GetString())
            .Should().Contain("learnstack-tenant-api").And.NotContain("learnstack-native-session");
        var optional = await admin.GetAsync(path + "/optional-client-scopes");
        optional.EnumerateArray().Select(scope => scope.GetProperty("name").GetString())
            .Should().NotContain(["learnstack-tenant-api", "learnstack-native-session"]);
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
