namespace LearnStack.SharedKernel.Identifiers;

/// <summary>
/// Production <see cref="IGuidFactory"/> backed by the BCL.
/// Registered as a singleton at the composition root.
/// </summary>
public sealed class SystemGuidFactory : IGuidFactory
{
    public Guid NewUuidV7() => Guid.CreateVersion7();

    public Guid NewUuidV4() => Guid.NewGuid();
}
