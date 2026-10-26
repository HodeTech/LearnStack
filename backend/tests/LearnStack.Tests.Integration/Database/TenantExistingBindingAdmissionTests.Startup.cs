using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
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

    private sealed class QualifiedStartupFactory(string connection, string issuer, string? authority) : ConstructionOnlyApiFactory
    {
        protected override bool RetainIdentityQualification => true;
        protected override void ConfigureTestHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Development);
            builder.UseSetting("ConnectionStrings:Default", connection);
            builder.UseSetting("Authentication:Tenant:Issuer", issuer);
            builder.UseSetting("Identity:AuthorityId", authority);
        }
    }
}
