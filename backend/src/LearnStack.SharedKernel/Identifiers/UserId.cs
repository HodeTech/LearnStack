using Vogen;

namespace LearnStack.SharedKernel.Identifiers;

/// <summary>
/// Cross-cutting strongly-typed identifier for the platform-wide user.
/// Lives in <see cref="LearnStack.SharedKernel"/> (per ADR-0023's
/// cross-cutting value-object placement) so audit metadata and other
/// kernel surfaces can reference users without depending on the Identity
/// module that lands in Phase 02b. The Identity module consumes the same
/// type once it ships; there is exactly one <c>UserId</c> shape.
/// </summary>
[ValueObject<Guid>(LearnStackVogenDefaults.IdMask)]
public readonly partial record struct UserId : IStronglyTypedId<Guid>
{
    public static UserId New() => From(Guid.CreateVersion7());
}
