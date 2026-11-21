namespace LearnStack.SharedKernel.Platform;

/// <summary>Which terminal state of a dead-lettered or pending row an inspection lists (ADR-0103 § 1).</summary>
public enum DeliveryInspectionState
{
    /// <summary>The row is dead-lettered: its terminal request committed, and a recovery may act on it.</summary>
    DeadLettered = 1,

    /// <summary>A terminal request is recorded and its commit is not: the state is shown and is not actionable.</summary>
    TerminalRequested,
}

/// <summary>One page of an inspection, with the opaque cursor of the next page when there is one.</summary>
/// <typeparam name="T">The row projection.</typeparam>
/// <param name="Items">The rows of this page, at most the requested limit.</param>
/// <param name="NextCursor">The cursor of the next page, or null on the last page.</param>
public sealed record InspectionPage<T>(IReadOnlyList<T> Items, string? NextCursor);

/// <summary>
/// An outbox row's metadata and the fence a producer reset needs: <see cref="Attempts"/> and
/// <see cref="DeadLetteredAt"/>. The event type is the registry's label, never stored text, and no envelope column is read.
/// </summary>
public sealed record OutboxMessageInspection(
    Guid EventId, Guid TenantId, Guid? OrganizationId, string EventType, DateTimeOffset OccurredAt, int Attempts,
    string? LastError, DateTimeOffset AvailableAfter, DateTimeOffset? LockedUntil, string? TerminalReason,
    DateTimeOffset? DeadLetteredAt, bool Actionable);

/// <summary>
/// A subscriber delivery's metadata and the fence a replay needs: <see cref="ReplayCount"/>,
/// <see cref="DeadLetteredAt"/> and <see cref="TerminalReason"/>.
/// </summary>
public sealed record EventDeliveryInspection(
    Guid TenantId, string SubscriptionId, Guid EventId, Guid? OrganizationId, string EventType, DateTimeOffset OccurredAt,
    int Attempts, string? LastError, DateTimeOffset AvailableAfter, DateTimeOffset? LockedUntil, string? TerminalReason,
    DateTimeOffset? DeadLetteredAt, int ReplayCount, DateTimeOffset? ReplayedAt, bool Actionable);

/// <summary>A quarantined job with its reservation occupancy. Nothing here can release or requeue it (ADR-0103 § 6).</summary>
public sealed record QuarantinedJobInspection(
    Guid JobId, Guid TenantId, Guid? OrganizationId, string DefinitionKey, string Profile, string Queue,
    string? CapacityGroup, DateTimeOffset CreatedAt, string Phase, int NormalAttempts, int FinalizeAttempts,
    string? QuarantineReason, string? LastError, DateTimeOffset? OwnershipUntil, DateTimeOffset? ProbeAfter,
    int UnsuccessfulProbes, string? FinalizationStatus, bool BusinessFinalizationIncomplete, int HeldReservations,
    int GroupOccupancy);

/// <summary>
/// The metadata-only operator inspection of asynchronous work (ADR-0103 § 1): each page runs in an entered platform
/// scope on the platform role's column grants, which the database enforces, so the entry row is its audit.
/// </summary>
/// <remarks>
/// A malformed or foreign cursor answers null, which the route reports as a validation failure. The scope's own
/// refusals are thrown as the scope throws them.
/// </remarks>
public interface IDeliveryInspection
{
    /// <summary>Lists outbox rows in <paramref name="state"/>, oldest first.</summary>
    Task<InspectionPage<OutboxMessageInspection>?> InspectOutboxAsync(
        PlatformAdmission admission, DeliveryInspectionState state, string? cursor, int limit, CancellationToken cancellationToken = default);

    /// <summary>Lists subscriber deliveries in <paramref name="state"/>, oldest first.</summary>
    Task<InspectionPage<EventDeliveryInspection>?> InspectDeliveriesAsync(
        PlatformAdmission admission, DeliveryInspectionState state, string? cursor, int limit, CancellationToken cancellationToken = default);

    /// <summary>Lists quarantined jobs with their reservation occupancy, oldest first.</summary>
    Task<InspectionPage<QuarantinedJobInspection>?> InspectJobsAsync(
        PlatformAdmission admission, string? cursor, int limit, CancellationToken cancellationToken = default);
}
