using System.Runtime.ExceptionServices;
using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LearnStack.Infrastructure.Jobs;

internal static class BackgroundJobControlLimits
{
    internal const int MaximumProbeBatch = 100;
    // Existing 64 execution and 128 completion-request bounds, plus one
    // background control batch, one terminal boundary's immutable cursor and
    // one sequential terminal app owner's original acquisition/history.
    // These are identity exclusions, not additional physical source slots.
    internal const int MaximumRetainedIds = 64 + 128 + MaximumProbeBatch + 2;
}

internal enum BackgroundJobControlLane { Recovery, Exhaustion, Retirement, CapacityProbe, ExternalRecovery, ExternalUnarmed, ExternalFinalize }
internal enum BackgroundJobCommitBoundary { Admission, Business, Control, TerminalClaim, Terminal, Renewal, CapacityRecovery, TransactionalCleanup, ExternalRecovery, ExternalKnownRetry, ExternalUnresolved, ExternalNoPlanTerminalRequest, ExternalFinalizeDisposition, ExternalUninvokableTerminalRequest }
internal enum BackgroundJobExternalRetryMode { OriginalCompleted, RecoveryKnown }
internal enum BackgroundJobExternalUnresolvedDisposition { Running, Unknown, ProbeFault, ProbeTimeout, ReconcileRequired }
internal enum BackgroundJobProbeDisposition { Unresolved, Missing, ChangedOwner, RolledBack, Admitted, AdmissionReleased, ControlCommitted, BusinessCompleted, TerminalCompleted, Renewed, CapacityRecoveryAcquired, CapacityCleanupCommitted }

internal sealed record BackgroundJobRenewal(BackgroundJobExecutionIdentity Identity,
    DateTimeOffset OwnershipUntil, DateTimeOffset DbNow)
{
    internal BackgroundJobExternalRecoveryRenewal? RecoveryLease { get; init; }
}
internal sealed record BackgroundJobControlChange(Guid JobId, long WakeGeneration, string Phase, string? TerminalReason);

/// <summary>Only immutable routing/control facts; no payload or executable receipt.</summary>
internal sealed record BackgroundJobCommitProbe(BackgroundJobCommitBoundary Boundary, Guid JobId, Guid TenantId,
    Guid? OrganizationId, string DefinitionKey, string Queue, long WakeGeneration, Guid? AttemptId, Guid Token,
    long? ExpectedGeneration = null, string? ExpectedPhase = null, DateTimeOffset? ExpectedUntil = null,
    string? CapacityGroup = null, Guid? RecoveryToken = null, int? Ordinal = null,
    DateTimeOffset? Deadline = null, DateTimeOffset? AdmittedAt = null,
    BackgroundJobCapacityCleanupEvidence? CleanupEvidence = null, DateTimeOffset? OriginalDueAt = null,
    string? Profile = null, BackgroundJobLedgerStage? LedgerStage = null, int? FinalizeOrdinal = null,
    Guid? SelectedNormalAttemptId = null, bool IsRecovery = false, Guid? OriginalAdmissionToken = null,
    DateTimeOffset? ExpectedAvailableAfter = null, DateTimeOffset? ExpectedProbeAfter = null,
    int? PreviousUnsuccessfulProbes = null, int? ExpectedUnsuccessfulProbes = null, string? ExpectedQuarantineReason = null,
    string? ExpectedTerminalReason = null)
{
    // Immutable metadata captured under the original status transaction job lock.
    internal BackgroundJobFinalizeControlHistory? FinalizationHistory { get; init; }
    internal BackgroundJobUninvokableRequestHistory? UninvokableHistory { get; init; }
    internal BackgroundJobTerminalClaim? TerminalClaim { get; init; }

    internal static BackgroundJobCommitProbe ForAdmission(BackgroundJobRoutingClaim claim) =>
        new(BackgroundJobCommitBoundary.Admission, claim.JobId.Value, claim.TenantId.Value, claim.OrganizationId?.Value,
            claim.DefinitionKey, claim.Queue, claim.WakeGeneration, null, claim.Token);

    internal static BackgroundJobCommitProbe ForBusiness(BackgroundJobExecutionIdentity identity)
    {
        if (identity.Profile != "transactional" || identity.LedgerStage != BackgroundJobLedgerStage.Normal)
            throw new InvalidOperationException("External history requires its exact checkpoint origin.");
        return new(BackgroundJobCommitBoundary.Business, identity.JobId.Value, identity.TenantId.Value, identity.OrganizationId?.Value,
            identity.DefinitionKey, identity.Queue, identity.WakeGeneration, identity.AttemptId.Value, identity.Token,
            CapacityGroup: identity.CapacityGroup);
    }

    internal static BackgroundJobCommitProbe ForRenewal(BackgroundJobExecutionIdentity identity, DateTimeOffset? expectedUntil) =>
        new(BackgroundJobCommitBoundary.Renewal, identity.JobId.Value, identity.TenantId.Value, identity.OrganizationId?.Value,
            identity.DefinitionKey, identity.Queue, identity.WakeGeneration, identity.AttemptId.Value, identity.Token,
            ExpectedUntil: expectedUntil, CapacityGroup: identity.CapacityGroup, Ordinal: identity.NormalAttemptOrdinal,
            Deadline: identity.Deadline, AdmittedAt: identity.AdmittedAt, OriginalDueAt: identity.OriginalDueAt,
            Profile: identity.Profile, LedgerStage: identity.LedgerStage, FinalizeOrdinal: identity.FinalizeAttemptOrdinal,
            SelectedNormalAttemptId: identity.SelectedNormalAttemptId?.Value,
            IsRecovery: identity.IsRecovery, OriginalAdmissionToken: identity.AdmissionToken);

    internal static BackgroundJobCommitProbe ForExternalKnownRetry(BackgroundJobExecutionIdentity identity) =>
        ForRenewal(identity, null) with { Boundary = BackgroundJobCommitBoundary.ExternalKnownRetry };

    internal static BackgroundJobCommitProbe ForExternalUnresolved(BackgroundJobExecutionIdentity identity) =>
        ForRenewal(identity, null) with { Boundary = BackgroundJobCommitBoundary.ExternalUnresolved };

    internal static BackgroundJobCommitProbe ForCapacityCleanup(BackgroundJobCapacityRecoveryReceipt receipt,
        BackgroundJobCapacityCleanupEvidence evidence) => new(BackgroundJobCommitBoundary.TransactionalCleanup,
            receipt.JobId.Value, receipt.TenantId.Value, receipt.OrganizationId?.Value, receipt.DefinitionKey,
            receipt.Queue, receipt.WakeGeneration, receipt.AttemptId.Value, receipt.AdmissionToken,
            CapacityGroup: receipt.CapacityGroup, RecoveryToken: receipt.RecoveryToken,
            Ordinal: receipt.NormalAttemptOrdinal, Deadline: receipt.Deadline, AdmittedAt: receipt.AdmittedAt,
            CleanupEvidence: evidence);
}

internal sealed record BackgroundJobProbeResult(BackgroundJobProbeDisposition Disposition, Guid JobId,
    string? Phase, long WakeGeneration, int NormalAttempts, Guid? CurrentAttemptId, Guid? OwnershipToken,
    DateTimeOffset? OwnershipUntil, DateTimeOffset? Deadline, DateTimeOffset DbNow)
{
    internal BackgroundJobCapacityRecoveryReceipt? RecoveryReceipt { get; init; }
    internal bool CurrentRecoveryAuthority { get; init; }
}

/// <summary>A claim is published only after the terminal acquisition COMMIT is acknowledged.</summary>
internal sealed class BackgroundJobTerminalClaim
{
    private BackgroundJobTerminalClaim(NpgsqlDataReader row)
    {
        if (row.FieldCount is not (14 or 24)) throw new InvalidOperationException("Terminal acquisition requires its exact returned projection.");
        JobId = row.GetGuid(0);
        TenantId = row.GetGuid(1);
        OrganizationId = row.IsDBNull(2) ? null : row.GetGuid(2);
        DefinitionKey = row.GetString(3);
        Queue = row.GetString(4);
        WakeGeneration = row.GetInt64(5);
        AttemptId = row.IsDBNull(6) ? null : row.GetGuid(6);
        NormalAttempts = row.GetInt32(7);
        Reason = row.GetString(8);
        Token = row.GetGuid(9);
        OwnershipUntil = row.GetFieldValue<DateTimeOffset>(10);
        DbNow = row.GetFieldValue<DateTimeOffset>(11);
        CapacityGroup = row.IsDBNull(12) ? null : row.GetString(12);
        Profile = row.GetString(13);
        if (row.FieldCount == 24) Prepared = BackgroundJobTerminalPreparedFacts.FromReturnedRow(row, NormalAttempts, AttemptId, Reason, DbNow);
        if (JobId.Version != 7 || TenantId == Guid.Empty || TenantId == LearnStack.SharedKernel.Identifiers.TenantId.PlatformSentinel.Value
            || OrganizationId == Guid.Empty || Token == Guid.Empty || WakeGeneration < 1
            || AttemptId is { Version: not 7 } || OwnershipUntil <= DbNow || OwnershipUntil - DbNow > TimeSpan.FromMinutes(10)
            || NormalAttempts is < 0 or > 6 || ((NormalAttempts == 0) != (AttemptId is null))
            || (Prepared is null ? Reason is not ("retired_definition" or "invalid_envelope" or "permanent_failure" or "retry_exhausted")
                : Profile != "external" || CapacityGroup is null)
            || (Reason == "retry_exhausted" && NormalAttempts != 6) || (Reason == "permanent_failure" && NormalAttempts == 0)
            || Profile is not ("transactional" or "external")
            || !BackgroundJobNames.Definition(DefinitionKey) || !BackgroundJobNames.QueueOrPool(Queue))
            throw new InvalidOperationException("Terminal acquisition returned invalid control facts.");
    }

    internal Guid JobId { get; }
    internal Guid TenantId { get; }
    internal Guid? OrganizationId { get; }
    internal string DefinitionKey { get; }
    internal string Queue { get; }
    internal long WakeGeneration { get; }
    internal Guid? AttemptId { get; }
    internal int NormalAttempts { get; }
    internal string Reason { get; }
    internal Guid Token { get; }
    internal DateTimeOffset OwnershipUntil { get; }
    internal DateTimeOffset DbNow { get; }
    internal string? CapacityGroup { get; }
    internal string Profile { get; }
    internal BackgroundJobTerminalPreparedFacts? Prepared { get; }
    internal static BackgroundJobTerminalClaim FromReturnedRow(NpgsqlDataReader row) => new(row);
    internal BackgroundJobCommitProbe Probe(BackgroundJobCommitBoundary boundary) =>
        new(boundary, JobId, TenantId, OrganizationId, DefinitionKey, Queue, WakeGeneration, AttemptId, Token,
            ExpectedUntil: OwnershipUntil, CapacityGroup: CapacityGroup, Profile: Profile, Ordinal: NormalAttempts,
            FinalizeOrdinal: Prepared?.FinalizeAttempts, SelectedNormalAttemptId: Prepared?.SelectedNormalAttemptId,
            IsRecovery: true, ExpectedTerminalReason: Reason)
        { TerminalClaim = Profile == "external" ? this : null };

    internal bool MatchesProbe(BackgroundJobCommitProbe probe) => ReferenceEquals(probe.TerminalClaim, this)
        && probe.JobId == JobId && probe.TenantId == TenantId && probe.OrganizationId == OrganizationId
        && probe.DefinitionKey == DefinitionKey && probe.Queue == Queue && probe.WakeGeneration == WakeGeneration
        && probe.AttemptId == AttemptId && probe.Token == Token && probe.CapacityGroup == CapacityGroup
        && probe.Profile == Profile && probe.Ordinal == NormalAttempts && probe.ExpectedTerminalReason == Reason
        && probe.ExpectedUntil == OwnershipUntil && probe.IsRecovery
        && probe.FinalizeOrdinal == Prepared?.FinalizeAttempts && probe.SelectedNormalAttemptId == Prepared?.SelectedNormalAttemptId
        && (probe.Boundary is BackgroundJobCommitBoundary.TerminalClaim or BackgroundJobCommitBoundary.Terminal
            || probe.Boundary == BackgroundJobCommitBoundary.Control
                && probe.ExpectedGeneration == WakeGeneration && probe.ExpectedPhase == "terminal_pending");
}

/// <summary>A completed operation keeps actual failed cleanup owners and COMMIT uncertainty distinct.</summary>
internal sealed class OwnedBackgroundJobControl<T>(T? value, BackgroundJobControlResources resources,
    IReadOnlyList<BackgroundJobCommitProbe> probes, object? retainedAuthority = null)
{
    // Retain the concrete owner even if only one of its disposal operations failed.
    private readonly BackgroundJobControlResources _resources = resources;
    private readonly object? _retainedAuthority = retainedAuthority;
    internal T? Value { get; } = value;
    internal bool CommitAcknowledged => _resources.CommitAcknowledged;
    internal bool CommitUncertain => _resources.CommitAttempted && !_resources.CommitAcknowledged;
    internal bool CleanupConfirmed => _resources.CleanupConfirmed;
    internal bool Failed => _resources.Failure is not null;
    internal bool KnownRollback => !_resources.IsProbe && !_resources.CommitAttempted && _resources.CleanupConfirmed;
    internal IReadOnlyList<BackgroundJobCommitProbe> Probes { get; } = probes;
    internal void ThrowIfFailed()
    {
        GC.KeepAlive(_retainedAuthority);
        _resources.Failure?.Throw();
    }
}

/// <summary>
/// The original physical acquisition owns one recovery receipt, including when
/// a later locked probe resolves its lost ACK. Reprobing cannot remint entry.
/// </summary>
internal sealed class OwnedBackgroundJobExternalRecovery(OwnedBackgroundJobControl<BackgroundJobExternalRecoveryReceipt> operation,
    BackgroundJobRunGate.RuntimeStop runtime)
{
    internal BackgroundJobRunGate.RuntimeStop Runtime { get; } = runtime;
    private BackgroundJobExternalRecoveryReceipt? _receipt = operation.Value;
    internal BackgroundJobExternalRecoveryReceipt? Value => Volatile.Read(ref _receipt);
    internal bool CommitAcknowledged => operation.CommitAcknowledged;
    internal bool CommitUncertain => operation.CommitUncertain;
    internal bool CleanupConfirmed => operation.CleanupConfirmed;
    internal bool Failed => operation.Failed;
    internal IReadOnlyList<BackgroundJobCommitProbe> Probes => operation.Probes;
    internal void ThrowIfFailed() => operation.ThrowIfFailed();

    internal void RequireDescriptor(BackgroundJobCommitProbe probe)
    {
        if (!Probes.Any(original => ReferenceEquals(original, probe)))
            throw new ArgumentException("Recovery confirmation requires the original operation's exact descriptor.", nameof(probe));
    }

    internal BackgroundJobExternalRecoveryReceipt PublishConfirmation(BackgroundJobCommitProbe probe,
        BackgroundJobExternalRecoveryReceipt confirmed)
    {
        RequireDescriptor(probe);
        if (!CleanupConfirmed) throw new InvalidOperationException("A new probe cannot clean the original acquisition's resources.");
        if (confirmed.JobId.Value != probe.JobId || confirmed.TenantId.Value != probe.TenantId
            || confirmed.OrganizationId?.Value != probe.OrganizationId || confirmed.DefinitionKey != probe.DefinitionKey
            || confirmed.Queue != probe.Queue || confirmed.CapacityGroup != probe.CapacityGroup
            || confirmed.OriginalDueAt != probe.OriginalDueAt || confirmed.AttemptId.Value != probe.AttemptId
            || confirmed.NormalAttemptOrdinal != probe.Ordinal || confirmed.AdmittedAt != probe.AdmittedAt
            || confirmed.Deadline != probe.Deadline || confirmed.AdmissionToken != probe.Token
            || confirmed.RecoveryToken != probe.RecoveryToken || confirmed.WakeGeneration != probe.WakeGeneration
            || confirmed.TerminalReason != probe.ExpectedTerminalReason
            || (confirmed.Purpose == BackgroundJobExternalRecoveryPurpose.TerminalNoStart) != (probe.ExpectedPhase == "terminal_pending"))
            throw new InvalidOperationException("Recovery confirmation must preserve the exact original acquisition identity.");
        return Interlocked.CompareExchange(ref _receipt, confirmed, null) ?? confirmed;
    }
}

/// <summary>One actual operation's fixed resource owners; cleanup faults never discard the owner.</summary>
internal sealed class BackgroundJobControlResources
{
    internal NpgsqlConnection? Connection { get; set; }
    internal NpgsqlTransaction? Transaction { get; set; }
    internal NpgsqlCommand? Command { get; set; }
    internal NpgsqlDataReader? Reader { get; set; }
    internal AsyncServiceScope? AuditScope { get; set; }
    internal bool CommitAttempted { get; set; }
    internal bool IsProbe { get; set; }
    internal bool CommitAcknowledged { get; set; }
    internal bool ProbeRollbackAcknowledged { get; set; }
    internal bool CleanupConfirmed { get; private set; }
    internal ExceptionDispatchInfo? Failure { get; private set; }
    private bool _readerAttempted;
    private bool _commandAttempted;
    private bool _cleanupFailed;

    internal void RecordFailure(Exception failure) => Failure ??= ExceptionDispatchInfo.Capture(failure);

    internal void SetCommand(NpgsqlCommand command)
    {
        if (Command is not null || Reader is not null) throw new InvalidOperationException("An owned SQL command must finish before the next command.");
        Command = command;
        _commandAttempted = false;
    }

    internal void SetReader(NpgsqlDataReader reader)
    {
        if (Reader is not null) throw new InvalidOperationException("An owned reader must finish before the next reader.");
        Reader = reader;
        _readerAttempted = false;
    }

    internal async Task FinishReaderAsync()
    {
        if (Reader is null) return;
        _readerAttempted = true;
        try { await Reader.DisposeAsync().ConfigureAwait(false); Reader = null; }
        catch (Exception failure) when (Catchable(failure)) { _cleanupFailed = true; RecordFailure(failure); throw; }
    }

    internal async Task FinishCommandAsync()
    {
        if (Command is null) return;
        _commandAttempted = true;
        try { await Command.DisposeAsync().ConfigureAwait(false); Command = null; }
        catch (Exception failure) when (Catchable(failure)) { _cleanupFailed = true; RecordFailure(failure); throw; }
    }

    internal async Task CleanupAsync()
    {
        if (Reader is not null && !_readerAttempted)
        {
            try { await FinishReaderAsync().ConfigureAwait(false); }
            catch (Exception failure) when (Catchable(failure)) { RecordFailure(failure); }
        }
        if (Command is not null && !_commandAttempted)
        {
            try { await FinishCommandAsync().ConfigureAwait(false); }
            catch (Exception failure) when (Catchable(failure)) { RecordFailure(failure); }
        }
        if (AuditScope is { } scope)
        {
            try { await scope.DisposeAsync().ConfigureAwait(false); }
            catch (Exception failure) when (Catchable(failure)) { _cleanupFailed = true; RecordFailure(failure); }
        }
        if (Transaction is { } transaction)
        {
            try { await transaction.DisposeAsync().ConfigureAwait(false); }
            catch (Exception failure) when (Catchable(failure)) { _cleanupFailed = true; RecordFailure(failure); }
        }
        if (Connection is { } connection)
        {
            try { await connection.DisposeAsync().ConfigureAwait(false); }
            catch (Exception failure) when (Catchable(failure)) { _cleanupFailed = true; RecordFailure(failure); }
        }
        CleanupConfirmed = !_cleanupFailed;
    }

    internal static bool Catchable(Exception failure) => failure.GetType() != typeof(OutOfMemoryException)
        && failure.GetType() != typeof(StackOverflowException);
}
