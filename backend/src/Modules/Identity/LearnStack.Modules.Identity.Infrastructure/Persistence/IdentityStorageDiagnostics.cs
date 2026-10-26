using LearnStack.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

/// <summary>Unexpected database failures retain a fixed operational signal, never a provider payload.</summary>
public sealed class IdentityStorageProviderException()
    : ProviderException("identity-storage", "Identity storage operation failed.", isClientError: false);

/// <summary>Replaces Identity's pre-adapter EF save-failure message under ADR-0073 G19.</summary>
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

    private static readonly Action<ILogger, Exception?> LogSaveFailed = LoggerMessage.Define(
        LogLevel.Error, new EventId(3191, "IdentityStorageSaveFailed"), "Identity storage save failed.");
}
