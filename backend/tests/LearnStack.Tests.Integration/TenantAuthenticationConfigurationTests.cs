using FluentAssertions;
using LearnStack.Api.Authentication;
using LearnStack.SharedKernel.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace LearnStack.Tests.Integration;

public sealed class TenantAuthenticationConfigurationTests
{
    [Fact]
    public void Backchannel_rejects_a_supplied_certificate_validation_bypass()
    {
        using var handler = new SocketsHttpHandler();
        handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, errors) => errors == System.Net.Security.SslPolicyErrors.None;
        var act = () => new TenantAuthenticationBackchannel(handler);
        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("issuer-canary")]
    [InlineData("http://issuer.invalid/realms/learnstack")]
    [InlineData("https://issuer.invalid/realms/learnstack-hub")]
    [InlineData("https://issuer.invalid/realms/learnstack/")]
    [InlineData("https://issuer.invalid//realms/learnstack")]
    [InlineData("https://issuer.invalid/auth/..%2fother/realms/learnstack")]
    [InlineData("https://issuer.invalid/auth%2Fother/realms/learnstack")]
    [InlineData("https://issuer.invalid/auth%5cother/realms/learnstack")]
    [InlineData("https://issuer.invalid/auth/%2e%2e/realms/learnstack")]
    [InlineData("https://issuer.invalid/auth/%252e%252e/realms/learnstack")]
    [InlineData("https://issuer.invalid/auth%252fother/realms/learnstack")]
    [InlineData("https://issuer.invalid:443/realms/learnstack")]
    [InlineData("https://issuer.invalid/realms/learnstack?secret=query-canary")]
    [InlineData("https://issuer.invalid/realms/learnstack#fragment-canary")]
    [InlineData("https://user-canary:password-canary@issuer.invalid/realms/learnstack")]
    [InlineData(" https://issuer.invalid/realms/learnstack")]
    public void Invalid_issuer_refuses_without_echoing_configuration(string? issuer)
    {
        var act = () => TenantAuthenticationOptions.Create(issuer, DeploymentMode.Development);

        var error = act.Should().Throw<InvalidOperationException>().Which;
        error.Message.Should().Contain(TenantAuthenticationOptions.IssuerConfigurationKey);
        error.ToString().Should().NotContain("canary");
        error.InnerException.Should().BeNull();
    }

    [Theory]
    [InlineData(DeploymentMode.SaaS)]
    [InlineData(DeploymentMode.Dedicated)]
    [InlineData(DeploymentMode.SelfHostedOnline)]
    [InlineData(DeploymentMode.SelfHostedAirGapped)]
    public void Loopback_http_is_refused_outside_development(DeploymentMode mode)
    {
        var act = () => TenantAuthenticationOptions.Create(
            "http://127.0.0.1:8080/realms/learnstack", mode);

        act.Should().Throw<InvalidOperationException>();
        TenantAuthenticationOptions.Create("https://issuer.invalid/realms/learnstack", mode)
            .Issuer.Should().Be("https://issuer.invalid/realms/learnstack");
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080/realms/learnstack")]
    [InlineData("http://[::1]:8080/realms/learnstack")]
    [InlineData("https://issuer.invalid/auth/realms/learnstack")]
    [InlineData("https://keycloak.identity.svc.cluster.local:8443/auth/realms/learnstack")]
    [InlineData("https://10.20.30.40:8443/auth/realms/learnstack")]
    [InlineData("https://issuer.invalid/auth%20portal/realms/learnstack")]
    [InlineData("https://issuer.invalid/caf%C3%A9/realms/learnstack")]
    public void Configuration_preserves_the_explicit_canonical_issuer(string issuer)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                [TenantAuthenticationOptions.IssuerConfigurationKey] = issuer,
            }).Build();
        var options = TenantAuthenticationOptions.FromConfiguration(
            configuration, DeploymentMode.Development);

        options.Issuer.Should().Be(issuer);
        options.MetadataAddress.Should().Be(issuer + "/.well-known/openid-configuration");
    }

    [Fact]
    public async Task Isolated_registration_has_one_closed_scheme_and_no_eager_metadata_io()
    {
        // An unavailable real address makes an eager retrieval observable without
        // replacing the stock configuration manager or its transport.
        var settings = TenantAuthenticationOptions.Create(
            "https://issuer.invalid/realms/learnstack", DeploymentMode.SaaS);
        var services = new ServiceCollection();
        services.AddLearnStackTenantAuthentication(settings);
        await using var provider = services.BuildServiceProvider();

        var schemes = await provider.GetRequiredService<IAuthenticationSchemeProvider>()
            .GetAllSchemesAsync();
        schemes.Select(scheme => scheme.Name).Should().Equal(TenantAuthenticationRegistration.Scheme);
        var options = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(TenantAuthenticationRegistration.Scheme);
        options.Authority.Should().Be(settings.Issuer);
        options.MetadataAddress.Should().Be(settings.MetadataAddress);
        options.RequireHttpsMetadata.Should().BeTrue();
        options.MapInboundClaims.Should().BeFalse();
        options.SaveToken.Should().BeFalse();
        options.IncludeErrorDetails.Should().BeFalse();
        options.BackchannelTimeout.Should().Be(TimeSpan.FromSeconds(5));
        options.Backchannel.Timeout.Should().Be(TimeSpan.FromSeconds(5));
        options.RefreshInterval.Should().Be(TimeSpan.FromSeconds(30));
        options.AutomaticRefreshInterval.Should().Be(TimeSpan.FromMinutes(5));
        var manager = options.ConfigurationManager.Should()
            .BeOfType<ConfigurationManager<OpenIdConnectConfiguration>>().Subject;
        manager.UseLastKnownGoodConfiguration.Should().BeFalse();
        manager.RefreshInterval.Should().Be(options.RefreshInterval);
        manager.AutomaticRefreshInterval.Should().Be(options.AutomaticRefreshInterval);

        var validation = options.TokenValidationParameters;
        validation.RequireSignedTokens.Should().BeTrue();
        validation.TryAllIssuerSigningKeys.Should().BeFalse();
        validation.ValidateIssuerSigningKey.Should().BeTrue();
        validation.ValidateIssuer.Should().BeTrue();
        validation.ValidIssuer.Should().Be(settings.Issuer);
        validation.ValidateAudience.Should().BeTrue();
        validation.RequireAudience.Should().BeTrue();
        validation.ValidAudience.Should().Be("learnstack-api");
        validation.IgnoreTrailingSlashWhenValidatingAudience.Should().BeFalse();
        validation.RequireExpirationTime.Should().BeTrue();
        validation.ValidateLifetime.Should().BeTrue();
        validation.ClockSkew.Should().Be(TimeSpan.FromSeconds(30));
        validation.ValidAlgorithms.Should().Equal(SecurityAlgorithms.RsaSha256);
        validation.ValidTypes.Should().Equal("JWT");
        validation.LogValidationExceptions.Should().BeFalse();
        validation.LogTokenId.Should().BeFalse();
        validation.IncludeTokenOnFailedValidation.Should().BeFalse();
    }
}
