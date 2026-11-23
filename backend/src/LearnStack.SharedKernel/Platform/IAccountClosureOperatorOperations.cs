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

/// <summary>
/// The confined owner of the operator's global account closure acts (ADR-0107 §§ 7 and 8): it consumes the operator's
/// admission in the platform scope, fences the person and every participant tenant on the platform connection, judges the
/// closure, moves the person's User and the request, and then fans the closure out to every served participant group.
/// </summary>
/// <remarks>
/// A transient database fault, a fence that could not be taken within its bound included, is thrown as
/// <see cref="OperatorRecoveryUnavailableException"/>, with its commit outcome unknown when the Begin's commit was already
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
    /// <exception cref="LearnStack.SharedKernel.Audit.AuditWriteFailedException">A MUST row could not be written; nothing changed.</exception>
    /// <exception cref="OperatorRecoveryUnavailableException">
    /// The database could not finish, a fence could not be taken in time, or the commit's outcome is unknown.
    /// </exception>
    /// <exception cref="InvalidOperationException">No closure fan-out is registered: the act refuses before it enters the scope.</exception>
    Task<ClosureBeginResult> BeginAsync(
        PlatformAdmission admission, Guid closureRequestId, CancellationToken cancellationToken = default);
}
