using FluentAssertions;
using LearnStack.Api.Common;
using LearnStack.SharedKernel.Errors;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Common;

/// <summary>
/// HttpStatusMap mirrors Standards 09 § Result Type's error-code table.
/// Adding a new code requires updating both places; these tests catch the
/// drift.
/// </summary>
public sealed class HttpStatusMapTests
{
    [Theory]
    [InlineData("validation_failed", 400)]
    [InlineData("unauthorized", 401)]
    [InlineData("forbidden", 403)]
    [InlineData("feature_disabled", 403)]
    [InlineData("not_found", 404)]
    [InlineData("tenant_mismatch", 404)]
    [InlineData("concurrency_conflict", 409)]
    [InlineData("business_rule_violation", 409)]
    [InlineData("rate_limited", 429)]
    [InlineData("dependency_unavailable", 503)]
    [InlineData("unknown_code", 500)]
    public void For_Code_Matches_StandardsTable(string code, int expected)
    {
        HttpStatusMap.For(code).Should().Be(expected);
    }

    [Fact]
    public void For_ProviderException_With_ClientError_Maps_To_400()
    {
        var ex = new ProviderException(
            providerName: "test",
            message: "bad input",
            isClientError: true);

        HttpStatusMap.For(ex).Should().Be(400);
    }

    [Fact]
    public void For_ProviderException_With_ServerError_Maps_To_503()
    {
        var ex = new ProviderException(
            providerName: "test",
            message: "upstream down",
            isClientError: false);

        HttpStatusMap.For(ex).Should().Be(503);
    }

    [Fact]
    public void For_OperationCanceled_Maps_To_499()
    {
        // 499 (client closed request) — Standards 10 § Tracing leaves
        // OperationCanceled spans Unset; the HTTP surface returns 499 for
        // the client disconnect.
        HttpStatusMap.For(new OperationCanceledException()).Should().Be(499);
    }
}
