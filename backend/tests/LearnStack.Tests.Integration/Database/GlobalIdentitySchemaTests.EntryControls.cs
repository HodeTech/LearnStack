using FluentAssertions;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class GlobalIdentitySchemaTests
{
    [Theory]
    [InlineData("attempt-body", "fn_identity_creation_attempt_admission:body")]
    [InlineData("discovery-privilege", "fn_identity_self_membership_scopes:public-execute")]
    [InlineData("allocation-caller", "fn_identity_user_capacity_guard:trigger-callers")]
    public async Task Applied_entry_function_controls_detect_committed_widening_and_a_clean_twin(
        string fault, string expectedViolation)
    {
        await using var clean = await DisposableSchemaDatabase.CreateAsync(fixture.Postgres);
        await using var dirty = await DisposableSchemaDatabase.CreateAsync(fixture.Postgres);
        await InstallAuthorityAsync(clean.MigrationConnectionString);
        await InstallAuthorityAsync(dirty.MigrationConnectionString);
        await using (var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(dirty.MigrationConnectionString))
        {
            (await FunctionViolationsAsync(owner)).Should().BeEmpty();
            var attempt = CompiledFunctions().Single(function => function.Name == "fn_identity_creation_attempt_admission");
            var mutation = fault switch
            {
                "attempt-body" => attempt.Definition.Replace("CREATE FUNCTION", "CREATE OR REPLACE FUNCTION", StringComparison.Ordinal)
                    .Replace(attempt.Body, "\nBEGIN RETURN QUERY SELECT true,NULL::integer; END\n", StringComparison.Ordinal),
                "discovery-privilege" => "GRANT EXECUTE ON FUNCTION public.fn_identity_self_membership_scopes(uuid,integer) TO PUBLIC",
                "allocation-caller" => "DROP TRIGGER tg_users_creation_capacity_record ON public.users",
                _ => throw new InvalidOperationException("Unknown planted self-entry function fault."),
            };
            await ExecuteAsync(owner, null, mutation);
        }

        // Independent connections observe the committed fault. No open fixture
        // transaction can make a rolled-back mutation look detected.
        await using (var observer = (NpgsqlConnection)await PostgresFixture.OpenAsync(dirty.MigrationConnectionString))
            (await FunctionViolationsAsync(observer)).Should().Contain(expectedViolation);
        await using (var observer = (NpgsqlConnection)await PostgresFixture.OpenAsync(clean.MigrationConnectionString))
            (await FunctionViolationsAsync(observer)).Should().BeEmpty();

        if (fault == "attempt-body")
        {
            (await DebitAndReadAsync(clean.AppConnectionString, clean.MigrationConnectionString)).Should().Be(1);
            (await DebitAndReadAsync(dirty.AppConnectionString, dirty.MigrationConnectionString)).Should().Be(0,
                "the planted body really admits the application caller without debiting durable capacity");
        }
        if (fault == "allocation-caller")
        {
            await InsertPairAsync(clean.AppConnectionString, NewSubject());
            await InsertPairAsync(dirty.AppConnectionString, NewSubject());
            (await RetainedAllocationAsync(clean.MigrationConnectionString)).Should().Be(1);
            (await RetainedAllocationAsync(dirty.MigrationConnectionString)).Should().Be(0,
                "removing only the AFTER trigger loses accounting even though both User/audit commits succeed");
        }
    }

    [Fact]
    public async Task Self_entry_upgrade_counts_existing_retained_Users_before_installing_the_capacity_fence()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(fixture.Postgres);
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(database.MigrationConnectionString, provider =>
                provider.MigrationsHistoryTable(IdentityDbContextFactory.HistoryTable)).Options;
        await using var context = new IdentityDbContext(options,
            StaticTenantContextAccessor.Unresolved, EmptyIdentityScope.Instance);
        var applied = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
        applied[^1].Should().Be("20261028155736_add_identity_self_entry");
        await context.GetService<IMigrator>().MigrateAsync(applied[^2]);

        await using (var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString))
        {
            await using var authority = new NpgsqlCommand(
                "INSERT INTO public.identity_authority(id,authority_id,issuer) VALUES(1,@authority,@issuer)", owner);
            authority.Parameters.AddWithValue("authority", GlobalIdentitySchemaFixture.Authority.Value);
            authority.Parameters.AddWithValue("issuer", GlobalIdentitySchemaFixture.Issuer);
            await authority.ExecuteNonQueryAsync();
        }
        // Actual app-role inserts on the preceding schema, each with its central
        // success audit witness. There is no capacity relation/trigger yet.
        await InsertPairAsync(database.AppConnectionString, NewSubject());
        await InsertPairAsync(database.AppConnectionString, NewSubject());

        await context.Database.MigrateAsync();

        (await RetainedAllocationAsync(database.MigrationConnectionString)).Should().Be(2);
        await InsertPairAsync(database.AppConnectionString, NewSubject());
        (await RetainedAllocationAsync(database.MigrationConnectionString)).Should().Be(3);
        await using var observer = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        (await FunctionViolationsAsync(observer)).Should().BeEmpty();
    }

    private static async Task<int> DebitAndReadAsync(string appConnectionString, string migrationConnectionString)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(appConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await AnnounceAsync(app, transaction, NewSubject());
        await using (var debit = new NpgsqlCommand("SELECT admitted FROM public.fn_identity_creation_attempt_admission()", app, transaction))
            ((bool)(await debit.ExecuteScalarAsync())!).Should().BeTrue();
        await transaction.CommitAsync();
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(migrationConnectionString);
        return await ScalarAsync<int>(owner, null, "SELECT minute_attempts FROM public.identity_creation_capacity");
    }

    private static async Task<long> RetainedAllocationAsync(string connectionString)
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);
        return await ScalarAsync<long>(owner, null, "SELECT allocated_users FROM public.identity_creation_capacity");
    }
}
