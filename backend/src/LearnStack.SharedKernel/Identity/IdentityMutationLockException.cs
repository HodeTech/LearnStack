using LearnStack.SharedKernel.Errors;

namespace LearnStack.SharedKernel.Identity;

/// <summary>Fixed lock-plan failure, without provider payload or target information.</summary>
public sealed class IdentityMutationLockException : InfrastructureException
{
    public IdentityMutationLockException() : base("Identity mutation admission was refused.") { }
}
