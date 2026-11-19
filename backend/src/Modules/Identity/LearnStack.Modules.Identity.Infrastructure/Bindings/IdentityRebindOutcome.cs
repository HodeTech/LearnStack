using System.Diagnostics.Metrics;

namespace LearnStack.Modules.Identity.Infrastructure.Bindings;

/// <summary>How a controlled issuer rebind ended (ADR-0104 § 9).</summary>
public enum IdentityRebindResult
{
    /// <summary>The issuer changed and its result row committed with it.</summary>
    Applied = 1,

    /// <summary>The stored issuer already equalled the target; nothing was written, not even a row.</summary>
    AlreadyApplied = 2,

    /// <summary>The state is unchanged; a retry repeats the whole proof.</summary>
    Refused = 3,

    /// <summary>The commit's outcome could not be read back; the operator reads the stored issuer before anything else.</summary>
    Unknown = 4,
}

/// <summary>Why a rebind was refused: a closed set that reports a class and never a value.</summary>
public enum IdentityRebindRefusal
{
    None = 0,
    NotAdmitted,
    RoleInvalid,
    RebindInProgress,
    DatabaseUnavailable,
    AuthorityMismatch,
    LiveSessions,
    NewRealmUnavailable,
    NewRealmIssuerMismatch,
    NewRealmIncomplete,
    ContinuityBroken,
    AuditFailed,
    CommitFailed,
    Cancelled,
}

/// <summary>The end of one run and what it counted.</summary>
/// <param name="Result">How the run ended.</param>
/// <param name="Refusal">The class of a refusal, or <see cref="IdentityRebindRefusal.None"/>.</param>
/// <param name="Continuity">The continuity counts, when the proof ran.</param>
/// <param name="LiveSessions">How many sessions of a runtime role the freeze check saw, when it ran.</param>
public sealed record IdentityRebindOutcome(
    IdentityRebindResult Result,
    IdentityRebindRefusal Refusal = IdentityRebindRefusal.None,
    IdentityRebindContinuityResult? Continuity = null,
    int? LiveSessions = null);

/// <summary>Counters of the rebind: runs by outcome, and the continuity proof's counts by class (ADR-0104 § 11).</summary>
/// <remarks>
/// The labels are closed sets. The offline tool is a short-lived process, so it prints the same classes in its
/// outcome line and exports these only when an OTLP endpoint is configured for it.
/// </remarks>
public static class IdentityRebindMetrics
{
    /// <summary>The meter name.</summary>
    public const string MeterName = "learnstack.identity.rebind";

    /// <summary>Runs by <c>outcome</c>: applied, already_applied, unknown, or a refusal class.</summary>
    public const string RunCounterName = "learnstack_identity_rebind_runs_total";

    /// <summary>Continuity counts by <c>class</c>.</summary>
    public const string ContinuityCounterName = "learnstack_identity_rebind_continuity_total";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Runs =
        Meter.CreateCounter<long>(RunCounterName, description: "Controlled issuer rebind runs by outcome.");

    private static readonly Counter<long> Continuity =
        Meter.CreateCounter<long>(ContinuityCounterName, description: "Continuity proof counts by class.");

    /// <summary>Records one finished run.</summary>
    public static void Record(IdentityRebindOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        Runs.Add(1, new KeyValuePair<string, object?>("outcome", Label(outcome)));
        if (outcome.Continuity is { } counts)
        {
            Add("active", counts.StoredActive);
            Add("closing", counts.StoredClosing);
            Add("closed", counts.StoredClosed);
            Add("reported", counts.Enumerated);
            Add("unkeyable", counts.Unkeyable);
            Add("missing_active", counts.MissingActive);
            Add("reappearing_closed", counts.ReappearingClosed);
            Add("unbootstrapped", counts.Unbootstrapped);
        }
    }

    /// <summary>The closed outcome label of a run.</summary>
    public static string Label(IdentityRebindOutcome outcome) => outcome.Result switch
    {
        IdentityRebindResult.Applied => "applied",
        IdentityRebindResult.AlreadyApplied => "already_applied",
        IdentityRebindResult.Unknown => "unknown",
        _ => outcome.Refusal switch
        {
            IdentityRebindRefusal.NotAdmitted => "refused_not_admitted",
            IdentityRebindRefusal.RoleInvalid => "refused_role_invalid",
            IdentityRebindRefusal.RebindInProgress => "refused_rebind_in_progress",
            IdentityRebindRefusal.DatabaseUnavailable => "refused_database_unavailable",
            IdentityRebindRefusal.AuthorityMismatch => "refused_authority_mismatch",
            IdentityRebindRefusal.LiveSessions => "refused_live_sessions",
            IdentityRebindRefusal.NewRealmUnavailable => "refused_new_realm_unavailable",
            IdentityRebindRefusal.NewRealmIssuerMismatch => "refused_new_realm_issuer_mismatch",
            IdentityRebindRefusal.NewRealmIncomplete => "refused_new_realm_incomplete",
            IdentityRebindRefusal.ContinuityBroken => "refused_continuity_broken",
            IdentityRebindRefusal.AuditFailed => "refused_audit_failed",
            IdentityRebindRefusal.CommitFailed => "refused_commit_failed",
            IdentityRebindRefusal.Cancelled => "refused_cancelled",
            _ => "refused",
        },
    };

    private static void Add(string name, int count)
    {
        if (count > 0)
        {
            Continuity.Add(count, new KeyValuePair<string, object?>("class", name));
        }
    }
}
