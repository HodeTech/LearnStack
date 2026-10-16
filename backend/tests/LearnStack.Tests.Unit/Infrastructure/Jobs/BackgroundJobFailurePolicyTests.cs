using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Jobs;

public sealed class BackgroundJobFailurePolicyTests
{
    [Theory]
    [InlineData("concurrency_conflict")]
    [InlineData("rate_limited")]
    [InlineData("dependency_unavailable")]
    [InlineData("audit_unavailable")]
    public void All_declared_transient_Result_codes_retry_with_the_exact_closed_code(string code)
    {
        Classify(Result<None>.Fail(Error(code))).Should().Be(new BackgroundJobFailureDecision(BackgroundJobFailureDisposition.Retry, code, null));
    }

    [Theory]
    [InlineData("validation_failed")]
    [InlineData("unauthorized")]
    [InlineData("forbidden")]
    [InlineData("tenant_mismatch")]
    [InlineData("resource_scope_violation")]
    [InlineData("not_found")]
    [InlineData("business_rule_violation")]
    [InlineData("feature_disabled")]
    [InlineData("unsupported_locale")]
    [InlineData("audit_unclassified_operation")]
    public void All_declared_permanent_Result_codes_terminalize_without_persisting_the_supplied_details(string code)
    {
        var error = new Error(new LocalizedMessage("lockey_" + code), new Dictionary<string, IReadOnlyList<LocalizedMessage>>
        { ["secret-field"] = [new("lockey_secret_parameter_value")] });
        Classify(Result<None>.Fail(error)).Should().Be(new BackgroundJobFailureDecision(BackgroundJobFailureDisposition.Terminal,
            "permanent_failure", BackgroundJobTerminalReason.PermanentFailure));
    }

    [Fact]
    public void Top_level_exception_types_follow_the_accepted_table_and_concurrency_matches_Result()
    {
        var cases = new (Exception Failure, BackgroundJobFailureDisposition Disposition, string Code)[]
        {
            (new AggregateConcurrencyException("secret"), BackgroundJobFailureDisposition.Retry, "concurrency_conflict"),
            (new InfrastructureException("secret"), BackgroundJobFailureDisposition.Retry, "dependency_unavailable"),
            (new AuditWriteFailedException("secret"), BackgroundJobFailureDisposition.Retry, "audit_unavailable"),
            (new DomainException("secret"), BackgroundJobFailureDisposition.Terminal, "permanent_failure"),
            (new TenantContextMissingException("secret"), BackgroundJobFailureDisposition.Terminal, "permanent_failure"),
            (new OperationCanceledException("foreign cancellation secret"), BackgroundJobFailureDisposition.Retry, "unexpected_fault"),
            (new InvalidOperationException("secret", new AuditWriteFailedException("inner secret")), BackgroundJobFailureDisposition.Retry, "unexpected_fault"),
        };
        foreach (var item in cases)
        {
            var actual = Classify(failure: item.Failure);
            actual.Disposition.Should().Be(item.Disposition);
            actual.ErrorCode.Should().Be(item.Code);
            actual.ToString().Should().NotContain("secret");
        }
        Classify(failure: new AggregateConcurrencyException("secret"))
            .Should().Be(Classify(Result<None>.Fail(Error("concurrency_conflict"))));
    }

    [Fact]
    public void Provider_transient_standard_code_precedes_client_flag_and_other_flags_have_finite_dispositions()
    {
        foreach (var code in new[] { "rate_limited", "dependency_unavailable", "concurrency_conflict", "audit_unavailable" })
            Classify(failure: new ProviderException(Error(code), "secret-provider", "secret", true))
                .Should().Be(new BackgroundJobFailureDecision(BackgroundJobFailureDisposition.Retry, code, null));
        Classify(failure: new ProviderException(Error("validation_failed"), "secret-provider", "secret", true)).Disposition
            .Should().Be(BackgroundJobFailureDisposition.Terminal);
        Classify(failure: new ProviderException(Error("unknown_secret"), "secret-provider", "secret", false))
            .Should().Be(new BackgroundJobFailureDecision(BackgroundJobFailureDisposition.Retry, "dependency_unavailable", null));
    }

    [Fact]
    public void Durability_then_owned_cancellation_precede_Result_and_exception_classification()
    {
        var refused = Result<None>.Fail(Error("forbidden"));
        var failure = new DomainException("secret");
        BackgroundJobFailurePolicy.Classify(refused, failure, true, true, true).Disposition.Should().Be(BackgroundJobFailureDisposition.Succeeded);
        BackgroundJobFailurePolicy.Classify(refused, failure, false, true, true).Disposition.Should().Be(BackgroundJobFailureDisposition.Reconcile);
        BackgroundJobFailurePolicy.Classify(refused, failure, false, false, true).Disposition.Should().Be(BackgroundJobFailureDisposition.RetainOwnership);
        Classify(Result<None>.Ok(None.Value)).Disposition.Should().Be(BackgroundJobFailureDisposition.Retry,
            "a successful handler Result does not itself acknowledge a business COMMIT");
        Classify(Result<None>.Fail(Error("unknown_secret"))).ErrorCode.Should().Be("unexpected_fault");
    }

    [Fact]
    public void Five_exact_waits_belong_to_six_committed_starts_and_terminal_reasons_are_not_error_codes()
    {
        Enumerable.Range(1, 5).Select(BackgroundJobFailurePolicy.GetRetryDelay).Should().Equal(
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5), TimeSpan.FromHours(1));
        var zero = () => BackgroundJobFailurePolicy.GetRetryDelay(0);
        var exhausted = () => BackgroundJobFailurePolicy.GetRetryDelay(6);
        zero.Should().Throw<ArgumentOutOfRangeException>();
        exhausted.Should().Throw<ArgumentOutOfRangeException>();
        BackgroundJobFailurePolicy.TerminalCode(BackgroundJobTerminalReason.RetiredDefinition).Should().Be("retired_definition");
        BackgroundJobFailurePolicy.TerminalCode(BackgroundJobTerminalReason.InvalidEnvelope).Should().Be("invalid_envelope");
        BackgroundJobFailurePolicy.TerminalCode(BackgroundJobTerminalReason.RetryExhausted).Should().Be("retry_exhausted");
    }

    private static Error Error(string code) => new(new LocalizedMessage("lockey_" + code));
    private static BackgroundJobFailureDecision Classify(Result<None>? result = null, Exception? failure = null) =>
        BackgroundJobFailurePolicy.Classify(result, failure, false, false, false);
}
