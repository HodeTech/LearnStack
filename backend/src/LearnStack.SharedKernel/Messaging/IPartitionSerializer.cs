namespace LearnStack.SharedKernel.Messaging;

/// <summary>
/// Runs work sequentially within one partition key and concurrently across
/// different ones.
/// </summary>
/// <remarks>
/// Serialization applies to invocation within this process. It does not
/// establish durable publish order across commits and retries; P02b-2 G3 owns
/// that decision.
/// </remarks>
public interface IPartitionSerializer
{
    /// <summary>Runs <paramref name="work"/> after anything already queued for this key.</summary>
    Task RunSequentiallyFor(string partitionKey, Func<Task> work);
}
