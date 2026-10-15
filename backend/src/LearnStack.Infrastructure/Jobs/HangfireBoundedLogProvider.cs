using Hangfire.Logging;

namespace LearnStack.Infrastructure.Jobs;

public enum HangfireDiagnosticLevel { Trace, Debug, Information, Warning, Error, Fatal }
public enum HangfireDiagnosticOutcome { Operational, Fault }
public readonly record struct HangfireDiagnostic(HangfireDiagnosticLevel Level, HangfireDiagnosticOutcome Outcome);

/// <summary>No arbitrary message factory, logger name or exception survives this boundary.</summary>
public sealed class HangfireBoundedLogProvider(Action<HangfireDiagnostic> observe) : ILogProvider
{
    private readonly ILog _logger = new BoundedLog(observe ?? throw new ArgumentNullException(nameof(observe)));
    public ILog GetLogger(string name) => _logger;

    private sealed class BoundedLog(Action<HangfireDiagnostic> observe) : ILog
    {
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
            observe(new(level.Value, exception is null ? HangfireDiagnosticOutcome.Operational : HangfireDiagnosticOutcome.Fault));
            return true;
        }
    }
}
