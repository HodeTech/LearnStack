using System.Data.Common;
using System.Diagnostics;
using LearnStack.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

/// <summary>Unexpected database failures retain a fixed operational signal, never a provider payload.</summary>
public sealed class IdentityStorageProviderException()
    : ProviderException("identity-storage", "Identity storage operation failed.", isClientError: false);

/// <summary>Source-owned, nonexported classification of Identity's actual EF command span.</summary>
public static class IdentityStorageTraceBoundary
{
    private const string MarkerKey = "LearnStack.Identity.StorageCommand";
    private static readonly object Marker = new();
    private const string EfSource = "OpenTelemetry.Instrumentation.EntityFrameworkCore";

    internal static void MarkCurrentCommand()
    {
        if (Activity.Current is { } activity && activity.Source.Name == EfSource)
            activity.SetCustomProperty(MarkerKey, Marker);
    }

    /// <summary>The pinned EF instrumentation ends a failed span before the adapter can translate its exception.</summary>
    public static void ApplyBeforeExport(Activity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        if (activity.Source.Name == EfSource && ReferenceEquals(activity.GetCustomProperty(MarkerKey), Marker)
            && activity.Status == ActivityStatusCode.Error)
            activity.SetStatus(ActivityStatusCode.Error, "Identity storage command failed.");
    }
}

internal sealed class IdentityStorageTraceInterceptor : DbCommandInterceptor
{
    internal static readonly IdentityStorageTraceInterceptor Instance = new();

    public override DbCommand CommandInitialized(CommandEndEventData eventData, DbCommand result)
    {
        // EF's CommandCreated diagnostic has started this command's Activity.
        // Mark that object, not an ambient parent or a SQL-text classification.
        if (eventData.Context is IdentityDbContext) IdentityStorageTraceBoundary.MarkCurrentCommand();
        return result;
    }

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData) =>
        IdentityStorageDiagnostics.ReportReadFailure(eventData);

    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        IdentityStorageDiagnostics.ReportReadFailure(eventData);
        return Task.CompletedTask;
    }
}

/// <summary>Replaces Identity's pre-adapter EF failure messages under ADR-0073 G19.</summary>
internal sealed class IdentityStorageDiagnostics : SaveChangesInterceptor
{
    internal static readonly IdentityStorageDiagnostics Instance = new();

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => Report(eventData);

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Report(eventData);
        return Task.CompletedTask;
    }

    private static void Report(DbContextErrorEventData eventData)
    {
        if (eventData.Context is { } context)
            LogSaveFailed(context.GetService<ILoggerFactory>().CreateLogger<IdentityStorageDiagnostics>(), null);
    }

    internal static void ReportReadFailure(CommandErrorEventData eventData)
    {
        // Classify the EF source, not SQL text. All Identity query adapters keep
        // an operational signal; existing provider/refusal/cancellation handling
        // receives the original exception unchanged and remains its sole owner.
        if (eventData.Context is IdentityDbContext context
            && eventData.CommandSource is CommandSource.LinqQuery or CommandSource.FromSqlQuery
            && eventData.Exception is not OperationCanceledException)
            LogReadFailed(context.GetService<ILoggerFactory>().CreateLogger<IdentityStorageDiagnostics>(), null);
    }

    private static readonly Action<ILogger, Exception?> LogSaveFailed = LoggerMessage.Define(
        LogLevel.Error, new EventId(3191, "IdentityStorageSaveFailed"), "Identity storage save failed.");

    private static readonly Action<ILogger, Exception?> LogReadFailed = LoggerMessage.Define(
        LogLevel.Error, new EventId(3192, "IdentityStorageReadFailed"), "Identity storage read failed.");
}
