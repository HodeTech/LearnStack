using System.Data;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace LearnStack.Infrastructure.Audit;

public sealed partial class PostgresAuditStore
{
    public async Task WriteGlobalIdentityPendingAsync(IUnitOfWork unitOfWork,
        GlobalIdentityCreationReference creation, CancellationToken cancellationToken = default)
    {
        JobApplicationSourceOwner.RefuseExternalEntry();
        if (unitOfWork is IUnitOfWorkWriteGuard writeGuard) writeGuard.RequireWriteAllowed();
        ArgumentNullException.ThrowIfNull(creation);
        creation.RequireLive(unitOfWork);
        if (!_capture.HasOpenFrame || _capture.Intents.Count != 1
            || !ReferenceEquals(_capture.Intents[0].GlobalIdentity, creation)
            || _capture.State != AuditIntentState.Pending)
            throw new ArgumentException("Global pending audit requires its single captured creation intent.", nameof(creation));
        RequireGlobalCatalog(creation);
        var intent = _capture.Intents[0];
        var draft = AuditDraftComposer.InTransaction(intent, _capture.ChangesOf(intent));
        if (draft.Outcome != AuditOutcome.Success)
            throw new ArgumentException("Only successful creation can use the global pending seam.", nameof(creation));
        try
        {
            await using var command = unitOfWork.Connection.CreateCommand();
            command.CommandTimeout = 10;
            command.Transaction = unitOfWork.Transaction;
            command.CommandText = InsertSql;
            Bind(command.Parameters, draft);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            _capture.MarkWrittenInTransaction();
        }
        catch (System.Data.Common.DbException failure)
        {
            throw new AuditWriteFailedException("The global creation audit could not be written on its owner.", failure);
        }
    }

    public async Task ReconcileGlobalIdentityAsync(AuditIntent intent, IReadOnlyList<CapturedEntityChange> changes,
        AuditIntentState state, DateTimeOffset timestamp, CancellationToken cancellationToken = default)
    {
        JobApplicationSourceOwner.RefuseExternalEntry();
        ArgumentNullException.ThrowIfNull(intent);
        var creation = intent.GlobalIdentity
            ?? throw new ArgumentException("Global reconciliation requires captured creation attribution.", nameof(intent));
        creation.RequireIntent(intent);
        if (_globalEnrollment.IsActive || _capture.HasOpenFrame || _capture.Intents.Count != 1 || !ReferenceEquals(_capture.Intents[0], intent)
            || state != _capture.State || state is not (AuditIntentState.RolledBack or AuditIntentState.Indeterminate)
            || intent.Result is null || timestamp == creation.Timestamp
            || timestamp != GlobalIdentityCreationReference.NormalizeTimestamp(timestamp)
            || !changes.SequenceEqual(_capture.ChangesOf(intent)))
            throw new ArgumentException("Global reconciliation requires this capture's closed non-success attempt.", nameof(intent));
        RequireGlobalCatalog(creation);
        var draft = AuditDraftComposer.Reconciled(intent, changes, state, timestamp);
        if (draft.Outcome is not (AuditOutcome.Failed or AuditOutcome.Indeterminate))
            throw new ArgumentException("Global reconciliation cannot assert creation success.", nameof(intent));

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            try
            {
                await WriteGlobalIdentityTransactionAsync(creation, draft, budget.Token).ConfigureAwait(false);
            }
            catch (PostgresException duplicate) when (duplicate.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // The failed INSERT transaction is disposed before evidence uses a clean transaction.
                if (!await HasGlobalIdentityEvidenceAsync(creation, draft, budget.Token).ConfigureAwait(false))
                    throw new AuditWriteFailedException("The duplicate global audit key has no exact durable evidence.", duplicate);
                LogGlobalDuplicate(_logger, null);
                _durableDuplicates.Add(1, new KeyValuePair<string, object?>("operation", GlobalIdentityCreationReference.Operation));
            }
            _health.ReportStandaloneWriteSucceeded();
        }
        catch (Exception failure)
        {
            LogGlobalWriteFailed(_logger, null);
            _standaloneWriteFailures.Add(1, new KeyValuePair<string, object?>("operation", GlobalIdentityCreationReference.Operation));
            _health.ReportStandaloneWriteFailed(GlobalIdentityCreationReference.Operation);
            if (failure is System.Data.Common.DbException)
                throw new AuditWriteFailedException("The global creation attempt could not be reconciled.", failure);
            throw;
        }
    }

    private void RequireGlobalCatalog(GlobalIdentityCreationReference creation)
    {
        if (!_catalog.TryGetOffPath(GlobalIdentityCreationReference.Operation, out var entry)
            || entry.ModuleName != "identity" || entry.OperationClass != OperationClass.Must
            || entry.OperationType != OperationType.SecurityEvent || entry.EntityType != creation.Descriptor.EntityType)
            throw new ArgumentException("Global creation requires its exact central MUST catalogue entry.");
    }

    private async Task WriteGlobalIdentityTransactionAsync(GlobalIdentityCreationReference creation,
        AuditEntryDraft draft, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.Value.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        await AnnounceGlobalIdentityAsync(connection, transaction, creation, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = InsertSql;
        Bind(command.Parameters, draft);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        command.CommandText = "SET CONSTRAINTS ALL IMMEDIATE";
        command.Parameters.Clear();
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> HasGlobalIdentityEvidenceAsync(GlobalIdentityCreationReference creation,
        AuditEntryDraft draft, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.Value.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        await using (var setup = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
            await setup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await AnnounceGlobalIdentityAsync(connection, transaction, creation, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT public.fn_identity_audit_evidence(@id, @timestamp, @authority_id, @subject_key, @target_id, @source, @outcome)
            """;
        Add(command.Parameters, "id", creation.AuditId.Value);
        Add(command.Parameters, "timestamp", draft.Timestamp);
        Add(command.Parameters, "authority_id", creation.AuthorityId.Value);
        Add(command.Parameters, "subject_key", creation.SubjectKey.ToByteArray());
        Add(command.Parameters, "target_id", creation.TargetUserId.Value);
        Add(command.Parameters, "source", creation.Source);
        Add(command.Parameters, "outcome", draft.Outcome.ToString().ToLowerInvariant());
        var matches = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return matches;
    }

    private static async Task AnnounceGlobalIdentityAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        GlobalIdentityCreationReference creation, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT set_config('app.tenant_id', '', true), set_config('app.organization_id', '', true),
                set_config('app.scope', '', true), set_config('app.resolving_host', '', true),
                set_config('app.identity_authority_id', @authority, true),
                set_config('app.identity_subject_key', @subject_key, true)
            """;
        Add(command.Parameters, "authority", creation.AuthorityId.Value.ToString("D"));
        Add(command.Parameters, "subject_key", creation.SubjectKey.Hex);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static readonly Action<ILogger, Exception?> LogGlobalDuplicate = LoggerMessage.Define(
        LogLevel.Warning, new EventId(4, nameof(LogGlobalDuplicate)),
        "An exact non-success global audit row is already durable. This does not establish the business transaction outcome.");

    private static readonly Action<ILogger, Exception?> LogGlobalWriteFailed = LoggerMessage.Define(
        LogLevel.Critical, new EventId(5, nameof(LogGlobalWriteFailed)),
        "The global identity attempt audit could not be reconciled. Audit health is unhealthy.");
}
