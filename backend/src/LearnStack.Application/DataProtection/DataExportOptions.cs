using System.Globalization;
using LearnStack.SharedKernel.DataProtection;

namespace LearnStack.Application.DataProtection;

/// <summary>
/// The export bundle ceiling, <c>DataExport:MaxBytes</c>: 16 MiB by default and 64 MiB at most
/// (<see href="../../../../docs/decisions/0107-data-request-fulfillment-and-account-closure.md">ADR-0107 § 4</see>).
/// </summary>
/// <remarks>
/// Fixed for the process and validated when the composition root builds it, so a misconfigured ceiling stops
/// the host at startup instead of failing the first export. The ceiling is not a secret.
/// </remarks>
public sealed record DataExportOptions
{
    public const string MaxBytesKey = "DataExport:MaxBytes";

    /// <exception cref="ArgumentOutOfRangeException">The ceiling is outside the supported range.</exception>
    public DataExportOptions(long maxBytes)
    {
        if (maxBytes is < DataRequestLimits.MinimumBundleBytes or > DataRequestLimits.MaximumBundleBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes), "The export bundle ceiling is outside its supported range.");
        }

        MaxBytes = maxBytes;
    }

    /// <summary>The largest bundle, in plaintext UTF-8 bytes, an export may produce.</summary>
    public long MaxBytes { get; }

    public static DataExportOptions Default { get; } = new(DataRequestLimits.DefaultMaximumBundleBytes);

    /// <summary>Reads the configured value: absent means the default; anything but a plain integer in range fails.</summary>
    /// <exception cref="InvalidOperationException">The value is present and invalid.</exception>
    public static DataExportOptions FromSetting(string? value)
    {
        if (value is null)
        {
            return Default;
        }

        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var maxBytes)
            || maxBytes is < DataRequestLimits.MinimumBundleBytes or > DataRequestLimits.MaximumBundleBytes)
        {
            throw new InvalidOperationException(
                $"{MaxBytesKey} must be a whole number of bytes from {DataRequestLimits.MinimumBundleBytes} to {DataRequestLimits.MaximumBundleBytes}.");
        }

        return new DataExportOptions(maxBytes);
    }
}
