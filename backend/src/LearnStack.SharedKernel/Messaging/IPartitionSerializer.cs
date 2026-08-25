namespace LearnStack.SharedKernel.Messaging;

/// <summary>
/// Runs work sequentially within one partition key and concurrently across
/// different ones.
/// </summary>
/// <remarks>
/// This is the in-process stand-in for what a broker gives you by assigning a
/// partition to one consumer. It exists so the development transport carries the
/// same ordering guarantee as the durable path rather than a weaker one:
/// ordering assumptions that hold only because everything happened to run on one
/// thread are discovered in production.
/// </remarks>
public interface IPartitionSerializer
{
    /// <summary>Runs <paramref name="work"/> after anything already queued for this key.</summary>
    Task RunSequentiallyFor(string partitionKey, Func<Task> work);
}
