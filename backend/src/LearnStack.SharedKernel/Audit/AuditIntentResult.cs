namespace LearnStack.SharedKernel.Audit;

/// <summary>
/// What the request that declared an intent returned, recorded on the intent when that
/// request's audit frame closes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per intent, because the outcome of a request and the fate of its transaction are
/// different facts.</b> The outcome used to live in a local variable of the behavior frame
/// that saw the response, so only the outermost frame's survived — and the owning frame
/// wrote every pending MUST row as <c>success</c>. Under
/// <see href="../../../../docs/decisions/0040-ambient-unit-of-work.md">ADR-0040
/// § Nesting</see> an inner request can be refused, have that refusal absorbed by the outer
/// handler, and see the transaction it joined commit: its row said <c>success</c>,
/// permanently, with no error key
/// (<see href="../../../../docs/decisions/0044-audit-write-path.md">ADR-0044 Amendment 6
/// § 3</see>).
/// </para>
/// <para>
/// <b>Three shapes, and the difference between the last two is load-bearing.</b> A
/// <see cref="Refused(AuditOutcome, string?)"/> result is a fact about the operation that no
/// commit changes, so it survives whatever the transaction did. <see cref="Thrown"/> is not:
/// the owning frame's exception is very often the <c>COMMIT</c> itself faulting, whose honest
/// outcome is <c>indeterminate</c> rather than <c>failed</c> — so a thrown result yields to
/// the transaction's state where a refusal does not.
/// </para>
/// </remarks>
public sealed record AuditIntentResult
{
    private AuditIntentResult(AuditOutcome outcome, string? errorKey, bool threw)
    {
        Outcome = outcome;
        ErrorKey = errorKey;
        Threw = threw;
    }

    /// <summary>The handler returned a success <c>Result</c>.</summary>
    public static AuditIntentResult Succeeded { get; } = new(AuditOutcome.Success, null, threw: false);

    /// <summary>
    /// The request left by an exception — the handler's, a behavior's, a cancellation, or a
    /// faulted <c>COMMIT</c>.
    /// </summary>
    public static AuditIntentResult Thrown { get; } = new(AuditOutcome.Failed, null, threw: true);

    /// <summary><c>success</c>, <c>denied</c> or <c>failed</c> — never <c>indeterminate</c>,
    /// which is the transaction's to say.</summary>
    public AuditOutcome Outcome { get; }

    /// <summary>The refusal's localization key, when the request was refused.</summary>
    public string? ErrorKey { get; }

    /// <summary>Whether the request left by an exception rather than by returning.</summary>
    public bool Threw { get; }

    /// <summary>The handler returned a failure <c>Result</c>.</summary>
    /// <param name="outcome"><see cref="AuditOutcome.Denied"/> or <see cref="AuditOutcome.Failed"/>.</param>
    /// <param name="errorKey">The refusal's localization key.</param>
    public static AuditIntentResult Refused(AuditOutcome outcome, string? errorKey) =>
        outcome is AuditOutcome.Denied or AuditOutcome.Failed
            ? new AuditIntentResult(outcome, errorKey, threw: false)
            : throw new ArgumentOutOfRangeException(
                nameof(outcome),
                outcome,
                "A refusal is denied or failed. Success is Succeeded, and indeterminate is the "
                + "transaction's to say rather than the request's.");
}
