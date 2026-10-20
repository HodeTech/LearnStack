namespace LearnStack.Api.Authentication;

internal enum SubjectAdmissionOutcome
{
    Acquired,
    QuotaExhausted,
    RegistryCapacity,
    RegistryFault,
}

/// <summary>A detached decision: no child lease or limiter ownership escapes admission.</summary>
internal sealed record SubjectAdmissionDecision(SubjectAdmissionOutcome Outcome, TimeSpan? RetryAfter)
{
    internal bool IsAcquired => Outcome == SubjectAdmissionOutcome.Acquired;
}
