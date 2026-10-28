using System.Net;
using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class TenantExistingBindingAdmissionTests
{
    [Fact]
    public async Task Actual_host_refuses_either_auxiliary_lane_on_another_cluster_with_the_same_database_name_and_authority()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var database = await DatabaseAsync(issuer);
        var authority = database.Registration.AuthorityId.Value.ToString("D");
        var otherCluster = new PostgresFixture();
        try
        {
            await otherCluster.InitializeAsync();
            var name = new NpgsqlConnectionStringBuilder(database.AppConnectionString).Database!;
            // The identifier comes only from our disposable database generator.
            name.Should().MatchRegex("^scope_proof_[0-9a-f]{32}$");
            await otherCluster.ExecuteAsSuperuserAsync($"CREATE DATABASE {name} OWNER learnstack_migration");
            var otherApp = SameNamedDatabase(otherCluster.AppConnectionString, name);
            var otherMigration = SameNamedDatabase(otherCluster.MigrationConnectionString, name);
            await MigrationChains.ApplyAllAsync(otherMigration);
            await using (var migration = (NpgsqlConnection)await PostgresFixture.OpenAsync(otherMigration))
                await IdentityAuthorityDeploymentInstaller.InstallAsync(migration, database.Registration);

            // Both isolated databases really satisfy the former role/anchor/name checks.
            await using (var read = new IdentityAuthorizationReadSource(otherApp))
            await using (var evidence = new AuthorizationEvidenceSource(otherApp))
            {
                await read.QualifyAsync(database.Registration.AuthorityId, issuer.Issuer);
                await evidence.QualifyAsync(database.Registration.AuthorityId, issuer.Issuer);
                read.IsQualified.Should().BeTrue();
                evidence.IsQualified.Should().BeTrue();
                read.QualifiedDatabaseName.Should().Be(name);
                evidence.QualifiedDatabaseName.Should().Be(name);
            }
            var before = await CountsAsync(database);
            var otherBefore = await SourceProofRowsAsync(otherApp, database.Registration);
            await HealthySourceProofHostAsync(database.AppConnectionString, issuer.Issuer, authority);
            await HealthySourceProofHostAsync(otherApp, issuer.Issuer, authority);

            foreach (var (authorization, evidence) in new[]
            {
                (otherApp, database.AppConnectionString),
                (database.AppConnectionString, otherApp),
            })
            {
                await using var refused = new QualifiedStartupFactory(database.AppConnectionString, issuer.Issuer,
                    authority, authorization, evidence);
                Action start = () => { using var client = refused.CreateClient(); };
                var failure = start.Should().Throw<InvalidOperationException>().Which;
                failure.Message.Should().Be("Identity authorization source qualification is unavailable or does not match deployment configuration.");
                failure.InnerException.Should().BeNull("the refusal cannot disclose database or provider details");
            }

            (await CountsAsync(database)).Should().Be(before);
            (await SourceProofRowsAsync(otherApp, database.Registration)).Should().Be(otherBefore);
            await HealthySourceProofHostAsync(database.AppConnectionString, issuer.Issuer, authority);
        }
        finally { await otherCluster.DisposeAsync(); }
    }

    private static string SameNamedDatabase(string connection, string name) =>
        new NpgsqlConnectionStringBuilder(connection) { Database = name, Pooling = false }.ConnectionString;

    private static async Task HealthySourceProofHostAsync(string connection, string issuer, string authority)
    {
        await using var factory = new QualifiedStartupFactory(connection, issuer, authority);
        using var client = factory.CreateClient();
        using var health = await client.GetAsync("/healthz");
        health.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task<(long Anchors, long Users, long Audits)> SourceProofRowsAsync(
        string connection, IdentityAuthorityRegistration registration)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(connection);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await AnnounceAsync(app, transaction,
            new LearnStack.SharedKernel.Identity.GlobalIdentitySubject(registration.AuthorityId, registration.Issuer, Subject));
        await using var command = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM public.identity_authority),
                   (SELECT count(*) FROM public.users),
                   (SELECT count(*) FROM public.audit_log WHERE operation='identity.user.create')
            """, app, transaction);
        await using var rows = await command.ExecuteReaderAsync();
        (await rows.ReadAsync()).Should().BeTrue();
        var result = (rows.GetInt64(0), rows.GetInt64(1), rows.GetInt64(2));
        result.Should().Be((1L, 0L, 0L), "startup only qualifies the installed anchor and never adopts a subject");
        return result;
    }
}
