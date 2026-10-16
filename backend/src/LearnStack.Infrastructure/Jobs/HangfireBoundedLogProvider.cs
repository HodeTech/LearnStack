using Hangfire.Logging;

namespace LearnStack.Infrastructure.Jobs;

public enum HangfireDiagnosticLevel { Trace, Debug, Information, Warning, Error, Fatal }
public enum HangfireDiagnosticOutcome { Operational, Fault }
public enum HangfireDiagnosticSource { Other, ServerHeartbeat, FetchedJobHeartbeat, Expiration }

/// <summary>Closed memory-only recording; no supplied callback runs on a vendor or exit thread.</summary>
public sealed class HangfireBoundedLogProvider : ILogProvider
{
    private const int LevelCount = 6;
    private const int OutcomeCount = 2;
    private readonly BoundedLog[] _loggers = Enumerable.Range(0, 4).Select(_ => new BoundedLog()).ToArray();

    public ILog GetLogger(string name) => _loggers[(int)(name switch
    {
        "Hangfire.Server.ServerHeartbeatProcess" => HangfireDiagnosticSource.ServerHeartbeat,
        "Hangfire.PostgreSql.PostgreSqlHeartbeatProcess" or "Hangfire.PostgreSql.PostgreSqlFetchedJob"
            => HangfireDiagnosticSource.FetchedJobHeartbeat,
        "Hangfire.PostgreSql.ExpirationManager" => HangfireDiagnosticSource.Expiration,
        _ => HangfireDiagnosticSource.Other,
    })];

    /// <summary>A finite counter dimension; reading invokes no provider, sink or delegate.</summary>
    public long Count(HangfireDiagnosticLevel level, HangfireDiagnosticOutcome outcome,
        HangfireDiagnosticSource source = HangfireDiagnosticSource.Other)
    {
        if (!Enum.IsDefined(level) || !Enum.IsDefined(outcome) || !Enum.IsDefined(source))
            throw new ArgumentOutOfRangeException(nameof(level));
        return _loggers[(int)source].Count(level, outcome);
    }

    private sealed class BoundedLog : ILog
    {
        private readonly long[] _counts = new long[LevelCount * OutcomeCount];

        internal long Count(HangfireDiagnosticLevel level, HangfireDiagnosticOutcome outcome) =>
            Interlocked.Read(ref _counts[(int)level * OutcomeCount + (int)outcome]);

        public bool Log(LogLevel logLevel, Func<string> messageFunc, Exception? exception = null)
        {
            if (messageFunc is null) return Enum.IsDefined(logLevel);
            var level = logLevel switch
            {
                LogLevel.Trace => HangfireDiagnosticLevel.Trace,
                LogLevel.Debug => HangfireDiagnosticLevel.Debug,
                LogLevel.Info => HangfireDiagnosticLevel.Information,
                LogLevel.Warn => HangfireDiagnosticLevel.Warning,
                LogLevel.Error => HangfireDiagnosticLevel.Error,
                LogLevel.Fatal => HangfireDiagnosticLevel.Fatal,
                _ => (HangfireDiagnosticLevel?)null,
            };
            if (level is null) return false;
            var outcome = exception is null ? HangfireDiagnosticOutcome.Operational : HangfireDiagnosticOutcome.Fault;
            Interlocked.Increment(ref _counts[(int)level.Value * OutcomeCount + (int)outcome]);
            return true;
        }
    }
}
