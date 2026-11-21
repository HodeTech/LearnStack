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

/// <summary>
/// Which check inside a refusal class failed: a closed set that names a step or a kind of failure, never a value, an
/// address or a credential, so the one printed line can tell an operator where to look.
/// </summary>
public enum IdentityRebindDetail
{
    None = 0,

    /// <summary>The connection is not the role's own session.</summary>
    RoleSession,

    /// <summary>A role attribute escalates: superuser, bypass, creation, replication, login or inheritance.</summary>
    RoleAttributes,

    /// <summary>The role belongs to a role or has a member.</summary>
    RoleMembership,

    /// <summary>The role can create objects, or holds a privilege beyond the declared grants or lacks one of them.</summary>
    RoleGrants,

    /// <summary>The server did not apply the session settings the run asked for, as a pooler's startup handling can.</summary>
    SessionSettings,

    /// <summary>The database could not be reached.</summary>
    DatabaseConnection,

    /// <summary>The database refused the role's credential.</summary>
    DatabaseAuthentication,

    /// <summary>The database refused a statement for lack of a privilege.</summary>
    DatabasePermission,

    /// <summary>A database call exceeded its time limit.</summary>
    DatabaseTimeout,

    /// <summary>The stored authority is missing, is not one row, or changed under the run.</summary>
    AuthorityState,

    /// <summary>The new realm's discovery document or issuer path was unreadable.</summary>
    RealmDiscovery,

    /// <summary>The new realm's key set was unreadable.</summary>
    RealmKeySet,

    /// <summary>The new realm refused the read-only client's token.</summary>
    RealmToken,

    /// <summary>The new realm's user count was refused or out of bounds.</summary>
    RealmUserCount,

    /// <summary>The new realm's user list was refused, unreadable or out of bounds.</summary>
    RealmUserList,
}

/// <summary>The closed spelling of <see cref="IdentityRebindDetail"/> in the printed line.</summary>
public static class IdentityRebindDetails
{
    /// <summary>The token of <paramref name="detail"/>; empty for <see cref="IdentityRebindDetail.None"/>.</summary>
    public static string Token(IdentityRebindDetail detail) => detail switch
    {
        IdentityRebindDetail.RoleSession => "role_session",
        IdentityRebindDetail.RoleAttributes => "role_attributes",
        IdentityRebindDetail.RoleMembership => "role_membership",
        IdentityRebindDetail.RoleGrants => "role_grants",
        IdentityRebindDetail.SessionSettings => "session_settings",
        IdentityRebindDetail.DatabaseConnection => "database_connection",
        IdentityRebindDetail.DatabaseAuthentication => "database_authentication",
        IdentityRebindDetail.DatabasePermission => "database_permission",
        IdentityRebindDetail.DatabaseTimeout => "database_timeout",
        IdentityRebindDetail.AuthorityState => "authority_state",
        IdentityRebindDetail.RealmDiscovery => "realm_discovery",
        IdentityRebindDetail.RealmKeySet => "realm_key_set",
        IdentityRebindDetail.RealmToken => "realm_token",
        IdentityRebindDetail.RealmUserCount => "realm_user_count",
        IdentityRebindDetail.RealmUserList => "realm_user_list",
        _ => string.Empty,
    };
}

/// <summary>The end of one run and what it counted.</summary>
/// <param name="Result">How the run ended.</param>
/// <param name="Refusal">The class of a refusal, or <see cref="IdentityRebindRefusal.None"/>.</param>
/// <param name="Continuity">The continuity counts, when the proof ran.</param>
/// <param name="LiveSessions">How many sessions of a runtime role the freeze check saw, when it ran.</param>
/// <param name="Detail">The check inside the class that failed, when the class has more than one.</param>
public sealed record IdentityRebindOutcome(
    IdentityRebindResult Result,
    IdentityRebindRefusal Refusal = IdentityRebindRefusal.None,
    IdentityRebindContinuityResult? Continuity = null,
    int? LiveSessions = null,
    IdentityRebindDetail Detail = IdentityRebindDetail.None);

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

    /// <summary>
    /// Records a run the tool ended before the database half began, under the label its printed line carries. The
    /// labels are the closed set the runbook lists: the operator token's refusal causes and the grant gate's.
    /// </summary>
    public static void RecordToolRefusal(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        Runs.Add(1, new KeyValuePair<string, object?>("outcome", label));
    }

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
