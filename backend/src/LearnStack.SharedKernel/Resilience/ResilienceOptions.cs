namespace LearnStack.SharedKernel.Resilience;

/// <summary>
/// Configuration shape bound from <c>appsettings.Resilience:&lt;portName&gt;:</c>
/// per ADR-0032 § Sub-decision 5. <c>AddProviderResilience&lt;TPort&gt;</c> reads
/// one of these per provider port and assembles the Polly v8 pipeline the
/// adapter then injects as a collaborator.
/// </summary>
/// <remarks>
/// Defaults match the conservative-but-useful shape from Standards 09
/// § Provider Resilience (up to 2 retries - three calls - with jitter, breaker after
/// 50 % failure ratio over 30 s, single-attempt timeout 10 s, no bulkhead).
/// </remarks>
public sealed class ResilienceOptions
{
    public RetryOptions Retry { get; set; } = new();

    public CircuitBreakerOptions CircuitBreaker { get; set; } = new();

    public TimeoutOptions Timeout { get; set; } = new();

    public BulkheadOptions? Bulkhead { get; set; }
}

public sealed class RetryOptions
{
    /// <summary>
    /// Number of RETRIES after the first call fails, not the total call count:
    /// <c>2</c> issues up to three calls. Named for Polly v8's
    /// <c>RetryStrategyOptions.MaxRetryAttempts</c>, which it maps to 1:1.
    /// <c>0</c> disables retry.
    /// </summary>
    /// <remarks>
    /// It was called <c>MaxAttempts</c>, which reads as a total and was
    /// documented as one ("retry up to 3 attempts") while behaving as a retry
    /// count - so every configured value issued one more call than the name
    /// promised. The tests had already re-read the field as retries. Renaming
    /// settles it in the direction that needs no arithmetic at the mapping.
    /// </remarks>
    public int MaxRetryAttempts { get; set; } = 2;
    public double DelaySeconds { get; set; } = 1.0;
    public bool UseJitter { get; set; } = true;
    public bool Enabled { get; set; } = true;
}

public sealed class CircuitBreakerOptions
{
    public double FailureRatio { get; set; } = 0.5;
    public double SamplingDurationSeconds { get; set; } = 30;
    public int MinimumThroughput { get; set; } = 10;
    public double BreakDurationSeconds { get; set; } = 30;
    public bool Enabled { get; set; } = true;
}

public sealed class TimeoutOptions
{
    public double TotalSeconds { get; set; } = 10;
    public bool Enabled { get; set; } = true;
}

public sealed class BulkheadOptions
{
    public int MaxConcurrency { get; set; } = 100;
    public int QueueLength { get; set; }
}
