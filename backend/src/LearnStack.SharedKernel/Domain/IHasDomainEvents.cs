namespace LearnStack.SharedKernel.Domain;

/// <summary>
/// Marker every entity that raises <see cref="IDomainEvent"/> implements.
/// The unit-of-work walks tracked entities, drains the events, and hands
/// them to MediatR's in-process publisher on commit.
/// </summary>
public interface IHasDomainEvents
{
    IReadOnlyCollection<IDomainEvent> DomainEvents { get; }

    void ClearDomainEvents();
}
