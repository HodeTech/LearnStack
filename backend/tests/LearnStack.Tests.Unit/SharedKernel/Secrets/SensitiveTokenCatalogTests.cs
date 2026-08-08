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
    public void IsSensitive_False_For_Ordinary_Names(string name)
    {
        SensitiveTokenCatalog.IsSensitive(name).Should().BeFalse();
    }
}
