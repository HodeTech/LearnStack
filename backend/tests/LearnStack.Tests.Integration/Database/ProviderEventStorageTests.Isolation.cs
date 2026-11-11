using FluentAssertions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class ProviderEventStorageTests
{
    internal static IReadOnlyDictionary<string, string> WithCheckProofs { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["identity_provider_event_scan"] = nameof(Intake_WITH_CHECK_independently_refuses_app_or_foreign_tenant_and_its_dirty_twin_admits),
            ["identity_provider_event_receipts"] = nameof(Intake_WITH_CHECK_independently_refuses_app_or_foreign_tenant_and_its_dirty_twin_admits),
            ["identity_provider_event_problems"] = nameof(Intake_WITH_CHECK_independently_refuses_app_or_foreign_tenant_and_its_dirty_twin_admits),
            ["identity_provider_event_targets"] = nameof(Intake_WITH_CHECK_independently_refuses_app_or_foreign_tenant_and_its_dirty_twin_admits),
        };

    [Theory]
    [InlineData("identity_provider_event_scan")]
    [InlineData("identity_provider_event_receipts")]
    [InlineData("identity_provider_event_problems")]
    [InlineData("identity_provider_event_targets")]
    public async Task Intake_WITH_CHECK_independently_refuses_app_or_foreign_tenant_and_its_dirty_twin_admits(string table)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(fixture.Postgres);
        await IdentityMembershipFixture.SeedSchemaAsync(database.MigrationConnectionString, database.AppConnectionString);
        await using var owner = await OpenAsync(database.MigrationConnectionString);
        await using var app = await OpenAsync(database.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        // This isolated database grants INSERT and widens only USING so the
        // clean/dirty pair exercises WITH CHECK rather than an earlier ACL.
        await SchemaQueries.ExecuteAsync(owner, null, $"""
            GRANT SELECT,INSERT ON public.{table} TO learnstack_app;
            ALTER POLICY {table}_isolation ON public.{table} USING (true);
            """);
        var insert = table switch
        {
            "identity_provider_event_scan" => """
                INSERT INTO public.identity_provider_event_scan(id,authority_id,realm_id,activation_lower,retention_seconds)
                VALUES(1,@authority,'learnstack',clock_timestamp(),604800)
                """,
            "identity_provider_event_receipts" => """
                INSERT INTO public.identity_provider_event_receipts(id,authority_id,realm_id,provider_event_id,
                    source_occurred_at,source_kind,fingerprint,disposition)
                VALUES(@id,@authority,'learnstack',@id,clock_timestamp(),'LOGIN',decode(repeat('01',32),'hex'),'pending')
                """,
            "identity_provider_event_problems" => """
                INSERT INTO public.identity_provider_event_problems(problem_key,authority_id,realm_id,range_from,range_to,reason)
                VALUES('test-problem',@authority,'learnstack',clock_timestamp(),clock_timestamp(),'malformed')
                """,
            "identity_provider_event_targets" => """
                INSERT INTO public.identity_provider_event_targets(id,tenant_id,receipt_id,event_id,authority_id,realm_id,
                    provider_event_id,affected_user_id,subject_key,source_occurred_at,source_kind,received_at,attempts)
                VALUES(@id,@foreign,@id,@id,@authority,'learnstack',@id,@person,decode(repeat('01',32),'hex'),
                    clock_timestamp(),'LOGIN',clock_timestamp(),0)
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(table)),
        };
        var id = Guid.CreateVersion7();
        async Task WriteAsync(NpgsqlTransaction transaction)
        {
            await using var command = new NpgsqlCommand(insert, app, transaction);
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("authority", IdentityMembershipFixture.Authority.AuthorityId.Value);
            command.Parameters.AddWithValue("foreign", SchemaFixture.TenantB);
            command.Parameters.AddWithValue("person", IdentityMembershipFixture.PersonB);
            (await command.ExecuteNonQueryAsync()).Should().Be(1);
        }
        await using (var clean = await app.BeginTransactionAsync())
        {
            await IdentityMembershipFixture.ScopeAsync(app, clean, SchemaFixture.TenantA);
            var refused = await Assert.ThrowsAsync<PostgresException>(() => WriteAsync(clean));
            refused.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
            refused.MessageText.Should().Contain("row-level security");
            await clean.RollbackAsync();
        }
        await SchemaQueries.ExecuteAsync(owner, null, $"ALTER POLICY {table}_isolation ON public.{table} WITH CHECK (true)");
        await using var dirty = await app.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(app, dirty, SchemaFixture.TenantA);
        await WriteAsync(dirty);
        (await IdentityMembershipFixture.ScalarAsync<long>(app, dirty, $"SELECT count(*) FROM public.{table}"))
            .Should().Be(1, "only the changed WITH CHECK permits the same real application-role statement");
        await dirty.RollbackAsync();
    }
}
