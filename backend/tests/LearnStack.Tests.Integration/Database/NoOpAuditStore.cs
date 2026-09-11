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

    public Task WritePlatformScopeAsync(
        AuditEntryDraft entry,
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
