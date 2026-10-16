namespace LearnStack.Infrastructure.Jobs;

/// <summary>Sanitized application-pool settings captured without constructing its lazy source.</summary>
public sealed record BackgroundJobApplicationPoolSettings(bool Pooling, int MaxPoolSize);
