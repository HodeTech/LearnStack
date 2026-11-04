using System.Data;
using System.Diagnostics;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Email;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Infrastructure.Email;

/// <summary>Four retained provider owners, independent of all ordinary and privileged sources.</summary>
public sealed class InvitationSmtpProviderDataSource : IAsyncDisposable
{
    private readonly NpgsqlDataSource _source;
    private readonly object _gate = new();
    private readonly HashSet<Owner> _owners = [];
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _closed;
    private bool _unhealthy;
    public InvitationSmtpProviderDataSource(string connectionString)
    {
        ApplicationDataSource.Validate(connectionString);
        var settings = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = true,
            MinPoolSize = 0,
            MaxPoolSize = 4,
            Multiplexing = false,
            Enlist = false,
            Timeout = 5,
            CommandTimeout = 5,
            CancellationTimeout = 1000,
            NoResetOnClose = false,
            IncludeErrorDetail = false,
            LogParameters = false,
            Options = "-c statement_timeout=5000 -c lock_timeout=4500 -c idle_in_transaction_session_timeout=5000",
        };
        _source = new NpgsqlDataSourceBuilder(settings.ConnectionString).Build();
    }
    public bool IsHealthy { get { lock (_gate) return !_closed && !_unhealthy; } }

    internal async Task<Owner?> OpenAsync(InvitationSmtpExecutionReceipt receipt, CancellationToken token)
    {
        Owner owner;
        lock (_gate)
        {
            if (_closed || _unhealthy || !receipt.IsActive || _owners.Count == 4) return null;
            owner = new(this, receipt);
            _owners.Add(owner);
        }
        try { await owner.OpenAsync(token).ConfigureAwait(false); return owner; }
        catch
        {
            await owner.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("Invitation SMTP provider source unavailable.");
        }
    }
    private void Released(Owner owner)
    {
        lock (_gate)
        {
            _owners.Remove(owner);
            if (_closed && _owners.Count == 0) _drained.TrySetResult();
        }
    }
    private void FailedCleanup() { lock (_gate) _unhealthy = true; }
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _closed = true;
            if (_owners.Count == 0) _drained.TrySetResult();
        }
        await _drained.Task.ConfigureAwait(false);
        await _source.DisposeAsync().ConfigureAwait(false);
    }

    internal sealed class Owner(InvitationSmtpProviderDataSource source, InvitationSmtpExecutionReceipt receipt) : IAsyncDisposable
    {
        private NpgsqlConnection? _connection;
        private Guid? _claim;
        private bool _disposed;
        private bool _failed;
        private int _busy;
        private long _openedAt;
        private bool _commitIssued;
        private IEmailProvider? _retainedSmtp;
        internal void RetainUnconfirmedSmtp(IEmailProvider provider)
        {
            _retainedSmtp = provider;
            source.FailedCleanup();
        }
        internal async Task OpenAsync(CancellationToken token)
        {
            _openedAt = Stopwatch.GetTimestamp();
            using var deadline = Budget(token);
            await OpenPhysicalAsync(deadline.Token).ConfigureAwait(false);
        }
        private async Task OpenPhysicalAsync(CancellationToken token)
        {
            _connection = await source._source.OpenConnectionAsync(token).ConfigureAwait(false);
            await ApplicationDataSource.RefuseBypassRoleAsync(_connection, token).ConfigureAwait(false);
            await using var qualification = new NpgsqlCommand(InvitationEmailDatabasePrivacy.QualificationSql + """
                  AND NULLIF(current_setting('app.tenant_id',true),'') IS NULL
                  AND NULLIF(current_setting('app.organization_id',true),'') IS NULL
                """, _connection) { CommandTimeout = 5 };
            if (await qualification.ExecuteScalarAsync(token).ConfigureAwait(false) is not true)
                throw new InvalidOperationException("Invitation SMTP database privacy qualification refused.");
        }
        internal async Task<ProviderRecord> ClaimOrReadAsync(Guid claim, Guid receiptId, Guid evidenceId, CancellationToken token)
        {
            if (!receipt.IsActive || _claim is not null) throw InvitationSmtpExecutionReceipt.Refused();
            var result = await CallAsync(claim, receiptId, evidenceId, null, token).ConfigureAwait(false);
            if (result.State == "sending" && result.ClaimId == claim && result.Envelope is not null) _claim = claim;
            return result;
        }
        internal Task<ProviderRecord> CompleteAsync(string state, Guid receiptId, Guid evidenceId)
        {
            if (_claim is not { } claim) throw InvitationSmtpExecutionReceipt.Refused();
            // The retained original claim can record actual late evidence after receipt invalidation.
            return CallAsync(claim, receiptId, evidenceId, state, CancellationToken.None);
        }
        private async Task<ProviderRecord> CallAsync(Guid claim, Guid receiptId, Guid evidenceId, string? state, CancellationToken token)
        {
            if (_disposed || _failed || _connection is null || Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
                throw InvitationSmtpExecutionReceipt.Refused();
            using var deadline = Budget(token, state is null ? TimeSpan.FromSeconds(5) - Stopwatch.GetElapsedTime(_openedAt) : null);
            try
            {
                try { return await ExecuteSqlAsync(claim, receiptId, evidenceId, state, false, deadline.Token).ConfigureAwait(false); }
                catch (Exception failure) when (_commitIssued && failure is NpgsqlException or TimeoutException or OperationCanceledException)
                {
                    // Keep this owner/slot. Actual prior physical cleanup precedes its sole locked verification.
                    await _connection.DisposeAsync().ConfigureAwait(false);
                    _connection = null;
                    deadline.Token.ThrowIfCancellationRequested();
                    await OpenPhysicalAsync(deadline.Token).ConfigureAwait(false);
                    return await ExecuteSqlAsync(claim, receiptId, evidenceId, state, true, deadline.Token).ConfigureAwait(false);
                }
            }
            catch (Exception failure) when (failure is NpgsqlException or TimeoutException or OperationCanceledException or InvalidOperationException)
            {
                _failed = true;
                throw new InvalidOperationException("Invitation SMTP provider observation unresolved.");
            }
            finally { Interlocked.Exchange(ref _busy, 0); }
        }
        private async Task<ProviderRecord> ExecuteSqlAsync(Guid claim, Guid receiptId, Guid evidenceId, string? state,
            bool verify, CancellationToken token)
        {
            _commitIssued = false;
            await using var transaction = await _connection!.BeginTransactionAsync(IsolationLevel.ReadCommitted, token).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(state is null ? ClaimSql : CompleteSql, _connection, transaction) { CommandTimeout = 5 };
            AddBinding(command);
            command.Parameters.AddWithValue("claim", claim);
            command.Parameters.AddWithValue("receipt", receiptId);
            command.Parameters.AddWithValue("evidence", evidenceId);
            command.Parameters.AddWithValue("verify", verify);
            if (state is null)
            {
                command.Parameters.AddWithValue("recovery", NpgsqlDbType.Uuid, (object?)receipt.RecoveryToken ?? DBNull.Value);
                command.Parameters.AddWithValue("wake", receipt.WakeGeneration);
                command.Parameters.AddWithValue("until", receipt.OwnershipUntil);
            }
            else command.Parameters.AddWithValue("state", state);
            ProviderRecord result;
            await using (var row = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            {
                if (!await row.ReadAsync(token).ConfigureAwait(false)) throw InvitationSmtpExecutionReceipt.Refused();
                result = new(row.GetString(0), row.GetGuid(1), row.IsDBNull(2) ? null : row.GetGuid(2), row.IsDBNull(3) ? null : row.GetGuid(3),
                    row.FieldCount == 8 && !row.IsDBNull(4) ? new(row.GetString(4), row.GetFieldValue<byte[]>(5), row.GetFieldValue<byte[]>(6), row.GetFieldValue<byte[]>(7)) : null);
                if (await row.ReadAsync(token).ConfigureAwait(false)) throw InvitationSmtpExecutionReceipt.Refused();
            }
            _commitIssued = true;
            await transaction.CommitAsync(token).ConfigureAwait(false);
            _commitIssued = false;
            return result;
        }
        private void AddBinding(NpgsqlCommand command)
        {
            command.Parameters.AddWithValue("tenant", receipt.TenantId);
            command.Parameters.AddWithValue("org", NpgsqlDbType.Uuid, (object?)receipt.OrganizationId ?? DBNull.Value);
            command.Parameters.AddWithValue("envelope", receipt.SpoolId);
            command.Parameters.AddWithValue("invitation", receipt.InvitationId);
            command.Parameters.AddWithValue("generation", receipt.GenerationId);
            command.Parameters.AddWithValue("job", receipt.JobId);
            command.Parameters.AddWithValue("attempt", receipt.AttemptId);
            command.Parameters.AddWithValue("operation", receipt.OperationId);
            command.Parameters.AddWithValue("fingerprint", receipt.Fingerprint);
            command.Parameters.AddWithValue("admission", receipt.AdmissionToken);
            command.Parameters.AddWithValue("deadline", receipt.Deadline);
        }
        public async ValueTask DisposeAsync()
        {
            if (_disposed || _retainedSmtp is not null) return;
            try
            {
                if (Volatile.Read(ref _busy) != 0) throw InvitationSmtpExecutionReceipt.Refused();
                if (_connection is not null) await _connection.DisposeAsync().ConfigureAwait(false);
                _disposed = true;
                source.Released(this);
            }
            catch { source.FailedCleanup(); throw new InvalidOperationException("Invitation SMTP provider cleanup unresolved."); }
        }
        private static CancellationTokenSource Budget(CancellationToken token, TimeSpan? remaining = null)
        {
            var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            var duration = remaining ?? TimeSpan.FromSeconds(5);
            if (duration <= TimeSpan.Zero) deadline.Cancel();
            else deadline.CancelAfter(duration);
            return deadline;
        }
        private const string ClaimSql = "SELECT * FROM public.fn_invitation_smtp_claim_or_read(@tenant,@org,@envelope,@invitation,@generation,@job,@attempt,@operation,@fingerprint,@admission,@deadline,@recovery,@wake,@until,@claim,@receipt,@evidence,@verify)";
        private const string CompleteSql = "SELECT * FROM public.fn_invitation_smtp_complete_attempt(@tenant,@org,@envelope,@invitation,@generation,@job,@attempt,@operation,@fingerprint,@admission,@deadline,@claim,@state,@receipt,@evidence,@verify)";
    }
}
internal sealed record ProviderRecord(string State, Guid ClaimId, Guid? ReceiptId, Guid? EvidenceId, InvitationEmailCiphertext? Envelope);
