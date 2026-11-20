using System.Data.Common;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Persistence;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// Wraps the real audit store and hands the operator-recovery row to a test before it is written, so a test can
/// substitute a field of the draft or the transaction and watch the real store refuse it.
/// </summary>
internal sealed class OperatorRecoveryAuditDecorator(
    IAuditStore inner,
    Func<AuditEntryDraft, OperatorRecoveryTransition, DbConnection, DbTransaction, CancellationToken, Task> recovery) : IAuditStore
{
    public Task WriteGlobalIdentityPendingAsync(IUnitOfWork unitOfWork, GlobalIdentityCreationReference creation,
        CancellationToken cancellationToken = default) =>
        inner.WriteGlobalIdentityPendingAsync(unitOfWork, creation, cancellationToken);

    public Task ReconcileGlobalIdentityAsync(AuditIntent intent, IReadOnlyList<CapturedEntityChange> changes,
        AuditIntentState state, DateTimeOffset timestamp, CancellationToken cancellationToken = default) =>
        inner.ReconcileGlobalIdentityAsync(intent, changes, state, timestamp, cancellationToken);

    public Task WritePendingAsync(IUnitOfWork unitOfWork, CancellationToken cancellationToken = default) =>
        inner.WritePendingAsync(unitOfWork, cancellationToken);

    public Task WriteStandaloneAsync(AuditEntryDraft entry, CancellationToken cancellationToken = default) =>
        inner.WriteStandaloneAsync(entry, cancellationToken);

    public Task WriteBestEffortAsync(AuditEntryDraft entry, CancellationToken cancellationToken = default) =>
        inner.WriteBestEffortAsync(entry, cancellationToken);

    public Task WritePlatformScopeAsync(AuditEntryDraft entry, DbConnection connection, DbTransaction transaction,
        CancellationToken cancellationToken = default) =>
        inner.WritePlatformScopeAsync(entry, connection, transaction, cancellationToken);

    public Task WriteEventTerminalAsync(AuditEntryDraft entry, EventTerminalTransition transition, DbConnection connection,
        DbTransaction transaction, CancellationToken cancellationToken = default) =>
        inner.WriteEventTerminalAsync(entry, transition, connection, transaction, cancellationToken);

    public Task WriteJobTerminalAsync(AuditEntryDraft entry, JobTerminalTransition transition, DbConnection connection,
        DbTransaction transaction, CancellationToken cancellationToken = default) =>
        inner.WriteJobTerminalAsync(entry, transition, connection, transaction, cancellationToken);

    public Task WriteOperatorRecoveryAsync(AuditEntryDraft entry, OperatorRecoveryTransition transition,
        DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken = default) =>
        recovery(entry, transition, connection, transaction, cancellationToken);
}
