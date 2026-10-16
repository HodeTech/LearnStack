using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Npgsql;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// Forwards a real PostgreSQL session, withholding exactly one completed COMMIT's
/// acknowledgement. The optional held socket proves cancellation independently of disconnect.
/// </summary>
internal sealed class CommitResponseDroppingProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stopping = new(TimeSpan.FromSeconds(30));
    private readonly List<Task> _sessions = [];
    private readonly string _targetHost;
    private readonly int _targetPort;
    private readonly bool _rejectReconciliationConnections;
    private readonly bool _holdCommitResponse;
    private readonly TaskCompletionSource _commitResponseHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseHeldResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _accepting;
    private int _droppedCommitResponses;
    private int _acceptedConnections;
    private readonly int _commitsToPass;
    private int _completedCommits;

    public CommitResponseDroppingProxy(string connectionString, bool rejectReconciliationConnections = false,
        int commitsToPass = 0, bool holdCommitResponse = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(commitsToPass);
        _commitsToPass = commitsToPass;
        _rejectReconciliationConnections = rejectReconciliationConnections;
        _holdCommitResponse = holdCommitResponse;
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
    public void ReleaseHeldResponse() => _releaseHeldResponse.TrySetResult();

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
            var toServer = clientStream.CopyToAsync(serverStream, stopping.Token);
            var toClient = ForwardResponsesAsync(serverStream, clientStream, stopping.Token);
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

    private async Task ForwardResponsesAsync(NetworkStream server, NetworkStream client, CancellationToken cancellationToken)
    {
        // Backend frames are a type byte, big-endian length including the length
        // itself, then a body. CommandComplete 'C' carries a null-terminated tag.
        // https://www.postgresql.org/docs/18/protocol-message-formats.html
        var header = new byte[5];
        while (await server.ReadAsync(header.AsMemory(0, 1), cancellationToken) != 0)
        {
            await server.ReadExactlyAsync(header.AsMemory(1, 4), cancellationToken);
            var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
            if (length is < 4 or > 1_048_576)
                throw new InvalidDataException("The test proxy received an invalid PostgreSQL response frame.");

            var body = new byte[length - 4];
            await server.ReadExactlyAsync(body, cancellationToken);
            if (header[0] == (byte)'C' && body.AsSpan().SequenceEqual("COMMIT\0"u8)
                && Interlocked.Increment(ref _completedCommits) > _commitsToPass
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
        }
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
