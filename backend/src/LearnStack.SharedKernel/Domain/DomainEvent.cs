namespace LearnStack.SharedKernel.Domain;

/// <summary>
/// Base record for in-process domain events. Concrete events derive from
/// this and add their own payload fields:
/// <code>
/// public sealed record CourseEnrolled(CourseId CourseId, UserId UserId) : DomainEvent;
///
/// // raised from inside an aggregate method:
/// RaiseDomainEvent(new CourseEnrolled(Id, learnerId)
/// {
///     EventId = guids.NewUuidV7(),
///     OccurredAt = clock.UtcNow,
/// });
/// </code>
/// </summary>
/// <remarks>
/// <see cref="EventId"/> and <see cref="OccurredAt"/> are <c>required init</c>:
/// every event MUST be stamped with the aggregate's injected
/// <c>IGuidFactory</c> / <c>IClock</c>. Defaulting these to
/// <c>Guid.CreateVersion7()</c> / <c>DateTimeOffset.UtcNow</c> would
/// silently bypass the deterministic-test abstractions every command
/// handler already threads in — which is the entire reason <c>IClock</c>
/// and <c>IGuidFactory</c> exist (Standards 02 § Time).
/// </remarks>
public abstract record DomainEvent : IDomainEvent
{
    public required Guid EventId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }
}
