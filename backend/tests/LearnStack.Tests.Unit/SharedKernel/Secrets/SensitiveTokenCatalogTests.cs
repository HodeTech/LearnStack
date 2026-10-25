using System.Globalization;
using FluentAssertions;
using LearnStack.SharedKernel.Secrets;
using Xunit;

namespace LearnStack.Tests.Unit.SharedKernel.Secrets;

/// <summary>
/// Word-boundary matching (review-4 L1): the catalog must flag genuine
/// sensitive segments without over-redacting common identifiers that merely
/// contain a token as a substring (e.g. "ssn" inside "className").
/// </summary>
public sealed class SensitiveTokenCatalogTests
{
    [Fact]
    public void DefaultTokens_Is_The_Complete_Immutable_Union()
    {
        string[] expected =
        [
            "apikey", "authenticationheader", "authorization", "authheader", "cardnumber",
            "connectionstring", "cookie", "credential", "creditcard", "cvc", "cvv", "dsn",
            "encryptionkey", "hmac", "iban", "jwt", "otp", "pan", "passwd", "password", "pin",
            "privatekey", "pwd", "secret", "setcookie", "signature", "signingkey", "ssn", "tckn",
            "token", "vkn",
        ];

        var snapshot = SensitiveTokenCatalog.DefaultTokens;
        snapshot.Should().BeEquivalentTo(expected);
        snapshot.Should().OnlyHaveUniqueItems();
        snapshot.Should().BeSameAs(SensitiveTokenCatalog.DefaultTokens, "the complete union is cached");
        snapshot.Should().NotBeOfType<string[]>();
        var collection = snapshot.Should().BeAssignableTo<ICollection<string>>().Subject;
        collection.IsReadOnly.Should().BeTrue();
        Action mutate = () => collection.Add("invented");
        mutate.Should().Throw<NotSupportedException>();
        SensitiveTokenCatalog.DefaultTokens.Should().BeEquivalentTo(expected);
    }

    [Theory]
    [InlineData("Password")]
    [InlineData("UserPassword")]
    [InlineData("password")]
    [InlineData("AccessToken")]
    [InlineData("token")]
    [InlineData("ApiKey")]
    [InlineData("api_key")]
    [InlineData("apikey")]
    [InlineData("CardNumber")]
    [InlineData("card_number")]
    [InlineData("Authorization")]
    [InlineData("auth_header")]
    [InlineData("AuthHeader")]
    [InlineData("authheader")]    // joined, no separator/case transition to split on
    [InlineData("Dsn")]
    [InlineData("Jwt")]
    [InlineData("Secret")]
    [InlineData("ClientSecret")]
    [InlineData("Ssn")]
    [InlineData("SSN")]
    [InlineData("SSNToken")]
    [InlineData("Tckn")]
    [InlineData("Vkn")]
    [InlineData("TaxVkn")]
    [InlineData("Iban")]
    [InlineData("Cvv")]
    [InlineData("Cvc")]
    // Added after a review pass walked the catalogue against the names the
    // kernel's own docs call secret: every one of these reached Loki / Sentry
    // in cleartext because the token list did not carry them.
    [InlineData("ConnectionString")]
    [InlineData("connection_string")]
    [InlineData("PrivateKey")]
    [InlineData("PrivateKeyPath")]
    [InlineData("SigningKey")]
    [InlineData("EncryptionKey")]
    [InlineData("Signature")]
    [InlineData("Hmac")]
    [InlineData("Cookie")]
    [InlineData("SetCookie")]
    [InlineData("Pwd")]
    [InlineData("Pin")]
    [InlineData("Otp")]
    [InlineData("CreditCard")]
    [InlineData("Pan")]
    // Accepted over-redaction: `signature` is a whole segment here, so a
    // count of signatures redacts too. Losing a count is cheaper than
    // shipping an HMAC that lets someone forge the envelope it signed.
    [InlineData("SignatureCount")]
    [InlineData("AuthenticationHeader")]
    [InlineData("authentication_header")]
    [InlineData("authenticationheader")]
    public void IsSensitive_True_For_Token_Segments(string name)
    {
        SensitiveTokenCatalog.IsSensitive(name).Should().BeTrue();
    }

    [Theory]
    [InlineData("ClassName")]      // contains "ssn" as a raw substring — must NOT match
    [InlineData("BusinessName")]   // contains "ssn"
    [InlineData("ProcessName")]    // contains "ssn"
    [InlineData("UserName")]
    [InlineData("DisplayName")]
    [InlineData("CourseId")]
    [InlineData("TenantId")]
    [InlineData("Description")]    // contains "dsn"? no — but defensive
    [InlineData("Status")]
    [InlineData("CreatedAt")]
    [InlineData("")]
    // Guards the tokens added alongside them: each of these contains a new
    // token as a raw substring and must survive the word-boundary rule.
    [InlineData("Panel")]          // contains "pan"
    [InlineData("Spanish")]        // contains "pan"
    [InlineData("Pinned")]         // contains "pin"
    [InlineData("Options")]        // contains "otp"
    [InlineData("Cookbook")]       // near "cookie"
    public void IsSensitive_False_For_Ordinary_Names(string name)
    {
        SensitiveTokenCatalog.IsSensitive(name).Should().BeFalse();
    }

    [Theory]
    [InlineData("api.key")]
    [InlineData("api-key")]
    [InlineData("api/key")]
    [InlineData("api key")]
    [InlineData("api::key")]
    [InlineData("__auth..header--")]
    [InlineData("request2Token")]
    [InlineData("HTTPAuthorization")]
    public void IsSensitive_Preserves_Separator_And_Acronym_Boundaries(string name) =>
        SensitiveTokenCatalog.IsSensitive(name).Should().BeTrue();

    [Fact]
    public void IsSensitive_Is_Ordinal_Under_Turkish_Culture_And_Handles_Null()
    {
        var previous = CultureInfo.CurrentCulture;
        var previousUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;
            SensitiveTokenCatalog.IsSensitive("SIGNING_KEY").Should().BeTrue();
            SensitiveTokenCatalog.IsSensitive("PRIVATE_KEY").Should().BeTrue();
            SensitiveTokenCatalog.IsSensitive("ClassName").Should().BeFalse();
            SensitiveTokenCatalog.IsSensitive("---").Should().BeFalse();
            SensitiveTokenCatalog.IsSensitive(null!).Should().BeFalse();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
            CultureInfo.CurrentUICulture = previousUi;
        }
    }
}
