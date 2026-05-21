using System.Text.Json;
using LearnStack.SharedKernel.Observability;
using Microsoft.Extensions.Logging;

namespace LearnStack.Infrastructure.ErrorTracking;

/// <summary>
/// <see cref="IErrorTrackingProvider"/> implementation that writes each
/// captured exception as a JSON envelope under the configured directory.
/// Selected by the composition root when
/// <c>DeploymentMode.SelfHostedAirGapped</c> — the runbook explains how to
/// ship those files off-network later if the customer ever wants to.
/// </summary>
/// <remarks>
/// File names use a sortable timestamp + the trace id when present, so
/// operators see new captures at the bottom of the directory listing.
/// Failures of the write itself (full disk, permissions) are logged and
/// swallowed; air-gapped capture is best-effort by definition.
/// </remarks>
internal sealed class LocalFileErrorTracker : IErrorTrackingProvider
{
    private readonly string _directory;
    private readonly ILogger<LocalFileErrorTracker> _logger;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
    };

    public LocalFileErrorTracker(string directory, ILogger<LocalFileErrorTracker> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(logger);

        _directory = directory;
        _logger = logger;
        Directory.CreateDirectory(_directory);
    }

    public async ValueTask CaptureAsync(
        Exception exception,
        CapturedContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(context);

        var envelope = new
        {
            timestamp = DateTimeOffset.UtcNow,
            exception = new
            {
                type = exception.GetType().FullName,
                message = exception.Message,
                stackTrace = exception.StackTrace,
                inner = exception.InnerException?.GetType().FullName,
            },
            context.CorrelationId,
            context.RequestPath,
            context.RequestMethod,
            context.TenantId,
            context.OrganizationId,
            context.UserId,
            context.ModuleName,
            additionalTags = context.AdditionalTags,
        };

        var safeCorrelation = SanitiseForFileName(context.CorrelationId);
        var fileName = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}-{safeCorrelation}.json";
        var path = Path.Combine(_directory, fileName);

        try
        {
            await using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(stream, envelope, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Air-gapped capture is best-effort; swallow the write failure but log it.
        catch (Exception writeFailure)
#pragma warning restore CA1031
        {
            LogWriteFailure(_logger, path, writeFailure);
        }
    }

    private static string SanitiseForFileName(string? correlationId)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            return "noid";
        }

        Span<char> buffer = stackalloc char[correlationId.Length];
        for (var i = 0; i < correlationId.Length; i++)
        {
            var c = correlationId[i];
            buffer[i] = char.IsLetterOrDigit(c) || c == '-' ? c : '_';
        }

        return new string(buffer);
    }

    private static readonly Action<ILogger, string, Exception?> LogWriteFailure =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1, nameof(LogWriteFailure)),
            "LocalFileErrorTracker failed to write capture envelope to {Path}.");
}
