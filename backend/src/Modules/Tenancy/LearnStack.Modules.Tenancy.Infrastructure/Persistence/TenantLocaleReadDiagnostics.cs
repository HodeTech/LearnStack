using System.Data.Common;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;

namespace LearnStack.Modules.Tenancy.Infrastructure.Persistence;

/// <summary>Only the owning locale port enters this scope; other Tenancy diagnostics keep their existing behavior.</summary>
internal sealed class TenantLocaleReadScope : IDisposable
{
    private static readonly AsyncLocal<TenantLocaleReadScope?> Current = new();
    private readonly TenantLocaleReadScope? _previous;
    private readonly TenancyDbContext _context;

    private TenantLocaleReadScope(TenancyDbContext context)
    {
        _context = context;
        _previous = Current.Value;
        Current.Value = this;
    }

    internal static TenantLocaleReadScope Enter(TenancyDbContext context) => new(context);
    internal static bool IsActive => Current.Value is not null;
    internal static bool Owns(object? context) => ReferenceEquals(Current.Value?._context, context);
    public void Dispose() => Current.Value = _previous;
}

/// <summary>Shared per existing factory; filters only the locale port's pre-adapter query-failure event.</summary>
internal sealed class TenantLocaleReadLoggerFactory(ILoggerFactory inner) : ILoggerFactory
{
    private static readonly ConditionalWeakTable<ILoggerFactory, TenantLocaleReadLoggerFactory> Wrappers = new();
    internal static ILoggerFactory Wrap(ILoggerFactory factory) => factory is TenantLocaleReadLoggerFactory
        ? factory : Wrappers.GetValue(factory, static current => new(current));

    public ILogger CreateLogger(string categoryName) => new LocaleLogger(inner.CreateLogger(categoryName));
    public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);
    public void Dispose() { }

    private sealed class LocaleLogger(ILogger logger) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => logger.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => logger.IsEnabled(logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (TenantLocaleReadScope.IsActive && eventId.Id == CoreEventId.QueryIterationFailed.Id) return;
            logger.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}

/// <summary>Nonexported source provenance for only the actual locale reader's EF command spans.</summary>
public static class TenantLocaleReadTraceBoundary
{
    private const string MarkerKey = "LearnStack.Tenancy.LocaleReadCommand";
    private const string EfSource = "OpenTelemetry.Instrumentation.EntityFrameworkCore";
    private static readonly object Marker = new();

    internal static void MarkCreatedCommand(Activity? current, Activity? caller)
    {
        for (var activity = current; !ReferenceEquals(activity, caller); activity = activity.Parent)
            if (activity is null || activity.Source.Name != EfSource) return;
        for (var activity = current; !ReferenceEquals(activity, caller); activity = activity!.Parent)
            activity!.SetCustomProperty(MarkerKey, Marker);
    }

    public static void ApplyBeforeExport(Activity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        if (activity.Source.Name == EfSource && ReferenceEquals(activity.GetCustomProperty(MarkerKey), Marker)
            && activity.Status == ActivityStatusCode.Error)
            activity.SetStatus(ActivityStatusCode.Error, "Tenant locale eligibility read failed.");
    }
}

internal sealed class TenantLocaleReadTraceInterceptor : DbCommandInterceptor
{
    private (Guid CommandId, Activity? Caller)? _creation;
    private static readonly Action<ILogger, Exception?> LogReadFailed = LoggerMessage.Define(
        LogLevel.Error, new EventId(3291, "TenantLocaleEligibilityReadFailed"), "Tenant locale eligibility read failed.");

    public override InterceptionResult<DbCommand> CommandCreating(CommandCorrelatedEventData eventData,
        InterceptionResult<DbCommand> result)
    {
        _creation = TenantLocaleReadScope.Owns(eventData.Context) ? (eventData.CommandId, Activity.Current) : null;
        return result;
    }

    public override DbCommand CommandCreated(CommandEndEventData eventData, DbCommand result)
    {
        var started = _creation;
        _creation = null;
        if (TenantLocaleReadScope.Owns(eventData.Context) && started is { } creation && creation.CommandId == eventData.CommandId)
            TenantLocaleReadTraceBoundary.MarkCreatedCommand(Activity.Current, creation.Caller);
        return result;
    }

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData) => Report(eventData);

    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Report(eventData);
        return Task.CompletedTask;
    }

    private static void Report(CommandErrorEventData eventData)
    {
        if (TenantLocaleReadScope.Owns(eventData.Context) && eventData.Context is TenancyDbContext context
            && eventData.CommandSource is CommandSource.LinqQuery or CommandSource.FromSqlQuery
            && eventData.Exception is not OperationCanceledException)
            LogReadFailed(context.GetService<ILoggerFactory>().CreateLogger<TenantLocaleReadTraceInterceptor>(), null);
    }
}
