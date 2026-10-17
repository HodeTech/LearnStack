using System.Data.Common;
using System.Text.Json;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;

namespace LearnStack.SharedKernel.Audit;

public sealed partial class JobTerminalTransition
{
    private sealed record AppFinalizationFacts(IUnitOfWork Unit, TenantContextAnnouncement Announcement,
        JobApplicationSourceOwner SourceOwner, Guid SelectedNormalAttempt, int FinalizeAttempts,
        string? State, string? Policy, string ModuleReason);
    private readonly AppFinalizationFacts? _finalization;

    private AppFinalizationFacts ReadAppFinalization(IUnitOfWork unit, DbDataReader row)
    {
        var announcement = unit.GetTenantContextAnnouncement(_transaction) ?? throw InvalidFinalization();
        var source = JobApplicationSourceOwner.Current ?? throw InvalidFinalization();
        RequireAppOwner(unit, announcement, source);
        var selectedNormalAttempt = row.GetGuid(15);
        var finalizeAttempts = row.GetInt32(14);
        var selectedState = NullableText(row, 18);
        var selectedPolicy = NullableText(row, 19);
        var finalizationReason = row.GetString(23);
        var observedAt = row.GetFieldValue<DateTimeOffset>(36);
        var admittedAt = row.GetFieldValue<DateTimeOffset>(29);
        var deadline = row.GetFieldValue<DateTimeOffset>(30);
        if (row.GetString(11) != "external" || row.GetString(12) != "terminal_pending" || !row.IsDBNull(13)
            || finalizeAttempts is < 1 or > 6 || selectedNormalAttempt.Version != 7
            || selectedNormalAttempt == AttemptId || row.GetString(16) != "normal" || row.GetInt32(17) != NormalAttempts
            || row.GetString(22) != "failed" || row.GetBoolean(24)
            || row.GetString(25) != "finalize" || row.GetInt32(26) != finalizeAttempts
            || row.GetGuid(27) != selectedNormalAttempt || row.GetGuid(28) != AcquisitionToken
            || row.GetString(31) != "finalizing" || row.GetString(32) != "execution"
            || row.GetFieldValue<DateTimeOffset>(33) <= observedAt || deadline <= observedAt
            || deadline != row.GetFieldValue<DateTimeOffset>(34) || admittedAt >= deadline || admittedAt > observedAt
            || DeadLetteredAt > observedAt || row.GetString(35) != "learnstack_app")
            throw InvalidFinalization();

        string expectedReason;
        if (selectedState is null)
        {
            // No provider report exists for this positive pre-effect failure.
            if (selectedPolicy is not null || !row.IsDBNull(20) || row.IsDBNull(21)
                || row.GetFieldValue<DateTimeOffset>(21) > DeadLetteredAt)
                throw InvalidFinalization();
            expectedReason = finalizationReason switch
            {
                "permanent_failure" => "permanent_failure",
                "normal_attempts_exhausted" when NormalAttempts == 6 => "retry_exhausted",
                _ => throw InvalidFinalization(),
            };
        }
        else
        {
            if (selectedState is not ("failed" or "cancelled") || row.IsDBNull(20) || !row.IsDBNull(21)
                || row.GetFieldValue<DateTimeOffset>(20) > DeadLetteredAt)
                throw InvalidFinalization();
            expectedReason = selectedPolicy switch
            {
                "permanent" when selectedState == "failed" => "permanent_failure",
                "transient_not_repeatable" => "external_retry_forbidden",
                "transient_repeatable" when NormalAttempts == 6 => "retry_exhausted",
                _ => throw InvalidFinalization(),
            };
            var expectedModuleReason = selectedPolicy == "transient_repeatable" ? "normal_attempts_exhausted"
                : selectedState == "failed" ? "external_failed" : "external_cancelled";
            if (finalizationReason != expectedModuleReason) throw InvalidFinalization();
        }
        if (Reason != expectedReason) throw InvalidFinalization();
        return new(unit, announcement, source, selectedNormalAttempt, finalizeAttempts, selectedState, selectedPolicy, finalizationReason);
    }

    private void RequireAppOwner()
    {
        if (_finalization is { } facts) RequireAppOwner(facts.Unit, facts.Announcement, facts.SourceOwner);
    }

    private void RequireAppOwner(IUnitOfWork unit, TenantContextAnnouncement announcement, JobApplicationSourceOwner source)
    {
        if (!ReferenceEquals(JobApplicationSourceOwner.Current, source)
            || !unit.HasActiveTransaction || unit.IsRollbackOnly || unit.Mode != TransactionMode.ReadWrite
            || !ReferenceEquals(unit.Connection, _connection) || !ReferenceEquals(unit.Transaction, _transaction)
            || announcement.Generation <= 0 || announcement.TenantId.Value != TenantId
            || announcement.OrganizationId?.Value != OrganizationId
            || unit.GetTenantContextAnnouncement(_transaction) != announcement)
            throw InvalidFinalization();
        source.RequireOwnedMainUnit(unit);
    }

    private void WriteFinalizationMetadata(Utf8JsonWriter writer)
    {
        if (_finalization is not { } facts) return;
        writer.WriteString("selected_normal_attempt_id", facts.SelectedNormalAttempt);
        writer.WriteNumber("finalize_attempts", facts.FinalizeAttempts);
        writer.WriteString("finalization_status", "failed");
        writer.WriteString("finalization_reason", facts.ModuleReason);
        writer.WriteBoolean("business_finalization_incomplete", false);
        if (facts.State is not null) writer.WriteString("external_state", facts.State);
        if (facts.Policy is not null)
        {
            writer.WriteString("external_failure_policy", facts.Policy);
            writer.WriteString("external_failure_class", facts.Policy == "permanent" ? "permanent" : "transient");
            writer.WriteBoolean("external_retry_permitted", facts.Policy == "transient_repeatable");
        }
    }

    private static string? NullableText(DbDataReader row, int index) => row.IsDBNull(index) ? null : row.GetString(index);
    private static ArgumentException InvalidFinalization() => new("The application terminal transition has inconsistent selected facts or owner.");
}
