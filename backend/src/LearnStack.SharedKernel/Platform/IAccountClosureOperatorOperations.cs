using LearnStack.SharedKernel.Audit;

namespace LearnStack.SharedKernel.Platform;

/// <summary>How a closure's Begin ended (ADR-0107 § 7).</summary>
public enum ClosureBeginOutcome
{
    /// <summary>
    /// This call moved the person's User from Active to Closing and the closure request to <c>fulfilling</c> in one
    /// transaction with its result row, then fanned the closure out.
    /// </summary>
    Begun = 1,

    /// <summary>
    /// The closure had begun already: the request was <c>fulfilling</c>, or the User was already Closing for another of the
    /// person's closures and this call moved only the request. The fan-out ran again and adopted what an earlier call started.
    /// </summary>
    Resumed,

    /// <summary>The request's execution time has not come, by the database clock: <c>blocked_window</c>.</summary>
    RefusedWindow,

    /// <summary>A hold source reports a hold for the closure: <c>blocked_hold</c>.</summary>
    RefusedHold,

    /// <summary>
    /// A participant tenant that allows ordinary work would be left without another eligible tenant-wide administrator:
    /// <c>blocked_last_administrator</c>.
    /// </summary>
    RefusedLastAdministrator,

    /// <summary>No request has that identifier.</summary>
    NotFound,

    /// <summary>The request is not a closure.</summary>
    NotClosure,

    /// <summary>The closure is <c>completed</c> or <c>cancelled</c>; its stored status is returned.</summary>
    NotBeginnable,

    /// <summary>The person's User is Closed: their closure has finished and nothing may begin for it.</summary>
    SubjectNotAdmitted,
}

/// <summary>
/// What a Begin's fan-out did, as closed counts over the person's participant groups and never an identifier.
/// </summary>
/// <param name="Served">The groups whose tenant allows ordinary work and whose organization, if any, is Active.</param>
/// <param name="Started">Served groups whose fan-out erasure this call created.</param>
/// <param name="Adopted">Served groups whose erasure an earlier call or the person had in flight, adopted as it was.</param>
/// <param name="Waiting">
/// Served groups with an erasure the person or an administrator filed that is past its window and not yet run: a repeated
/// Begin picks them up.
/// </param>
/// <param name="Failed">Served groups whose act did not report a start or an adoption: a repeated Begin retries them.</param>
/// <param name="Unserved">
/// Groups whose tenant does not allow ordinary work or whose organization is not Active (<c>unserved_group</c>): no
/// fan-out runs there, their data stays, and Phase 11's offboarding owns it.
/// </param>
public sealed record ClosureBeginTally(int Served, int Started, int Adopted, int Waiting, int Failed, int Unserved)
{
    /// <summary>Whether every served group is started or adopted, so no repeat of Begin is owed.</summary>
    public bool Complete => Waiting == 0 && Failed == 0;
}

/// <summary>The end of one closure Begin.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Status">The request's stored status after the call, when a request was found.</param>
/// <param name="Refusal">
/// For a refusal, its closed code (<c>blocked_window</c>, <c>blocked_hold</c> or <c>blocked_last_administrator</c>), which
/// the request records as its <c>last_refusal</c>.
/// </param>
/// <param name="Groups">For a Begin or a resume, what the fan-out did per group.</param>
public sealed record ClosureBeginResult(
    ClosureBeginOutcome Outcome, string? Status = null, string? Refusal = null, ClosureBeginTally? Groups = null);

/// <summary>How a closure's Finalize ended (ADR-0107 § 7, ADR-0108 § 8).</summary>
public enum ClosureFinalizeOutcome
{
    /// <summary>
    /// This call redacted the person's audit rows, removed their identity-provider account and, in its second transaction,
    /// moved the User from Closing to Closed, completed the participant erasures awaiting the privileged step, the closure
    /// and every other open closure of the person, and wrote the result row.
    /// </summary>
    Finalized = 1,

    /// <summary>The closure is <c>completed</c> and its person's User is Closed: an earlier call finished it.</summary>
    AlreadyFinalized,

    /// <summary>No request has that identifier.</summary>
    NotFound,

    /// <summary>The request is not a closure.</summary>
    NotClosure,

    /// <summary>The closure was never begun (<c>scheduled</c>) or was cancelled; its stored status is returned.</summary>
    NotFinalizable,

    /// <summary>
    /// A served participant group's newest erasure of the person neither awaits the privileged step nor completed during
    /// this closure: its fan-out has not finished. Repeat Begin, wait for the group's erasure job, and finalize again.
    /// </summary>
    GroupsPending,

    /// <summary>
    /// The identity provider definitively refused the account's deletion (a configuration or authority fault). The redaction
    /// committed with its intent row; the closure stays <c>fulfilling</c> and its User Closing, and a repeat resumes it.
    /// </summary>
    ProviderRefused,

    /// <summary>The identity provider could not be reached before the deletion was sent. A repeat resumes the closure.</summary>
    ProviderUnavailable,

    /// <summary>
    /// The deletion was sent and its answer was lost: the account may be gone. A repeat resumes the closure, and a missing
    /// account is success.
    /// </summary>
    ProviderUncertain,
}

/// <summary>
/// Where a Finalize found the person's participant groups, as closed counts and never an identifier.
/// </summary>
/// <param name="Served">The groups whose tenant allows ordinary work and whose organization, if any, is Active.</param>
/// <param name="Ready">
/// Served groups whose newest erasure of the person awaits the privileged step, or completed during this closure, and covers
/// every Membership the person holds there.
/// </param>
/// <param name="Pending">Served groups that are not ready.</param>
/// <param name="Unserved">
/// Groups whose tenant does not allow ordinary work or whose organization is not Active (<c>unserved_group</c>): their data
/// stays and Phase 11's offboarding owns it.
/// </param>
/// <param name="ErasuresCompleted">The participant erasures this call completed; zero unless it finalized.</param>
/// <param name="ClosuresCompleted">The person's closure requests this call completed, this one included; zero unless it finalized.</param>
public sealed record ClosureFinalizeTally(
    int Served, int Ready, int Pending, int Unserved, int ErasuresCompleted = 0, int ClosuresCompleted = 0);

/// <summary>The end of one closure Finalize.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Status">The request's stored status after the call, when a request was found.</param>
/// <param name="Groups">For a finalization or a group still pending, the closed counts of the participant groups.</param>
/// <param name="UnservedGroups">
/// For a finalization, the unserved groups by tenant and organization: never a person, and the operator's list of what
/// Phase 11's offboarding owns.
/// </param>
/// <param name="Redaction">For every call that reached the provider step, this call's redaction passes, summed.</param>
/// <param name="Retained">
/// For a finalization or an earlier one, the closure request's recorded result: the audit log's retained kinds summed over
/// every pass, and <c>unserved_group</c>.
/// </param>
public sealed record ClosureFinalizeResult(
    ClosureFinalizeOutcome Outcome,
    string? Status = null,
    ClosureFinalizeTally? Groups = null,
    IReadOnlyList<ClosureParticipantGroup>? UnservedGroups = null,
    AuditRedactionAggregate? Redaction = null,
    IReadOnlyDictionary<string, long>? Retained = null);

/// <summary>
/// The confined owner of the operator's global account closure acts (ADR-0107 §§ 7 and 8): it consumes the operator's
/// admission in the platform scope, fences the person and every participant tenant on the platform connection, judges the
/// closure, moves the person's User and the request, and then fans the closure out to every served participant group; and
/// it finalizes a begun closure in two platform transactions around the identity provider's account deletion.
/// </summary>
/// <remarks>
/// A transient database fault, a fence that could not be taken within its bound included, is thrown as
/// <see cref="OperatorRecoveryUnavailableException"/>, with its commit outcome unknown when the act's commit was already
/// sent; the scope's own refusals are thrown as the scope throws them.
/// </remarks>
public interface IAccountClosureOperatorOperations
{
    /// <summary>
    /// Begins one person's closure: in one platform transaction under the person's User fence and every participant
    /// tenant's fence, refuses <c>blocked_window</c>, <c>blocked_hold</c> or <c>blocked_last_administrator</c> by recording
    /// it on the request, or moves the User to Closing and the request to <c>fulfilling</c>; after that commit, fans the
    /// closure out to every served participant group. A repeat resumes: it skips what is done and fans out again.
    /// </summary>
    /// <exception cref="AuditWriteFailedException">A MUST row could not be written; nothing changed.</exception>
    /// <exception cref="OperatorRecoveryUnavailableException">
    /// The database could not finish, a fence could not be taken in time, or the commit's outcome is unknown.
    /// </exception>
    /// <exception cref="InvalidOperationException">No closure fan-out is registered: the act refuses before it enters the scope.</exception>
    Task<ClosureBeginResult> BeginAsync(
        PlatformAdmission admission, Guid closureRequestId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finalizes one person's begun closure (ADR-0107 § 7, ADR-0108 § 8). The first platform transaction, under the
    /// person's User fence and every participant tenant's fence, finds every served group ready, redacts the person's
    /// audit rows in every participant tenant and the platform sentinel tenant and commits with the intent row that carries
    /// the summed counts. The identity provider's account is then deleted. The second transaction, under the fences again,
    /// moves the User from Closing to Closed with its raw provider subject removed, completes every participant erasure
    /// awaiting the privileged step, the closure and every other open closure of the person, and commits with the result
    /// row. Every step is idempotent: a repeat after a crash or a provider failure resumes, and a missing account is success.
    /// </summary>
    /// <exception cref="AuditWriteFailedException">
    /// The intent row could not be written or committed: the provider step did not start.
    /// </exception>
    /// <exception cref="OperatorRecoveryUnavailableException">
    /// The database could not finish, a fence could not be taken in time, or the final commit's outcome is unknown.
    /// </exception>
    Task<ClosureFinalizeResult> FinalizeAsync(
        PlatformAdmission admission, Guid closureRequestId, CancellationToken cancellationToken = default);
}
