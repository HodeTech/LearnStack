using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace LearnStack.Infrastructure.Audit;

public sealed partial class PostgresAuditStore
{
    public async Task WriteAuthorizationPendingAsync(IUnitOfWork unitOfWork,
        AuthorizationEvidenceOccurrence occurrence, CancellationToken cancellationToken = default)
    {
        JobApplicationSourceOwner.RefuseExternalEntry();
        ArgumentNullException.ThrowIfNull(unitOfWork);
        if (unitOfWork is IUnitOfWorkWriteGuard writeGuard) writeGuard.RequireWriteAllowed();
        var draft = RequireAuthorizationCatalog(occurrence);
        if (_globalEnrollment.IsActive || unitOfWork is IGlobalIdentityUnitOfWork { IsGlobalIdentityTransaction: true }
            || unitOfWork.Mode != TransactionMode.ReadWrite || !unitOfWork.HasActiveTransaction || unitOfWork.IsRollbackOnly
            || unitOfWork.Transaction is not NpgsqlTransaction { IsolationLevel: IsolationLevel.ReadCommitted } transaction
            || unitOfWork.Connection is not NpgsqlConnection connection || !ReferenceEquals(transaction.Connection, connection)
            || unitOfWork.GetTenantContextAnnouncement(transaction) is not { Generation: > 0 } announced
            || announced.TenantId != draft.TenantId || announced.OrganizationId != draft.OrganizationId)
            throw new ArgumentException("Authorization evidence requires its matching live ReadWrite owner.", nameof(unitOfWork));
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = InsertSql;
            Bind(command.Parameters, draft);
            if (unitOfWork is IUnitOfWorkWriteGuard finalWriteGuard) finalWriteGuard.RequireWriteAllowed();
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbException)
        {
            throw new AuditWriteFailedException("Authorization evidence could not be written on its business owner.");
        }
    }

    public async Task WriteAuthorizationStandaloneAsync(AuthorizationEvidenceOccurrence occurrence,
        CancellationToken cancellationToken = default)
    {
        JobApplicationSourceOwner.RefuseExternalEntry();
        var draft = RequireAuthorizationCatalog(occurrence);
        try
        {
            if (_authorizationEvidence is null)
                throw new AuditWriteFailedException("The authorization evidence source is unavailable.");
            var session = await _authorizationEvidence.OpenAsync(occurrence, cancellationToken).ConfigureAwait(false)
                ?? throw new AuditWriteFailedException("The authorization evidence source is at capacity.");
            await using (session.ConfigureAwait(false))
            {
                try
                {
                    await InsertAuthorizationEvidenceAsync(session, draft).ConfigureAwait(false);
                    await session.CompleteAsync().ConfigureAwait(false);
                }
                catch (PostgresException duplicate) when (duplicate.SqlState == PostgresErrorCodes.UniqueViolation
                    && duplicate.SchemaName == "public" && duplicate.TableName == "audit_log"
                    && duplicate.ConstraintName == "audit_log_pkey")
                {
                    await session.BeginVerificationAsync().ConfigureAwait(false);
                    if (!await HasAuthorizationEvidenceAsync(session, draft).ConfigureAwait(false))
                        throw new AuditWriteFailedException("The duplicate authorization event has no exact durable evidence.");
                    await session.CompleteAsync().ConfigureAwait(false);
                    LogAuthorizationDuplicate(_logger, null);
                    _durableDuplicates.Add(1, new KeyValuePair<string, object?>("operation", draft.Operation));
                }
            }
            _health.ReportStandaloneWriteSucceeded();
        }
        catch (Exception failure) when (failure is not (OutOfMemoryException or StackOverflowException))
        {
            LogAuthorizationWriteFailed(_logger, null);
            _standaloneWriteFailures.Add(1, new KeyValuePair<string, object?>("operation", draft.Operation));
            _health.ReportStandaloneWriteFailed(draft.Operation);
            if (failure is OperationCanceledException) throw;
            throw new AuditWriteFailedException("Authorization evidence could not be made durable.");
        }
    }

    private AuditEntryDraft RequireAuthorizationCatalog(AuthorizationEvidenceOccurrence occurrence)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        var draft = occurrence.Draft;
        var operation = occurrence.Kind switch
        {
            AuthorizationEvidenceKind.Denial => "identity.authorization.deny",
            AuthorizationEvidenceKind.ClaimAssertion => "tenancy.assertion.reject_claim_context",
            _ => throw new ArgumentException("Authorization evidence kind is invalid.", nameof(occurrence)),
        };
        if (!_catalog.TryGetOffPath(operation, out var declared)
            || declared.Operation != operation || declared.ModuleName != draft.ModuleName
            || declared.OperationClass != OperationClass.Must || declared.OperationType != OperationType.SecurityEvent
            || declared.EntityType is not null || draft.EntityType is not null || draft.EntityId is not null
            || draft.Operation != declared.Operation || draft.OperationClass != declared.OperationClass
            || draft.OperationType != declared.OperationType || draft.Outcome != AuditOutcome.Denied)
            throw new ArgumentException("Authorization evidence requires its exact central MUST catalogue entry.", nameof(occurrence));
        return draft;
    }

    private static async Task InsertAuthorizationEvidenceAsync(IAuthorizationEvidenceSession session, AuditEntryDraft draft)
    {
        var command = session.Unit.Connection.CreateCommand();
        try
        {
            command.Transaction = session.Unit.Transaction;
            command.CommandText = InsertSql;
            Bind(command.Parameters, draft);
            await command.ExecuteNonQueryAsync(session.DeadlineToken).ConfigureAwait(false);
        }
        finally { await DisposeAuthorizationStatementAsync(session, command).ConfigureAwait(false); }
    }

    private static async Task<bool> HasAuthorizationEvidenceAsync(IAuthorizationEvidenceSession session, AuditEntryDraft draft)
    {
        var command = session.Unit.Connection.CreateCommand();
        try
        {
            command.Transaction = session.Unit.Transaction;
            // All persisted fields use the one INSERT's parameters. In particular the
            // nullable JSON values are typed JSONB by Bind, never text or string equality.
            // The nonnullable composite key uses equality so verification can seek its index.
            command.CommandText = """
                SELECT EXISTS (
                    SELECT 1 FROM public.audit_log
                    WHERE id = @id
                      AND tenant_id IS NOT DISTINCT FROM @tenant_id
                      AND organization_id IS NOT DISTINCT FROM @organization_id
                      AND actor_user_id IS NOT DISTINCT FROM @actor_user_id
                      AND actor_email IS NOT DISTINCT FROM @actor_email
                      AND module IS NOT DISTINCT FROM @module
                      AND operation IS NOT DISTINCT FROM @operation
                      AND operation_type IS NOT DISTINCT FROM @operation_type
                      AND operation_class IS NOT DISTINCT FROM @operation_class
                      AND entity_type IS NOT DISTINCT FROM @entity_type
                      AND entity_id IS NOT DISTINCT FROM @entity_id
                      AND outcome IS NOT DISTINCT FROM @outcome
                      AND error_key IS NOT DISTINCT FROM @error_key
                      AND reason IS NOT DISTINCT FROM @reason
                      AND before_state IS NOT DISTINCT FROM @before_state
                      AND after_state IS NOT DISTINCT FROM @after_state
                      AND changes IS NOT DISTINCT FROM @changes
                      AND correlation_id IS NOT DISTINCT FROM @correlation_id
                      AND ip_address IS NOT DISTINCT FROM @ip_address
                      AND user_agent IS NOT DISTINCT FROM @user_agent
                      AND timestamp = @timestamp
                      AND metadata IS NOT DISTINCT FROM @metadata)
                """;
            Bind(command.Parameters, draft);
            return await command.ExecuteScalarAsync(session.DeadlineToken).ConfigureAwait(false) is true;
        }
        finally { await DisposeAuthorizationStatementAsync(session, command).ConfigureAwait(false); }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "An actual statement cleanup fault permanently poisons the retained session.")]
    private static async Task DisposeAuthorizationStatementAsync(IAuthorizationEvidenceSession session, DbCommand command)
    {
        try { await command.DisposeAsync().ConfigureAwait(false); }
        catch { session.MarkCleanupFailed(); throw; }
    }

    private static readonly Action<ILogger, Exception?> LogAuthorizationDuplicate = LoggerMessage.Define(
        LogLevel.Warning, new EventId(6, nameof(LogAuthorizationDuplicate)),
        "An exact authorization event is already durable. This establishes no business transaction outcome.");

    private static readonly Action<ILogger, Exception?> LogAuthorizationWriteFailed = LoggerMessage.Define(
        LogLevel.Critical, new EventId(7, nameof(LogAuthorizationWriteFailed)),
        "Authorization evidence could not be made durable. Audit health is unhealthy.");
}
