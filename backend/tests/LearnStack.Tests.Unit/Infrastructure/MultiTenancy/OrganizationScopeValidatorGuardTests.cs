using FluentAssertions;
using LearnStack.Infrastructure.MultiTenancy;
using LearnStack.SharedKernel.Identifiers;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.MultiTenancy;

/// <summary>
/// The refusal <c>OrganizationScopeValidator</c> makes before it opens anything.
/// </summary>
/// <remarks>
/// <para>
/// No Docker, deliberately: the guard returns before a connection exists, so a
/// Testcontainers case could not tell it from a query that found nothing. The
/// <c>Lazy</c> is what proves the distinction — it throws if forced, so a test that
/// completes at all is a test where no connection was attempted.
/// </para>
/// <para>
/// Measured before this file existed: deleting the whole guard left all five
/// Docker-bound cases green. Without it an uninitialized Vogen id reaching
/// <c>tenantId.Value</c> throws <c>ValueObjectValidationException</c> — a 500 at the
/// request edge — where the code's own comment promises a documented <c>false</c>.
/// </para>
/// </remarks>
public sealed class OrganizationScopeValidatorGuardTests
{
    private static readonly TenantId Tenant =
        TenantId.From(Guid.Parse("018f4d40-0000-7000-8000-00000000a001"));

    private static readonly OrganizationId Organization =
        OrganizationId.From(Guid.Parse("018f4d40-0000-7000-8000-0000000000a1"));

    [Fact]
    public async Task An_Uninitialized_Tenant_Is_Refused_Without_Opening_A_Connection()
    {
        // Reached through an array element, because the analyzer refuses `default(TId)`
        // outright — which is the point: the id that gets here comes from a field
        // nobody assigned, not from a literal anyone would write.
        var uninitialized = new TenantId[1];
        var dataSource = Unopenable();

        var belongs = await new OrganizationScopeValidator(dataSource)
            .BelongsToTenantAsync(uninitialized[0], Organization, CancellationToken.None);

        belongs.Should().BeFalse();
        dataSource.IsValueCreated.Should().BeFalse("the refusal precedes the connection");
    }

    [Fact]
    public async Task An_Uninitialized_Organization_Is_Refused_The_Same_Way()
    {
        var uninitialized = new OrganizationId[1];
        var dataSource = Unopenable();

        var belongs = await new OrganizationScopeValidator(dataSource)
            .BelongsToTenantAsync(Tenant, uninitialized[0], CancellationToken.None);

        belongs.Should().BeFalse();
        dataSource.IsValueCreated.Should().BeFalse();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task An_All_Zero_Id_Is_Refused_Explicitly_Rather_Than_By_Accident(
        bool zeroTenant, bool zeroOrganization)
    {
        // Vogen validates the SHAPE of an id, not that it names anything, so
        // TenantId.From(Guid.Empty) is a legal, initialized id — measured. It would be
        // fail-closed anyway, because no row carries it; the guard is what makes the
        // answer "no" rather than "no, by accident", and this is what holds the code to
        // the comment that says so.
        var dataSource = Unopenable();

        var belongs = await new OrganizationScopeValidator(dataSource).BelongsToTenantAsync(
            zeroTenant ? TenantId.From(Guid.Empty) : Tenant,
            zeroOrganization ? OrganizationId.From(Guid.Empty) : Organization,
            CancellationToken.None);

        belongs.Should().BeFalse();
        dataSource.IsValueCreated.Should().BeFalse();
    }

    /// <summary>A data source whose creation is itself the failure.</summary>
    private static Lazy<NpgsqlDataSource> Unopenable() =>
        new(() => throw new InvalidOperationException(
            "The guard must refuse before anything reaches the data source."));
}
