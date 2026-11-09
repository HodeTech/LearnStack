using System.Data.Common;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;

namespace LearnStack.Modules.Customization.Infrastructure.Persistence;

/// <summary>Fixed failure contract without a provider exception or authored JSON in its inner chain.</summary>
public sealed class CustomFieldDefinitionStorageProviderException()
    : ProviderException("custom-field-definition-storage", "Custom field definition storage failed.", isClientError: false);

/// <summary>Only the new definition adapters enter; existing customization ports keep their diagnostics.</summary>
internal sealed class CustomFieldDefinitionStorageScope : IDisposable
{
    private static readonly AsyncLocal<CustomFieldDefinitionStorageScope?> Current = new();
    private readonly CustomFieldDefinitionStorageScope? _previous;
    private readonly CustomizationDbContext _context;

    private CustomFieldDefinitionStorageScope(CustomizationDbContext context)
    {
        _context = context;
        _previous = Current.Value;
        Current.Value = this;
    }

    internal static CustomFieldDefinitionStorageScope Enter(CustomizationDbContext context) => new(context);
    internal static bool IsActive => Current.Value is not null;
    internal static bool Owns(object? context) => ReferenceEquals(Current.Value?._context, context);
    public void Dispose() => Current.Value = _previous;
}

internal static class CustomFieldDefinitionStorageBoundary
{
    internal static async Task<T> ReadAsync<T>(CustomizationDbContext db, IUnitOfWork unit, Func<Task<T>> read)
    {
        using var boundary = CustomFieldDefinitionStorageScope.Enter(db);
        try { return await read(); }
        catch (Exception failure) when (IsProviderOrPayloadFailure(failure))
        {
            Refuse(db, unit);
            throw new CustomFieldDefinitionStorageProviderException();
        }
    }

    internal static async Task WriteAsync(CustomizationDbContext db, IUnitOfWork unit, Func<Task> write)
    {
        using var boundary = CustomFieldDefinitionStorageScope.Enter(db);
        try { await write(); }
        catch (AggregateConcurrencyException)
        {
            MarkRollback(unit);
            throw new AggregateConcurrencyException("Custom field definition changed after it was read.");
        }
        catch (AggregateConflictException)
        {
            MarkRollback(unit);
            // The handler only needs the closed conflict outcome, never a provider's constraint payload.
            throw new AggregateConflictException("Custom field definition conflicts with retained history.");
        }
        catch (Exception failure) when (IsProviderOrPayloadFailure(failure))
        {
            Refuse(db, unit);
            throw new CustomFieldDefinitionStorageProviderException();
        }
        catch
        {
            MarkRollback(unit);
            throw;
        }
    }

    private static bool IsProviderOrPayloadFailure(Exception failure) => failure switch
    {
        OperationCanceledException => false,
        DbException or TimeoutException or JsonException => true,
        DbUpdateException { InnerException: DbException or TimeoutException or JsonException } => true,
        InvalidOperationException { InnerException: DbException or TimeoutException or JsonException } => true,
        InvalidOperationException { InnerException: DbUpdateException { InnerException: DbException or TimeoutException or JsonException } } => true,
        _ => false,
    };

    private static void Refuse(CustomizationDbContext db, IUnitOfWork unit)
    {
        MarkRollback(unit);
        LogFailed(db.GetService<ILoggerFactory>().CreateLogger<CustomFieldDefinitionStorageProviderException>(), null);
    }
    private static void MarkRollback(IUnitOfWork unit)
    { if (unit.HasActiveTransaction) unit.MarkRollbackOnly(); }
    private static readonly Action<ILogger, Exception?> LogFailed = LoggerMessage.Define(
        LogLevel.Error, new EventId(3391, "CustomFieldDefinitionStorageFailed"), "Custom field definition storage failed.");
}

/// <summary>Configures only source-owned, per-operation definition diagnostics on the shared module context.</summary>
public static class CustomFieldDefinitionDiagnostics
{
    public static void Configure(DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        var core = optionsBuilder.Options.FindExtension<CoreOptionsExtension>();
        var loggers = core?.LoggerFactory ?? core?.ApplicationServiceProvider?.GetService(typeof(ILoggerFactory)) as ILoggerFactory;
        if (loggers is not null) optionsBuilder.UseLoggerFactory(CustomFieldDefinitionLoggerFactory.Wrap(loggers));
        optionsBuilder.AddInterceptors(new CustomFieldDefinitionTraceInterceptor());
    }
}

internal sealed class CustomFieldDefinitionLoggerFactory(ILoggerFactory inner) : ILoggerFactory
{
    private static readonly ConditionalWeakTable<ILoggerFactory, CustomFieldDefinitionLoggerFactory> Wrappers = new();
    internal static ILoggerFactory Wrap(ILoggerFactory factory) => factory is CustomFieldDefinitionLoggerFactory
        ? factory : Wrappers.GetValue(factory, static current => new(current));
    public ILogger CreateLogger(string categoryName) => new DefinitionLogger(inner.CreateLogger(categoryName), categoryName);
    public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);
    public void Dispose() { }

    private sealed class DefinitionLogger(ILogger logger, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => logger.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => logger.IsEnabled(logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // Exclude the complete payload-bearing event, including successful parameter logs if a
            // diagnostic host enabled sensitive-data logging. Fixed failure signals remain above.
            if (CustomFieldDefinitionStorageScope.IsActive && (categoryName == DbLoggerCategory.ChangeTracking.Name
                || eventId.Id == CoreEventId.QueryIterationFailed.Id
                || eventId.Id == CoreEventId.SaveChangesFailed.Id || eventId.Id == RelationalEventId.CommandError.Id
                || eventId.Id == RelationalEventId.CommandExecuting.Id || eventId.Id == RelationalEventId.CommandExecuted.Id)) return;
            logger.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}

/// <summary>Nonexported provenance confines fixed failure descriptions to actual definition EF spans.</summary>
public static class CustomFieldDefinitionTraceBoundary
{
    private const string MarkerKey = "LearnStack.Customization.CustomFieldDefinitionCommand";
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
            activity.SetStatus(ActivityStatusCode.Error, "Custom field definition storage failed.");
    }
}

internal sealed class CustomFieldDefinitionTraceInterceptor : DbCommandInterceptor
{
    private (Guid CommandId, Activity? Caller)? _creation;
    public override InterceptionResult<DbCommand> CommandCreating(CommandCorrelatedEventData eventData, InterceptionResult<DbCommand> result)
    {
        _creation = CustomFieldDefinitionStorageScope.Owns(eventData.Context) ? (eventData.CommandId, Activity.Current) : null;
        return result;
    }
    public override DbCommand CommandCreated(CommandEndEventData eventData, DbCommand result)
    {
        var creation = _creation;
        _creation = null;
        if (CustomFieldDefinitionStorageScope.Owns(eventData.Context) && creation is { } started && started.CommandId == eventData.CommandId)
            CustomFieldDefinitionTraceBoundary.MarkCreatedCommand(Activity.Current, started.Caller);
        return result;
    }
}
