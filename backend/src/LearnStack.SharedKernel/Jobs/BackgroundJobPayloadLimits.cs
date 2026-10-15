namespace LearnStack.SharedKernel.Jobs;

/// <summary>Inclusive canonical UTF-8 bounds, independent of JSONB storage formatting.</summary>
public static class BackgroundJobPayloadLimits
{
    public const int Parameters = 262_144;
    public const int Plan = 65_536;
    public const int Outcome = 65_536;
    public const int FailureDescriptor = 256;
}
