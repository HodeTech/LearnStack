using System.Data.Common;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Application.Jobs;

/// <summary>
/// Closed job completion capability. Its sole production subtype and callers are
/// enumerated; this is not a general transaction participant or callback hook.
/// </summary>
public abstract class BackgroundJobCompletionParticipant(IUnitOfWork unit, ITenantContextAccessor accessor)
{
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

    protected ConfirmedStartReceipt Receipt => _receipt
        ?? throw Refused();
    protected IUnitOfWork Unit => _unit;
    internal BackgroundJobCommitGate Gate => _gate ?? throw Refused();

    /// <summary>Only JobExecutor arms the fresh scoped participant before root construction.</summary>
    public void Arm(ConfirmedStartReceipt receipt, BackgroundJobCommitGate gate)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(gate);
        if (_receipt is not null || _unit.HasActiveTransaction || _unit.IsRollbackOnly
            || gate.IsClosed || gate.CommitStarted || gate.CommitAcknowledged
            || _accessor.Current is not { } context)
            throw Refused();
        _receipt = receipt;
        _gate = gate;
        _context = context;
        RequireContext(context);
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
        CancellationToken cancellationToken)
    {
        RequireRoot(unit, rootType, rootFrame);
        await FenceAsync(cancellationToken).ConfigureAwait(false);
        RequireRoot(unit, rootType, rootFrame);
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
    protected abstract Task FenceAsync(CancellationToken cancellationToken);

    private static InvalidOperationException Refused() =>
        new("The background-job participant requires its exact live ordinary owner.");
}
