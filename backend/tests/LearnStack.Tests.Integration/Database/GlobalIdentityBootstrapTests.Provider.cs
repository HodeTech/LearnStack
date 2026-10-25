using System.Net;
using FluentAssertions;
using LearnStack.Modules.Identity.Application.Contracts.Bindings;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.SharedKernel.Identity;
using LearnStack.Tests.Integration.TenancyIdentity;
using LearnStack.Tools.Seeder;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class GlobalIdentityBootstrapTests
{
    private static readonly string[] MfaSetupActions = ["CONFIGURE_TOTP"];

    [Fact]
    public async Task Signed_provider_and_authenticated_readback_facts_bind_the_preserved_actor_without_password_reuse()
    {
        await using var providerFixture = await KeycloakFixture.StartAsync();
        using var admin = await providerFixture.CreateAdminClientAsync();
        var declaration = SeedData.IdentityUsers[0];
        using var browser = providerFixture.Browser();
        var authorization = await browser.AuthorizeAsync(declaration.Username, KeycloakFixture.DemoPassword,
            "https://" + declaration.Tenant.Host + ":3000/api/auth/callback/keycloak");
        authorization.Authorized.Should().BeTrue();
        var exchange = await browser.RedeemAsync(authorization);
        exchange.StatusCode.Should().Be(HttpStatusCode.OK);
        var signed = await KeycloakTokenVerifier.ValidateAsync(providerFixture, exchange.AccessToken, "learnstack-api");
        var account = await admin.UserAsync(declaration.Username);
        var opaqueSubject = signed.GetProperty("sub").GetString()
            ?? throw new InvalidOperationException("The verified fixture subject is absent.");
        (account.GetProperty("id").GetString() == opaqueSubject).Should().BeTrue();
        (signed.GetProperty("user_id").GetString() == declaration.UserId.Value.ToString("D")).Should().BeTrue();
        (account.GetProperty("attributes").GetProperty("user_id")[0].GetString()
            == declaration.UserId.Value.ToString("D")).Should().BeTrue();

        await using var database = await DisposableSchemaDatabase.CreateAsync(_schema.Postgres);
        var registration = new IdentityAuthorityRegistration(AuthorityId, providerFixture.Issuer);
        await InstallAsync(database.MigrationConnectionString, registration);
        var subject = new GlobalIdentitySubject(AuthorityId, providerFixture.Issuer, opaqueSubject);
        await using var services = BuildProvider(database.AppConnectionString, registration,
            new FixtureAdmission(subject, declaration.UserId));
        await QualifyAsync(services);
        var created = await AdoptAsync(services, subject, declaration.UserId);
        created.Outcome.Should().Be(IdentityBindingOutcome.Created);
        created.Actor.Should().Be(declaration.UserId);

        // The disposable provider now requires a different password and MFA setup.
        // Adoption consumes authenticated provider facts, never the user's login credential.
        var accountId = account.GetProperty("id").GetString()!;
        (await admin.PutAsync("users/" + Uri.EscapeDataString(accountId) + "/reset-password",
            new { type = "password", value = "changed-fixture-bootstrap-password", temporary = false }))
            .Should().Be(HttpStatusCode.NoContent);
        (await admin.PutAsync("users/" + Uri.EscapeDataString(accountId),
            new { requiredActions = MfaSetupActions })).Should().Be(HttpStatusCode.NoContent);
        var preserved = await admin.UserAsync(declaration.Username);
        (preserved.GetProperty("id").GetString() == opaqueSubject).Should().BeTrue();
        (preserved.GetProperty("attributes").GetProperty("user_id")[0].GetString()
            == declaration.UserId.Value.ToString("D")).Should().BeTrue();
        (await AdoptAsync(services, subject, declaration.UserId)).Outcome.Should().Be(IdentityBindingOutcome.Existing);
        (await ReadAsync(services, subject, declaration.UserId)).Outcome.Should().Be(IdentityBindingOutcome.Existing);
        (await AdoptAsync(services, new(AuthorityId, providerFixture.Issuer, "unverified-asserted-subject"), declaration.UserId))
            .Outcome.Should().Be(IdentityBindingOutcome.AdmissionDenied);
        (await AdoptAsync(services, subject, SeedData.IdentityUsers[1].UserId))
            .Outcome.Should().Be(IdentityBindingOutcome.AdmissionDenied);
    }
}
