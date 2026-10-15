// Exact-pin, disposable qualification only. No LearnStack runtime is referenced.
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Hangfire;
using Hangfire.PostgreSql;
using Hangfire.Server;
using Hangfire.Storage;
using Npgsql;

try
{
    var port = int.Parse(args.Single(), System.Globalization.CultureInfo.InvariantCulture);
    Require(port is > 0 and <= 65535);
    var direct = ConnectionString(port);
    await WaitForDatabase(direct);
    await using var source = NpgsqlDataSource.Create(direct);
    Require(await Scalar<bool>(source, "SELECT current_setting('statement_timeout')::interval=interval '5 seconds' AND current_setting('lock_timeout')::interval=interval '5 seconds'"));
    // Only this disposable probe's migration connection may install a schema.
    _ = new PostgreSqlStorage(new Factory(source), Options(prepareSchema: true));
    var storage = new PostgreSqlStorage(new Factory(source), Options());
#pragma warning disable CS0618
    var components = storage.GetComponents().ToArray();
#pragma warning restore CS0618
    string[] expected = ["Hangfire.PostgreSql.ExpirationManager", "Hangfire.PostgreSql.CountersAggregator", "Hangfire.PostgreSql.PostgreSqlHeartbeatProcess"];
    Require(components.Length == expected.Length && components.Select(x => x.GetType().FullName).SequenceEqual(expected));
    Require(components.All(x => x.GetType().Assembly == typeof(PostgreSqlStorage).Assembly));
    var filteredStorage = new SuppressedCounterStorage(new Factory(source), Options());
#pragma warning disable CS0618
    var retained = filteredStorage.GetComponents().ToArray();
#pragma warning restore CS0618
    Require(retained.Select(x => x.GetType().FullName).SequenceEqual(new[] { expected[0], expected[2] }));
    var expiration = retained[0];
    var beforeFilters = GlobalJobFilters.Filters.Select(x => x.Instance.GetType()).ToArray();
    Require(beforeFilters.Count(x => x == typeof(StatisticsHistoryAttribute)) == 1);
    GlobalJobFilters.Filters.Remove<StatisticsHistoryAttribute>();
    Require(GlobalJobFilters.Filters.Select(x => x.Instance.GetType()).SequenceEqual(beforeFilters.Where(x => x != typeof(StatisticsHistoryAttribute))));
    await Sql(source, "ALTER TABLE public.counter SET (autovacuum_enabled=false); ALTER TABLE public.aggregatedcounter SET (autovacuum_enabled=false)");
    var measurements = new List<object>();
    foreach (var completions in new[] { 1_000, 10_000, 100_000 })
    foreach (var rowsPerTransition in new[] { 1, 2 })
    {
        await Sql(source, "TRUNCATE public.counter,public.aggregatedcounter RESTART IDENTITY");
        await using (var seed = source.CreateCommand("INSERT INTO public.counter(key,value) SELECT 'stats:succeeded',1 FROM generate_series(1,$1)"))
        {
            seed.Parameters.Add(new NpgsqlParameter { Value = completions });
            await seed.ExecuteNonQueryAsync();
        }
        if (rowsPerTransition == 2)
        {
            await using var seed = source.CreateCommand("INSERT INTO public.counter(key,value) SELECT 'stats:deleted',1 FROM generate_series(1,$1)");
            seed.Parameters.Add(new NpgsqlParameter { Value = completions });
            await seed.ExecuteNonQueryAsync();
        }
        var before = await Size(source);
        using (var counters = (JobStorageConnection)storage.GetConnection())
        {
            Require(counters.GetCounter("stats:succeeded") == completions);
            Require(counters.GetCounter("stats:deleted") == (rowsPerTransition == 2 ? completions : 0));
        }
        var elapsed = Stopwatch.StartNew();
        // Actual stock Execute: positive 1 ms final interval, no replacement SQL.
        // If waiting fails, Python owns the process and removes the server in finally.
        var actual = Task.Run(() => expiration.Execute(CancellationToken.None));
        await actual.WaitAsync(TimeSpan.FromSeconds(15));
        elapsed.Stop();
        var after = await Size(source);
        Require(after.Rows == rowsPerTransition && after.Sum == (long)completions * rowsPerTransition);
        using (var counters = (JobStorageConnection)storage.GetConnection())
        {
            Require(counters.GetCounter("stats:succeeded") == completions);
            Require(counters.GetCounter("stats:deleted") == (rowsPerTransition == 2 ? completions : 0));
        }
        await Sql(source, "VACUUM public.counter");
        var vacuumed = await Size(source);
        measurements.Add(new { transitions = completions, rows_per_transition = rowsPerTransition,
            before, after_actual_expiration_execute = after, after_vacuum = vacuumed,
            execution_ms = elapsed.ElapsedMilliseconds });
    }
    await Sql(source, """
        TRUNCATE public.counter,public.aggregatedcounter RESTART IDENTITY;
        INSERT INTO public.counter(key,value,expireat) VALUES
            ('stats:failed:2000-01-01',1,now()-interval '1 second'),
            ('stats:failed:2099-01-01',1,now()+interval '1 day');
        INSERT INTO public.aggregatedcounter(key,value,expireat)
            VALUES ('stats:deleted:2000-01-01',1,now()-interval '1 second');
        """);
    await Task.Run(() => expiration.Execute(CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(15));
    Require(await Scalar<long>(source, "SELECT count(*) FROM public.counter") == 1);
    Require(await Scalar<long>(source, "SELECT count(*) FROM public.aggregatedcounter") == 0);
    Require(await Scalar<long>(source, "SELECT count(*) FROM public.counter WHERE expireat>now()") == 1);
    // Start the blackhole only after connection establishment. Its upstream
    // reader consumes responses (TCP ACKs continue) but never forwards them.
    await using var proxy = new ResponseBlackhole(port);
    await using var blackholeSource = NpgsqlDataSource.Create(ConnectionString(proxy.Port));
    var blackholeStorage = new PostgreSqlStorage(new Factory(blackholeSource, () => proxy.Withhold = true), Options());
#pragma warning disable CS0618
    var aggregator = blackholeStorage.GetComponents().Single(x => x.GetType().FullName == expected[1]);
#pragma warning restore CS0618
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var operation = Task.Run(() =>
    {
        try { aggregator.Execute(cancellation.Token); return "returned"; }
        catch (NpgsqlException) { return "driver_failure"; }
        catch (OperationCanceledException) { return "canceled"; }
        catch { return "unexpected_failure"; }
    });
    bool pendingAfterEightSeconds;
    string releasedOutcome;
    try
    {
        pendingAfterEightSeconds = await Task.WhenAny(operation, Task.Delay(TimeSpan.FromSeconds(8))) != operation;
        Require(pendingAfterEightSeconds && proxy.WithheldBytes > 0 && cancellation.IsCancellationRequested);
    }
    finally
    {
        // Keep the actual operation owned until its sockets close and it finishes.
        await proxy.StopAsync();
        releasedOutcome = await operation.WaitAsync(TimeSpan.FromSeconds(8));
    }
    Require(releasedOutcome == "driver_failure");
    var version = await Scalar<string>(source, "SHOW server_version");
    Require(version.StartsWith("18.4", StringComparison.Ordinal));
    Console.WriteLine(JsonSerializer.Serialize(new { pass = true, postgres = version,
        stock_components = expected, global_history_removed_preserving_other_filters = true,
        public_component_hook_suppresses_only_aggregator = true,
        actual_expiration_removes_expired_raw_and_aggregated_rows = true,
        worker_or_business_admission_proof = false, measurements,
        blackhole = new { actual_stock_execute = true, pending_after_eight_seconds = pendingAfterEightSeconds,
            connection_default_command_timeout_seconds = 5, stock_aggregator_command_timeout_seconds = 0,
            cancellation_timeout_ms = 1000,
            server_statement_timeout_ms = 5000, server_lock_timeout_ms = 5000,
            cancellation_requested = true, response_bytes_withheld = proxy.WithheldBytes,
            actual_cleanup_after_socket_release = releasedOutcome } }));
}
catch
{
    // Never emit provider messages, exception objects, SQL or connection strings.
    Console.WriteLine("{\"pass\":false,\"failure\":\"component_probe_failed\"}");
    Environment.ExitCode = 1;
}

static void Require(bool condition) { if (!condition) throw new InvalidOperationException(); }
static string ConnectionString(int port) => new NpgsqlConnectionStringBuilder
{
    Host = "127.0.0.1", Port = port, Username = "postgres", Database = "postgres",
    Pooling = false, Timeout = 5, CommandTimeout = 5, CancellationTimeout = 1000,
    SslMode = SslMode.Disable, Enlist = false,
    Options = "-c statement_timeout=5000 -c lock_timeout=5000",
}.ConnectionString;
static PostgreSqlStorageOptions Options(bool prepareSchema = false) => new()
{
    SchemaName = "public", PrepareSchemaIfNecessary = prepareSchema,
    AllowDegradedModeWithoutStorage = false, StartupConnectionMaxRetries = 0,
    EnableTransactionScopeEnlistment = false, UseNativeDatabaseTransactions = true,
    UseSlidingInvisibilityTimeout = true, EnableLongPolling = false,
    JobExpirationCheckInterval = TimeSpan.FromMilliseconds(1),
    CountersAggregateInterval = TimeSpan.FromMilliseconds(1),
};
static async Task WaitForDatabase(string connectionString)
{
    var deadline = Stopwatch.StartNew();
    while (true)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            return;
        }
        catch (NpgsqlException) when (deadline.Elapsed < TimeSpan.FromSeconds(15))
        { await Task.Delay(200); }
    }
}
static async Task Sql(NpgsqlDataSource source, string sql)
{
    await using var command = source.CreateCommand(sql);
    await command.ExecuteNonQueryAsync();
}
static async Task<T> Scalar<T>(NpgsqlDataSource source, string sql)
{
    await using var command = source.CreateCommand(sql);
    return (T)(await command.ExecuteScalarAsync())!;
}
static async Task<CounterSize> Size(NpgsqlDataSource source)
{
    await using var command = source.CreateCommand("SELECT count(*),coalesce(sum(value),0)::bigint,pg_relation_size('public.counter'),pg_indexes_size('public.counter'),pg_total_relation_size('public.counter') FROM public.counter");
    await using var reader = await command.ExecuteReaderAsync();
    await reader.ReadAsync();
    return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4));
}
public sealed record CounterSize(long Rows, long Sum, long HeapBytes, long IndexBytes, long TotalBytes);
public sealed class Factory(NpgsqlDataSource source, Action? afterOpen = null) : IConnectionFactory
{
    public NpgsqlConnection GetOrCreateConnection()
    {
        var connection = source.OpenConnection();
        afterOpen?.Invoke();
        return connection;
    }
}
#pragma warning disable CS0618
// Probe-only public override: no reflection, replacement SQL or lifecycle owner.
public sealed class SuppressedCounterStorage(IConnectionFactory factory, PostgreSqlStorageOptions options)
    : PostgreSqlStorage(factory, options)
{
    public override IEnumerable<IServerComponent> GetComponents()
    {
        var stock = base.GetComponents().ToArray();
        var matching = stock.Where(x => x.GetType().Assembly == typeof(PostgreSqlStorage).Assembly &&
            x.GetType().FullName == "Hangfire.PostgreSql.CountersAggregator").ToArray();
        if (matching.Length != 1) throw new InvalidOperationException();
        return stock.Where(x => !ReferenceEquals(x, matching[0]));
    }
}
#pragma warning restore CS0618
public sealed class ResponseBlackhole : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly int upstreamPort;
    private readonly CancellationTokenSource stop = new();
    private readonly List<TcpClient> clients = [];
    private readonly List<Task> pumps = [];
    private readonly Task accepts;
    private long withheldBytes;
    private int stopped;
    public volatile bool Withhold;
    public long WithheldBytes => Interlocked.Read(ref withheldBytes);
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public ResponseBlackhole(int port) { upstreamPort = port; listener.Start(); accepts = AcceptAsync(); }
    private async Task AcceptAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                var upstream = new TcpClient();
                lock (clients) { clients.Add(client); clients.Add(upstream); }
                await upstream.ConnectAsync(IPAddress.Loopback, upstreamPort, stop.Token);
                lock (clients)
                {
                    pumps.Add(Pump(client.GetStream(), upstream.GetStream(), false));
                    pumps.Add(Pump(upstream.GetStream(), client.GetStream(), true));
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException or IOException) { }
    }
    private async Task Pump(NetworkStream input, NetworkStream output, bool responses)
    {
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer, stop.Token);
                if (read == 0) return;
                if (responses && Withhold) Interlocked.Add(ref withheldBytes, read);
                else await output.WriteAsync(buffer.AsMemory(0, read), stop.Token);
            }
        }
        // EPIPE/EOF on either socket is expected during owned teardown.
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException or IOException) { }
    }
    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0) return;
        stop.Cancel();
        listener.Stop();
        lock (clients) foreach (var client in clients) client.Dispose();
        await accepts.WaitAsync(TimeSpan.FromSeconds(3));
        Task[] pending;
        lock (clients)
        {
            foreach (var client in clients) client.Dispose();
            pending = pumps.ToArray();
        }
        await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(3));
    }
    public async ValueTask DisposeAsync() { await StopAsync(); stop.Dispose(); }
}
