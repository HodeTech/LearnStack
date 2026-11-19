using System.Text;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Api.Authentication;
using LearnStack.Infrastructure.PlatformOperator;
using LearnStack.SharedKernel.Hosting;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Platform;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Authentication;

/// <summary>
/// The closed operator token profile, the failure labels, the settings guards and the classifier
/// guard (ADR-0101 § 2, § 3). The profile runs after the stock handler verified the signature, so
/// these unsigned fixtures exercise only the claim reading.
/// </summary>
public sealed class PlatformOperatorAuthenticationTests
{
    private const string Issuer = "https://operator.example.test/realms/learnstack-hub";
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_Token_Meeting_The_Whole_Profile_Yields_A_Witness_For_The_Pinned_Issuer()
    {
        var read = PlatformOperatorTokenProfile.TryRead(
            Token(), Settings(), Now, PlatformOperatorSettings.Executor, "00-trace-01", out var witness, out var cause);

        read.Should().BeTrue();
        witness!.Issuer.Should().Be(Issuer);
        witness.SubjectDigest.Should().Be(PlatformOperatorSubject.Digest(Issuer, "operator-sub-1"));
        witness.Acr.Should().Be("mfa");
        witness.Executor.Should().Be(PlatformOperatorSettings.Executor);
        witness.CorrelationId.Should().Be("00-trace-01");
        witness.AuthenticatedAt.Should().Be(Now.AddSeconds(-30));
        cause.Should().Be(default(PlatformOperatorTokenCause));
    }

    [Theory]
    [InlineData("iss-other", "Issuer")]
    [InlineData("iss-case", "Issuer")]
    [InlineData("iss-missing", "Issuer")]
    [InlineData("sub-missing", "Subject")]
    [InlineData("sub-blank", "Subject")]
    [InlineData("azp-missing", "AuthorizedParty")]
    [InlineData("azp-other", "AuthorizedParty")]
    [InlineData("acr-missing", "AuthenticationContext")]
    [InlineData("acr-other", "AuthenticationContext")]
    [InlineData("iat-missing", "IssuedAt")]
    [InlineData("iat-out-of-range", "IssuedAt")]
    [InlineData("auth-time-missing", "AuthenticationTime")]
    [InlineData("auth-time-out-of-range", "AuthenticationTime")]
    [InlineData("exp-missing", "Lifetime")]
    [InlineData("lifetime-too-long", "Lifetime")]
    [InlineData("lifetime-not-positive", "Lifetime")]
    [InlineData("auth-time-future", "AuthenticationAge")]
    [InlineData("auth-time-old", "AuthenticationAge")]
    public void Each_Profile_Defect_Is_Refused_With_Its_Own_Closed_Cause(string defect, string expectedCause)
    {
        var expected = Enum.Parse<PlatformOperatorTokenCause>(expectedCause);
        var iat = Now.ToUnixTimeSeconds() - 5;
        var claims = Claims();
        switch (defect)
        {
            case "iss-other": claims["iss"] = "https://elsewhere.example.test/realms/x"; break;
            case "iss-case": claims["iss"] = Issuer.ToUpperInvariant(); break;
            case "iss-missing": claims.Remove("iss"); break;
            case "sub-missing": claims.Remove("sub"); break;
            case "sub-blank": claims["sub"] = "  "; break;
            case "azp-missing": claims.Remove("azp"); break;
            case "azp-other": claims["azp"] = "learnstack-web"; break;
            case "acr-missing": claims.Remove("acr"); break;
            case "acr-other": claims["acr"] = "0"; break;
            case "iat-missing": claims.Remove("iat"); break;
            case "iat-out-of-range": claims["iat"] = long.MaxValue; break;
            case "auth-time-missing": claims.Remove("auth_time"); break;
            case "auth-time-out-of-range": claims["auth_time"] = long.MinValue; break;
            case "exp-missing": claims.Remove("exp"); break;
            case "lifetime-too-long": claims["exp"] = iat + 16 * 60; break;
            case "lifetime-not-positive": claims["exp"] = iat; break;
            case "auth-time-future": claims["auth_time"] = Now.ToUnixTimeSeconds() + 600; break;
            case "auth-time-old": claims["auth_time"] = Now.ToUnixTimeSeconds() - 3600; break;
            default: throw new ArgumentOutOfRangeException(nameof(defect));
        }

        PlatformOperatorTokenProfile.TryRead(Token(claims), Settings(), Now, PlatformOperatorSettings.Executor, null, out var witness, out var cause)
            .Should().BeFalse();
        witness.Should().BeNull();
        cause.Should().Be(expected);
        cause.Label().Should().NotBe("profile_invalid", "every cause has a closed counter label");
    }

    [Fact]
    public void Every_Profile_Cause_Has_A_Distinct_Closed_Label()
    {
        var labels = Enum.GetValues<PlatformOperatorTokenCause>().Select(cause => cause.Label()).ToList();

        labels.Should().OnlyHaveUniqueItems().And.OnlyContain(label => label.StartsWith("profile_", StringComparison.Ordinal));
    }

    public static TheoryData<Exception?, string> Failures() => new()
    {
        { null, "token_missing" },
        { new PlatformOperatorProfileException("profile_azp"), "profile_azp" },
        { new SecurityTokenExpiredException("x"), "token_expired" },
        { new SecurityTokenNotYetValidException("x"), "token_lifetime" },
        { new SecurityTokenNoExpirationException("x"), "token_lifetime" },
        { new SecurityTokenInvalidSignatureException("x"), "token_signature" },
        { new SecurityTokenSignatureKeyNotFoundException("x"), "token_signature" },
        { new SecurityTokenInvalidIssuerException("x"), "token_issuer" },
        { new SecurityTokenInvalidAudienceException("x"), "token_audience" },
        { new SecurityTokenInvalidAlgorithmException("x"), "token_algorithm" },
        { new SecurityTokenInvalidTypeException("x"), "token_type" },
        { new AggregateException(new SecurityTokenExpiredException("x"), new IOException()), "token_expired" },
        { new InvalidOperationException("IDX20803: Unable to obtain configuration."), "metadata_unavailable" },
        { new InvalidOperationException("wrapped", new IOException("refused")), "metadata_unavailable" },
        { new HttpRequestException("down"), "metadata_unavailable" },
        { new FormatException("other"), "token_invalid" },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public void An_Authentication_Failure_Maps_To_One_Closed_Label(Exception? failure, string expected) =>
        PlatformOperatorFailure.Classify(failure).Should().Be(expected);

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    public void A_Single_Factor_Acr_Is_Not_An_Accepted_Assurance(string acr)
    {
        var act = () => FromConfiguration(("PlatformOperator:AcceptedAcr:0", "mfa"), ("PlatformOperator:AcceptedAcr:1", acr));

        act.Should().Throw<InvalidOperationException>().WithMessage("*single-factor*");
    }

    [Fact]
    public void The_Operator_Issuer_Cannot_Be_The_Tenant_Issuer()
    {
        var act = () => FromConfiguration((TenantAuthenticationOptions.IssuerConfigurationKey, Issuer + "/"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*tenant issuer*");
    }

    [Fact]
    public void A_Different_Tenant_Issuer_Leaves_The_Operator_Configuration_Valid()
    {
        var settings = FromConfiguration(
            (TenantAuthenticationOptions.IssuerConfigurationKey, "https://identity.example.test/realms/learnstack"));

        settings.Should().NotBeNull();
        settings!.Issuer.Should().Be(Issuer);
    }

    [Fact]
    public void An_Unreadable_Maximum_Authentication_Age_Names_The_Expected_Format()
    {
        var act = () => FromConfiguration(("PlatformOperator:MaxAuthenticationAge", "fifteen minutes"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*00:15:00*");
    }

    [Fact]
    public void Operator_Metadata_Is_Honoured_Only_On_The_Operator_Surface_Behind_The_Operator_Policy()
    {
        var classifier = new OperationHttpClassifier(new EmptyCatalog());
        var operatorMetadata = InfrastructureOperationMetadata.PlatformOperator;
        var policy = new AuthorizeAttribute(PlatformOperatorAuthentication.PolicyName);

        Classify(classifier, "/api/platform/v1/killswitches/{key}", operatorMetadata, policy)
            .Should().Be(HttpOperationClassification.Infrastructure);
        Classify(classifier, "/API/Platform/V1/killswitches/{key}", operatorMetadata, policy)
            .Should().Be(HttpOperationClassification.Infrastructure, "routing is case-insensitive, so is the prefix");

        // The declaration alone proves nothing: a route off the prefix, a missing policy, another
        // policy or an anonymous allowance is refused, so the metadata cannot be used to make a
        // tenant route look like infrastructure and skip the tenant admission middlewares.
        Classify(classifier, "/api/v1/public/site", operatorMetadata, policy)
            .Should().Be(HttpOperationClassification.Refused);
        Classify(classifier, "/api/platform/v2/killswitches", operatorMetadata, policy)
            .Should().Be(HttpOperationClassification.Refused);
        Classify(classifier, "/api/platform/v1/killswitches/{key}", operatorMetadata)
            .Should().Be(HttpOperationClassification.Refused);
        Classify(classifier, "/api/platform/v1/killswitches/{key}", operatorMetadata, new AuthorizeAttribute("Other"))
            .Should().Be(HttpOperationClassification.Refused);
        Classify(classifier, "/api/platform/v1/killswitches/{key}", operatorMetadata, policy, new AllowAnonymousAttribute())
            .Should().Be(HttpOperationClassification.Refused);
    }

    [Fact]
    public async Task A_Challenge_For_A_Caller_Who_Hung_Up_Is_Not_Counted_As_An_Issuer_Fault()
    {
        // The abort raises a cancellation that the failure mapping would read as a metadata fault, and a
        // burst of aborted calls would then look like an outage on a dashboard.
        var causes = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == LearnStack.Infrastructure.PlatformOperator.PlatformOperatorMetrics.MeterName
                && instrument.Name == LearnStack.Infrastructure.PlatformOperator.PlatformOperatorMetrics.RefusalCounterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag is { Key: "cause", Value: string cause })
                {
                    causes.Add(cause);
                }
            }
        });
        listener.Start();

        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var http = new DefaultHttpContext
        {
            RequestAborted = aborted.Token,
            RequestServices = new ServiceCollection().AddOptions().AddLogging().BuildServiceProvider(),
        };
        http.Response.Body = new MemoryStream();
        var events = new PlatformOperatorEvents(
            Settings(), new FixedClock(Now), new PlatformOperatorMetadataHealth(new FixedClock(Now)),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PlatformOperatorEvents>.Instance);
        var challenge = new Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerChallengeContext(
            http,
            new Microsoft.AspNetCore.Authentication.AuthenticationScheme(
                PlatformOperatorAuthentication.Scheme, null, typeof(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerHandler)),
            new Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions(),
            new Microsoft.AspNetCore.Authentication.AuthenticationProperties())
        {
            AuthenticateFailure = new TaskCanceledException(),
        };

        // Writing the 401 to a caller who has gone is itself cancelled; the counting precedes it.
        await FluentActions.Awaiting(() => events.Challenge(challenge)).Should().ThrowAsync<OperationCanceledException>();

        causes.Should().Contain("request_aborted");
        causes.Should().NotContain("metadata_unavailable");
    }

    private sealed class FixedClock(DateTimeOffset instant) : LearnStack.SharedKernel.Time.IClock
    {
        public DateTimeOffset UtcNow => instant;
    }

    private static HttpOperationClassification Classify(
        OperationHttpClassifier classifier, string pattern, params object[] metadata) =>
        classifier.Classify(
            new RouteEndpoint(
                _ => Task.CompletedTask,
                RoutePatternFactory.Parse(pattern),
                0,
                new EndpointMetadataCollection(metadata),
                "test"),
            out _);

    private static PlatformOperatorSettings Settings() => FromConfiguration()!;

    private static PlatformOperatorSettings? FromConfiguration(params (string Key, string Value)[] extra)
    {
        var values = new Dictionary<string, string?>
        {
            ["PlatformOperator:Issuer"] = Issuer,
            ["PlatformOperator:AcceptedAcr:0"] = "mfa",
            ["PlatformOperator:AuthorizedParties:0"] = "learnstack-operator",
        };
        foreach (var (key, value) in extra)
        {
            values[key] = value;
        }

        return PlatformOperatorSettings.FromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(), DeploymentMode.SaaS);
    }

    private static Dictionary<string, object> Claims()
    {
        var issued = Now.ToUnixTimeSeconds() - 5;
        return new Dictionary<string, object>
        {
            ["iss"] = Issuer,
            ["sub"] = "operator-sub-1",
            ["aud"] = "learnstack-platform",
            ["azp"] = "learnstack-operator",
            ["acr"] = "mfa",
            ["iat"] = issued,
            ["exp"] = issued + 600,
            ["auth_time"] = Now.ToUnixTimeSeconds() - 30,
        };
    }

    private static JsonWebToken Token(Dictionary<string, object>? claims = null) =>
        new(Segment(new { alg = "RS256", typ = "JWT", kid = "owned-key" }) + "." + Segment(claims ?? Claims()) + ".c2ln");

    private static string Segment(object value) =>
        Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)));

    private sealed class EmptyCatalog : IOperationPolicyCatalog
    {
        public IReadOnlyCollection<OperationPolicy> All => [];
        public IReadOnlyCollection<PermissionRegistration> Definitions => [];
        public bool TryGet(Type requestType, out OperationPolicy policy)
        {
            policy = null!;
            return false;
        }

        public bool TryGetDefinition(PermissionScope scope, string key, out PermissionRegistration definition)
        {
            definition = null!;
            return false;
        }
    }
}
