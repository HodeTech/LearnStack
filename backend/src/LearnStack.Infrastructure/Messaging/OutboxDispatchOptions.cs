namespace LearnStack.Infrastructure.Messaging;

/// <summary>Finite execution bounds for ADR-0046's single dispatcher.</summary>
public sealed record OutboxDispatchOptions
{
    public TimeSpan SqlTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan PublishTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan IdlePoll { get; init; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan ProgressTimeout { get; init; } = TimeSpan.FromSeconds(45);
    public TimeSpan SnapshotInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxOutstandingPublishes { get; init; } = 4;

    public void Validate()
    {
        if (SqlTimeout <= TimeSpan.Zero || SqlTimeout > TimeSpan.FromSeconds(5)
            || PublishTimeout <= TimeSpan.Zero || PublishTimeout > TimeSpan.FromSeconds(10)
            || LeaseDuration <= PublishTimeout + SqlTimeout + SqlTimeout
            || LeaseDuration > TimeSpan.FromSeconds(30)
            || IdlePoll <= TimeSpan.Zero || IdlePoll > TimeSpan.FromSeconds(1)
            || SnapshotInterval <= TimeSpan.Zero || SnapshotInterval > TimeSpan.FromSeconds(5)
            || ProgressTimeout <= LeaseDuration || ProgressTimeout > TimeSpan.FromSeconds(45)
            || ShutdownTimeout <= TimeSpan.Zero || ShutdownTimeout > TimeSpan.FromSeconds(30)
            || MaxOutstandingPublishes is < 1 or > 4)
        {
            throw new ArgumentException("Outbox execution bounds are invalid.");
        }
    }
}
