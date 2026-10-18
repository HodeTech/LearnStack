using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Npgsql;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// Forwards a real PostgreSQL session, withholding exactly one completed COMMIT's
/// acknowledgement. The optional held socket proves cancellation independently of disconnect.
/// </summary>
internal sealed class CommitResponseDroppingProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stopping;
    private readonly List<Task> _sessions = [];
    private readonly string _targetHost;
    private readonly int _targetPort;
    private readonly bool _rejectReconciliationConnections;
    private readonly bool _holdCommitResponse;
    private readonly bool _dropCommitRequest;
    private readonly TaskCompletionSource _commitRequestDropped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _issuedCommits, _droppedCommitRequests;
    private readonly TaskCompletionSource _commitResponseHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseHeldResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _capacityRollbackHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseCapacityRollback = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _holdCapacityRollback;
    private int _completedCapacityRollbacks;
    private readonly TaskCompletionSource _checkpointRollbackHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseCheckpointRollback = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _holdCheckpointRollback;
    private bool _loseCheckpointRollbackResponse;
    private readonly TaskCompletionSource _externalRecoveryRollbackHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseExternalRecoveryRollback = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _holdExternalRecoveryRollback;
    private bool _loseExternalRecoveryRollbackResponse;
    private int _holdCapacityRecoveryCommit;
    private readonly TaskCompletionSource _capacityRecoveryCommitHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseCapacityRecoveryCommit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _holdRenewalCommit;
    private bool _loseRenewalCommitResponse;
    private readonly TaskCompletionSource _renewalCommitHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseRenewalCommit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _quarantineRollbackHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseQuarantineRollback = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _holdQuarantineRollback, _quarantineReads;
    private bool _loseQuarantineRollbackResponse;
    private readonly Task _accepting;
    private int _droppedCommitResponses;
    private int _acceptedConnections;
    private readonly int _commitsToPass;
    private int _completedCommits;
    private readonly Guid? _observedCheckpointJob;
    private readonly TaskCompletionSource _emptyCheckpointLockRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _observeCheckpointLock, _checkpointLockReads;

    public CommitResponseDroppingProxy(string connectionString, bool rejectReconciliationConnections = false,
        int commitsToPass = 0, bool holdCommitResponse = false, TimeSpan? lifetime = null, bool dropCommitRequest = false,
        Guid? observedCheckpointJob = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(commitsToPass);
        _stopping = new(lifetime ?? TimeSpan.FromSeconds(30));
        _commitsToPass = commitsToPass;
        _rejectReconciliationConnections = rejectReconciliationConnections;
        _holdCommitResponse = holdCommitResponse;
        _dropCommitRequest = dropCommitRequest;
        _observedCheckpointJob = observedCheckpointJob;
        var target = new NpgsqlConnectionStringBuilder(connectionString);
        _targetHost = target.Host ?? throw new ArgumentException("The test proxy requires a PostgreSQL host.", nameof(connectionString));
        _targetPort = target.Port;
        _listener.Start();
        ConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Host = IPAddress.Loopback.ToString(),
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port,
            // Test-only plaintext forwarding makes protocol frames observable;
            // production credentials and data-source validation are unchanged.
            SslMode = SslMode.Disable,
            GssEncryptionMode = GssEncryptionMode.Disable,
            Pooling = false,
            Timeout = 5,
        }.ConnectionString;
        _accepting = AcceptAsync();
    }

    public string ConnectionString { get; }
    public int DroppedCommitResponses => Volatile.Read(ref _droppedCommitResponses);
    public int CompletedCommits => Volatile.Read(ref _completedCommits);
    public int AcceptedConnections => Volatile.Read(ref _acceptedConnections);
    public Task CommitResponseHeld => _commitResponseHeld.Task;
    public Task CommitRequestDropped => _commitRequestDropped.Task;
    public int DroppedCommitRequests => Volatile.Read(ref _droppedCommitRequests);
    public void ReleaseHeldResponse() => _releaseHeldResponse.TrySetResult();
    public Task CapacityRollbackHeld => _capacityRollbackHeld.Task;
    public int CompletedCapacityRollbacks => Volatile.Read(ref _completedCapacityRollbacks);
    public void HoldNextCapacityProbeRollbackResponse() => Interlocked.CompareExchange(ref _holdCapacityRollback, 1, 0);
    public void ReleaseCapacityRollbackResponse() => _releaseCapacityRollback.TrySetResult();
    public Task CheckpointHistoryRollbackHeld => _checkpointRollbackHeld.Task;
    public void HoldNextCheckpointHistoryRollbackResponse(bool loseResponse)
    {
        _loseCheckpointRollbackResponse = loseResponse;
        Interlocked.CompareExchange(ref _holdCheckpointRollback, 1, 0);
    }
    public void ReleaseCheckpointHistoryRollbackResponse() => _releaseCheckpointRollback.TrySetResult();
    public Task ExternalRecoveryRollbackHeld => _externalRecoveryRollbackHeld.Task;
    public void HoldNextExternalRecoveryRollbackResponse(bool loseResponse)
    {
        _loseExternalRecoveryRollbackResponse = loseResponse;
        Interlocked.CompareExchange(ref _holdExternalRecoveryRollback, 1, 0);
    }
    public void ReleaseExternalRecoveryRollbackResponse() => _releaseExternalRecoveryRollback.TrySetResult();
    public Task CapacityRecoveryCommitHeld => _capacityRecoveryCommitHeld.Task;
    public void HoldNextCapacityRecoveryCommitResponse() => Interlocked.CompareExchange(ref _holdCapacityRecoveryCommit, 1, 0);
    public void ReleaseCapacityRecoveryCommitResponse() => _releaseCapacityRecoveryCommit.TrySetResult();
    public Task RenewalCommitHeld => _renewalCommitHeld.Task;
    public void HoldNextRenewalCommitResponse(bool loseResponse)
    {
        _loseRenewalCommitResponse = loseResponse;
        Interlocked.CompareExchange(ref _holdRenewalCommit, 1, 0);
    }
    public void ReleaseRenewalCommitResponse() => _releaseRenewalCommit.TrySetResult();
    public Task EmptyCheckpointLockRead => _emptyCheckpointLockRead.Task;
    public int CheckpointLockReads => Volatile.Read(ref _checkpointLockReads);
    public void ObserveNextEmptyCheckpointLockRead() => Interlocked.CompareExchange(ref _observeCheckpointLock, 1, 0);

    public int QuarantineReads => Volatile.Read(ref _quarantineReads);
    public Task QuarantineRollbackHeld => _quarantineRollbackHeld.Task;
    public void HoldNextQuarantineRollbackResponse(bool loseResponse)
    {
        _loseQuarantineRollbackResponse = loseResponse;
        Interlocked.CompareExchange(ref _holdQuarantineRollback, 1, 0);
    }
    public void ReleaseQuarantineRollbackResponse() => _releaseQuarantineRollback.TrySetResult();

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _listener.Stop();
        try
        {
            await _accepting.WaitAsync(TimeSpan.FromSeconds(5));
            // AcceptAsync alone mutates the list; it has completed before this read.
            await Task.WhenAll(_sessions).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            _stopping.Dispose();
        }
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stopping.Token);
                Interlocked.Increment(ref _acceptedConnections);
                if (_rejectReconciliationConnections && DroppedCommitResponses != 0)
                {
                    // Refuse the fresh durability probe only after the real
                    // terminal transaction committed and its response was dropped.
                    client.Dispose();
                    continue;
                }
                _sessions.Add(ForwardSessionAsync(client));
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // Listener shutdown is an owned cancellation, not a swallowed fault.
        }
        catch (SocketException) when (_stopping.IsCancellationRequested)
        {
            // Stop can close the listener before cancellation reaches Accept.
        }
    }

    private async Task ForwardSessionAsync(TcpClient client)
    {
        using (client)
        using (var server = new TcpClient())
        using (var stopping = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token))
        {
            client.NoDelay = true;
            server.NoDelay = true;
            await server.ConnectAsync(_targetHost, _targetPort, stopping.Token);
            await using var clientStream = client.GetStream();
            await using var serverStream = server.GetStream();
            var checkpointLock = new CheckpointLockSession(_observedCheckpointJob);
            var toServer = _dropCommitRequest || _observedCheckpointJob is not null
                ? ForwardRequestsAsync(clientStream, serverStream, checkpointLock, stopping.Token)
                : clientStream.CopyToAsync(serverStream, stopping.Token);
            var toClient = ForwardResponsesAsync(serverStream, clientStream, checkpointLock, stopping.Token);
            var completed = await Task.WhenAny(toServer, toClient);
            try
            {
                await completed;
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                // The proxy's finite lifetime or disposal owns this cancellation.
            }
            finally
            {
                await stopping.CancelAsync();
                client.Close();
                server.Close();
                await ObserveStoppedPumpAsync(toServer, stopping.Token);
                await ObserveStoppedPumpAsync(toClient, stopping.Token);
            }
        }
    }

    private async Task ForwardRequestsAsync(NetworkStream client, NetworkStream server, CheckpointLockSession checkpointLock,
        CancellationToken token)
    {
        // SSL/GSS negotiation is disabled by this test proxy. Forward the
        // untyped startup packet, then inspect complete typed frontend frames.
        var lengthBytes = new byte[4];
        await client.ReadExactlyAsync(lengthBytes, token);
        var startupLength = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
        if (startupLength is < 8 or > 1_048_576) throw new InvalidDataException("Invalid PostgreSQL startup frame.");
        var startup = new byte[startupLength - 4];
        await client.ReadExactlyAsync(startup, token);
        await server.WriteAsync(lengthBytes, token);
        await server.WriteAsync(startup, token);
        var header = new byte[5];
        while (await client.ReadAsync(header.AsMemory(0, 1), token) != 0)
        {
            await client.ReadExactlyAsync(header.AsMemory(1, 4), token);
            var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
            if (length is < 4 or > 1_048_576) throw new InvalidDataException("Invalid PostgreSQL request frame.");
            var body = new byte[length - 4];
            await client.ReadExactlyAsync(body, token);
            checkpointLock.Request(header[0], body);
            if (_dropCommitRequest && header[0] == (byte)'Q' && body.AsSpan().SequenceEqual("COMMIT\0"u8)
                && Interlocked.Increment(ref _issuedCommits) > _commitsToPass
                && Interlocked.CompareExchange(ref _droppedCommitRequests, 1, 0) == 0)
            {
                // The client attempted its real COMMIT. PostgreSQL never sees
                // that frame; closing this session rolls its transaction back.
                _commitRequestDropped.TrySetResult();
                return;
            }
            await server.WriteAsync(header, token);
            await server.WriteAsync(body, token);
        }
    }

    private async Task ForwardResponsesAsync(NetworkStream server, NetworkStream client, CheckpointLockSession checkpointLock,
        CancellationToken cancellationToken)
    {
        // Backend frames are a type byte, big-endian length including the length
        // itself, then a body. CommandComplete 'C' carries a null-terminated tag.
        // https://www.postgresql.org/docs/18/protocol-message-formats.html
        var header = new byte[5];
        var capacityFactsRead = false;
        var capacityRollbackResponse = false;
        var capacityRecoveryRead = false;
        var renewalRead = false;
        var checkpointHistoryRead = false;
        var externalRecoveryRead = false;
        var quarantineRead = false;
        var emptyCheckpointReadCompleted = false;
        while (await server.ReadAsync(header.AsMemory(0, 1), cancellationToken) != 0)
        {
            await server.ReadExactlyAsync(header.AsMemory(1, 4), cancellationToken);
            var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
            if (length is < 4 or > 1_048_576)
                throw new InvalidDataException("The test proxy received an invalid PostgreSQL response frame.");

            var body = new byte[length - 4];
            await server.ReadExactlyAsync(body, cancellationToken);
            if (header[0] == (byte)'C' && (body.AsSpan().SequenceEqual("SELECT 0\0"u8)
                    || body.AsSpan().SequenceEqual("SELECT 1\0"u8)) && checkpointLock.Complete())
            {
                Interlocked.Increment(ref _checkpointLockReads);
                if (body.AsSpan().SequenceEqual("SELECT 0\0"u8)
                    && Interlocked.CompareExchange(ref _observeCheckpointLock, 2, 1) == 1)
                    emptyCheckpointReadCompleted = true;
            }
            if (header[0] == (byte)'T' && body.AsSpan().IndexOf("observation_family\0"u8) >= 0)
            {
                quarantineRead = true;
                Interlocked.Increment(ref _quarantineReads);
            }
            if (header[0] == (byte)'T' && BinaryPrimitives.ReadInt16BigEndian(body.AsSpan()) == 18
                && body.AsSpan().IndexOf("admitted\0"u8) >= 0
                && body.AsSpan().IndexOf("admission_token\0"u8) >= 0
                && body.AsSpan().IndexOf("ownership_until\0"u8) >= 0
                && body.AsSpan().IndexOf("stage\0"u8) >= 0)
                renewalRead = true;
            if (header[0] == (byte)'T' && body.AsSpan().IndexOf("transactional_cleanup_at\0"u8) >= 0
                && body.AsSpan().IndexOf("release_reason\0"u8) >= 0)
                capacityFactsRead = true;
            if (header[0] == (byte)'T' && body.AsSpan().IndexOf("admitted_at\0"u8) >= 0
                && body.AsSpan().IndexOf("ownership_until\0"u8) >= 0 && body.AsSpan().IndexOf("capacity_group\0"u8) >= 0)
                capacityRecoveryRead = true;
            if (header[0] == (byte)'T' && body.AsSpan().IndexOf("business_finalization_incomplete\0"u8) >= 0
                && body.AsSpan().IndexOf("execute_intent_at\0"u8) >= 0
                && body.AsSpan().IndexOf("release_reason\0"u8) >= 0)
                checkpointHistoryRead = true;
            if (header[0] == (byte)'T' && BinaryPrimitives.ReadInt16BigEndian(body.AsSpan()) == 20
                && body.AsSpan().IndexOf("admission_token\0"u8) >= 0
                && body.AsSpan().IndexOf("ownership_token\0"u8) >= 0
                && body.AsSpan().IndexOf("original_due_at\0"u8) >= 0
                && body.AsSpan().IndexOf("stage\0"u8) >= 0)
                externalRecoveryRead = true;
            if (header[0] == (byte)'C' && body.AsSpan().SequenceEqual("ROLLBACK\0"u8)
                && quarantineRead && Interlocked.CompareExchange(ref _holdQuarantineRollback, 2, 1) == 1)
            {
                _quarantineRollbackHeld.TrySetResult();
                await _releaseQuarantineRollback.Task.WaitAsync(cancellationToken);
                if (_loseQuarantineRollbackResponse) return;
            }
            if (header[0] == (byte)'C' && body.AsSpan().SequenceEqual("ROLLBACK\0"u8)
                && externalRecoveryRead && Interlocked.CompareExchange(ref _holdExternalRecoveryRollback, 2, 1) == 1)
            {
                _externalRecoveryRollbackHeld.TrySetResult();
                await _releaseExternalRecoveryRollback.Task.WaitAsync(cancellationToken);
                if (_loseExternalRecoveryRollbackResponse) return;
            }
            if (header[0] == (byte)'C' && body.AsSpan().SequenceEqual("ROLLBACK\0"u8)
                && checkpointHistoryRead && Interlocked.CompareExchange(ref _holdCheckpointRollback, 2, 1) == 1)
            {
                // Hold precisely the completed historical probe's ROLLBACK,
                // after PostgreSQL released its lock, before any ACK can publish.
                _checkpointRollbackHeld.TrySetResult();
                await _releaseCheckpointRollback.Task.WaitAsync(cancellationToken);
                if (_loseCheckpointRollbackResponse) return;
            }
            if (header[0] == (byte)'C' && body.AsSpan().SequenceEqual("COMMIT\0"u8)
                && capacityRecoveryRead && Interlocked.CompareExchange(ref _holdCapacityRecoveryCommit, 2, 1) == 1)
            {
                _capacityRecoveryCommitHeld.TrySetResult();
                await _releaseCapacityRecoveryCommit.Task.WaitAsync(cancellationToken);
                return; // The acquisition committed; its client never receives acknowledgement.
            }
            if (header[0] == (byte)'C' && body.AsSpan().SequenceEqual("COMMIT\0"u8)
                && renewalRead && Interlocked.CompareExchange(ref _holdRenewalCommit, 2, 1) == 1)
            {
                _renewalCommitHeld.TrySetResult();
                await _releaseRenewalCommit.Task.WaitAsync(cancellationToken);
                if (_loseRenewalCommitResponse) return;
            }
            if (header[0] == (byte)'C' && body.AsSpan().SequenceEqual("ROLLBACK\0"u8) && capacityFactsRead)
            {
                capacityRollbackResponse = true;
                if (Interlocked.CompareExchange(ref _holdCapacityRollback, 2, 1) == 1)
                {
                    // PostgreSQL already released the historical probe's row lock.
                    // Hold only its response, then forward the original frames intact.
                    _capacityRollbackHeld.TrySetResult();
                    await _releaseCapacityRollback.Task.WaitAsync(cancellationToken);
                }
            }
            if (header[0] == (byte)'C' && (body.AsSpan().SequenceEqual("ROLLBACK\0"u8)
                || body.AsSpan().SequenceEqual("COMMIT\0"u8)))
            {
                capacityFactsRead = false;
                capacityRecoveryRead = false;
                renewalRead = false;
                checkpointHistoryRead = false;
                externalRecoveryRead = false;
                quarantineRead = false;
            }
            if (header[0] == (byte)'C' && body.AsSpan().SequenceEqual("COMMIT\0"u8)
                && Interlocked.Increment(ref _completedCommits) > _commitsToPass && !_dropCommitRequest
                && Interlocked.CompareExchange(ref _droppedCommitResponses, 1, 0) == 0)
            {
                // The real server has committed. Forward neither CommandComplete
                // nor its following ReadyForQuery. Held mode keeps both sockets
                // alive until the client cancels or this owned proxy is disposed.
                if (_holdCommitResponse)
                {
                    _commitResponseHeld.TrySetResult();
                    await _releaseHeldResponse.Task.WaitAsync(cancellationToken);
                }
                return;
            }

            await client.WriteAsync(header, cancellationToken);
            await client.WriteAsync(body, cancellationToken);
            if (header[0] == (byte)'Z' && emptyCheckpointReadCompleted)
            {
                emptyCheckpointReadCompleted = false;
                _emptyCheckpointLockRead.TrySetResult();
            }
            if (header[0] == (byte)'Z' && capacityRollbackResponse)
            {
                Interlocked.Increment(ref _completedCapacityRollbacks);
                capacityRollbackResponse = false;
            }
        }
    }

    // Narrow observer only: match this exact query AND its actual binary UUID
    // binding. Every frame is forwarded unchanged; unrelated SELECT 0 responses
    // cannot release the test's real row-lock barrier.
    private sealed class CheckpointLockSession(Guid? job)
    {
        private bool _parsed;
        private int _bound;

        internal void Request(byte kind, ReadOnlySpan<byte> body)
        {
            if (job is null) return;
            if (kind == (byte)'P')
            {
                var nameEnd = body.IndexOf((byte)0);
                var sql = nameEnd < 0 ? ReadOnlySpan<byte>.Empty : body[(nameEnd + 1)..];
                var sqlEnd = sql.IndexOf((byte)0);
                _parsed = sqlEnd >= 0 && Encoding.UTF8.GetString(sql[..sqlEnd])
                    == "SELECT id FROM background_jobs WHERE id=$1 FOR UPDATE SKIP LOCKED";
            }
            if (kind != (byte)'B') return;
            var first = body.IndexOf((byte)0);
            if (first < 0) return;
            var rest = body[(first + 1)..];
            var second = rest.IndexOf((byte)0);
            if (second < 0) return;
            rest = rest[(second + 1)..];
            // One binary-format UUID parameter, followed by result formats.
            var match = _parsed && rest.Length >= 26 && BinaryPrimitives.ReadInt16BigEndian(rest) == 1
                && BinaryPrimitives.ReadInt16BigEndian(rest[2..]) == 1
                && BinaryPrimitives.ReadInt16BigEndian(rest[4..]) == 1
                && BinaryPrimitives.ReadInt32BigEndian(rest[6..]) == 16
                && new Guid(rest.Slice(10, 16), bigEndian: true) == job.Value;
            Interlocked.Exchange(ref _bound, match ? 1 : 0);
        }

        internal bool Complete() => Interlocked.Exchange(ref _bound, 0) != 0;
    }

    private static async Task ObserveStoppedPumpAsync(Task pump, CancellationToken stopped)
    {
        try
        {
            await pump;
        }
        catch (OperationCanceledException) when (stopped.IsCancellationRequested)
        {
            // The other pump ended and this socket's ownership has been canceled.
        }
        catch (IOException) when (stopped.IsCancellationRequested)
        {
            // Closing an owned socket also wakes a pending read/write with IO failure.
        }
        catch (ObjectDisposedException) when (stopped.IsCancellationRequested)
        {
            // Cleanup can close the socket before the canceled pump resumes.
        }
    }
}
