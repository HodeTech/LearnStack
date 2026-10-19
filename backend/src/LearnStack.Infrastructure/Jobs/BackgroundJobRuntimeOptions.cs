namespace LearnStack.Infrastructure.Jobs;

/// <summary>ADR-0047's finite runtime bounds; validate before any worker is started.</summary>
public sealed record BackgroundJobRuntimeOptions
{
    public TimeSpan SqlTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan TransactionTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ExternalWaitTimeout { get; init; } = TimeSpan.FromHours(1);
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan RenewalInterval { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan QuiescenceGrace { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ControlPoll { get; init; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan SnapshotInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan HandoffRepairGrace { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan RecoveryProbeInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ProgressTimeout { get; init; } = TimeSpan.FromSeconds(75);
    public TimeSpan BridgeWaitTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public int ControlBatchSize { get; init; } = 100;
    public int FanoutPageSize { get; init; } = 100;

    public const int RetainedStopThreshold = 4;
    public const int MaxOutstandingBridgeCalls = 4;
    public const int MaxNormalAttempts = 6;
    public const int MaxFinalizeAttempts = 6;
    public const int MaxUnsuccessfulRecoveryProbes = 6;

    /// <summary>Five waits between six separately committed admissions; definitions cannot override them.</summary>
    public static IReadOnlyList<TimeSpan> RetryDelays { get; } = Array.AsReadOnly(new[]
    {
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(5), TimeSpan.FromHours(1),
    });

    public static int RetainedHardBound(int workers) => workers is >= 1 and <= 16
        ? workers + RetainedStopThreshold
        : throw new ArgumentOutOfRangeException(nameof(workers));

    public void Validate()
    {
        if (!PositiveThrough(SqlTimeout, TimeSpan.FromSeconds(5))
            || !PositiveThrough(ProbeTimeout, TimeSpan.FromSeconds(30))
            || !PositiveThrough(TransactionTimeout, TimeSpan.FromMinutes(5))
            || !PositiveThrough(ExternalWaitTimeout, TimeSpan.FromHours(24))
            || !Between(LeaseDuration, TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(10))
            || !Between(RenewalInterval, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60))
            || LeaseDuration <= RenewalInterval + RenewalInterval + SqlTimeout + SqlTimeout + TimeSpan.FromSeconds(5)
            || !PositiveThrough(QuiescenceGrace, TimeSpan.FromSeconds(30))
            || !Between(ControlPoll, TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(1))
            || !Between(SnapshotInterval, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60))
            || !Between(HandoffRepairGrace, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(300))
            || !Between(RecoveryProbeInterval, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(300))
            || !PositiveThrough(ShutdownTimeout, TimeSpan.FromSeconds(30))
            || !Between(ProgressTimeout, LeaseDuration + TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(15))
            || !PositiveThrough(BridgeWaitTimeout, TimeSpan.FromSeconds(10))
            || ControlBatchSize is < 1 or > 100 || FanoutPageSize is < 1 or > 100)
            throw new ArgumentException("Background job runtime bounds are invalid.");
    }

    private static bool PositiveThrough(TimeSpan value, TimeSpan maximum) => value > TimeSpan.Zero && value <= maximum;
    private static bool Between(TimeSpan value, TimeSpan minimum, TimeSpan maximum) => value >= minimum && value <= maximum;
}
