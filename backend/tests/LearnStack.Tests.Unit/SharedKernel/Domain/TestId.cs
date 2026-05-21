using LearnStack.SharedKernel.Identifiers;
using Vogen;

namespace LearnStack.Tests.Unit.SharedKernel.Domain;

/// <summary>
/// Synthetic Vogen-emitted ID used by the Entity / AuditableEntity / Vogen
/// smoke tests. Lives in the test project (not <c>SharedKernel</c>) because
/// concrete aggregate IDs belong to their owning module — but the emitter
/// pipeline has to be exercisable end-to-end before any module ships one.
/// </summary>
[ValueObject<Guid>(LearnStackVogenDefaults.IdMask)]
public readonly partial record struct TestId : IStronglyTypedId<Guid>
{
    public static TestId New() => From(Guid.CreateVersion7());
}
