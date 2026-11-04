using System.Data.Common;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Contracts.Branding;
using LearnStack.Modules.Tenancy.Application.Contracts.Locales;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace LearnStack.Modules.Tenancy.Infrastructure.Persistence;

/// <summary>Only the new administration adapters enter this source-owned failure boundary.</summary>
internal sealed class TenancyAdministrationStorageScope : IDisposable
{
    private static readonly AsyncLocal<TenancyAdministrationStorageScope?> Current = new();
    private readonly TenancyAdministrationStorageScope? _previous;
    private readonly TenancyDbContext _context;

    private TenancyAdministrationStorageScope(TenancyDbContext context)
    {
        _context = context;
        _previous = Current.Value;
        Current.Value = this;
    }

    internal static TenancyAdministrationStorageScope Enter(TenancyDbContext context) => new(context);
    internal static bool IsActive => Current.Value is not null;
    internal static bool Owns(object? context) => ReferenceEquals(Current.Value?._context, context);
    internal static bool IsHumanWrite(IOperationInvocationContext? invocations) =>
        invocations?.CurrentInvocation is
        {
            IsActive: true, EntryKind: OperationEntryKind.Application,
            TrustedSource: null, Policy.Kind: OperationPolicyKind.Protected
        } invocation
        && invocation.Policy.RequestType == invocation.Operation.GetType()
        && invocation.Operation is CreateManagedOrganizationCommand or RenameOrganizationCommand or ArchiveOrganizationCommand
            or SelectDefaultOrganizationCommand or AddManagedTenantLocaleCommand or SetManagedDefaultTenantLocaleCommand
            or SetTenantLocaleEnabledCommand or RemoveTenantLocaleCommand or SetManagedTenantBrandingCommand;
    public void Dispose() => Current.Value = _previous;
}

internal static class TenancyAdministrationStorageBoundary
{
    internal static async Task<T> ReadAsync<T>(Func<Task<T>> read)
    {
        try { return await read(); }
        catch (InvalidOperationException failure) when (failure.InnerException is DbException or TimeoutException)
        {
            throw new TenancyAdministrationStorageException(TenancyAdministrationStorageFailure.Provider);
        }
        catch (Exception failure) when (failure is DbException or TimeoutException)
        {
            throw new TenancyAdministrationStorageException(TenancyAdministrationStorageFailure.Provider);
        }
    }

    internal static async Task WriteAsync(TenancyDbContext db, IUnitOfWork? unit,
        IOperationInvocationContext? invocations, Func<Task> write)
    {
        // Existing Seed and direct store contracts keep their established failure behavior.
        if (!TenancyAdministrationStorageScope.IsHumanWrite(invocations)) { await write(); return; }
        using var boundary = TenancyAdministrationStorageScope.Enter(db);
        try { await write(); }
        catch (AggregateConcurrencyException)
        {
            unit?.MarkRollbackOnly();
            throw new TenancyAdministrationStorageException(TenancyAdministrationStorageFailure.Conflict);
        }
        catch (AggregateConflictException conflict)
        {
            unit?.MarkRollbackOnly();
            throw new TenancyAdministrationStorageException(conflict.ConstraintName == "ux_organizations_tenant_id_slug"
                ? TenancyAdministrationStorageFailure.SlugTaken : TenancyAdministrationStorageFailure.Conflict);
        }
        catch (Exception failure) when (Provider(failure) is not null)
        {
            unit?.MarkRollbackOnly();
            throw new TenancyAdministrationStorageException(Classify(Provider(failure)!));
        }
        catch
        {
            unit?.MarkRollbackOnly();
            throw;
        }
    }

    private static Exception? Provider(Exception failure) => failure switch
    {
        OperationCanceledException => null,
        DbException or TimeoutException => failure,
        DbUpdateException { InnerException: DbException or TimeoutException } update => update.InnerException,
        InvalidOperationException { InnerException: DbException or TimeoutException } wrapped => wrapped.InnerException,
        InvalidOperationException { InnerException: DbUpdateException { InnerException: DbException or TimeoutException } update } => update.InnerException,
        _ => null,
    };

    private static TenancyAdministrationStorageFailure Classify(Exception failure) => failure switch
    {
        PostgresException { SqlState: "P0311" } => TenancyAdministrationStorageFailure.Plan,
        PostgresException { SqlState: "P0312" } => TenancyAdministrationStorageFailure.Invariant,
        PostgresException
        {
            SqlState: PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.SerializationFailure
            or PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.QueryCanceled
        } => TenancyAdministrationStorageFailure.Conflict,
        _ => TenancyAdministrationStorageFailure.Provider,
    };
}

internal sealed class TenancyAdministrationLoggerFactory(ILoggerFactory inner) : ILoggerFactory
{
    private static readonly ConditionalWeakTable<ILoggerFactory, TenancyAdministrationLoggerFactory> Wrappers = new();
    internal static ILoggerFactory Wrap(ILoggerFactory factory) => factory is TenancyAdministrationLoggerFactory
        ? factory : Wrappers.GetValue(factory, static current => new(current));
    public ILogger CreateLogger(string categoryName) => new StorageLogger(inner.CreateLogger(categoryName));
    public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);
    public void Dispose() { }

    private sealed class StorageLogger(ILogger logger) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => logger.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => logger.IsEnabled(logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (TenancyAdministrationStorageScope.IsActive
                && (eventId.Id == CoreEventId.QueryIterationFailed.Id || eventId.Id == CoreEventId.SaveChangesFailed.Id
                    || eventId.Id == RelationalEventId.CommandError.Id)) return;
            logger.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}

/// <summary>Nonexported provenance confines fixed span descriptions to this port's actual EF commands.</summary>
public static class TenancyAdministrationTraceBoundary
{
    private const string MarkerKey = "LearnStack.Tenancy.AdministrationCommand";
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
            activity.SetStatus(ActivityStatusCode.Error, "Tenancy administration storage failed.");
    }
}

internal sealed class TenancyAdministrationTraceInterceptor : DbCommandInterceptor
{
    private (Guid CommandId, Activity? Caller)? _creation;
    private static readonly Action<ILogger, Exception?> LogFailed = LoggerMessage.Define(
        LogLevel.Error, new EventId(3292, "TenancyAdministrationStorageFailed"), "Tenancy administration storage failed.");

    public override InterceptionResult<DbCommand> CommandCreating(CommandCorrelatedEventData eventData, InterceptionResult<DbCommand> result)
    {
        _creation = TenancyAdministrationStorageScope.Owns(eventData.Context) ? (eventData.CommandId, Activity.Current) : null;
        return result;
    }

    public override DbCommand CommandCreated(CommandEndEventData eventData, DbCommand result)
    {
        var creation = _creation;
        _creation = null;
        if (TenancyAdministrationStorageScope.Owns(eventData.Context) && creation is { } started && started.CommandId == eventData.CommandId)
            TenancyAdministrationTraceBoundary.MarkCreatedCommand(Activity.Current, started.Caller);
        return result;
    }

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData) => Report(eventData);
    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Report(eventData);
        return Task.CompletedTask;
    }

    private static void Report(CommandErrorEventData eventData)
    {
        if (TenancyAdministrationStorageScope.Owns(eventData.Context) && eventData.Context is TenancyDbContext context
            && eventData.Exception is not OperationCanceledException)
            LogFailed(context.GetService<ILoggerFactory>().CreateLogger<TenancyAdministrationTraceInterceptor>(), null);
    }
}
