using System.Threading.RateLimiting;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Resilience;
using Polly;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Retry;
using Polly.Timeout;

namespace LearnStack.Infrastructure.Resilience;

/// <summary>
/// Default <see cref="IProviderResilience{TPort}"/> implementation that
/// assembles a Polly v8 <see cref="ResiliencePipeline"/> from the supplied
/// <see cref="ResilienceOptions"/>. Per ADR-0032 § Sub-decision 5 every
/// provider adapter consumes one of these in its constructor and routes
/// outbound calls through <see cref="Pipeline"/>.
/// </summary>
/// <remarks>
/// The pipeline order — retry → circuit breaker → timeout → bulkhead — is
/// the Polly recommended ordering: retries see the underlying failure;
/// the breaker opens against sustained failure ratios; the timeout bounds
/// a single attempt; the bulkhead caps concurrent in-flight calls so a
/// slow upstream cannot starve the host. Retry only applies when the
/// exception is a non-client <see cref="ProviderException"/> or a
/// transient <see cref="InfrastructureException"/>.
/// </remarks>
internal sealed class ProviderResilience<TPort> : IProviderResilience<TPort>
    where TPort : class
{
    public ProviderResilience(string portName, ResilienceOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        ArgumentNullException.ThrowIfNull(options);

        PortName = portName;
        Pipeline = BuildPipeline(options);
    }

    public ResiliencePipeline Pipeline { get; }

    public string PortName { get; }

    private static ResiliencePipeline BuildPipeline(ResilienceOptions options)
    {
        var builder = new ResiliencePipelineBuilder();

        if (options.Retry.Enabled && options.Retry.MaxAttempts > 0)
        {
            builder.AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<InfrastructureException>()
                    .Handle<ProviderException>(static ex => !ex.IsClientError),
                MaxRetryAttempts = options.Retry.MaxAttempts,
                Delay = TimeSpan.FromSeconds(options.Retry.DelaySeconds),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = options.Retry.UseJitter,
            });
        }

        if (options.CircuitBreaker.Enabled)
        {
            builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<InfrastructureException>()
                    .Handle<ProviderException>(static ex => !ex.IsClientError),
                FailureRatio = options.CircuitBreaker.FailureRatio,
                SamplingDuration = TimeSpan.FromSeconds(options.CircuitBreaker.SamplingDurationSeconds),
                MinimumThroughput = options.CircuitBreaker.MinimumThroughput,
                BreakDuration = TimeSpan.FromSeconds(options.CircuitBreaker.BreakDurationSeconds),
            });
        }

        if (options.Timeout.Enabled && options.Timeout.TotalSeconds > 0)
        {
            builder.AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = TimeSpan.FromSeconds(options.Timeout.TotalSeconds),
            });
        }

        // Bulkhead — Polly v8 maps "bulkhead" onto the rate-limiter
        // strategy backed by System.Threading.RateLimiting.ConcurrencyLimiter.
        // Only wired when MaxConcurrency > 0 so the default options shape
        // (Bulkhead = null) leaves the pipeline unchanged.
        if (options.Bulkhead is { MaxConcurrency: > 0 } bulkhead)
        {
            builder.AddRateLimiter(new RateLimiterStrategyOptions
            {
                DefaultRateLimiterOptions = new ConcurrencyLimiterOptions
                {
                    PermitLimit = bulkhead.MaxConcurrency,
                    QueueLimit = Math.Max(0, bulkhead.QueueLength),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                },
            });
        }

        return builder.Build();
    }
}
