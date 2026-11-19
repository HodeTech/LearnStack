using System.Data.Common;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Application.Audit;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LearnStack.Tests.Integration.Database;

/// <summary>The pieces the controlled-rebind proofs share: the real audit store, admissions and fault injection.</summary>
internal static class RebindProofSupport
{
    internal static PlatformAdmission Admit(string? ticket = "CHG-104", DateTimeOffset? issuedAt = null) =>
        TestPlatformAdmissions.Admission(
            PlatformOperations.IdentityAuthorityRebind, ticket,
            correlationId: "00-rebind-" + Guid.NewGuid().ToString("N"), issuedAt: issuedAt);

    /// <summary>The real store over the real Identity catalogue; rows go through the connection the caller passes.</summary>
    internal static AsyncServiceScope Store() =>
        new ServiceCollection()
            .AddMetrics()
            .AddLogging()
            .AddScoped<IAuditStateCapture, AuditStateCapture>()
            .AddSingleton<IAuditHealth, AuditHealth>()
            .AddSingleton<IAuditCatalog>(new AuditCatalog([new IdentityAuditCatalogSource()]))
            .AddSingleton<ITenantContextAccessor>(StaticTenantContextAccessor.Unresolved)
            .AddScoped<IGlobalIdentityEnrollment, GlobalIdentityEnrollment>()
            .AddSingleton(new Lazy<NpgsqlDataSource>(
                () => throw new InvalidOperationException("The rebind rows are written on the caller's own connection.")))
            .AddScoped<IAuditStore, PostgresAuditStore>()
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>()
            .CreateAsyncScope();

    internal static string DatabaseOf(string connectionString) => new NpgsqlConnectionStringBuilder(connectionString).Database!;

    internal static string In(string connectionString, string database) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Database = database }.ConnectionString;
}

/// <summary>A new realm that reports what the test says, or fails the way the test says.</summary>
internal sealed class FixedRealm(Func<string, CancellationToken, Task<NewRealmSnapshot>> read) : INewRealmContinuitySource
{
    internal int Reads { get; private set; }

    public Task<NewRealmSnapshot> ReadAsync(string issuer, CancellationToken cancellationToken = default)
    {
        Reads++;
        return read(issuer, cancellationToken);
    }

    internal static FixedRealm Reporting(IEnumerable<string> subjects, string? discoveredIssuer = null,
        int keys = 2, bool complete = true) =>
        new((issuer, _) => Task.FromResult(new NewRealmSnapshot(discoveredIssuer ?? issuer, keys, complete, subjects.ToArray())));

    internal static FixedRealm Failing() =>
        new((_, _) => throw new NewRealmUnavailableException());
}

/// <summary>The real store, with seams at the two points a crash or an outage would strike.</summary>
internal sealed class FaultingAuditStore(IAuditStore inner) : IAuditStore
{
    internal Func<DbConnection, Task>? AfterResult { get; set; }
    internal bool FailIntent { get; set; }
    internal bool FailResult { get; set; }

    public async Task WriteIdentityRebindIntentAsync(AuditEntryDraft entry, PlatformAdmission admission,
        DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken = default)
    {
        if (FailIntent) throw new AuditWriteFailedException("forced intent failure");
        await inner.WriteIdentityRebindIntentAsync(entry, admission, connection, transaction, cancellationToken);
    }

    public async Task WriteIdentityRebindResultAsync(AuditEntryDraft entry, PlatformAdmission admission,
        DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken = default)
    {
        if (FailResult) throw new AuditWriteFailedException("forced result failure");
        await inner.WriteIdentityRebindResultAsync(entry, admission, connection, transaction, cancellationToken);
        if (AfterResult is not null) await AfterResult(connection);
    }

    public Task WriteGlobalIdentityPendingAsync(LearnStack.SharedKernel.Persistence.IUnitOfWork unit,
        GlobalIdentityCreationReference creation, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task ReconcileGlobalIdentityAsync(AuditIntent intent, IReadOnlyList<CapturedEntityChange> changes,
        AuditIntentState state, DateTimeOffset timestamp, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task WritePendingAsync(LearnStack.SharedKernel.Persistence.IUnitOfWork unitOfWork,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task WriteStandaloneAsync(AuditEntryDraft entry, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task WriteBestEffortAsync(AuditEntryDraft entry, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task WritePlatformScopeAsync(AuditEntryDraft entry, DbConnection connection, DbTransaction transaction,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task WriteJobTerminalAsync(AuditEntryDraft entry, JobTerminalTransition transition, DbConnection connection,
        DbTransaction transaction, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task WriteEventTerminalAsync(AuditEntryDraft entry, EventTerminalTransition transition, DbConnection connection,
        DbTransaction transaction, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

/// <summary>
/// A loopback TCP relay in front of PostgreSQL that lets the first COMMIT that follows the issuer UPDATE reach the
/// server and then drops the answer and the connection, which is exactly the commit whose outcome the client cannot
/// know.
/// </summary>
internal sealed class CommitAckDroppingProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly string _host;
    private readonly int _port;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _pumps = [];
    private int _dropped;

    internal CommitAckDroppingProxy(string upstreamConnectionString)
    {
        var upstream = new NpgsqlConnectionStringBuilder(upstreamConnectionString);
        _host = upstream.Host!;
        _port = upstream.Port;
        _listener.Start();
        _pumps.Add(Task.Run(AcceptAsync));
    }

    internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    internal bool Dropped => Volatile.Read(ref _dropped) > 0;

    internal string Through(string connectionString) => new NpgsqlConnectionStringBuilder(connectionString)
    {
        Host = "127.0.0.1",
        Port = Port,
        SslMode = SslMode.Disable,
    }.ConnectionString;

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                lock (_pumps) _pumps.Add(Task.Run(() => RelayAsync(client)));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task RelayAsync(TcpClient client)
    {
        using var clientSide = client;
        using var upstream = new TcpClient();
        try
        {
            await upstream.ConnectAsync(_host, _port, _stop.Token);
            var armed = false;
            var dropAnswer = false;
            var toServer = Task.Run(async () =>
            {
                var buffer = new byte[16 * 1024];
                var fromClient = clientSide.GetStream();
                var toUpstream = upstream.GetStream();
                int read;
                while ((read = await fromClient.ReadAsync(buffer, _stop.Token)) > 0)
                {
                    var text = Encoding.ASCII.GetString(buffer, 0, read);
                    armed |= text.Contains("SET issuer", StringComparison.Ordinal);
                    if (armed && buffer[0] == (byte)'Q' && text.Contains("COMMIT", StringComparison.Ordinal))
                    {
                        // The server will get this COMMIT; whatever it answers is discarded and the connection cut.
                        Volatile.Write(ref dropAnswer, true);
                    }

                    await toUpstream.WriteAsync(buffer.AsMemory(0, read), _stop.Token);
                }
            }, _stop.Token);
            var toClient = Task.Run(async () =>
            {
                var buffer = new byte[16 * 1024];
                var fromUpstream = upstream.GetStream();
                var toClientStream = clientSide.GetStream();
                int read;
                while ((read = await fromUpstream.ReadAsync(buffer, _stop.Token)) > 0)
                {
                    if (Volatile.Read(ref dropAnswer))
                    {
                        Interlocked.Increment(ref _dropped);
                        clientSide.Close();
                        upstream.Close();
                        return;
                    }

                    await toClientStream.WriteAsync(buffer.AsMemory(0, read), _stop.Token);
                }
            }, _stop.Token);
            await Task.WhenAny(toServer, toClient);
        }
        catch (Exception failure) when (failure is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        Task[] pumps;
        lock (_pumps) pumps = [.. _pumps];
        try { await Task.WhenAll(pumps); } catch (Exception failure) when (failure is OperationCanceledException or IOException or ObjectDisposedException) { }
        _stop.Dispose();
    }
}
