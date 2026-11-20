namespace LearnStack.SharedKernel.Platform;

/// <summary>
/// The fence an operator read at inspection and presents with a subscriber replay: the delivery's replay count, the
/// instant it was dead-lettered and its terminal reason (ADR-0103 § 2).
/// </summary>
/// <param name="TenantId">The delivery's tenant.</param>
/// <param name="SubscriptionId">The delivery's subscription.</param>
/// <param name="EventId">The delivery's event.</param>
/// <param name="ReplayCount">The replay count the inspection showed.</param>
/// <param name="DeadLetteredAt">The dead-letter instant the inspection showed, to the microsecond.</param>
/// <param name="TerminalReason">The terminal reason the inspection showed.</param>
public sealed record DeliveryReplayRequest(
    Guid TenantId, string SubscriptionId, Guid EventId, int ReplayCount, DateTimeOffset DeadLetteredAt, string TerminalReason);

/// <summary>The fence an operator presents with a producer reset: the attempt count and the dead-letter instant (ADR-0103 § 3).</summary>
/// <param name="TenantId">The outbox row's tenant.</param>
/// <param name="EventId">The outbox row's identifier, which is the event's.</param>
/// <param name="Attempts">The attempt count the inspection showed.</param>
/// <param name="DeadLetteredAt">The dead-letter instant the inspection showed, to the microsecond.</param>
public sealed record OutboxResetRequest(Guid TenantId, Guid EventId, int Attempts, DateTimeOffset DeadLetteredAt);

/// <summary>How an operator recovery ended.</summary>
public enum OperatorRecoveryOutcome
{
    /// <summary>The row was reset, and its result row committed with it.</summary>
    Applied = 1,

    /// <summary>The row no longer shows the fence the operator presented: another act or the dispatcher moved it.</summary>
    StaleFence,

    /// <summary>The fence holds but the state is not one a reset may act on; nothing changed.</summary>
    NotRecoverable,

    /// <summary>No such row exists, or it is outside what an operator may reach.</summary>
    NotFound,
}

/// <summary>The end of one recovery and the count the row now carries when it was applied.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="CountAfter">The fence's count after an applied reset: the new replay count, or zero attempts.</param>
/// <param name="Refusal">
/// For a refused state, the closed token that says which rule refused it: <c>processed</c>, <c>leased</c>,
/// <c>invalid_envelope</c>, <c>reason_refused</c>, <c>subscription_not_active</c>, <c>type_not_dispatchable</c> or
/// <c>tenant_not_serving</c>.
/// </param>
public sealed record OperatorRecoveryResult(OperatorRecoveryOutcome Outcome, int? CountAfter = null, string? Refusal = null);

/// <summary>
/// The recovery owner could not finish and nothing is known to be wrong with the request: the credential or the pool
/// is unavailable, the owner's one lane is busy, or the commit's outcome is unknown (ADR-0103 § 5).
/// </summary>
/// <remarks>
/// The message is fixed and carries no address, credential or row value. The cause, when there is one, is for the
/// operator's logs only: no response carries it.
/// </remarks>
public sealed class OperatorRecoveryUnavailableException : Exception
{
    /// <summary>Builds the refusal.</summary>
    /// <param name="commitOutcomeUnknown">Whether the transition's COMMIT may have reached the database.</param>
    /// <param name="cause">The fault that made the owner unavailable, kept so the route can log it.</param>
    public OperatorRecoveryUnavailableException(bool commitOutcomeUnknown = false, Exception? cause = null)
        : base("The operator recovery owner is unavailable.", cause) => CommitOutcomeUnknown = commitOutcomeUnknown;

    /// <summary>Whether the commit may have landed, so the operator inspects the row before repeating the request.</summary>
    public bool CommitOutcomeUnknown { get; }
}

/// <summary>
/// The confined owner of the two fenced recovery transitions (ADR-0103 § 2 to § 5): it consumes the operator's
/// admission and makes the change on the dispatcher's outbox-role credential, with a MUST row on the same transaction.
/// </summary>
public interface IOperatorRecovery
{
    /// <summary>Resets one dead-lettered subscriber delivery under its fence.</summary>
    /// <exception cref="OperatorRecoveryUnavailableException">The owner could not finish.</exception>
    /// <exception cref="LearnStack.SharedKernel.Audit.AuditWriteFailedException">The MUST row could not be written; nothing changed.</exception>
    Task<OperatorRecoveryResult> ReplayDeliveryAsync(
        PlatformAdmission admission, DeliveryReplayRequest request, CancellationToken cancellationToken = default);

    /// <summary>Resets one dead-lettered producer row, whose terminal reason is retry exhaustion, under its fence.</summary>
    /// <exception cref="OperatorRecoveryUnavailableException">The owner could not finish.</exception>
    /// <exception cref="LearnStack.SharedKernel.Audit.AuditWriteFailedException">The MUST row could not be written; nothing changed.</exception>
    Task<OperatorRecoveryResult> ResetOutboxAsync(
        PlatformAdmission admission, OutboxResetRequest request, CancellationToken cancellationToken = default);
}
