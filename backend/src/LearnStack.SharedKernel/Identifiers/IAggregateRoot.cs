namespace LearnStack.SharedKernel.Identifiers;

/// <summary>
/// Marker for the root entity of an aggregate. Repositories accept and
/// return only aggregate roots; entities inside an aggregate are reached
/// through the root.
/// </summary>
/// <typeparam name="TId">
/// The aggregate's strongly-typed identifier — per ADR-0023 every aggregate
/// root carries an <see cref="IStronglyTypedId{TKey}"/>-shaped Vogen-emitted
/// ID over <see cref="Guid"/>.
/// </typeparam>
public interface IAggregateRoot<out TId> : IHasId<TId>
    where TId : struct, IStronglyTypedId<Guid>
{
}

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
