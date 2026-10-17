using System.Data.Common;
using System.Diagnostics;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Application.Jobs;

/// <summary>
/// Closed job completion capability. Its sole production subtype and callers are
/// enumerated; this is not a general transaction participant or callback hook.
/// </summary>
public abstract class BackgroundJobCompletionParticipant(IUnitOfWork unit, ITenantContextAccessor accessor)
{
    private const int BudgetInitializing = 1;
    private const int BudgetReadStarted = 2;
    private const int BudgetRecording = 3;
    private const int BudgetRecorded = 4;
    private const int BudgetConsumed = 5;
    private readonly IUnitOfWork _unit = unit ?? throw new ArgumentNullException(nameof(unit));
    private readonly ITenantContextAccessor _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
    private ITenantContext? _context;
    private ConfirmedStartReceipt? _receipt;
    private BackgroundJobCommitGate? _gate;
    private object? _rootFrame;
    private Type? _rootType;
    private DbConnection? _connection;
    private DbTransaction? _transaction;
    private TenantContextAnnouncement? _announcement;
    private BackgroundJobExternalCheckpointAuthority? _externalAuthority;
    private long _commitBudgetStartedAt;
    private TimeSpan _commitBudgetRemaining;
    private int _commitBudgetState;

    protected ConfirmedStartReceipt Receipt => _receipt
        ?? throw Refused();
    protected IUnitOfWork Unit => _unit;
    protected BackgroundJobExternalCheckpointAuthority ExternalAuthority => _externalAuthority ?? throw Refused();
    internal BackgroundJobCommitGate Gate => _gate ?? throw Refused();

    /// <summary>Only JobExecutor arms the fresh scoped participant before root construction.</summary>
    public void Arm(ConfirmedStartReceipt receipt, BackgroundJobCommitGate gate)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(gate);
        var kind = receipt.Stage switch
        {
            SharedKernel.Jobs.BackgroundJobExecutionStage.Transactional => BackgroundJobCheckpointKind.Transactional,
            SharedKernel.Jobs.BackgroundJobExecutionStage.Prepare => BackgroundJobCheckpointKind.Prepare,
            SharedKernel.Jobs.BackgroundJobExecutionStage.Finalize => BackgroundJobCheckpointKind.Finalize,
            _ => throw Refused(),
        };
        if (_receipt is not null || _unit.HasActiveTransaction || _unit.IsRollbackOnly
            || gate.Kind != kind || gate.IsClosed || gate.CommitStarted || gate.CommitAcknowledged
            || !ReferenceEquals(gate.Run, receipt.Run)
            || _accessor.Current is not { } context)
            throw Refused();
        _receipt = receipt;
        _gate = gate;
        _context = context;
        RequireContext(context);
        if (receipt.Claim.Profile == SharedKernel.Jobs.BackgroundJobExecutionProfile.External)
            _externalAuthority = new(receipt);
    }

    internal void RequireArmed(ConfirmedStartReceipt receipt, IUnitOfWork unit)
    {
        if (!ReferenceEquals(_receipt, receipt) || !ReferenceEquals(_unit, unit)
            || _rootFrame is not null || unit.HasActiveTransaction || unit.IsRollbackOnly)
            throw Refused();
        RequireContext(_context!);
    }

    internal bool PermitsContext(ITenantContext context) => _receipt is { } receipt
        && ReferenceEquals(_context, context) && ReferenceEquals(_accessor.Current, _context)
        && context.IsResolved && context.Origin == TenantContextOrigin.Ambient && context.HostScope is null
        && context.UserId == SharedKernel.Identifiers.UserId.SystemActor
        && context.TenantId == receipt.Envelope.TenantId && context.OrganizationId == receipt.Envelope.OrganizationId
        && !Gate.IsClosed && !Gate.KnownRollback && !Gate.CommitAcknowledged;

    internal void RequireContext(ITenantContext context)
    {
        if (!PermitsContext(context)) throw Refused();
    }

    internal void RequireEntry(IUnitOfWork unit, bool root, TransactionMode mode)
    {
        if (root) Receipt.SourceOwner.RequireFreshScopeReady();
        else Receipt.SourceOwner.RequireOwnedMainUnit(unit);
        if (!ReferenceEquals(_unit, unit) || unit.IsRollbackOnly || mode != TransactionMode.ReadWrite)
            throw Refused();
        RequireContext(_context!);
        if (root)
        {
            if (_rootFrame is not null || unit.HasActiveTransaction) throw Refused();
        }
        else
        {
            RequireCurrent();
        }
    }

    internal async Task BindAndPreflightAsync(IUnitOfWork unit, bool owner, Type rootType,
        object rootFrame, CancellationToken cancellationToken)
    {
        if (!owner || !ReferenceEquals(unit, _unit) || _rootFrame is not null
            || unit.Transaction is not { } transaction)
            throw Refused();
        _rootFrame = rootFrame;
        _rootType = rootType;
        _transaction = transaction;
        _connection = unit.Connection;
        _announcement = unit.GetTenantContextAnnouncement(transaction);
        RequireCurrent();
        await PreflightAsync(cancellationToken).ConfigureAwait(false);
        RequireCurrent();
    }

    internal void RequireRoot(IUnitOfWork unit, Type rootType, object rootFrame)
    {
        if (!ReferenceEquals(unit, _unit) || _rootType != rootType || !ReferenceEquals(_rootFrame, rootFrame))
            throw Refused();
        RequireCurrent();
    }

    internal async Task FenceOwnerAsync(IUnitOfWork unit, Type rootType, object rootFrame,
        IResultBase response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.IsFailure) throw Refused();
        RequireRoot(unit, rootType, rootFrame);
        await FenceAsync(response, cancellationToken).ConfigureAwait(false);
        RequireRoot(unit, rootType, rootFrame);
    }

    /// <summary>Starts one conservative interval before the actual locked SQL observation.</summary>
    protected void BeginCommitBudgetRead()
    {
        RequireCurrent();
        if (Interlocked.CompareExchange(ref _commitBudgetState, BudgetInitializing, 0) != 0) throw Refused();
        _commitBudgetStartedAt = Stopwatch.GetTimestamp();
        Volatile.Write(ref _commitBudgetState, BudgetReadStarted);
    }

    /// <summary>Only the concrete owner records its fresh lease/deadline row, before any completion writes.</summary>
    protected void RecordCommitBudget(DbDataReader row)
    {
        ArgumentNullException.ThrowIfNull(row);
        RequireCurrent();
        // Own the one record before reading or writing fields. A concurrent or
        // repeated record cannot overwrite the first interval, even if it faults.
        if (Interlocked.CompareExchange(ref _commitBudgetState, BudgetRecording, BudgetReadStarted) != BudgetReadStarted
            || row.FieldCount != 3
            || row.IsDBNull(0) || row.IsDBNull(1) || row.IsDBNull(2)) throw Refused();
        var until = row.GetFieldValue<DateTimeOffset>(0);
        var deadline = row.GetFieldValue<DateTimeOffset>(1);
        var now = row.GetFieldValue<DateTimeOffset>(2);
        if (deadline != Receipt.Deadline) throw Refused();
        var remaining = (until < deadline ? until : deadline) - now;
        if (remaining <= TimeSpan.Zero || remaining > TimeSpan.FromMinutes(10)) throw Refused();
        _commitBudgetRemaining = remaining;
        Volatile.Write(ref _commitBudgetState, BudgetRecorded);
    }

    internal bool TryBeginCommit(IUnitOfWork unit, Type rootType, object rootFrame)
    {
        RequireRoot(unit, rootType, rootFrame);
        if (Interlocked.CompareExchange(ref _commitBudgetState, BudgetConsumed, BudgetRecorded) != BudgetRecorded) throw Refused();
        return Gate.TryBeginCheckpointCommit(_commitBudgetStartedAt, _commitBudgetRemaining);
    }

    /// <summary>Checks the original physical pair and successful announcement after every await.</summary>
    protected void RequireCurrent()
    {
        Receipt.SourceOwner.RequireOwnedMainUnit(_unit);
        RequireContext(_context!);
        if (_rootFrame is null || !_unit.HasActiveTransaction || _unit.IsRollbackOnly
            || _unit.Mode != TransactionMode.ReadWrite || _announcement is not { Generation: > 0 }
            || _announcement.TenantId != Receipt.Envelope.TenantId
            || _announcement.OrganizationId != Receipt.Envelope.OrganizationId
            || !ReferenceEquals(_unit.Transaction, _transaction) || !ReferenceEquals(_unit.Connection, _connection)
            || !ReferenceEquals(_transaction?.Connection, _connection)
            || _unit.GetTenantContextAnnouncement(_transaction) != _announcement)
            throw Refused();
    }

    protected abstract Task PreflightAsync(CancellationToken cancellationToken);
    protected abstract Task FenceAsync(IResultBase response, CancellationToken cancellationToken);

    private static InvalidOperationException Refused() =>
        new("The background-job participant requires its exact live ordinary owner.");
}
