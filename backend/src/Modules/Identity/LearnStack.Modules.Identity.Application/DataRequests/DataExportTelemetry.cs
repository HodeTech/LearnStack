using System.Diagnostics.Metrics;

namespace LearnStack.Modules.Identity.Application.DataRequests;

/// <summary>How one export job run ended inside its transaction, the closed label set of the export counter.</summary>
public enum DataExportOutcome
{
    /// <summary>The request reached <c>completed</c> with a stored artifact.</summary>
    Completed = 1,

    /// <summary>The bundle passed <c>DataExport:MaxBytes</c>; the request reached <c>failed</c> with <c>export_too_large</c>.</summary>
    FailedTooLarge = 2,

    /// <summary>A missing, foreign-kind, cancelled or already terminal request: a recorded no-op.</summary>
    NoOp = 3,

    /// <summary>The attempt threw; its transaction rolls back and the runtime retries or quarantines it.</summary>
    Failed = 4,
}

/// <summary>How one purge job run ended, the closed label set of the purge counter.</summary>
public enum DataExportPurgeOutcome
{
    /// <summary>The artifact holds no chunk and is recorded <c>purged</c> (by this run or an earlier one).</summary>
    Purged = 1,

    /// <summary>The artifact is absent or was revoked: nothing was left to purge.</summary>
    NoOp = 2,

    /// <summary>The database clock has not reached the expiry; the attempt throws and the runtime retries.</summary>
    NotDue = 3,

    /// <summary>The attempt threw; its transaction rolls back and the runtime retries or quarantines it.</summary>
    Failed = 4,
}

/// <summary>Which download operation a download measurement is about, the closed <c>operation</c> label.</summary>
public enum DataExportDownloadOperation
{
    /// <summary>A ticket mint.</summary>
    Mint = 1,

    /// <summary>A chunk read through a ticket.</summary>
    Chunk = 2,
}

/// <summary>How one download operation ended, the closed <c>outcome</c> label of the download counter.</summary>
public enum DataExportDownloadOutcome
{
    /// <summary>A ticket was minted, or a chunk was served.</summary>
    Served = 1,

    /// <summary>Refused for any reason but the two below: absent, unreadable, foreign, unbound or invalid.</summary>
    Refused = 2,

    /// <summary>A mint refused by a ticket bound (<c>data_export_ticket_limit</c>).</summary>
    Limit = 3,

    /// <summary>The artifact's key is no longer in the ring, or a stored chunk could not be opened.</summary>
    CustodyFailed = 4,
}

/// <summary>
/// The export workflow's metrics (<see href="../../../../../docs/standards/10-observability.md">Standard 10</see>,
/// <see href="../../../../../docs/decisions/0107-data-request-fulfillment-and-account-closure.md">ADR-0107 § 2–§ 4</see>).
/// </summary>
/// <remarks>
/// <para>
/// Names follow the platform convention (<c>learnstack_*_total</c>, <c>_bytes</c>, <c>_seconds</c>), the meter is
/// covered by the host's <c>learnstack.*</c> subscription, and every label is a closed word from the enumerations
/// above: never a tenant, organization, request, artifact, person or collection name.
/// </para>
/// <para>
/// <b>Recorded inside the transaction.</b> The job runtime owns the commit, so an outcome here is what the handler
/// reached; a commit that then fails is the runtime's <c>learnstack_job_failed_total</c>, and the retry records again.
/// A download outcome is likewise what the handler reached before its request's MUST audit row and commit.
/// </para>
/// </remarks>
public static class DataExportTelemetry
{
    /// <summary>The meter name; covered by the host's <c>learnstack.*</c> subscription.</summary>
    public const string MeterName = "learnstack.data_export";

    /// <summary>Export job runs by <c>outcome</c> (<c>completed</c>, <c>failed_too_large</c>, <c>noop</c>, <c>failed</c>).</summary>
    public const string ExportCounterName = "learnstack_data_export_total";

    /// <summary>The plaintext size of each stored bundle (no label).</summary>
    public const string BundleBytesHistogramName = "learnstack_data_export_bundle_bytes";

    /// <summary>Export job run duration by <c>outcome</c>.</summary>
    public const string DurationHistogramName = "learnstack_data_export_duration_seconds";

    /// <summary>Collections a stored bundle states as truncated at their cap (no label).</summary>
    public const string TruncationCounterName = "learnstack_data_export_truncations_total";

    /// <summary>Purge job runs by <c>outcome</c> (<c>purged</c>, <c>noop</c>, <c>not_due</c>, <c>failed</c>).</summary>
    public const string PurgeCounterName = "learnstack_data_export_purges_total";

    /// <summary>
    /// Download operations by <c>operation</c> (<c>mint</c>, <c>chunk</c>) and <c>outcome</c> (<c>served</c>,
    /// <c>refused</c>, <c>limit</c>, <c>custody_failed</c>).
    /// </summary>
    public const string DownloadCounterName = "learnstack_data_export_downloads_total";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Exports =
        Meter.CreateCounter<long>(ExportCounterName, description: "Data export job runs by outcome.");

    private static readonly Histogram<long> BundleBytes =
        Meter.CreateHistogram<long>(BundleBytesHistogramName, unit: "By", description: "Plaintext size of each stored export bundle.");

    private static readonly Histogram<double> Durations =
        Meter.CreateHistogram<double>(DurationHistogramName, unit: "s", description: "Data export job run duration by outcome.");

    private static readonly Counter<long> Truncations =
        Meter.CreateCounter<long>(TruncationCounterName, description: "Collections stored export bundles state as truncated.");

    private static readonly Counter<long> Purges =
        Meter.CreateCounter<long>(PurgeCounterName, description: "Data export purge job runs by outcome.");

    private static readonly Counter<long> Downloads =
        Meter.CreateCounter<long>(DownloadCounterName, description: "Data export ticket mints and chunk reads by operation and outcome.");

    /// <summary>The closed label of an export outcome.</summary>
    public static string Label(DataExportOutcome outcome) => outcome switch
    {
        DataExportOutcome.Completed => "completed",
        DataExportOutcome.FailedTooLarge => "failed_too_large",
        DataExportOutcome.NoOp => "noop",
        _ => "failed",
    };

    /// <summary>The closed label of a purge outcome.</summary>
    public static string Label(DataExportPurgeOutcome outcome) => outcome switch
    {
        DataExportPurgeOutcome.Purged => "purged",
        DataExportPurgeOutcome.NoOp => "noop",
        DataExportPurgeOutcome.NotDue => "not_due",
        _ => "failed",
    };

    /// <summary>The closed label of a download operation.</summary>
    public static string Label(DataExportDownloadOperation operation) => operation == DataExportDownloadOperation.Mint ? "mint" : "chunk";

    /// <summary>The closed label of a download outcome.</summary>
    public static string Label(DataExportDownloadOutcome outcome) => outcome switch
    {
        DataExportDownloadOutcome.Served => "served",
        DataExportDownloadOutcome.Limit => "limit",
        DataExportDownloadOutcome.CustodyFailed => "custody_failed",
        _ => "refused",
    };

    /// <summary>Records one export run; the size and truncations only for a stored bundle.</summary>
    public static void Export(DataExportOutcome outcome, TimeSpan duration, long? bundleBytes = null, int truncatedCollections = 0)
    {
        var label = new KeyValuePair<string, object?>("outcome", Label(outcome));
        Exports.Add(1, label);
        Durations.Record(duration.TotalSeconds, label);
        if (outcome == DataExportOutcome.Completed && bundleBytes is { } bytes)
        {
            BundleBytes.Record(bytes);
            if (truncatedCollections > 0)
            {
                Truncations.Add(truncatedCollections);
            }
        }
    }

    /// <summary>Records one purge run.</summary>
    public static void Purge(DataExportPurgeOutcome outcome) =>
        Purges.Add(1, new KeyValuePair<string, object?>("outcome", Label(outcome)));

    /// <summary>Records one download operation, as the handler reached it inside the request's transaction.</summary>
    public static void Download(DataExportDownloadOperation operation, DataExportDownloadOutcome outcome) =>
        Downloads.Add(1, new KeyValuePair<string, object?>("operation", Label(operation)),
            new KeyValuePair<string, object?>("outcome", Label(outcome)));
}
