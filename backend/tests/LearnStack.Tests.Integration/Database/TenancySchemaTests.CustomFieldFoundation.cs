using FluentAssertions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class TenancySchemaTests
{
    [Theory]
    [InlineData("tenant_custom_field_defs", false)]
    [InlineData("tenant_custom_field_defs", true)]
    [InlineData("membership_field_consents", false)]
    [InlineData("membership_field_consents", true)]
    public async Task Custom_field_foundation_grants_no_platform_or_dispatcher_select(string table, bool dispatcher)
    {
        await using var connection = await PostgresFixture.OpenAsync(dispatcher
            ? _schema.Postgres.OutboxConnectionString : _schema.Postgres.PlatformConnectionString);
        // These are fixed theory cases. BYPASSRLS does not grant table SELECT.
        var read = async () => await SchemaQueries.CountAsync(connection, $"SELECT count(*) FROM public.{table}");
        (await read.Should().ThrowAsync<PostgresException>()).Which.SqlState
            .Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Consent_catalogue_subjects_include_tenant_rows_and_both_organization_boundaries()
    {
        await using var app = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(app, transaction, SchemaFixture.TenantA);
        await SchemaQueries.SetSettingAsync(app, transaction, "app.organization_id", "");
        (await SchemaQueries.CountAsync(app, "SELECT count(*) FROM membership_field_consents", transaction)).Should().Be(1);
        await SchemaQueries.SetSettingAsync(app, transaction, "app.organization_id", SchemaFixture.OrgA1.ToString("D"));
        (await SchemaQueries.CountAsync(app, "SELECT count(*) FROM membership_field_consents", transaction)).Should().Be(2);
        (await SchemaQueries.CountAsync(app,
            $"SELECT count(*) FROM membership_field_consents WHERE organization_id='{SchemaFixture.OrgA2:D}'", transaction))
            .Should().Be(0, "the sibling row exists but this organization's RLS cannot disclose it");
        await SchemaQueries.SetSettingAsync(app, transaction, "app.organization_id", SchemaFixture.OrgA2.ToString("D"));
        (await SchemaQueries.CountAsync(app,
            $"SELECT count(*) FROM membership_field_consents WHERE organization_id='{SchemaFixture.OrgA2:D}'", transaction))
            .Should().Be(1, "the sibling's positive half proves the earlier zero was isolation");
        await SchemaQueries.SetSettingAsync(app, transaction, "app.organization_id", "");
        await SchemaQueries.SetTenantAsync(app, transaction, SchemaFixture.TenantB);
        (await SchemaQueries.CountAsync(app, "SELECT count(*) FROM membership_field_consents", transaction)).Should().Be(1);
        await SchemaQueries.SetSettingAsync(app, transaction, "app.organization_id", SchemaFixture.OrgB1.ToString("D"));
        (await SchemaQueries.CountAsync(app, "SELECT count(*) FROM membership_field_consents", transaction)).Should().Be(2);
    }
}
