namespace LearnStack.SharedKernel.Audit;

/// <summary>
/// Whether the MUST-class standalone write path is currently working.
/// </summary>
/// <remarks>
/// <para>
/// <b>A singleton, and the state behind the <c>audit</c> health check.</b>
/// <see href="../../../../docs/decisions/0033-audit-durability-model.md">ADR-0033
/// Amendment 3</see> assigns Packet 9 the observable half of the fail-closed rule: the
/// check, the counter beside it, and the <c>Critical</c> log line. Only the deployment-level
/// backstop — ceasing to serve past a configured unhealthy window — is demand-gated to
/// <see href="../../../../docs/roadmap/phase-11-production-hardening.md">Phase 11</see>,
/// which is where a readiness surface and an operator to answer the page it raises exist.
/// </para>
/// <para>
/// <b>Last outcome wins; it is not a count.</b> The rule is "unhealthy while the most
/// recent MUST-class standalone write has failed and no later one has succeeded", so a
/// single later success clears it. A counter would answer a different question — how often
/// — and that question is the metric's, which is why both ship rather than either standing
/// in for the other.
/// </para>
/// <para>
/// <b>Only the standalone path reports here.</b> An in-transaction failure rolls the
/// business write back and the caller is answered <c>503 audit_unavailable</c>, so it is
/// already visible as a failed request; a SHOULD/MAY loss is an accepted one, written down
/// in the module's coverage matrix. The standalone MUST failure is the one that leaves an
/// operation *succeeded* and unrecorded, which is the state nothing else reports.
/// </para>
/// </remarks>
public interface IAuditHealth
{
    /// <summary>Whether the last MUST-class standalone write succeeded.</summary>
    bool IsHealthy { get; }

    /// <summary>The operation whose write failed, while unhealthy.</summary>
    string? FailingOperation { get; }

    /// <summary>Records that a MUST-class standalone write failed.</summary>
    void ReportStandaloneWriteFailed(string operation);

    /// <summary>
    /// Records that a MUST-class standalone write landed.
    /// </summary>
    /// <remarks>
    /// A duplicate-key refusal counts as one: it is positive evidence that the business
    /// <c>COMMIT</c> landed and that the in-transaction row under this id is already
    /// durable, which is the opposite of the state this check exists to report.
    /// </remarks>
    void ReportStandaloneWriteSucceeded();
}
