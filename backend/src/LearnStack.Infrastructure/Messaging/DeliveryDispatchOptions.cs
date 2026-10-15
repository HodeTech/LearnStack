namespace LearnStack.Infrastructure.Messaging;

/// <summary>Finite execution bounds for ADR-0046's independent subscriber worker.</summary>
public sealed record DeliveryDispatchOptions
{
    public TimeSpan SqlTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ExecutionTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan IdlePoll { get; init; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan ProgressTimeout { get; init; } = TimeSpan.FromSeconds(45);
    public TimeSpan SnapshotInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Bounds retained execution/cleanup tasks; normal execution is awaited serially.</summary>
    public int MaxOutstandingExecutions { get; init; } = 4;

    public void Validate()
    {
        if (SqlTimeout <= TimeSpan.Zero || SqlTimeout > TimeSpan.FromSeconds(5)
            || ExecutionTimeout <= TimeSpan.Zero || ExecutionTimeout > TimeSpan.FromSeconds(10)
            || LeaseDuration <= ExecutionTimeout + SqlTimeout + SqlTimeout
            || LeaseDuration > TimeSpan.FromSeconds(30)
            || IdlePoll <= TimeSpan.Zero || IdlePoll > TimeSpan.FromSeconds(1)
            || SnapshotInterval <= TimeSpan.Zero || SnapshotInterval > TimeSpan.FromSeconds(5)
            || ProgressTimeout <= LeaseDuration || ProgressTimeout > TimeSpan.FromSeconds(45)
            || ShutdownTimeout <= TimeSpan.Zero || ShutdownTimeout > TimeSpan.FromSeconds(30)
            || MaxOutstandingExecutions is < 1 or > 4)
        {
            throw new ArgumentException("Delivery execution bounds are invalid.");
        }
    }
}
