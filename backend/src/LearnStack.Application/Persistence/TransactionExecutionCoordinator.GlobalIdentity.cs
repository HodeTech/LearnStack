using System.Data.Common;
using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Application.Persistence;

public sealed partial class TransactionExecutionCoordinator
{
    public static Task<PreparedGlobalIdentityOwner> PrepareGlobalIdentityOwnerAsync(
        IUnitOfWork unit, IAuditStateCapture capture, ITenantContext tenantContext, GlobalIdentityOwnerDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        ArgumentNullException.ThrowIfNull(tenantContext);
        if (tenantContext.IsResolved || tenantContext is EventTenantContext or JobTenantContext)
            throw new InvalidOperationException("Global identity preparation cannot inherit tenant execution.");
        return PreparedGlobalIdentityOwner.CreateAsync(unit, capture, descriptor, cancellationToken);
    }

    public Task<TResponse> ExecutePreparedGlobalIdentityAsync<TResponse>(PreparedGlobalIdentityOwner owner,
        GlobalIdentityCreationReference creation, Func<Task<TResponse>> next,
        CancellationToken cancellationToken = default) where TResponse : IResultBase
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(creation);
        ArgumentNullException.ThrowIfNull(next);
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        if (tenantContext.IsResolved || capture.Intents.Count != 1
            || !ReferenceEquals(capture.Intents[0].GlobalIdentity, creation)
            || !ReferenceEquals(creation.Descriptor, owner.Descriptor))
            throw new InvalidOperationException("Global creation requires its one central intent and unresolved context.");
        creation.RequireLive(unitOfWork);
        var frame = owner.Consume(unitOfWork, capture);
        return ExecuteFrameAsync(frame, null, GlobalIdentityCreationReference.Operation, next,
            null, null, null, null, null, null, null, null, null, cancellationToken, owner, creation);
    }

#pragma warning disable CA1034 // Private construction retains the original common owner frame without friend assemblies.
    public sealed class PreparedGlobalIdentityOwner : IAsyncDisposable
#pragma warning restore CA1034
    {
        private readonly IGlobalIdentityUnitOfWork _unit;
        private readonly IAuditStateCapture _capture;
        private readonly IUnitOfWorkScope _frame;
        private readonly DbConnection _connection;
        private readonly DbTransaction _transaction;
        private readonly GlobalIdentityContextAnnouncement _announcement;
        private bool _absenceAdmitted;
        private bool _consumed;
        private bool _disposed;

        private PreparedGlobalIdentityOwner(IGlobalIdentityUnitOfWork unit, IAuditStateCapture capture,
            IUnitOfWorkScope frame, GlobalIdentityContextAnnouncement announcement)
        {
            _unit = unit;
            _capture = capture;
            _frame = frame;
            _connection = unit.Connection;
            _transaction = unit.Transaction!;
            _announcement = announcement;
        }

        public GlobalIdentityOwnerDescriptor Descriptor => _announcement.Descriptor;
        public AuditIntentState RecordedOutcome { get; private set; }
        internal IGlobalIdentityUnitOfWork Unit => _unit;
        internal GlobalIdentityContextAnnouncement Announcement => _announcement;
        internal bool HasBeenConsumed => _consumed;

        internal static async Task<PreparedGlobalIdentityOwner> CreateAsync(IUnitOfWork unit,
            IAuditStateCapture capture, GlobalIdentityOwnerDescriptor descriptor, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(capture);
            ArgumentNullException.ThrowIfNull(descriptor);
            if (unit is not IGlobalIdentityUnitOfWork global || unit.HasActiveTransaction || unit.IsRollbackOnly
                || capture.HasOpenFrame || capture.Intents.Count != 0 || capture.Changes.Count != 0
                || capture.State != AuditIntentState.None || descriptor.Kind == GlobalIdentityOwnerKind.Lookup)
                throw new InvalidOperationException("Global creation preparation requires a fresh unit and capture.");
            var frame = await global.BeginGlobalIdentityTransactionAsync(descriptor, TransactionMode.ReadWrite, cancellationToken);
            try
            {
                await global.AcquireGlobalIdentitySubjectLockAsync(descriptor, cancellationToken);
                var announcement = global.GetGlobalIdentityAnnouncement(global.Transaction)
                    ?? throw new InvalidOperationException("Global preparation requires its successful announcement.");
                var owner = new PreparedGlobalIdentityOwner(global, capture, frame, announcement);
                owner.RequireLive(unit, capture);
                return owner;
            }
            catch
            {
                unit.MarkRollbackOnly();
                try { await frame.FailAsync(CancellationToken.None); }
                catch { /* Preserve preparation failure. */ }
                try { await frame.DisposeAsync(); }
                catch { /* The owning scope retains connection cleanup. */ }
                throw;
            }
        }

        /// <summary>Only the censused internal lock-and-fresh-read path admits serialized absence.</summary>
        public void AdmitSerializedAbsence()
        {
            RequireLive(_unit, _capture);
            if (_absenceAdmitted || _consumed || _capture.HasOpenFrame || _capture.Intents.Count != 0)
                throw new InvalidOperationException("Serialized absence can be admitted only once before audit entry.");
            _absenceAdmitted = true;
        }

        /// <summary>Completes an existing-binding lookup without declaring a creation intent.</summary>
        public async Task CompleteExistingAsync(CancellationToken cancellationToken = default)
        {
            RequireLive(_unit, _capture);
            if (_absenceAdmitted || _consumed || _capture.HasOpenFrame || _capture.Intents.Count != 0
                || _capture.Changes.Count != 0 || _capture.State != AuditIntentState.None)
                throw new InvalidOperationException("An absent binding cannot complete as an existing binding.");
            await _unit.ValidateGlobalIdentityConstraintsAsync(Descriptor, cancellationToken);
            await _frame.CompleteAsync(cancellationToken);
            _disposed = true;
            await _frame.DisposeAsync();
        }

        internal void RequireCreationEntry(IAuditStateCapture capture)
        {
            RequireLive(_unit, capture);
            if (!_absenceAdmitted || _consumed || capture.HasOpenFrame || capture.Intents.Count != 0
                || capture.Changes.Count != 0 || capture.State != AuditIntentState.None)
                throw new InvalidOperationException("Global audit entry requires this owner's serialized absence.");
        }

        internal void RequireLive(IUnitOfWork unit, IAuditStateCapture capture)
        {
            if (_disposed || !ReferenceEquals(unit, _unit) || !ReferenceEquals(capture, _capture) || !_frame.IsOwner
                || !unit.HasActiveTransaction || unit.IsRollbackOnly || unit.Mode != TransactionMode.ReadWrite
                || !ReferenceEquals(unit.Connection, _connection) || !ReferenceEquals(unit.Transaction, _transaction)
                || _announcement.Generation <= 0 || _unit.GetGlobalIdentityAnnouncement(_transaction) != _announcement)
                throw new InvalidOperationException("The prepared global owner no longer matches its live transaction and capture.");
        }

        internal IUnitOfWorkScope Consume(IUnitOfWork unit, IAuditStateCapture capture)
        {
            RequireLive(unit, capture);
            if (!_absenceAdmitted || _consumed)
                throw new InvalidOperationException("An admitted global owner can be consumed only once.");
            _consumed = true;
            return _frame;
        }

        internal void RecordOutcome(AuditIntentState state) => RecordedOutcome = state;

        internal async Task FailBeforeConsumptionAsync()
        {
            if (_consumed || _disposed) return;
            _unit.MarkRollbackOnly();
            try { await _frame.FailAsync(CancellationToken.None); }
            finally
            {
                RecordedOutcome = AuditIntentState.RolledBack;
                _capture.MarkRolledBack();
                _disposed = true;
                await _frame.DisposeAsync();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            if (!_consumed) await _frame.DisposeAsync();
        }
    }
}
