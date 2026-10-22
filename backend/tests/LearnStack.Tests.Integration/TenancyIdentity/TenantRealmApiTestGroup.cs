using LearnStack.Tests.Integration.Database;
using Xunit;

namespace LearnStack.Tests.Integration.TenancyIdentity;

// Production API/Serilog ownership is separate from the shared read-only
// provider collection. Only this collection also pays for application PostgreSQL.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TenantRealmApiTestGroup : ICollectionFixture<PublicReadFixture>, ICollectionFixture<KeycloakFixture>
{
    public const string Name = "tenant-keycloak-production-api";
}
