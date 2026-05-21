namespace LearnStack.SharedKernel.Domain;

/// <summary>
/// Base record for in-process domain events. Concrete events derive from
/// this and add their own payload fields:
/// <code>
/// public sealed record CourseEnrolled(CourseId CourseId, UserId UserId) : DomainEvent;
/// </code>
/// </summary>
/// <remarks>
/// The <see cref="EventId"/> and <see cref="OccurredAt"/> defaults call
/// the BCL directly rather than going through <c>IClock</c> /
/// <c>IGuidFactory</c>: domain events are minted inside aggregate methods
/// that already received the relevant abstractions through their command
/// boundary. Overriding the defaults via record <c>init</c> in a test
/// remains straightforward.
/// </remarks>
public abstract record DomainEvent : IDomainEvent
{
    public Guid EventId { get; init; } = Guid.CreateVersion7();

    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}
