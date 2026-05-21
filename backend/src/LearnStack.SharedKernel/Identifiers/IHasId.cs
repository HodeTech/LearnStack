namespace LearnStack.SharedKernel.Identifiers;

/// <summary>
/// Tagging interface for any entity that exposes an identifier of type
/// <typeparamref name="TId"/>. Kept separate from
/// <see cref="IAggregateRoot{TId}"/> so child entities inside an aggregate
/// can share the <c>Id</c> shape without claiming aggregate-root status.
/// </summary>
public interface IHasId<out TId>
    where TId : struct, IStronglyTypedId<Guid>
{
    TId Id { get; }
}
