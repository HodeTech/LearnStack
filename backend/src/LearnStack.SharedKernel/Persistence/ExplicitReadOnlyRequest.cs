namespace LearnStack.SharedKernel.Persistence;

/// <summary>
/// An exact request type whose module composition declares a physical read-only
/// transaction through the existing pipeline owner (ADR-0064).
/// </summary>
public sealed class ExplicitReadOnlyRequest
{
    public ExplicitReadOnlyRequest(Type requestType)
    {
        ArgumentNullException.ThrowIfNull(requestType);
        RequestType = requestType;
    }

    /// <summary>The exact CLR request type; no naming or assignability inference.</summary>
    public Type RequestType { get; }
}
