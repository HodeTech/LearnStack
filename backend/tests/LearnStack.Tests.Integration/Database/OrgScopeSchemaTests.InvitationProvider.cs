using FluentAssertions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class OrgScopeSchemaTests
{
    [Theory]
    [InlineData("invitation_email_envelopes", false, true)]
    [InlineData("invitation_email_envelopes", true, false)]
    [InlineData("invitation_email_envelopes", true, true)]
    [InlineData("invitation_email_attempt_receipts", false, true)]
    [InlineData("invitation_email_attempt_receipts", true, false)]
    [InlineData("invitation_email_attempt_receipts", true, true)]
    public async Task Provider_immutable_binding_independently_refuses_every_organization_move(
        string table, bool originalBranch, bool replacementBranch)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(_schema.Postgres);
        await BackgroundJobSchemaSeed.SeedAsync(database.AppConnectionString, database.OutboxConnectionString);
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        var envelope = table == "invitation_email_envelopes";
        var originalOrg = originalBranch ? SchemaFixture.OrgA1 : (Guid?)null;
        var replacementOrg = replacementBranch ? SchemaFixture.OrgA2 : (Guid?)null;
        var row = BackgroundJobSchemaSeed.Find(SchemaFixture.TenantA, originalOrg);
        var id = Guid.CreateVersion7();
        // Seed a valid FK graph while isolating this proof from job-definition
        // admission. Restore the real binding trigger before the observed UPDATE.
        await SchemaQueries.ExecuteAsync(owner, null,
            "ALTER TABLE public.invitation_email_envelopes DISABLE TRIGGER tg_invitation_email_envelopes_guard");
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using (var seed = await app.BeginTransactionAsync())
        {
            await EducationSchemaSeed.AnnounceAsync(app, seed, row.TenantId, row.OrganizationId);
            await InvitationProviderSchemaRows.InsertEnvelopeAsync(app, seed, id, row);
            await seed.CommitAsync();
        }
        if (!envelope)
        {
            await using var seed = await owner.BeginTransactionAsync();
            await InvitationProviderSchemaRows.InsertAsync(owner, seed, false, id, row);
            await seed.CommitAsync();
        }
        await SchemaQueries.ExecuteAsync(owner, null, $"""
            ALTER TABLE public.invitation_email_envelopes ENABLE TRIGGER tg_invitation_email_envelopes_guard;
            GRANT SELECT, UPDATE ON public.{table} TO learnstack_app;
            ALTER POLICY {table}_isolation ON public.{table} USING (true) WITH CHECK (true);
            """);

        await using var transaction = await app.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(app, transaction, row.TenantId, replacementOrg);
        var key = envelope ? "id" : "envelope_id";
        var mutation = $"UPDATE public.{table} SET organization_id=@org WHERE {key}=@id";
        if (!envelope)
        {
            await using var unchanged = new NpgsqlCommand(mutation, app, transaction);
            unchanged.Parameters.AddWithValue("org", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)originalOrg ?? DBNull.Value);
            unchanged.Parameters.AddWithValue("id", id);
            (await unchanged.ExecuteNonQueryAsync()).Should().Be(1,
                "an unresolved receipt permits an unchanged immutable binding before terminal observation");
        }
        var move = () => SchemaQueries.ExecuteAsync(app, transaction, mutation,
            ("org", (object?)replacementOrg ?? DBNull.Value), ("id", id));
        var refusal = (await move.Should().ThrowAsync<PostgresException>()).Which;
        refusal.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        refusal.MessageText.Should().Be(envelope ? "Provider envelope mutation refused." : "Provider observation mutation refused.",
            "the real trigger must refuse the move independently of privileges and RLS");
    }
}
