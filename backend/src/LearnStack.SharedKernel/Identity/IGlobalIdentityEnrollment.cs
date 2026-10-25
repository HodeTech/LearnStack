namespace LearnStack.SharedKernel.Identity;

/// <summary>Scoped read-only enrollment state. Reading it never creates a unit, connection or data source.</summary>
public interface IGlobalIdentityEnrollment
{
    bool IsActive { get; }
}
