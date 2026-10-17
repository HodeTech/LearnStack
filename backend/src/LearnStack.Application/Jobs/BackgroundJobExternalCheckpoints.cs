using System.Data.Common;
using System.Diagnostics;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Application.Jobs;

/// <summary>Immutable app-role checkpoint data; these values confer no execution permission.</summary>
public sealed record BackgroundJobExternalSnapshot(string? PlanJson, string? PlanFingerprint, Guid? OperationId,
    string? OutcomeJson, BackgroundJobExternalState? OutcomeState,
    BackgroundJobExternalFailurePolicy? FailurePolicy)
{
    public BackgroundJobEnvelope? Envelope { get; init; }
    public bool ExecuteIntentRecorded { get; init; }
    public bool NoStartRecorded { get; init; }
    public BackgroundJobAttemptId? SelectedNormalAttemptId { get; init; }
    public BackgroundJobFinalizationStatus? FinalizationStatus { get; init; }
    public BackgroundJobFinalizationReason? FinalizationReason { get; init; }
}

public sealed class ReadBackgroundJobExternalStateQuery
    : IRequest<Result<BackgroundJobExternalSnapshot>>
{
    public ReadBackgroundJobExternalStateQuery(ConfirmedStartReceipt receipt) =>
        Owner = new(new(receipt), null, typeof(ReadBackgroundJobExternalStateQuery));
    public ReadBackgroundJobExternalStateQuery(BackgroundJobExternalRecoveryBinding recovery) =>
        Owner = new(new(recovery), null, typeof(ReadBackgroundJobExternalStateQuery));
    internal BackgroundJobExternalCheckpointOwner Owner { get; }
    public BackgroundJobExternalCheckpointAuthority Authority => Owner.Authority;
    public bool CommitAcknowledged => Owner.CommitAcknowledged;
    public BackgroundJobExternalSnapshot? RecordedSnapshot => Owner.RecordedRead;
    public void RequireOwner(IUnitOfWork unit) => Owner.RequireOwner(unit);
    public void RecordRead(IUnitOfWork unit, BackgroundJobExternalSnapshot snapshot) => Owner.RecordRead(unit, snapshot);
}

/// <summary>Writes the original attempt's ExecuteIntent before any provider entry.</summary>
public sealed class RecordBackgroundJobArmCommand(ConfirmedStartReceipt receipt, BackgroundJobCommitGate gate,
    string planFingerprint, Guid operationId) : IRequest<Result<None>>
{
    internal BackgroundJobExternalCheckpointOwner Owner { get; } = new(new(receipt),
        gate is { Kind: BackgroundJobCheckpointKind.Arm } ? gate
            : throw new ArgumentException("Arm requires its exact prebound stage.", nameof(gate)),
        typeof(RecordBackgroundJobArmCommand));
    public ConfirmedStartReceipt Receipt => Owner.Authority.Original;
    public BackgroundJobExternalCheckpointAuthority Authority => Owner.Authority;
    public BackgroundJobCommitGate Gate => Owner.Gate!;
    public bool CommitAcknowledged => Owner.CommitAcknowledged;
    public string PlanFingerprint { get; } = planFingerprint;
    public Guid OperationId { get; } = operationId;
    public void RequireOwner(IUnitOfWork unit) => Owner.RequireOwner(unit);
    public void RecordCheckpoint(IUnitOfWork unit, long observedAt, TimeSpan remaining) => Owner.RecordCheckpoint(unit, observedAt, remaining);
}

/// <summary>Persists a completed observation and its exact reservation release together.</summary>
public sealed class RecordBackgroundJobOutcomeCommand
    : IRequest<Result<None>>
{
    private enum OutcomeMode { Report, NoStart, StoredCompleted, TerminalNoStart }
    private sealed record CompletedFacts(string OutcomeJson,
        BackgroundJobExternalState State, BackgroundJobExternalFailurePolicy? Policy,
        Guid QuiescenceId, BackgroundJobQuiescenceKind QuiescenceKind);
    private readonly CompletedFacts? _completed;
    private readonly OutcomeMode _mode;
    private readonly string? _planFingerprint;
    private readonly Guid? _operationId;
    private CompletedFacts Completed => _completed
        ?? throw new InvalidOperationException("This checkpoint carries no provider state, payload, policy or quiescence report.");

    public RecordBackgroundJobOutcomeCommand(ConfirmedStartReceipt receipt, BackgroundJobCommitGate gate,
        string planFingerprint, Guid operationId, string outcomeJson, BackgroundJobExternalState state,
        BackgroundJobExternalFailurePolicy? policy, Guid quiescenceId, BackgroundJobQuiescenceKind quiescenceKind)
        : this(new(receipt), gate, OutcomeMode.Report, planFingerprint, operationId,
            new(outcomeJson, state, policy, quiescenceId, quiescenceKind))
    { }

    public RecordBackgroundJobOutcomeCommand(BackgroundJobExternalRecoveryBinding recovery, BackgroundJobCommitGate gate,
        string planFingerprint, Guid operationId, string outcomeJson, BackgroundJobExternalState state,
        BackgroundJobExternalFailurePolicy? policy, Guid quiescenceId, BackgroundJobQuiescenceKind quiescenceKind)
        : this(new(recovery), gate, OutcomeMode.Report, planFingerprint, operationId,
            new(outcomeJson, state, policy, quiescenceId, quiescenceKind))
    { }

    private RecordBackgroundJobOutcomeCommand(BackgroundJobExternalCheckpointAuthority authority,
        BackgroundJobCommitGate gate, OutcomeMode mode, string? planFingerprint, Guid? operationId, CompletedFacts? completed)
    {
        ArgumentNullException.ThrowIfNull(gate);
        if (!Enum.IsDefined(mode) || (completed is not null) != (mode == OutcomeMode.Report)
            || gate.Kind != (mode is OutcomeMode.NoStart or OutcomeMode.TerminalNoStart
                ? BackgroundJobCheckpointKind.NoStart : BackgroundJobCheckpointKind.Outcome)
            || mode != OutcomeMode.Report && !authority.IsRecovery
            || authority.IsTerminalNoStart != (mode == OutcomeMode.TerminalNoStart)
            || mode is OutcomeMode.NoStart or OutcomeMode.TerminalNoStart && (planFingerprint is not null || operationId is not null)
            || mode == OutcomeMode.StoredCompleted && (string.IsNullOrWhiteSpace(planFingerprint)
                || operationId is null || operationId == Guid.Empty))
            throw new ArgumentException("Outcome or no-start requires its exact prebound stage.", nameof(gate));
        _mode = mode;
        _planFingerprint = planFingerprint;
        _operationId = operationId;
        _completed = completed;
        Owner = new(authority, gate, typeof(RecordBackgroundJobOutcomeCommand));
    }

    public static RecordBackgroundJobOutcomeCommand ForNoStart(BackgroundJobExternalRecoveryBinding recovery,
        BackgroundJobCommitGate gate) => new(new(recovery), gate, OutcomeMode.NoStart, null, null, null);

    public static RecordBackgroundJobOutcomeCommand ForTerminalNoStart(BackgroundJobExternalRecoveryBinding recovery,
        BackgroundJobCommitGate gate) => new(new(recovery), gate, OutcomeMode.TerminalNoStart, null, null, null);

    /// <summary>Selects finalization from stored facts without supplying another provider report.</summary>
    public static RecordBackgroundJobOutcomeCommand ForStoredCompleted(BackgroundJobExternalRecoveryBinding recovery,
        BackgroundJobCommitGate gate, string planFingerprint, Guid operationId) =>
        new(new(recovery), gate, OutcomeMode.StoredCompleted, planFingerprint, operationId, null);

    internal BackgroundJobExternalCheckpointOwner Owner { get; }
    public BackgroundJobExternalCheckpointAuthority Authority => Owner.Authority;
    public BackgroundJobCommitGate Gate => Owner.Gate!;
    public bool IsNoStart => _mode is OutcomeMode.NoStart or OutcomeMode.TerminalNoStart;
    public bool IsTerminalNoStart => _mode == OutcomeMode.TerminalNoStart;
    public bool IsStoredCompleted => _mode == OutcomeMode.StoredCompleted;
    public bool CommitAcknowledged => Owner.CommitAcknowledged;
    public string PlanFingerprint => _planFingerprint
        ?? throw new InvalidOperationException("No-start has no caller-supplied plan identity.");
    public Guid OperationId => _operationId
        ?? throw new InvalidOperationException("No-start has no caller-supplied operation identity.");
    public string OutcomeJson => Completed.OutcomeJson;
    public BackgroundJobExternalState State => Completed.State;
    public BackgroundJobExternalFailurePolicy? Policy => Completed.Policy;
    public Guid QuiescenceId => Completed.QuiescenceId;
    public BackgroundJobQuiescenceKind QuiescenceKind => Completed.QuiescenceKind;
    public void RequireOwner(IUnitOfWork unit) => Owner.RequireOwner(unit);
    public void RecordCheckpoint(IUnitOfWork unit, long observedAt, TimeSpan remaining) => Owner.RecordCheckpoint(unit, observedAt, remaining);
}

// This sealed state belongs to exactly the three named requests above. It is
// neither a transaction participant nor a module-supplied callback seam.
internal sealed class BackgroundJobExternalCheckpointOwner
{
    private IUnitOfWork? _unit;
    private DbConnection? _connection;
    private DbTransaction? _transaction;
    private TenantContextAnnouncement? _announcement;
    private bool _recorded;
    private long _observedAt;
    private TimeSpan _remaining;
    internal BackgroundJobExternalSnapshot? RecordedRead { get; private set; }
    internal bool CommitAcknowledged { get; private set; }

    internal BackgroundJobExternalCheckpointOwner(BackgroundJobExternalCheckpointAuthority authority, BackgroundJobCommitGate? gate, Type requestType)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (gate is not null && !ReferenceEquals(gate.Run, authority.Run)
            || requestType == typeof(RecordBackgroundJobArmCommand) && authority.IsRecovery
            || authority.LedgerStage == BackgroundJobLedgerStage.Finalize && requestType != typeof(ReadBackgroundJobExternalStateQuery)
            || requestType != typeof(ReadBackgroundJobExternalStateQuery)
                && requestType != typeof(RecordBackgroundJobArmCommand)
                && requestType != typeof(RecordBackgroundJobOutcomeCommand))
            throw Refused();
        Authority = authority;
        Gate = gate;
        RequestType = requestType;
    }

    internal BackgroundJobExternalCheckpointAuthority Authority { get; }
    internal BackgroundJobCommitGate? Gate { get; }
    internal Type RequestType { get; }
    internal bool IsReadOnly => RequestType == typeof(ReadBackgroundJobExternalStateQuery);

    internal void BindOwner(IUnitOfWork unit, bool owner)
    {
        BackgroundJobExecutionAdmission.RequireExternalCheckpoint(this);
        if (!owner || _unit is not null || unit.Mode != (IsReadOnly ? TransactionMode.ReadOnly : TransactionMode.ReadWrite))
            throw Refused();
        _unit = unit;
        _connection = unit.Connection;
        _transaction = unit.Transaction;
        _announcement = unit.GetTenantContextAnnouncement(_transaction);
        RequireOwner(unit);
    }

    internal void RequireOwner(IUnitOfWork unit)
    {
        BackgroundJobExecutionAdmission.RequireExternalCheckpoint(this);
        Authority.SourceOwner.RequireOwnedMainUnit(unit);
        if (!ReferenceEquals(Authority.SourceOwner, JobApplicationSourceOwner.Current)
            || !ReferenceEquals(_unit, unit) || !ReferenceEquals(_connection, unit.Connection)
            || !ReferenceEquals(_transaction, unit.Transaction) || !ReferenceEquals(_transaction?.Connection, _connection)
            || !unit.HasActiveTransaction || unit.IsRollbackOnly || Authority.Run.IsClosed || Gate?.IsClosed == true
            || unit.Mode != (IsReadOnly ? TransactionMode.ReadOnly : TransactionMode.ReadWrite)
            || _announcement is not { Generation: > 0 }
            || _announcement != unit.GetTenantContextAnnouncement(_transaction)
            || _announcement.TenantId != Authority.TenantId
            || _announcement.OrganizationId != Authority.OrganizationId)
            throw Refused();
    }

    internal void RecordCheckpoint(IUnitOfWork unit, long observedAt, TimeSpan remaining)
    {
        RequireOwner(unit);
        if (IsReadOnly || _recorded || observedAt > Stopwatch.GetTimestamp()
            || remaining <= TimeSpan.Zero || remaining > TimeSpan.FromMinutes(10)) throw Refused();
        _observedAt = observedAt;
        _remaining = remaining;
        _recorded = true;
    }

    internal void RecordRead(IUnitOfWork unit, BackgroundJobExternalSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        RequireOwner(unit);
        if (!IsReadOnly || _recorded || snapshot.Envelope is not { } envelope || !Authority.MatchesEnvelope(envelope))
            throw Refused();
        RecordedRead = snapshot;
        _recorded = true;
    }

    internal void MarkCommitting(IUnitOfWork unit, IResultBase response)
    {
        RequireOwner(unit);
        if (IsReadOnly)
        {
            if (!_recorded || response is not Result<BackgroundJobExternalSnapshot> { IsSuccess: true } read
                || !ReferenceEquals(read.Value, RecordedRead)) throw Refused();
            return;
        }
        if (response is not Result<None> { IsSuccess: true }) throw Refused();
        if (!IsReadOnly && (!_recorded || Gate?.TryBeginCheckpointCommit(_observedAt, _remaining) != true))
            throw new OperationCanceledException("External checkpoint COMMIT admission is closed.");
    }

    internal void RecordCommitted(IUnitOfWork unit)
    {
        if (!ReferenceEquals(_unit, unit) || !IsReadOnly && (!_recorded || Gate is not { CommitStarted: true, CommitAcknowledged: false }))
            throw Refused();
        if (!IsReadOnly) Gate!.RecordCommitted();
        CommitAcknowledged = true;
    }
    internal void RecordRolledBack() { if (!IsReadOnly) Gate!.RecordRolledBack(); }

    internal static BackgroundJobExternalCheckpointOwner? For(object request) => request switch
    {
        ReadBackgroundJobExternalStateQuery read => read.Owner,
        RecordBackgroundJobArmCommand arm => arm.Owner,
        RecordBackgroundJobOutcomeCommand outcome => outcome.Owner,
        _ => null,
    };

    private static InvalidOperationException Refused() =>
        new("External checkpoints require their exact live app-role request owner.");
}
