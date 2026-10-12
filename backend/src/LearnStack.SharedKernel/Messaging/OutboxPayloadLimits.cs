namespace LearnStack.SharedKernel.Messaging;

/// <summary>The producer's complete canonical UTF-8 payload budget (ADR-0057).</summary>
public static class OutboxPayloadLimits
{
    /// <summary>Inclusive; measured before INSERT, independently of JSONB storage size.</summary>
    public const int MaxBytes = 256 * 1024;
}
