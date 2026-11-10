using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using LearnStack.SharedKernel.Audit;

namespace LearnStack.Infrastructure.Persistence;

/// <summary>
/// Confines driver diagnostics while a specifically admitted custom-field owner
/// completes. Provider messages and notices can contain complete authored values.
/// </summary>
public static class CustomFieldCommitDiagnostics
{
    private static readonly AsyncLocal<Frame?> Current = new();
    private static readonly ConditionalWeakTable<ILoggerFactory, ScopedLoggerFactory> Wrappers = new();

    /// <summary>Enter only for a P03-5 operation's owned physical commit.</summary>
    public static IDisposable Enter()
    {
        var frame = new Frame(Current.Value);
        Current.Value = frame;
        return frame;
    }

    /// <summary>Wrap the factory supplied to the application's Npgsql data source.</summary>
    public static ILoggerFactory Wrap(ILoggerFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return factory is ScopedLoggerFactory ? factory : Wrappers.GetValue(factory, static inner => new(inner));
    }

    private sealed class Frame(Frame? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }

    private sealed class ScopedLoggerFactory(ILoggerFactory inner) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) =>
            new ScopedLogger(inner.CreateLogger(categoryName), categoryName.StartsWith("Npgsql", StringComparison.Ordinal));
        public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);
        public void Dispose() { }
    }

    private sealed class ScopedLogger(ILogger inner, bool isDriver) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (isDriver && (Current.Value is not null || ConfidentialAuditDiagnostics.IsActive)) return;
            inner.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}
