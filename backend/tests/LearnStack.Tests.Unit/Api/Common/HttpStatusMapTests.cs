using FluentAssertions;
using LearnStack.Api.Common;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
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
    public void For_ProviderException_Default_Derives_503_From_DependencyUnavailable_Code()
    {
        // IsClientError gates Sentry capture only — it does NOT drive the
        // HTTP status. A bare provider failure carries the default
        // dependency_unavailable code → 503, regardless of IsClientError,
        // so the body code and status stay consistent.
        var clientError = new ProviderException("test", "bad input", isClientError: true);
        var serverError = new ProviderException("test", "upstream down", isClientError: false);

        HttpStatusMap.For(clientError).Should().Be(503);
        HttpStatusMap.For(serverError).Should().Be(503);
    }

    [Fact]
    public void For_ProviderException_With_Explicit_Error_Derives_Status_From_That_Code()
    {
        // An adapter surfacing a provider 4xx as client-actionable passes an
        // explicit Error; the status follows that code so body+status agree.
        var ex = new ProviderException(
            error: new Error(new LocalizedMessage("lockey_validation_failed")),
            providerName: "test",
            message: "provider rejected the request",
            isClientError: true);

        HttpStatusMap.For(ex).Should().Be(400);
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
