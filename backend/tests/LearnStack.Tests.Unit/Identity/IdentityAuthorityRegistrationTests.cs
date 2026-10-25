using FluentAssertions;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.SharedKernel.Identifiers;
using Xunit;

namespace LearnStack.Tests.Unit.Identity;

public sealed class IdentityAuthorityRegistrationTests
{
    private static readonly IdentityAuthorityId Authority = IdentityAuthorityId.From(
        Guid.Parse("01930000-0000-7000-8000-00000000f301"));

    [Theory]
    [InlineData("https://identity.example.test/realms/learnstack")]
    [InlineData("https://identity.example.test/auth/realms/learnstack")]
    [InlineData("https://identity.example.test/auth%20prefix/realms/learnstack")]
    [InlineData("http://localhost:8080/realms/learnstack")]
    [InlineData("http://[::1]:8080/realms/learnstack")]
    public void Canonical_deployment_prefixes_and_local_development_transport_remain_valid(string issuer)
    {
        var registration = new IdentityAuthorityRegistration(Authority, issuer);
        registration.Issuer.Should().Be(issuer);
        registration.AuthorityId.Should().Be(Authority);
    }

    [Theory]
    [InlineData("http://identity.example.test/realms/learnstack")]
    [InlineData("https://user:secret@identity.example.test/realms/learnstack")]
    [InlineData("https://identity.example.test/realms/learnstack?redirect=x")]
    [InlineData("https://identity.example.test/realms/learnstack#fragment")]
    [InlineData("https://identity.example.test/auth//realms/learnstack")]
    [InlineData("https://identity.example.test/auth%2fprefix/realms/learnstack")]
    [InlineData("https://identity.example.test/auth%5cprefix/realms/learnstack")]
    [InlineData("https://identity.example.test/%252e%252e/realms/learnstack")]
    [InlineData("https://identity.example.test/auth%0aprefix/realms/learnstack")]
    [InlineData("https://identity.example.test/realms/learnstack-hub")]
    [InlineData("https://identity.example.test/realms/learnstack/")]
    public void Invalid_or_ambiguous_authority_configuration_refuses_with_fixed_diagnostics(string issuer)
    {
        var create = () => new IdentityAuthorityRegistration(Authority, issuer);
        create.Should().Throw<ArgumentException>().WithMessage("Identity authority configuration is invalid.");
    }
}
