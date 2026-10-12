namespace LearnStack.SharedKernel.Domain;

/// <summary>
/// Marker every entity that raises <see cref="IDomainEvent"/> implements.
/// Successful saves promote the saving sources' facts into the scoped collector.
/// Only the transaction owner dispatches them sequentially before MUST audit and
/// commit; every owner terminal clears retained source lists, including detached ones.
/// </summary>
public interface IHasDomainEvents
{
    IReadOnlyCollection<IDomainEvent> DomainEvents { get; }

    void ClearDomainEvents();
}
