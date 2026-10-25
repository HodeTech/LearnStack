using System.Data.Common;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Persistence;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// A store that records nothing, for the cases that are about the transaction boundary
/// rather than about the row.
/// </summary>
/// <remarks>
/// Deliberately a double rather than the real <c>PostgresAuditStore</c>. These cases open
/// and resolve transactions to prove things about the ambient unit of work; giving them a
/// real store would add rows the shared schema fixture counts, and cleanup that has
/// nothing to do with what they assert. The real store is exercised against the real table
/// by <c>AuditStoreTests</c>, and the real pipeline by <c>AuditPipelineTests</c>.
/// </remarks>
internal sealed class NoOpAuditStore : IAuditStore
{
    public Task WriteGlobalIdentityPendingAsync(LearnStack.SharedKernel.Persistence.IUnitOfWork unit,
        GlobalIdentityCreationReference creation, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Global identity has its own cases.");

    public Task ReconcileGlobalIdentityAsync(AuditIntent intent, IReadOnlyList<CapturedEntityChange> changes,
        AuditIntentState state, DateTimeOffset timestamp, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Global identity has its own cases.");

    public int PendingWrites { get; private set; }

    public Task WritePendingAsync(IUnitOfWork unitOfWork, CancellationToken cancellationToken = default)
    {
        PendingWrites++;
        return Task.CompletedTask;
    }

    public Task WriteStandaloneAsync(AuditEntryDraft entry, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task WriteBestEffortAsync(AuditEntryDraft entry, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task WriteJobTerminalAsync(AuditEntryDraft entry, JobTerminalTransition transition,
        DbConnection connection, DbTransaction transaction,
        CancellationToken cancellationToken = default) => throw new NotSupportedException("The job-terminal path has its own cases.");

    public Task WriteEventTerminalAsync(
        AuditEntryDraft entry,
        EventTerminalTransition transition,
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The terminal-event path has its own cases.");

    public Task WritePlatformScopeAsync(
        AuditEntryDraft entry,
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
