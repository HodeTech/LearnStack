using System.Text.RegularExpressions;
using FluentAssertions;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>ADR-0099 Step 1: the connection registry persists no secret, token, assertion or certificate.</summary>
public sealed partial class FederationConnectionConventionTests
{
    private static readonly string[] ExpectedColumns =
    [
        "id", "tenant_id", "protocol", "alias", "display_name", "issuer_or_entity_id", "state", "source_kind", "client_id",
        "configuration_revision", "configuration_digest", "desired_digest", "last_provider_digest",
        "pending_operation_kind", "pending_operation_id", "pending_actor", "pending_started_at", "pending_quiet_until",
        "pending_desired_digest", "test_attempt_id", "test_actor", "test_revision", "test_expires_at", "test_result",
        "test_passed_at", "created_at", "created_by", "updated_at", "updated_by", "deleted_at", "deleted_by", "row_version",
    ];

    [Fact]
    public void Federation_Connection_Persists_No_Secret_Column()
    {
        using var context = Modules.ModelOnly<IdentityDbContext>();
        var entity = context.Model.FindEntityType(typeof(FederationConnection));
        entity.Should().NotBeNull("Fix: map ADR-0099's connection registry in the Identity context.");
        entity!.GetTableName().Should().Be("federation_connections");
        var columns = Columns(entity!);
        SecretColumnOffenders(columns).Should().BeEmpty(
            "Fix: a client secret, token, assertion, certificate or key exists only in memory for one provider call (ADR-0099)");
        columns.Should().BeEquivalentTo(ExpectedColumns,
            "the column set is closed: a new column is reviewed against the no-secret rule before it is admitted here");
        typeof(FederationConnection).GetCustomAttributes(typeof(TenantOwnedAttribute), false).Should().ContainSingle();
        typeof(FederationConnection).GetCustomAttributes(typeof(OrganizationScopedAttribute), false).Should().BeEmpty(
            "the connection is tenant-wide; a branch never owns one");
        typeof(ITenantOwned).IsAssignableFrom(typeof(FederationConnection)).Should().BeTrue();
        typeof(IOrganizationScoped).IsAssignableFrom(typeof(FederationConnection)).Should().BeFalse();
        entity!.GetDeclaredQueryFilters().Should().NotBeEmpty("the tenant EF filter is the second layer beside RLS");
    }

    [Theory]
    [InlineData("client_secret")]
    [InlineData("secret_ciphertext")]
    [InlineData("access_token")]
    [InlineData("refresh_token")]
    [InlineData("id_token")]
    [InlineData("saml_assertion")]
    [InlineData("signing_certificate")]
    [InlineData("certificate_pem")]
    [InlineData("private_key")]
    [InlineData("client_password")]
    [InlineData("credential")]
    public void The_secret_column_detector_refuses_each_planted_secret_shape(string planted)
    {
        SecretColumnOffenders([.. ExpectedColumns, planted]).Should().Equal(planted);
    }

    [Fact]
    public void The_secret_column_detector_admits_the_reviewed_digest_and_identifier_columns()
    {
        SecretColumnOffenders(ExpectedColumns).Should().BeEmpty("nonsecret digests and pseudonymous identifiers are not secrets");
    }

    private static string[] Columns(IEntityType entity)
    {
        var table = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
        return [.. entity.GetProperties().Select(property => property.GetColumnName(table)!)];
    }

    private static List<string> SecretColumnOffenders(IEnumerable<string> columns) =>
        [.. columns.Where(column => SecretShape().IsMatch(column)).Order(StringComparer.Ordinal)];

    [GeneratedRegex("secret|token|assertion|certificate|cert_|_cert|pem|private_key|password|credential",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SecretShape();
}
