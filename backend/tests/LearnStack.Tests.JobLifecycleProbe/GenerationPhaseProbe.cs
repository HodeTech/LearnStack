using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using Npgsql;

namespace LearnStack.Tests.JobLifecycleProbe;

/// <summary>Qualified child supervision; synthetic fault placement, actual production generation ownership.</summary>
internal static class GenerationPhaseProbe
{
    internal const string ReceiptPrefix = "LearnStackGenerationReceipt:";
    internal const string EventPrefix = "LearnStackGenerationEvent:";
    private const string Root = "/proof";
    private static readonly TimeSpan ChildBudget = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DirectCallbackWait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan OwnedProcessJoinBudget = TimeSpan.FromSeconds(10);
    private static readonly string[] DirectCallbackEvents =
    [
        "armed", "fault_site", "first_announcement", "required_thread_held", "private_callback_held",
        "private_cancel_retained", "finite_host_wait_ended", "main_returning_unresolved", "direct_core_callback_entered",
    ];
    private static readonly string[] Scenarios =
    [
        "gen_constructor_before", "gen_constructor_after", "gen_required_before_capture",
        "gen_supplied_after_capture", "gen_post_builder", "gen_notify_succeeded", "gen_wait_entry",
        "gen_dispose_second_announce", "gen_dispose_outer_complete", "gen_announce_log",
        "gen_host_stop", "gen_final_exit", "gen_process_exit", "gen_process_exit_direct",
        "gen_stall_constructor", "gen_stall_required", "gen_stall_supplied", "gen_stall_postbuilder",
    ];
    internal static readonly string[] Events =
    [
        "armed", "fault_site", "first_announcement", "preannounce_fault", "notify_entered",
        "required_thread_held", "capture_dispose", "removal_entered", "required_dispose_fault",
        "second_announcement", "outer_failure", "outer_dispatcher_fault", "normal_cleanup",
        "host_stopped", "final_selected", "final_observed", "terminal_retained",
        "startup_stalled", "late_handle", "late_progress", "late_cleanup",
        "capture_dispose_returned", "second_announcement_returned", "constructor_boundary_returned",
        "outer_completion_returned", "fatal_gate_returned",
        "private_callback_held", "private_cancel_retained", "finite_host_wait_ended",
        "private_callback_joined", "main_returning_unresolved", "direct_core_callback_entered",
    ];

    internal static bool IsScenario(string value) => Scenarios.Contains(value, StringComparer.Ordinal);

    internal static bool ValidMutation(string scenario, string mutation) => mutation switch
    {
        "none" => true,
        "sink" or "capture_dispose" => scenario == "gen_post_builder",
        "constructor" => scenario == "gen_constructor_before",
        "second_announce" => scenario == "gen_dispose_second_announce",
        "outer_completion" => scenario == "gen_dispose_outer_complete",
        _ => false,
    };

    internal static async Task<int> RunAsync(string mode, string scenario, string mutation)
    {
        if (mode is not ("supervise" or "child" or "child-twin") || !ValidMutation(scenario, mutation)) throw Refused();
        var phase = "envelope";
        try
        {
            var envelope = await ReadEnvelopeAsync().ConfigureAwait(false);
            if (mode != "supervise")
            {
                phase = "child";
                return await GenerationPhaseDriver.RunAsync(scenario, mode == "child-twin", envelope.ConnectionString, mutation)
                    .ConfigureAwait(false);
            }

            phase = "twin";
            var twin = await ObserveChildAsync(scenario, twin: true, envelope, mutation).ConfigureAwait(false);
            var twinPassed = twin.Exited && twin.ExitCode == 0 && !twin.TimedOut && !twin.Diagnostic
                && twin.Finally && twin.ProcessExit && !twin.ProtocolInvalid
                && !twin.ExternalKill && !twin.OwnedProcessJoined
                && twin.Events.Contains("armed", StringComparer.Ordinal)
                && twin.Events.Contains("fault_site", StringComparer.Ordinal)
                && twin.Events.Contains("normal_cleanup", StringComparer.Ordinal)
                && ArtifactsAllowed(scenario);
            if (!twinPassed)
            {
                Emit(new PhaseReceipt(1, "benign_control_failed", scenario,
                    RuntimeInformation.FrameworkDescription, RuntimeInformation.ProcessArchitecture.ToString(),
                    File.ReadAllText("/proc/sys/kernel/core_pattern").Trim(), false, twin, null, false, -1, -1));
                return 79;
            }

            phase = "fault";
            var observed = await ObserveChildAsync(scenario, twin: false, envelope, mutation).ConfigureAwait(false);
            var artifacts = ArtifactsAllowed(scenario);
            phase = "rows";
            var rows = observed.Exited ? await ReadRowsAsync(envelope.ConnectionString).ConfigureAwait(false) : (-1L, -1L);
            phase = "receipt";
            Emit(new PhaseReceipt(1, "none", scenario, RuntimeInformation.FrameworkDescription,
                RuntimeInformation.ProcessArchitecture.ToString(), File.ReadAllText("/proc/sys/kernel/core_pattern").Trim(),
                true, twin, observed, artifacts, rows.Item1, rows.Item2));
            // A dirty hang is a decisive control only after explicit owned kill
            // and actual process/output join. It is never a native-exit success.
            var dirtyJoined = scenario == "gen_process_exit_direct" && QualifiedDirectCallbackHang(observed);
            return observed.Exited && artifacts && (!observed.TimedOut || dirtyJoined) ? 0 : 79;
        }
        catch (Exception failure) when (failure.GetType() != typeof(OutOfMemoryException)
            && failure.GetType() != typeof(StackOverflowException))
        {
            throw new PhaseRefusal("phase_" + phase + "_" + Category(failure));
        }
    }

    internal sealed class PhaseRefusal(string code) : Exception
    {
        internal string Code { get; } = code;
    }

    private static string Category(Exception failure) => failure switch
    {
        PostgresException postgres => postgres.SqlState switch
        {
            "28P01" or "28000" => "authentication",
            "42501" => "privilege",
            "42P01" => "table",
            _ => "postgres",
        },
        NpgsqlException => "npgsql",
        IOException => "io",
        JsonException => "json",
        TimeoutException => "timeout",
        OperationCanceledException => "canceled",
        ArgumentException => "argument",
        InvalidOperationException => "invalid_operation",
        _ => "other",
    };

    internal static string DirectoryFor(string scenario, bool twin) => Path.Combine(Root, scenario + (twin ? "-twin" : "-fault"));

    internal static void Sentinel(string directory, string name) => File.WriteAllText(Path.Combine(directory, name), "done");

    internal static string Diagnostic(string scenario) => scenario switch
    {
        "gen_constructor_before" or "gen_constructor_after" => "LearnStack jobs fatal: transactional:constructor",
        "gen_wait_entry" => "LearnStack jobs fatal: transactional:lifecycle_unwind",
        _ => "LearnStack jobs fatal: transactional:startup_unwind",
    };

    private static async Task<FixtureEnvelope> ReadEnvelopeAsync()
    {
        // A bounded line protocol avoids an unbounded ReadLine/ReadToEnd and
        // needs no shell command, environment variable or EOF to carry credentials.
        var characters = new char[1];
        var line = new StringBuilder(512);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (line.Length < 4096)
        {
            var read = await Console.In.ReadAsync(characters.AsMemory(), deadline.Token).ConfigureAwait(false);
            if (read == 0) throw Refused();
            if (characters[0] == '\n') break;
            if (characters[0] == '\r') throw Refused();
            line.Append(characters[0]);
        }
        if (line.Length is 0 or >= 4096) throw Refused();
        using var document = JsonDocument.Parse(line.ToString());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2
            || !root.TryGetProperty("Protocol", out var protocol) || !protocol.TryGetInt32(out var version) || version != 1
            || !root.TryGetProperty("ConnectionString", out var connection) || connection.ValueKind != JsonValueKind.String)
            throw Refused();
        var value = connection.GetString();
        if (value is null || value.Length is 0 or > 2048) throw Refused();
        var parsed = new NpgsqlConnectionStringBuilder(value);
        if (parsed.Host != "phase-postgres" || parsed.Port != 5432 || parsed.Database != "learnstack"
            || parsed.Username != OutboxDispatcherDataSource.Role || string.IsNullOrWhiteSpace(parsed.Password)
            || parsed.GssEncryptionMode != GssEncryptionMode.Disable
            || parsed.IncludeErrorDetail || parsed.LogParameters || parsed.PersistSecurityInfo) throw Refused();
        OutboxDispatcherDataSource.Validate(value);
        return new(1, value);
    }

    private static async Task<ChildObservation> ObserveChildAsync(string scenario, bool twin, FixtureEnvelope envelope, string mutation)
    {
        var directory = DirectoryFor(scenario, twin);
        Directory.CreateDirectory(directory);
        var start = new ProcessStartInfo("/usr/share/dotnet/dotnet")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("/opt/probe/LearnStack.Tests.JobLifecycleProbe.dll");
        start.ArgumentList.Add(twin ? "child-twin" : "child");
        start.ArgumentList.Add(scenario);
        if (mutation != "none") start.ArgumentList.Add(mutation);
        start.Environment.Clear();
        foreach (var (key, value) in Program.ClearedChildEnvironment) start.Environment.Add(key, value);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw Refused();
        var stdout = new ChildOutput(Diagnostic(scenario), allowEvents: true);
        var stderr = new ChildOutput(Diagnostic(scenario), allowEvents: false);
        var outDrain = stdout.DrainAsync(process.StandardOutput);
        var errorDrain = stderr.DrainAsync(process.StandardError);
        using var deadline = new CancellationTokenSource(ChildBudget);
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(envelope).AsMemory(), deadline.Token).ConfigureAwait(false);
        process.StandardInput.Close();
        try
        {
            var actualExit = process.WaitForExitAsync(deadline.Token);
            if (!twin && scenario == "gen_process_exit_direct")
            {
                var first = await Task.WhenAny(actualExit, stdout.DirectCallbackEntered.Task)
                    .WaitAsync(deadline.Token).ConfigureAwait(false);
                if (first == stdout.DirectCallbackEntered.Task)
                {
                    try { await actualExit.WaitAsync(DirectCallbackWait, deadline.Token).ConfigureAwait(false); }
                    catch (TimeoutException)
                    {
                        return await TerminateAndJoinAsync().ConfigureAwait(false);
                    }
                }
                else await actualExit.ConfigureAwait(false);
            }
            else await actualExit.ConfigureAwait(false);
            await Task.WhenAll(outDrain, errorDrain).WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            // Explicitly settle only this owned child before the supervisor
            // leaves. Even a successful kill/join cannot rescue normal proof.
            return await TerminateAndJoinAsync().ConfigureAwait(false);
        }
        return Observation(process.HasExited, process.ExitCode, timedOut: false);

        async Task<ChildObservation> TerminateAndJoinAsync()
        {
            using var cleanupDeadline = new CancellationTokenSource(OwnedProcessJoinBudget);
            var externalKill = !process.HasExited;
            if (externalKill) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(cleanupDeadline.Token).ConfigureAwait(false);
            await Task.WhenAll(outDrain, errorDrain).WaitAsync(cleanupDeadline.Token).ConfigureAwait(false);
            if (!process.HasExited) throw Refused();
            return Observation(exited: true, exitCode: process.ExitCode, timedOut: true,
                externalKill: externalKill, ownedProcessJoined: true);
        }

        ChildObservation Observation(bool exited, int? exitCode, bool timedOut,
            bool externalKill = false, bool ownedProcessJoined = false) => new(exited, exitCode, timedOut,
            stderr.Diagnostic, HasSentinel("finally.flag"), HasSentinel("process-exit.flag"),
            stdout.ProtocolInvalid || stderr.ProtocolInvalid, stdout.Observed.ToArray(),
            Math.Min(1_000_000, stdout.Discarded + stderr.Discarded), externalKill, ownedProcessJoined);
        bool HasSentinel(string name)
        {
            var path = Path.Combine(directory, name);
            return File.Exists(path) && new FileInfo(path).Length == 4 && File.ReadAllText(path) == "done";
        }
    }

    private static bool QualifiedDirectCallbackHang(ChildObservation child) => child.Exited && child.ExitCode == 137
        && child.TimedOut && child.ExternalKill && child.OwnedProcessJoined && !child.Diagnostic
        && child.Finally && child.ProcessExit && !child.ProtocolInvalid
        && DirectCallbackEvents.All(value => child.Events.Contains(value, StringComparer.Ordinal))
        && !child.Events.Contains("final_observed", StringComparer.Ordinal)
        && !child.Events.Contains("normal_cleanup", StringComparer.Ordinal)
        && !child.Events.Contains("private_callback_joined", StringComparer.Ordinal);

    private static async Task<(long Servers, long Business)> ReadRowsAsync(string connectionString)
    {
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(connectionString));
        await using var connection = await source.OpenConnectionAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM public.server),
                (SELECT count(*) FROM public.background_jobs) + (SELECT count(*) FROM public.background_job_attempts)
            """, connection);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        if (!await reader.ReadAsync().ConfigureAwait(false)) throw Refused();
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static bool ArtifactsAllowed(string scenario)
    {
        var directories = new HashSet<string>(StringComparer.Ordinal)
        {
            Path.Combine(Root, "home"), Path.Combine(Root, "tmp"), DirectoryFor(scenario, true), DirectoryFor(scenario, false),
        };
        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (var twin in new[] { false, true })
            foreach (var name in new[] { "finally.flag", "process-exit.flag" }) files.Add(Path.Combine(DirectoryFor(scenario, twin), name));
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new(Root));
        var count = 0;
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if (++count > 32 || entry.LinkTarget is not null || (entry.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                if (entry is DirectoryInfo child)
                {
                    if (!directories.Contains(child.FullName)) return false;
                    pending.Push(child);
                }
                else if (entry is not FileInfo file || !files.Contains(file.FullName)
                    || file.Length != 4 || File.ReadAllText(file.FullName) != "done") return false;
            }
        }
        return true;
    }

    private static InvalidOperationException Refused() => new("The generation phase fixture refused its input or state.");

    private static void Emit(PhaseReceipt receipt)
    {
        var json = JsonSerializer.Serialize(receipt);
        if (json.Length > 8192) throw Refused();
        Console.Out.WriteLine(ReceiptPrefix + json);
        Console.Out.Flush();
    }

    private sealed record FixtureEnvelope(int Protocol, string ConnectionString);
    private sealed record ChildObservation(bool Exited, int? ExitCode, bool TimedOut, bool Diagnostic,
        bool Finally, bool ProcessExit, bool ProtocolInvalid, string[] Events, long Discarded,
        bool ExternalKill, bool OwnedProcessJoined);
    private sealed record PhaseReceipt(int Protocol, string Error, string Scenario, string Runtime,
        string Architecture, string Collector, bool BenignControlPassed, ChildObservation Twin,
        ChildObservation? Child, bool ArtifactsAllowed, long ServerRowsAfter, long BusinessRowsAfter);

    private sealed class ChildOutput(string diagnostic, bool allowEvents)
    {
        private readonly StringBuilder _line = new(256);
        private bool _overflow;
        internal List<string> Observed { get; } = [];
        internal TaskCompletionSource DirectCallbackEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Diagnostic { get; private set; }
        internal bool ProtocolInvalid { get; private set; }
        internal long Discarded { get; private set; }

        internal async Task DrainAsync(StreamReader stream)
        {
            var buffer = new char[1024];
            int read;
            while ((read = await stream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                for (var index = 0; index < read; index++)
                {
                    var character = buffer[index];
                    if (character == '\n') CompleteLine();
                    else if (character != '\r')
                    {
                        if (!_overflow && _line.Length < 2048) _line.Append(character);
                        else _overflow = true;
                    }
                }
            }
            if (_line.Length > 0 || _overflow) CompleteLine();
        }

        private void CompleteLine()
        {
            var value = _line.ToString();
            if (!_overflow && allowEvents && value.StartsWith(EventPrefix, StringComparison.Ordinal))
            {
                var marker = value[EventPrefix.Length..];
                if (!Events.Contains(marker, StringComparer.Ordinal) || Observed.Contains(marker, StringComparer.Ordinal)
                    || Observed.Count >= Events.Length) ProtocolInvalid = true;
                else
                {
                    Observed.Add(marker);
                    if (marker == "direct_core_callback_entered") DirectCallbackEntered.TrySetResult();
                }
            }
            else if (!_overflow && !allowEvents && (value == diagnostic || value == "Process terminated. " + diagnostic))
                Diagnostic = true;
            else
            {
                if (allowEvents) ProtocolInvalid = true;
                Discarded = Math.Min(1_000_000, Discarded + (_overflow ? 2049 : value.Length));
            }
            _line.Clear();
            _overflow = false;
        }
    }
}
