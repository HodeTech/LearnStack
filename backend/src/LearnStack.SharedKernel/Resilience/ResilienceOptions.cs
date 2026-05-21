namespace LearnStack.SharedKernel.Resilience;

/// <summary>
/// Configuration shape bound from <c>appsettings.Resilience:&lt;portName&gt;:</c>
/// per ADR-0032 § Sub-decision 5. The decorator reads one of these per
/// provider port and assembles the Polly v8 pipeline.
/// </summary>
/// <remarks>
/// Defaults match the conservative-but-useful shape from Standards 09
/// § Provider Resilience (retry up to 3 attempts with jitter, breaker after
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
    public int MaxAttempts { get; set; } = 3;
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
