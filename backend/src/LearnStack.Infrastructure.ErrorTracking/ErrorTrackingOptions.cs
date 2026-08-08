namespace LearnStack.Infrastructure.ErrorTracking;

/// <summary>
/// Configuration bound from <c>ErrorTracking:</c> in <c>appsettings.json</c>.
/// Per ADR-0032 § Sub-decision 9 the DSN itself comes from
/// <c>ISecretProvider</c> in non-Dev modes; the composition root reads the
/// secret and overwrites <see cref="SentrySettings.Dsn"/> before
/// <c>SentrySdk.Init</c>.
/// </summary>
public sealed class ErrorTrackingOptions
{
    public const string SectionName = "ErrorTracking";

    public SentrySettings Sentry { get; set; } = new();

    public LocalFileOptions LocalFile { get; set; } = new();
}

public sealed class SentrySettings
{
    public string? Dsn { get; set; }
    public string? Environment { get; set; }
    public double TracesSampleRate { get; set; } = 0.1;
}

public sealed class LocalFileOptions
{
    public string Directory { get; set; } = "/var/learnstack/errors/";
}
