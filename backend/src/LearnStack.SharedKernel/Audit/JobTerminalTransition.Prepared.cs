using System.Data.Common;
using System.Text.Json;

namespace LearnStack.SharedKernel.Audit;

public sealed partial class JobTerminalTransition
{
    private sealed record PreparedBypassFacts(Guid? SelectedNormalAttempt, int FinalizeAttempts,
        string? State, string? Policy, string? Status, string? ModuleReason);
    private readonly PreparedBypassFacts? _prepared;

    private PreparedBypassFacts ReadPreparedBypass(DbDataReader row)
    {
        var finalizeAttempts = row.GetInt32(13);
        var selected = row.IsDBNull(14) ? (Guid?)null : row.GetGuid(14);
        var currentStage = row.GetString(15);
        var currentOrdinal = row.GetInt32(16);
        var currentToken = row.GetGuid(17);
        var currentDeadline = row.GetFieldValue<DateTimeOffset>(18);
        var normal = row.GetGuid(20);
        var state = NullableText(row, 23);
        var policy = NullableText(row, 24);
        var quiescent = row.IsDBNull(25) ? (DateTimeOffset?)null : row.GetFieldValue<DateTimeOffset>(25);
        var noStart = row.IsDBNull(26) ? (DateTimeOffset?)null : row.GetFieldValue<DateTimeOffset>(26);
        var status = NullableText(row, 27);
        var moduleReason = NullableText(row, 28);
        var fingerprint = row.GetString(30);
        var operation = row.GetGuid(31);
        var preparedAt = row.GetFieldValue<DateTimeOffset>(32);
        var ownerUntil = row.GetFieldValue<DateTimeOffset>(35);
        var observedAt = row.GetFieldValue<DateTimeOffset>(37);
        var admittedAt = row.GetFieldValue<DateTimeOffset>(38);
        var capacity = row.GetString(39);
        if (row.GetString(11) != "external" || !row.GetBoolean(12) || !row.GetBoolean(29)
            || NormalAttempts is < 1 or > 6 || finalizeAttempts is < 0 or > 6
            || selected is { Version: not 7 } || normal.Version != 7
            || row.GetString(21) != "normal" || row.GetInt32(22) != NormalAttempts
            || currentToken == Guid.Empty || currentToken == AcquisitionToken
            || currentDeadline != row.GetFieldValue<DateTimeOffset>(19)
            || !Finite(admittedAt) || !Finite(currentDeadline) || admittedAt >= currentDeadline
            || currentDeadline - admittedAt > TimeSpan.FromHours(24) || admittedAt > observedAt
            || row.GetString(33) != "terminal_pending" || row.GetString(34) != "recovery"
            || !Finite(ownerUntil) || ownerUntil <= observedAt || ownerUntil - observedAt > TimeSpan.FromMinutes(10)
            || row.GetString(36) != "learnstack_outbox_admin" || !Finite(observedAt)
            || !Finite(DeadLetteredAt) || DeadLetteredAt > observedAt
            || !Finite(preparedAt) || preparedAt > DeadLetteredAt
            || operation == Guid.Empty || fingerprint.Length != 64
            || fingerprint.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            || string.IsNullOrWhiteSpace(capacity) || capacity.Length > 150
            || (finalizeAttempts == 0
                ? currentStage != "normal" || currentOrdinal != NormalAttempts || normal != AttemptId
                : currentStage != "finalize" || currentOrdinal != finalizeAttempts || normal == AttemptId || selected is null)
            || selected is { } selectedId && selectedId != normal
            || Reason is "finalize_refused" or "finalize_exhausted" && (finalizeAttempts == 0 || selected is null)
            || Reason == "finalize_exhausted" && finalizeAttempts != 6)
            throw InvalidPrepared();

        if (state is null)
        {
            if (policy is not null || quiescent is not null || noStart is not { } stopped
                || !Finite(stopped) || stopped > DeadLetteredAt) throw InvalidPrepared();
        }
        else
        {
            if (noStart is not null || quiescent is not { } ended || !Finite(ended) || ended > DeadLetteredAt
                || (state, policy) is not (("succeeded", null) or ("failed", "permanent")
                    or ("failed" or "cancelled", "transient_repeatable" or "transient_not_repeatable")))
                throw InvalidPrepared();
        }
        if (selected is null)
        {
            if (finalizeAttempts != 0 || status is not null || moduleReason is not null
                || Reason is not ("retired_definition" or "invalid_envelope")) throw InvalidPrepared();
        }
        else
        {
            var valid = (state, policy, status, moduleReason) switch
            {
                ("succeeded", null, "succeeded", "none") => true,
                ("failed", "permanent" or "transient_not_repeatable", "failed", "external_failed") => true,
                ("cancelled", "transient_not_repeatable", "failed", "external_cancelled") => true,
                ("failed" or "cancelled", "transient_repeatable", "failed", "normal_attempts_exhausted") => NormalAttempts == 6,
                (null, null, "failed", "permanent_failure") => true,
                (null, null, "failed", "normal_attempts_exhausted") => NormalAttempts == 6,
                _ => false,
            };
            if (!valid) throw InvalidPrepared();
        }
        return new(selected, finalizeAttempts, state, policy, status, moduleReason);
    }

    private void WritePreparedMetadata(Utf8JsonWriter writer)
    {
        if (_prepared is not { } facts) return;
        if (facts.SelectedNormalAttempt is { } selected) writer.WriteString("selected_normal_attempt_id", selected);
        writer.WriteNumber("finalize_attempts", facts.FinalizeAttempts);
        if (facts.Status is not null) writer.WriteString("finalization_status", facts.Status);
        if (facts.ModuleReason is not null) writer.WriteString("finalization_reason", facts.ModuleReason);
        writer.WriteBoolean("business_finalization_incomplete", true);
        if (facts.State is not null) writer.WriteString("external_state", facts.State);
        if (facts.Policy is not null)
        {
            writer.WriteString("external_failure_policy", facts.Policy);
            writer.WriteString("external_failure_class", facts.Policy == "permanent" ? "permanent" : "transient");
            writer.WriteBoolean("external_retry_permitted", facts.Policy == "transient_repeatable");
        }
    }

    private static bool Finite(DateTimeOffset value) => value != DateTimeOffset.MinValue && value != DateTimeOffset.MaxValue;
    private static ArgumentException InvalidPrepared() => new("Prepared terminal UPDATE returned inconsistent history or owner.");
}
