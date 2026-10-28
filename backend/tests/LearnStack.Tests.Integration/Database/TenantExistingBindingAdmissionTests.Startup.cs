using System.Net;
using FluentAssertions;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class TenantExistingBindingAdmissionTests
{
    [Fact]
    public async Task Actual_host_startup_requires_the_installed_authority_exact_configuration_and_application_role()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var database = await DatabaseAsync(issuer);
        var authority = database.Registration.AuthorityId.Value.ToString("D");
        await using (var control = new QualifiedStartupFactory(database.AppConnectionString, issuer.Issuer, authority))
        {
            using var client = control.CreateClient();
            using var healthy = await client.GetAsync("/healthz");
            healthy.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        var counts = await CountsAsync(database);
        await using var missing = await DisposableSchemaDatabase.CreateAsync(parent.Postgres);
        foreach (var variant in new[]
        {
            new StartupVariant("missing anchor", missing.AppConnectionString, issuer.Issuer, authority),
            new StartupVariant("mismatched authority", database.AppConnectionString, issuer.Issuer, Guid.NewGuid().ToString("D")),
            new StartupVariant("mismatched issuer", database.AppConnectionString,
                issuer.Issuer.Replace("/realms/", "/different/realms/", StringComparison.Ordinal), authority),
            new StartupVariant("missing configured authority", database.AppConnectionString, issuer.Issuer, null),
            new StartupVariant("wrong runtime role", database.MigrationConnectionString, issuer.Issuer, authority),
        })
        {
            await using var factory = new QualifiedStartupFactory(variant.Connection, variant.Issuer, variant.Authority);
            Action start = () => { using var client = factory.CreateClient(); };
            var failure = start.Should().Throw<InvalidOperationException>(variant.Name).Which;
            if (variant.Name == "wrong runtime role")
                failure.Message.StartsWith("ConnectionStrings:Default names Username='learnstack_migration'", StringComparison.Ordinal)
                    .Should().BeTrue("the full production composition must reject an owner credential before qualification");
            else if (variant.Name == "missing configured authority")
                failure.Message.Should().Be("Identity:AuthorityId requires an explicit nonempty authority UUID.",
                    "invalid deployment configuration refuses before the startup database qualifier");
            else
                failure.Message.Should().Be("Identity authority startup qualification is unavailable or does not match deployment configuration.");
        }
        (await CountsAsync(database)).Should().Be(counts, "startup refusal must not install a replacement authority or allocate a binding");
        await using var restored = new QualifiedStartupFactory(database.AppConnectionString, issuer.Issuer, authority);
        using var restoredClient = restored.CreateClient();
        using var restoredHealth = await restoredClient.GetAsync("/healthz");
        restoredHealth.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed record StartupVariant(string Name, string Connection, string Issuer, string? Authority);

    [Fact]
    public async Task Actual_host_startup_refuses_auxiliary_role_anchor_and_actual_database_mismatch_without_repair()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var database = await DatabaseAsync(issuer);
        var authority = database.Registration.AuthorityId.Value.ToString("D");
        await using var missing = await DisposableSchemaDatabase.CreateAsync(parent.Postgres);
        await using var other = await DisposableSchemaDatabase.CreateAsync(parent.Postgres);
        await using (var migration = (NpgsqlConnection)await PostgresFixture.OpenAsync(other.MigrationConnectionString))
            await IdentityAuthorityDeploymentInstaller.InstallAsync(migration, database.Registration);
        var before = await CountsAsync(database);
        foreach (var (authorization, evidence) in new[]
        {
            (missing.AppConnectionString, database.AppConnectionString),
            (database.AppConnectionString, missing.AppConnectionString),
            (other.AppConnectionString, database.AppConnectionString),
            (database.AppConnectionString, other.AppConnectionString),
            (database.MigrationConnectionString, database.AppConnectionString),
            (database.AppConnectionString, database.MigrationConnectionString),
            (database.AppConnectionString, ""),
        })
        {
            await using var refused = new QualifiedStartupFactory(database.AppConnectionString, issuer.Issuer,
                authority, authorization, evidence);
            Action start = () => { using var client = refused.CreateClient(); };
            var failure = start.Should().Throw<InvalidOperationException>().Which;
            failure.Message.Should().Be("Identity authorization source qualification is unavailable or does not match deployment configuration.");
            failure.InnerException.Should().BeNull("source refusal must not expose provider or credential payloads");
        }
        (await CountsAsync(database)).Should().Be(before);
        await using var restored = new QualifiedStartupFactory(database.AppConnectionString, issuer.Issuer, authority);
        using var client = restored.CreateClient();
        using var response = await client.GetAsync("/healthz");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed class QualifiedStartupFactory(string connection, string issuer, string? authority,
        string? authorization = null, string? evidence = null) : ConstructionOnlyApiFactory
    {
        protected override bool RetainIdentityQualification => true;
        protected override void ConfigureTestHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Development);
            builder.UseSetting("ConnectionStrings:Default", connection);
            builder.UseSetting("ConnectionStrings:IdentityAuthorization", authorization ?? connection);
            builder.UseSetting("ConnectionStrings:AuthorizationEvidence", evidence ?? connection);
            builder.UseSetting("Authentication:Tenant:Issuer", issuer);
            builder.UseSetting("Identity:AuthorityId", authority);
        }
    }
}
