using LearnStack.SharedKernel.Errors;

namespace LearnStack.Modules.Identity.Application.Abstractions;

/// <summary>A fixed adapter failure; it retains no provider values or original inner payload.</summary>
public sealed class UserBindingStorageException : InfrastructureException
{
    public UserBindingStorageException() : base("Identity binding storage is unavailable.") { }
}
