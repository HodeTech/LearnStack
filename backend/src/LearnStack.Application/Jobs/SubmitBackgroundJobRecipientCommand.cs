using System.Data.Common;
using System.Diagnostics;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;

namespace LearnStack.Application.Jobs;

/// <summary>One exact stored route, with no caller-selected target, parameters or tenant.</summary>
public sealed class SubmitBackgroundJobRecipientCommand : IRequest<Result<BackgroundJobId>>
{
    public SubmitBackgroundJobRecipientCommand(BackgroundJobRecipientClaim claim, BackgroundJobCommitGate gate,
        BackgroundJobRecipientCompletionParticipant completion, BackgroundJobLifecycleProjection projection)
    {
        Claim = claim ?? throw new ArgumentNullException(nameof(claim));
        Gate = gate is { Kind: BackgroundJobCheckpointKind.RecipientSubmission }
            ? gate : throw new ArgumentException("Recipient submission requires its exact prebound stage.", nameof(gate));
        Completion = completion ?? throw new ArgumentNullException(nameof(completion));
        Projection = projection ?? throw new ArgumentNullException(nameof(projection));
        SourceOwner = JobApplicationSourceOwner.Current
            ?? throw new InvalidOperationException("Recipient submission requires its application-source owner.");
    }

    public BackgroundJobRecipientClaim Claim { get; }
    public BackgroundJobCommitGate Gate { get; }
    public BackgroundJobRecipientCompletionParticipant Completion { get; }
    public BackgroundJobLifecycleProjection Projection { get; }
    public JobApplicationSourceOwner SourceOwner { get; }
    public BackgroundJobId? ProvisionalChild => Completion.Child;
    public BackgroundJobId? AcceptedChild => Gate.CommitAcknowledged ? Completion.Child : null;
    public bool Ineligible => Completion.Ineligible;
}

/// <summary>Closed recipient completion; the existing coordinator alone owns its transaction and COMMIT.</summary>
public abstract class BackgroundJobRecipientCompletionParticipant(IUnitOfWork unit, ITenantContextAccessor accessor)
{
    private readonly IUnitOfWork _unit = unit ?? throw new ArgumentNullException(nameof(unit));
    private readonly ITenantContextAccessor _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
    private ITenantContext? _context;
    private SubmitBackgroundJobRecipientCommand? _command;
    private DbConnection? _connection;
    private DbTransaction? _transaction;
    private TenantContextAnnouncement? _announcement;
    private BackgroundJobId? _child;
    private bool _existing;
    private bool _ineligible;
    private long _observedAt;
    private TimeSpan _remaining;
    private int _budgetState;

    protected IUnitOfWork Unit => _unit;
    protected SubmitBackgroundJobRecipientCommand Command => _command ?? throw Refused();
    public BackgroundJobId? Child => _child;
    public bool Existing => _existing;
    public bool Ineligible => _ineligible;

    /// <summary>Only the executor arms one fresh scoped participant.</summary>
    public void Arm(SubmitBackgroundJobRecipientCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (_command is not null || _unit.HasActiveTransaction || _unit.IsRollbackOnly
            || !ReferenceEquals(command.Completion, this) || command.Gate.IsClosed
            || command.Gate.CommitStarted || command.Gate.CommitAcknowledged
            || !ReferenceEquals(command.SourceOwner, JobApplicationSourceOwner.Current)) throw Refused();
        command.SourceOwner.RequireFreshScopeReady();
        _command = command;
        _context = _accessor.Current;
        if (_context is null || !PermitsContext(_context)) throw Refused();
    }

    internal bool PermitsContext(ITenantContext context) => ReferenceEquals(context, _context)
        && ReferenceEquals(_accessor.Current, context) && context.IsResolved
        && context.Origin == TenantContextOrigin.Ambient && context.HostScope is null
        && context.UserId == SharedKernel.Identifiers.UserId.SystemActor
        && context.TenantId == Command.Claim.TenantId && context.OrganizationId == Command.Claim.OrganizationId;

    internal void RequireArmed(SubmitBackgroundJobRecipientCommand command, IUnitOfWork unit)
    {
        if (!ReferenceEquals(_command, command) || !ReferenceEquals(_unit, unit) || _transaction is not null
            || unit.HasActiveTransaction || unit.IsRollbackOnly) throw Refused();
    }

    internal void BindOwner(IUnitOfWork unit, bool owner)
    {
        BackgroundJobExecutionAdmission.RequireRecipient(Command);
        if (!owner || !ReferenceEquals(_unit, unit) || _transaction is not null) throw Refused();
        _connection = unit.Connection;
        _transaction = unit.Transaction;
        _announcement = unit.GetTenantContextAnnouncement(_transaction);
        RequireOwner(unit);
    }

    public void RequireOwner(IUnitOfWork unit)
    {
        BackgroundJobExecutionAdmission.RequireRecipient(Command);
        Command.SourceOwner.RequireOwnedMainUnit(unit);
        if (!ReferenceEquals(_unit, unit) || !ReferenceEquals(Command.SourceOwner, JobApplicationSourceOwner.Current)
            || _context is null || !PermitsContext(_context)
            || Command.Gate.IsClosed || Command.Gate.KnownRollback || Command.Gate.CommitAcknowledged
            || !_unit.HasActiveTransaction || _unit.IsRollbackOnly || _unit.Mode != TransactionMode.ReadWrite
            || !ReferenceEquals(_connection, unit.Connection) || !ReferenceEquals(_transaction, unit.Transaction)
            || !ReferenceEquals(_transaction?.Connection, _connection) || _announcement is not { Generation: > 0 }
            || _announcement != unit.GetTenantContextAnnouncement(_transaction)
            || _announcement.TenantId != Command.Claim.TenantId
            || _announcement.OrganizationId != Command.Claim.OrganizationId) throw Refused();
    }

    /// <summary>Only successful insertion or the locked stored receipt may supply an identity.</summary>
    public void RecordChild(IUnitOfWork unit, BackgroundJobId child, bool existing)
    {
        RequireOwner(unit);
        if (_child is not null || _ineligible || !child.IsInitialized() || child.Value.Version != 7) throw Refused();
        _child = child;
        _existing = existing;
    }

    protected void RecordIneligible()
    {
        RequireOwner(_unit);
        if (_child is not null) throw Refused();
        _ineligible = true;
    }

    protected void BeginCommitBudgetRead()
    {
        RequireOwner(_unit);
        if (Interlocked.CompareExchange(ref _budgetState, 1, 0) != 0) throw Refused();
        _observedAt = Stopwatch.GetTimestamp();
    }

    protected void RecordCommitBudget(DbDataReader row)
    {
        RequireOwner(_unit);
        if (Interlocked.CompareExchange(ref _budgetState, 2, 1) != 1 || row.FieldCount != 2
            || row.IsDBNull(0) || row.IsDBNull(1)) throw Refused();
        _remaining = row.GetFieldValue<DateTimeOffset>(0) - row.GetFieldValue<DateTimeOffset>(1);
        if (_remaining <= TimeSpan.Zero || _remaining > TimeSpan.FromMinutes(10)) throw Refused();
        Volatile.Write(ref _budgetState, 3);
    }

    internal async Task FenceOwnerAsync(IUnitOfWork unit, IResultBase response, CancellationToken cancellationToken)
    {
        RequireOwner(unit);
        if (_ineligible || response is not Result<BackgroundJobId> { IsSuccess: true } success
            || _child is not { } child || success.Value != child) throw Refused();
        await FenceReceiptAsync(cancellationToken).ConfigureAwait(false);
        RequireOwner(unit);
    }

    internal void MarkCommitting(IUnitOfWork unit)
    {
        RequireOwner(unit);
        if (_child is null || Interlocked.CompareExchange(ref _budgetState, 4, 3) != 3
            || !Command.Gate.TryBeginCheckpointCommit(_observedAt, _remaining))
            throw new OperationCanceledException("Recipient COMMIT admission is closed.");
    }

    internal void RecordCommitted(IUnitOfWork unit)
    {
        if (!ReferenceEquals(_unit, unit) || _child is null || !Command.Gate.CommitStarted || Command.Gate.CommitAcknowledged)
            throw Refused();
        Command.Gate.RecordCommitted();
    }

    internal void RecordRolledBack() => Command.Gate.RecordRolledBack();
    protected abstract Task FenceReceiptAsync(CancellationToken cancellationToken);

    private static InvalidOperationException Refused() =>
        new("Recipient completion requires its exact live announced framework owner.");
}
