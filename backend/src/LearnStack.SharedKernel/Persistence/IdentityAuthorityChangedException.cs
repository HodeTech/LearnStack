using LearnStack.SharedKernel.Errors;

namespace LearnStack.SharedKernel.Persistence;

/// <summary>
/// A creation transaction found the stored identity authority different from the facts its process qualified at
/// start, which is what a controlled issuer rebind leaves behind for a process that was not restarted (ADR-0104).
/// </summary>
/// <remarks>A fixed adapter failure; it retains no issuer, authority or subject value.</remarks>
public sealed class IdentityAuthorityChangedException()
    : InfrastructureException("The stored identity authority no longer matches this process.");
