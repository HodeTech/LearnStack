using System.Data.Common;
using System.Globalization;
using System.Runtime.ExceptionServices;
using LearnStack.Infrastructure.Jobs;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobPositiveSdkTests
{
    private void WriteCausalRestartCounts(long reconciles, int finalizers, long ledger, bool forced,
        BackgroundJobRuntime runtime)
    {
        static string Count(long? value) => value is >= 0 and <= 10000
            ? value.Value.ToString(CultureInfo.InvariantCulture) : "out_of_range";
        try
        {
            var control = runtime.Snapshot.Control;
            output.WriteLine("Causal restart counts;reconciles=" + Count(reconciles)
                + ";finalize=" + Count(finalizers) + ";ledger=" + Count(ledger)
                + ";forced=" + (forced ? "yes" : "no")
                + ";leaseElapsed=" + Count(control?.LeaseLossElapsed)
                + ";renewalRefused=" + Count(control?.LeaseLossRenewalRefused) + ".");
        }
        catch (Exception error) when (NativeWaitCatchable(error)) { }
    }

    private sealed record CausalRestartRowObservation(int Normal, int Finalize, long Ledger, long Release,
        long Audits, bool Incomplete, string Outcome, string Policy, string Reason, int Nulls);

    private static CausalRestartRowObservation ReadCausalRestartObservation(DbDataReader row)
    {
        static string Closed(DbDataReader row, int index, params string[] allowed)
        {
            if (row.IsDBNull(index)) return "missing";
            var value = row.GetString(index);
            return allowed.Contains(value, StringComparer.Ordinal) ? value : "other";
        }
        int[] nullable = [0, 1, 2, 3, 11, 13, 14, 15, 16];
        var missing = 0;
        for (var index = 0; index < nullable.Length; index++)
            if (row.IsDBNull(nullable[index])) missing |= 1 << index;
        return new(row.GetInt32(4), row.GetInt32(5), row.GetInt64(9), row.GetInt64(10), row.GetInt64(12),
            row.GetBoolean(8), Closed(row, 0, "succeeded", "failed", "cancelled"),
            Closed(row, 1, "permanent", "transient_repeatable", "transient_not_repeatable"),
            Closed(row, 3, "permanent_failure", "external_retry_forbidden", "retry_exhausted", "finalize_refused", "finalize_exhausted"),
            missing);
    }

    // First body failure survives every later explicit cleanup failure. Timeouts
    // remain observation bounds and never become actual completion evidence.
    private static async Task StopCausalRestartRuntimeAsync(BackgroundJobRuntime runtime, bool bodyFailed,
        string prefix, Action release, Action<string, string> observe)
    {
        ExceptionDispatchInfo? failure = null;
        var boundary = prefix + "_cleanup_request";
        void Preserve(Exception error)
        {
            if (failure is not null) return;
            failure = ExceptionDispatchInfo.Capture(error);
            if (!bodyFailed) observe("failure", boundary);
        }
        try { runtime.RequestStop(); }
        catch (Exception error) when (NativeWaitCatchable(error)) { Preserve(error); }
        boundary = prefix + "_cleanup_release";
        try { release(); }
        catch (Exception error) when (NativeWaitCatchable(error)) { Preserve(error); }
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        boundary = prefix + "_cleanup_stop";
        try { await runtime.StopAsync(cleanup.Token); }
        catch (Exception error) when (NativeWaitCatchable(error)) { Preserve(error); }
        boundary = prefix + "_cleanup_completion";
        try { await runtime.Completion.WaitAsync(cleanup.Token); }
        catch (Exception error) when (NativeWaitCatchable(error)) { Preserve(error); }
        if (runtime.Completion.IsCompletedSuccessfully) boundary = prefix + "_cleanup_complete";
        if (bodyFailed || failure is not null) observe("cleanup", boundary);
        if (!bodyFailed) failure?.Throw();
    }

    // Diagnostic-only projection of already read app-role rows and memory; no
    // additional SQL, provider call, identity or exception text is exported.
    private void WriteCausalRestartObservation(string kind, string boundary, CausalRestartRowObservation? row,
        BackgroundJobRuntime runtime, CausalFinalizerRetryProbe? retry)
    {
        static string Count(long? value, int maximum) => value switch
        {
            null => "unavailable",
            >= 0 when value <= maximum => value.Value.ToString(CultureInfo.InvariantCulture),
            _ => "out_of_range",
        };
        try
        {
            var snapshot = runtime.Snapshot;
            var state = snapshot.State switch
            {
                BackgroundJobRuntimeState.NotStarted => "not_started",
                BackgroundJobRuntimeState.Bootstrapping => "bootstrapping",
                BackgroundJobRuntimeState.Starting => "starting",
                BackgroundJobRuntimeState.Running => "running",
                BackgroundJobRuntimeState.Stopping => "stopping",
                BackgroundJobRuntimeState.Retained => "retained",
                BackgroundJobRuntimeState.Stopped => "stopped",
                _ => "unknown",
            };
            output.WriteLine("Causal restart " + kind + ";boundary=" + boundary + ";runtime=" + state
                + ";executions=" + Count(snapshot.Control?.Executions, 64)
                + ";renewalRefused=" + Count(snapshot.Control?.LeaseLossRenewalRefused, 64)
                + ";leaseElapsed=" + Count(snapshot.Control?.LeaseLossElapsed, 64)
                + ";row=" + (row is null ? "unobserved" : "observed")
                + ";normal=" + Count(row?.Normal, 6) + ";finalize=" + Count(row?.Finalize, 6)
                + ";ledger=" + Count(row?.Ledger, 12) + ";release=" + Count(row?.Release, 64)
                + ";audits=" + Count(row?.Audits, 64)
                + ";incomplete=" + (row is null ? "unavailable" : row.Incomplete ? "yes" : "no")
                + ";outcome=" + (row?.Outcome ?? "unavailable") + ";policy=" + (row?.Policy ?? "unavailable")
                + ";reason=" + (row?.Reason ?? "unavailable") + ";nulls=" + Count(row?.Nulls, 511)
                + ";calls=" + Count(retry?.Calls ?? 0, 6) + ";forced=" + Count(retry?.ForcedConflicts ?? 0, 1) + ".");
        }
        catch (Exception error) when (NativeWaitCatchable(error))
        {
            try { output.WriteLine("Causal restart " + kind + ";boundary=" + boundary + ";snapshot=unavailable."); }
            catch (Exception outputError) when (NativeWaitCatchable(outputError)) { }
        }
    }
}
