using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;

namespace LearnStack.Infrastructure.Jobs;

internal enum BackgroundJobFailureDisposition
{
    Succeeded,
    Reconcile,
    RetainOwnership,
    Retry,
    Terminal,
}

internal enum BackgroundJobTerminalReason
{
    RetiredDefinition,
    InvalidEnvelope,
    PermanentFailure,
    RetryExhausted,
}

/// <summary>A finite control decision; it carries no handler or exception text.</summary>
internal sealed record BackgroundJobFailureDecision(BackgroundJobFailureDisposition Disposition,
    string? ErrorCode, BackgroundJobTerminalReason? TerminalReason);

/// <summary>ADR-0047's ordered top-level Result/exception policy.</summary>
internal static class BackgroundJobFailurePolicy
{
    internal static BackgroundJobFailureDecision Classify(Result<None>? result, Exception? failure,
        bool commitAcknowledged, bool durableOutcomeUncertain, bool ownedCancellationRequested)
    {
        if (commitAcknowledged) return new(BackgroundJobFailureDisposition.Succeeded, null, null);
        if (durableOutcomeUncertain) return new(BackgroundJobFailureDisposition.Reconcile, "unknown_outcome", null);
        if (ownedCancellationRequested) return new(BackgroundJobFailureDisposition.RetainOwnership, null, null);

        // No recursive inner-exception inspection: wrapping is not authority to
        // reinterpret the actual operation's top-level outcome.
        if (failure is not null)
        {
            return failure switch
            {
                AggregateConcurrencyException => Retry("concurrency_conflict"),
                AuditWriteFailedException => Retry("audit_unavailable"),
                InfrastructureException => Retry("dependency_unavailable"),
                DomainException or TenantContextMissingException => Permanent(),
                ProviderException provider when Transient(provider.Error.Code) => Retry(provider.Error.Code),
                ProviderException { IsClientError: true } => Permanent(),
                ProviderException => Retry("dependency_unavailable"),
                _ => Retry("unexpected_fault"),
            };
        }

        return result is { IsFailure: true } ? ClassifyCode(result.Error.Code) : Retry("unexpected_fault");
    }

    internal static TimeSpan GetRetryDelay(int committedNormalAttempts) => committedNormalAttempts is >= 1 and < BackgroundJobRuntimeOptions.MaxNormalAttempts
        ? BackgroundJobRuntimeOptions.RetryDelays[committedNormalAttempts - 1]
        : throw new ArgumentOutOfRangeException(nameof(committedNormalAttempts));

    internal static string TerminalCode(BackgroundJobTerminalReason reason) => reason switch
    {
        BackgroundJobTerminalReason.RetiredDefinition => "retired_definition",
        BackgroundJobTerminalReason.InvalidEnvelope => "invalid_envelope",
        BackgroundJobTerminalReason.PermanentFailure => "permanent_failure",
        BackgroundJobTerminalReason.RetryExhausted => "retry_exhausted",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    private static BackgroundJobFailureDecision ClassifyCode(string code) => code switch
    {
        "concurrency_conflict" or "rate_limited" or "dependency_unavailable" or "audit_unavailable" => Retry(code),
        "validation_failed" or "unauthorized" or "forbidden" or "tenant_mismatch" or "resource_scope_violation"
            or "not_found" or "business_rule_violation" or "feature_disabled" or "unsupported_locale"
            or "audit_unclassified_operation" => Permanent(),
        _ => Retry("unexpected_fault"),
    };

    private static bool Transient(string code) => code is "concurrency_conflict" or "rate_limited"
        or "dependency_unavailable" or "audit_unavailable";
    private static BackgroundJobFailureDecision Retry(string code) => new(BackgroundJobFailureDisposition.Retry, code, null);
    private static BackgroundJobFailureDecision Permanent() => new(BackgroundJobFailureDisposition.Terminal,
        "permanent_failure", BackgroundJobTerminalReason.PermanentFailure);
}
