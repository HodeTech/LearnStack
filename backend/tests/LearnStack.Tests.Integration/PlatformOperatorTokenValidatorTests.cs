using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using LearnStack.Infrastructure.PlatformOperator;
using LearnStack.SharedKernel.Hosting;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Time;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LearnStack.Tests.Integration;

/// <summary>
/// The offline caller of the operator validator (ADR-0104 § 3) against a real loopback issuer: the same
/// cryptographic parameters, bounded metadata manager and closed profile the API scheme applies.
/// </summary>
/// <remarks>
/// <see cref="PlatformOperatorHttpTests"/> proves the same refusals through the HTTP scheme; here they are proved for
/// the second caller, so the two cannot drift. A refusal is one closed label and never a value the token carried.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "xUnit disposes the fields through IAsyncLifetime.DisposeAsync.")]
public sealed class PlatformOperatorTokenValidatorTests : IAsyncLifetime
{
    private const string Subject = "operator-sub-7";
    private const string Correlation = "00-rebind-tool-01";

    private TenantAuthenticationIssuer _issuer = null!;
    private PlatformOperatorBackchannel _backchannel = null!;
    private PlatformOperatorTokenValidator _validator = null!;

    public async Task InitializeAsync()
    {
        _issuer = await TenantAuthenticationIssuer.StartAsync();
        _backchannel = new PlatformOperatorBackchannel();
        _validator = ValidatorFor(_issuer);
    }

    public async Task DisposeAsync()
    {
        _backchannel.Dispose();
        await _issuer.DisposeAsync();
    }

    [Fact]
    public async Task A_Token_Meeting_The_Whole_Profile_Yields_A_Witness_For_The_Tool_Executor()
    {
        var result = await Validate(Sign());

        result.IsValid.Should().BeTrue();
        result.Cause.Should().BeNull();
        var witness = result.Witness!;
        witness.Issuer.Should().Be(_issuer.Issuer);
        witness.SubjectDigest.Should().Be(PlatformOperatorSubject.Digest(_issuer.Issuer, Subject));
        witness.Acr.Should().Be("mfa");
        witness.Executor.Should().Be(PlatformOperatorSettings.RebindToolExecutor);
        witness.CorrelationId.Should().Be(Correlation);
    }

    [Theory]
    [InlineData(null, "token_missing")]
    [InlineData("", "token_missing")]
    [InlineData("   ", "token_missing")]
    [InlineData("not-a-token", "token_invalid")]
    [InlineData("a.b.c", "token_invalid")]
    public async Task A_Missing_Or_Malformed_Token_Is_Refused_With_One_Closed_Label(string? token, string cause)
    {
        var result = await _validator.ValidateAsync(token, PlatformOperatorSettings.RebindToolExecutor, Correlation);

        result.IsValid.Should().BeFalse();
        result.Cause.Should().Be(cause);
    }

    [Fact]
    public async Task A_Token_Over_The_Length_Bound_Is_Refused_Without_Being_Parsed()
    {
        var result = await _validator.ValidateAsync(
            new string('a', PlatformOperatorTokenValidator.MaximumTokenCharacters + 1),
            PlatformOperatorSettings.RebindToolExecutor, Correlation);

        result.Cause.Should().Be("token_invalid");
    }

    [Fact]
    public async Task Each_Cryptographic_Failure_Has_Its_Own_Closed_Label()
    {
        (await Validate(Sign(("aud", "learnstack-api")))).Cause.Should().Be("token_audience", "the wrong audience");
        (await Validate(Sign(("iss", "https://elsewhere.example.test/realms/x")))).Cause.Should().Be("token_issuer", "another issuer");
        (await Validate(Sign(("exp", DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds())))).Cause.Should().Be("token_expired", "an expired token");
        (await Validate(_issuer.Token(Payload([]), key: _issuer.NewKey()))).Cause.Should().Be("token_signature", "a key the issuer never published");
        (await Validate(_issuer.Token(Payload([]), header: Header("at+jwt")))).Cause.Should().Be("token_type", "another token type");
    }

    [Fact]
    public async Task Each_Profile_Failure_Has_Its_Own_Closed_Label()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        (await Validate(Sign(("azp", "learnstack-web")))).Cause.Should().Be("profile_azp", "a client the profile does not allow");
        (await Validate(Sign(("acr", "pwd")))).Cause.Should().Be("profile_acr", "an assurance the profile does not accept");
        (await Validate(SignWithout("acr"))).Cause.Should().Be("profile_acr", "no assurance claim");
        (await Validate(SignWithout("auth_time"))).Cause.Should().Be("profile_auth_time", "no authentication time");
        (await Validate(Sign(("auth_time", now - 7200)))).Cause.Should().Be("profile_auth_age", "an authentication older than the configured age");
        (await Validate(Sign(("exp", now + 7200)))).Cause.Should().Be("profile_lifetime", "a lifetime above fifteen minutes");
        (await Validate(Sign(("sub", "")))).Cause.Should().Be("profile_subject", "a blank subject");
    }

    [Fact]
    public async Task An_Unreachable_Issuer_Refuses_The_Operator_Rather_Than_Trusting_A_Key_It_Never_Read()
    {
        await using var down = await TenantAuthenticationIssuer.StartAsync();
        down.DiscoveryStatusCode = 500;
        using var backchannel = new PlatformOperatorBackchannel();
        var validator = ValidatorFor(down, backchannel);

        var result = await validator.ValidateAsync(
            down.Token(Payload([], down)), PlatformOperatorSettings.RebindToolExecutor, Correlation);

        result.IsValid.Should().BeFalse();
        result.Cause.Should().Be("metadata_unavailable");
    }

    [Fact]
    public async Task A_Discovery_Document_That_Names_Another_Issuer_Is_Refused()
    {
        await using var misbehaving = await TenantAuthenticationIssuer.StartAsync();
        // Only the document's own issuer property differs: the key set and the endpoints stay at the issuer's origin.
        var document = JsonNode.Parse(misbehaving.DiscoveryDocument)!.AsObject();
        document["issuer"] = "https://elsewhere.example.test/realms/x";
        misbehaving.DiscoveryResponse = document.ToJsonString();
        using var backchannel = new PlatformOperatorBackchannel();
        var validator = ValidatorFor(misbehaving, backchannel);

        var result = await validator.ValidateAsync(
            misbehaving.Token(Payload([], misbehaving)), PlatformOperatorSettings.RebindToolExecutor, Correlation);

        result.IsValid.Should().BeFalse("a discovery issuer other than the pinned one is never trusted");
        result.Cause.Should().Be("metadata_unavailable", "the configuration validator refuses it, not a missing key");
    }

    [Fact]
    public async Task A_Key_Set_At_Another_Origin_Is_Refused()
    {
        await using var foreign = await TenantAuthenticationIssuer.StartAsync();
        await using var misbehaving = await TenantAuthenticationIssuer.StartAsync();
        // A live key set at another origin: a reader that followed it would succeed, so refusing is the control.
        var document = JsonNode.Parse(misbehaving.DiscoveryDocument)!.AsObject();
        document["jwks_uri"] = foreign.Issuer + "/keys";
        misbehaving.DiscoveryResponse = document.ToJsonString();
        using var backchannel = new PlatformOperatorBackchannel();
        var validator = ValidatorFor(misbehaving, backchannel);

        var result = await validator.ValidateAsync(
            misbehaving.Token(Payload([], misbehaving)), PlatformOperatorSettings.RebindToolExecutor, Correlation);

        result.IsValid.Should().BeFalse();
        foreign.JwksRequests.Should().Be(0, "the other origin is never called");
    }

    [Theory]
    [InlineData("operator.other")]
    [InlineData("")]
    [InlineData("operator.api ")]
    public async Task An_Executor_That_Is_Not_One_Of_The_Two_Registered_Constants_Is_Refused_Before_Any_Token_Is_Read(string executor)
    {
        var act = async () => await _validator.ValidateAsync(Sign(), executor, Correlation);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void The_Tenant_Issuer_Key_The_Settings_Refuse_Is_The_Key_The_Tenant_Scheme_Reads()
    {
        PlatformOperatorSettings.TenantIssuerConfigurationKey.Should().Be(
            LearnStack.Api.Authentication.TenantAuthenticationOptions.IssuerConfigurationKey);
    }

    private Task<PlatformOperatorValidationResult> Validate(string token) =>
        _validator.ValidateAsync(token, PlatformOperatorSettings.RebindToolExecutor, Correlation);

    private PlatformOperatorTokenValidator ValidatorFor(TenantAuthenticationIssuer issuer, PlatformOperatorBackchannel? backchannel = null)
    {
        var settings = PlatformOperatorSettings.FromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PlatformOperator:Issuer"] = issuer.Issuer,
                ["PlatformOperator:AuthorizedParties:0"] = "learnstack-operator",
                ["PlatformOperator:AcceptedAcr:0"] = "mfa",
            }).Build(),
            DeploymentMode.Development)!;
        var health = new PlatformOperatorMetadataHealth(new SystemClock());
        var manager = PlatformOperatorValidation.CreateConfigurationManager(
            settings, backchannel ?? _backchannel, health, NullLoggerFactory.Instance);
        return new PlatformOperatorTokenValidator(settings, manager, health, new SystemClock());
    }

    private string Sign(params (string Name, object? Value)[] replacements) => _issuer.Token(Payload(replacements));

    private string SignWithout(string name)
    {
        using var baseline = JsonDocument.Parse(Payload([]));
        return _issuer.Token("{" + string.Join(",", baseline.RootElement.EnumerateObject()
            .Where(property => property.Name != name)
            .Select(property => JsonSerializer.Serialize(property.Name) + ":" + property.Value.GetRawText())) + "}");
    }

    private string Payload((string Name, object? Value)[] replacements, TenantAuthenticationIssuer? issuer = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var claims = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["iss"] = (issuer ?? _issuer).Issuer,
            ["sub"] = Subject,
            ["aud"] = "learnstack-platform",
            ["iat"] = now - 5,
            ["exp"] = now + 600,
            ["azp"] = "learnstack-operator",
            ["acr"] = "mfa",
            ["auth_time"] = now - 30,
        };
        foreach (var (name, value) in replacements)
        {
            claims[name] = value;
        }

        return JsonSerializer.Serialize(claims);
    }

    private string Header(string type) =>
        JsonSerializer.Serialize(new { alg = "RS256", typ = type, kid = _issuer.CurrentKey.Kid });
}
