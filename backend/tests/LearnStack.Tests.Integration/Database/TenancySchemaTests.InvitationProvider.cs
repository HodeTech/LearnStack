using FluentAssertions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class TenancySchemaTests
{
    private static readonly IReadOnlyDictionary<string, string> InvitationProviderWithCheckProofs =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["invitation_email_envelopes"] = nameof(Invitation_provider_WITH_CHECK_has_independent_clean_and_dirty_app_controls),
            ["invitation_email_attempt_receipts"] = nameof(Invitation_provider_WITH_CHECK_has_independent_clean_and_dirty_app_controls),
        };

    [Theory]
    [InlineData("invitation_email_envelopes")]
    [InlineData("invitation_email_attempt_receipts")]
    public async Task Invitation_provider_WITH_CHECK_has_independent_clean_and_dirty_app_controls(string table)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(_schema.Postgres);
        await BackgroundJobSchemaSeed.SeedAsync(database.AppConnectionString, database.OutboxConnectionString);
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        var envelope = table == "invitation_email_envelopes";
        (await ScalarAsync(app, $"SELECT has_any_column_privilege(current_user,'public.{table}','INSERT')"))
            .Should().Be(envelope, "only immutable envelope staging has an ordinary application INSERT grant");
        (await ScalarAsync(app, $"SELECT has_table_privilege(current_user,'public.{table}','SELECT')"))
            .Should().Be(false);

        // Only this disposable database gains test grants/visibility. Isolate the
        // policy from the independent envelope binding trigger. The dirty twin
        // changes only WITH CHECK after the clean statement has proved RLS denial.
        await SchemaQueries.ExecuteAsync(owner, null, $"""
            ALTER TABLE public.invitation_email_envelopes DISABLE TRIGGER tg_invitation_email_envelopes_guard;
            GRANT SELECT, INSERT ON public.{table} TO learnstack_app;
            ALTER POLICY {table}_isolation ON public.{table} USING (true);
            """);
        var policy = $"{table}_isolation";
        var original = (await SchemaQueries.ReadStringsAsync(owner,
            $"SELECT pg_get_expr(polwithcheck,polrelid) FROM pg_policy WHERE polrelid='public.{table}'::regclass AND polname='{policy}'")).Single();

        foreach (var (rowOrg, callerTenant, callerOrg) in new (Guid?, Guid, Guid?)[]
        {
            (null, SchemaFixture.TenantA, null),
            (SchemaFixture.OrgB1, SchemaFixture.TenantA, SchemaFixture.OrgA1),
            (null, SchemaFixture.TenantB, SchemaFixture.OrgB1),
            (SchemaFixture.OrgB1, SchemaFixture.TenantB, null),
        })
        {
            var id = Guid.CreateVersion7();
            var job = BackgroundJobSchemaSeed.Find(SchemaFixture.TenantB, rowOrg);
            if (!envelope)
            {
                await using var seed = await app.BeginTransactionAsync();
                await EducationSchemaSeed.AnnounceAsync(app, seed, SchemaFixture.TenantB, rowOrg);
                await InvitationProviderSchemaRows.InsertEnvelopeAsync(app, seed, id, job);
                await seed.CommitAsync();
            }

            await using (var clean = await app.BeginTransactionAsync())
            {
                await EducationSchemaSeed.AnnounceAsync(app, clean, callerTenant, callerOrg);
                var insert = () => InvitationProviderSchemaRows.InsertAsync(app, clean, envelope, id, job);
                var denied = (await insert.Should().ThrowAsync<PostgresException>()).Which;
                denied.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
                denied.MessageText.Should().Contain("row-level security",
                    "the policy itself, rather than a missing grant, trigger or FK, must reject this application write");
                await clean.RollbackAsync();
            }
            await SchemaQueries.ExecuteAsync(owner, null, $"ALTER POLICY {policy} ON public.{table} WITH CHECK (true)");
            await using (var dirty = await app.BeginTransactionAsync())
            {
                await EducationSchemaSeed.AnnounceAsync(app, dirty, callerTenant, callerOrg);
                await InvitationProviderSchemaRows.InsertAsync(app, dirty, envelope, id, job);
                (await ScalarAsync(app, $"SELECT count(*) FROM public.{table} WHERE {(envelope ? "id" : "envelope_id")}='{id}'", dirty))
                    .Should().Be(1L, "the dirty policy must admit and persist the exact row the clean policy refused");
                await dirty.RollbackAsync();
            }
            await SchemaQueries.ExecuteAsync(owner, null, $"ALTER POLICY {policy} ON public.{table} WITH CHECK ({original})");
            if (!envelope)
                await SchemaQueries.ExecuteAsync(owner, null,
                    "DELETE FROM public.invitation_email_envelopes WHERE id=@id", ("id", id));
        }

        // The provider receipt policy is role-only: even an exactly announced
        // tenant/organization is not an ordinary application insertion authority.
        await using var exact = await app.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(app, exact, SchemaFixture.TenantB, null);
        var ownJob = BackgroundJobSchemaSeed.Find(SchemaFixture.TenantB, null);
        var ownId = Guid.CreateVersion7();
        if (envelope)
            await InvitationProviderSchemaRows.InsertEnvelopeAsync(app, exact, ownId, ownJob);
        else
        {
            await InvitationProviderSchemaRows.InsertEnvelopeAsync(app, exact, ownId, ownJob);
            var denied = () => InvitationProviderSchemaRows.InsertAsync(app, exact, false, ownId, ownJob);
            var refusal = (await denied.Should().ThrowAsync<PostgresException>()).Which;
            refusal.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
            refusal.MessageText.Should().Contain("row-level security");
        }
    }

}
