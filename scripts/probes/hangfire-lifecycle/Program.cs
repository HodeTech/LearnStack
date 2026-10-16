using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Hangfire;
using Hangfire.Common;
using Hangfire.Logging;
using Hangfire.PostgreSql;
using Hangfire.Server;
using Hangfire.Storage;

LogProvider.SetCurrentLogProvider(new SilentLogProvider());
if (args.Length != 1) throw new ArgumentException("Select one declared lifecycle probe mode.");
if (args[0].StartsWith("builder-", StringComparison.Ordinal))
{
    await BuilderProbe.RunAsync(args[0]);
    return;
}
if (args[0] != "stock-restart") throw new ArgumentException("Unknown lifecycle probe mode.");
var watch = Stopwatch.StartNew();
void Emit(object row) => Console.WriteLine(JsonSerializer.Serialize(new { elapsed_ms = watch.ElapsedMilliseconds, row }));
Emit(new
{
    core = typeof(BackgroundProcessingServer).Assembly.FullName,
    provider = typeof(PostgreSqlStorage).Assembly.FullName,
    claim = "Actual pinned stock Core lifecycle; fake storage; no PostgreSQL/vendor-SQL qualification"
});
var process = new HeldProcess();
var storage = new ProbeStorage(process, Emit);
var options = new BackgroundProcessingServerOptions
{
    ServerName = "restart-proof",
    HeartbeatInterval = TimeSpan.FromMilliseconds(50),
    ServerTimeout = TimeSpan.FromMinutes(1),
    ServerCheckInterval = TimeSpan.FromHours(1),
    CancellationCheckInterval = TimeSpan.FromHours(1),
    StopTimeout = TimeSpan.Zero,
    ShutdownTimeout = Timeout.InfiniteTimeSpan,
    RestartDelay = TimeSpan.FromMilliseconds(25),
    ExcludeStorageProcesses = true,
};
var server = new BackgroundProcessingServer(storage, new IBackgroundProcess[] { process }, new Dictionary<string, object>(), options);
try
{
    await process.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await process.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
    var first = process.FirstContext!;
    var tokens = new Dictionary<string, string>();
    foreach (var pair in new[] { ("stopping", first.StoppingToken), ("stopped", first.StoppedToken), ("shutdown", first.ShutdownToken) })
    {
        try { _ = pair.Item2.WaitHandle; tokens[pair.Item1] = "accessible"; }
        catch (ObjectDisposedException) { tokens[pair.Item1] = "ObjectDisposedException"; }
    }
    if (Volatile.Read(ref process.Live) < 2 || process.FirstFinished.Task.IsCompleted
        || tokens.Values.Any(value => value != "ObjectDisposedException"))
        throw new InvalidOperationException("Stock restart failure was not reproduced.");
    Emit(new
    {
        event_name = "second_generation_with_first_still_live",
        live = Volatile.Read(ref process.Live),
        announced = storage.Announced.Count,
        removed = storage.Removed.Count,
        first_completion = process.FirstFinished.Task.IsCompleted,
        token_wait_handles = tokens
    });
}
finally
{
    process.Release.Set();
    server.SendStop();
    await server.WaitForShutdownAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
    await process.FirstFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));
    foreach (var thread in process.Threads.Values)
        if (!thread.Join(TimeSpan.FromSeconds(10))) throw new InvalidOperationException("Held stock thread did not finish.");
    server.Dispose();
    process.Release.Dispose();
}
if (Volatile.Read(ref process.Live) != 0 || Volatile.Read(ref process.Completed) < 2)
    throw new InvalidOperationException("Stock reproduction cleanup did not finish.");
Emit(new { event_name = "assertions_passed", actual_live = Volatile.Read(ref process.Live) });

sealed class HeldProcess : IBackgroundProcess
{
    public readonly ManualResetEventSlim Release = new(false);
    public readonly ConcurrentDictionary<int, Thread> Threads = new();
    public readonly TaskCompletionSource FirstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource SecondStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource FirstFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public BackgroundProcessContext? FirstContext;
    public int Started, Live, Completed;
    public void Execute(BackgroundProcessContext context)
    {
        Threads.TryAdd(Environment.CurrentManagedThreadId, Thread.CurrentThread);
        var number = Interlocked.Increment(ref Started);
        Interlocked.Increment(ref Live);
        if (number == 1) { FirstContext = context; FirstStarted.TrySetResult(); }
        if (number == 2) SecondStarted.TrySetResult();
        Release.Wait();
        Interlocked.Decrement(ref Live);
        Interlocked.Increment(ref Completed);
        if (number == 1) FirstFinished.TrySetResult();
        context.StoppingToken.ThrowIfCancellationRequested();
    }
}
sealed class ProbeStorage(HeldProcess process, Action<object> emit) : JobStorage
{
    public readonly ConcurrentBag<string> Announced = new();
    public readonly ConcurrentBag<string> Removed = new();
    private int _forced;
    public override IMonitoringApi GetMonitoringApi() => throw new NotSupportedException();
    public override IStorageConnection GetConnection() => new ProbeConnection(this);
    public void Beat()
    {
        if (process.FirstStarted.Task.IsCompleted && Interlocked.CompareExchange(ref _forced, 1, 0) == 0)
        {
            emit(new { event_name = "forced_heartbeat_server_gone", live = Volatile.Read(ref process.Live) });
            throw new BackgroundServerGoneException();
        }
    }
}
sealed class ProbeConnection(ProbeStorage storage) : JobStorageConnection
{
    public override void AnnounceServer(string id, ServerContext context) => storage.Announced.Add(id);
    public override void RemoveServer(string id) => storage.Removed.Add(id);
    public override void Heartbeat(string id) => storage.Beat();
    public override int RemoveTimedOutServers(TimeSpan timeout) => 0;
    public override IWriteOnlyTransaction CreateWriteTransaction() => throw new NotSupportedException();
    public override IDisposable AcquireDistributedLock(string key, TimeSpan timeout) => throw new NotSupportedException();
    public override string CreateExpiredJob(Job job, IDictionary<string, string> parameters, DateTime created, TimeSpan expire) => throw new NotSupportedException();
    public override IFetchedJob FetchNextJob(string[] queues, CancellationToken token) => throw new NotSupportedException();
    public override void SetJobParameter(string id, string name, string value) => throw new NotSupportedException();
    public override string GetJobParameter(string id, string name) => throw new NotSupportedException();
    public override JobData GetJobData(string id) => throw new NotSupportedException();
    public override StateData GetStateData(string id) => throw new NotSupportedException();
    public override HashSet<string> GetAllItemsFromSet(string key) => new();
    public override string GetFirstByLowestScoreFromSet(string key, double lower, double upper) => null!;
    public override void SetRangeInHash(string key, IEnumerable<KeyValuePair<string, string>> pairs) => throw new NotSupportedException();
    public override Dictionary<string, string> GetAllEntriesFromHash(string key) => new();
}
sealed class SilentLogProvider : ILogProvider
{
    public ILog GetLogger(string name) => new SilentLog();
    sealed class SilentLog : ILog
    {
        public bool Log(LogLevel level, Func<string> message, Exception? exception = null) => message is null;
    }
}
