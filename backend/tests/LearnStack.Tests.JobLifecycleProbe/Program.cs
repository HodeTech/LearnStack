using System.Collections;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using LearnStack.Infrastructure.Jobs;

namespace LearnStack.Tests.JobLifecycleProbe;

internal static class Program
{
    private const string ReceiptPrefix = "LearnStackLifecycleReceipt:";
    private const string Armed = "LearnStackLifecycleArmed";
    private const string Closed = "LearnStackLifecycleGate:closed_before_boundary";
    private const string Returned = "LearnStackLifecycleFinalExitReturned";
    private const string ProbeRoot = "/proof";
    private const string Dotnet = "/usr/share/dotnet/dotnet";
    private const string ProbeDll = "/opt/probe/LearnStack.Tests.JobLifecycleProbe.dll";
    private static readonly TimeSpan ChildWait = TimeSpan.FromSeconds(20);
    private static readonly string[] Scenarios =
    [
        "benign", "constructor", "startup_unwind", "lifecycle_unwind",
        "host_stop_then_fatal", "final_exit_then_fatal",
    ];
    private static readonly string[] SentinelNames = ["finally.flag", "process-exit.flag"];
    private static readonly FrozenDictionary<string, string> ChildEnvironment = CreateEnvironment();

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length != 2 || !Scenarios.Contains(args[1], StringComparer.Ordinal))
                return Refuse("argument_refused");
            if (!QualifiedEnvironment()) return Refuse("managed_preflight_refused");
            return args[0] switch
            {
                "supervise" => await SuperviseAsync(args[1]).ConfigureAwait(false),
                "child" => RunChild(args[1]),
                _ => Refuse("argument_refused"),
            };
        }
        catch (Exception failure) when (failure.GetType() != typeof(OutOfMemoryException)
            && failure.GetType() != typeof(StackOverflowException))
        {
            // Neither runtime/provider text nor an exception object leaves this fixture.
            return Refuse("probe_failed");
        }
    }

    private static async Task<int> SuperviseAsync(string scenario)
    {
        var benign = await ObserveChildAsync("benign").ConfigureAwait(false);
        if (!benign.Exited || benign.ExitCode != 0 || !benign.Armed || !benign.Finally || !benign.ProcessExit)
            return Refuse("benign_control_failed");
        if (!ArtifactsAllowed("benign")) return Refuse("benign_artifact_refused");

        var observed = scenario == "benign" ? benign : await ObserveChildAsync(scenario).ConfigureAwait(false);
        var artifacts = ArtifactsAllowed(scenario);
        var receipt = new ProbeReceipt(1, "none", scenario, RuntimeInformation.FrameworkDescription,
            RuntimeInformation.ProcessArchitecture.ToString(), true, true, true,
            File.ReadAllText("/proc/sys/kernel/core_pattern").Trim(), true, true,
            benign.Exited && benign.ExitCode == 0 && benign.Finally && benign.ProcessExit,
            observed.Exited, observed.ExitCode, observed.TimedOut, false, observed.Armed,
            observed.Diagnostic, observed.Finally, observed.ProcessExit, observed.GateClosedBeforeBoundary,
            observed.BoundaryReturned, artifacts, observed.Discarded);
        Emit(receipt);
        return observed.Exited && !observed.TimedOut && artifacts ? 0 : 79;
    }

    private static int RunChild(string scenario)
    {
        var directory = Path.Combine(ProbeRoot, scenario);
        Directory.CreateDirectory(directory);
        Directory.SetCurrentDirectory(directory);
        var pool = BackgroundJobInfrastructureRegistry.CreateDefault();
        // ForPool requires the exact frozen registration instance of this gate.
        var gate = new BackgroundJobRuntimeGate(pool);
        var boundary = gate.ForPool(pool.Pools["transactional"]);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => WriteSentinel(directory, "process-exit.flag");
        try
        {
            if (!gate.TryAdmit()) return 80;
            Console.Out.WriteLine(Armed);
            Console.Out.Flush();
            if (scenario == "benign") return 0;
            if (scenario == "host_stop_then_fatal")
            {
                gate.StopStarts();
                if (gate.State != BackgroundJobProcessState.Stopping || gate.TryAdmit()) return 81;
                Console.Out.WriteLine(Closed);
                Console.Out.Flush();
            }
            if (scenario == "final_exit_then_fatal")
            {
                gate.BeginFinalExit();
                if (gate.State != BackgroundJobProcessState.FinalExit || gate.TryAdmit()
                    || !gate.FinalExitSignal.IsCompletedSuccessfully) return 82;
                Console.Out.WriteLine(Closed);
                boundary.Terminate(HangfireFatalStage.LifecycleUnwind);
                Console.Out.WriteLine(Returned);
                Console.Out.Flush();
                return 0;
            }
            boundary.Terminate(Stage(scenario));
            // A selected fatal boundary cannot return in these scenarios.
            return 83;
        }
        finally
        {
            WriteSentinel(directory, "finally.flag");
        }
    }

    private static async Task<ChildObservation> ObserveChildAsync(string scenario)
    {
        var directory = Path.Combine(ProbeRoot, scenario);
        Directory.CreateDirectory(directory);
        var start = new ProcessStartInfo(Dotnet)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(ProbeDll);
        start.ArgumentList.Add("child");
        start.ArgumentList.Add(scenario);
        start.Environment.Clear();
        foreach (var (key, value) in ChildEnvironment) start.Environment.Add(key, value);

        using var child = new Process { StartInfo = start };
        if (!child.Start()) return new(false, null, false, false, false, false, false, false, false, 0);
        var stdout = new BoundedObservation(Diagnostic(scenario));
        var stderr = new BoundedObservation(Diagnostic(scenario));
        var outDrain = stdout.DrainAsync(child.StandardOutput);
        var errorDrain = stderr.DrainAsync(child.StandardError);
        using var deadline = new CancellationTokenSource(ChildWait);
        try
        {
            await child.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await Task.WhenAll(outDrain, errorDrain).WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            // No kill supplies an expected native result. The owned container's
            // later teardown is failure cleanup, not successful fatal qualification.
            return new(false, null, true, stdout.Armed, stderr.Diagnostic, false, false,
                stdout.GateClosedBeforeBoundary, stdout.BoundaryReturned, Math.Min(1_000_000, stdout.Discarded + stderr.Discarded));
        }

        return new(child.HasExited, child.ExitCode, false, stdout.Armed, stderr.Diagnostic,
            HasSentinel(directory, "finally.flag"), HasSentinel(directory, "process-exit.flag"),
            stdout.GateClosedBeforeBoundary, stdout.BoundaryReturned, Math.Min(1_000_000, stdout.Discarded + stderr.Discarded));
    }

    private static bool QualifiedEnvironment()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.FrameworkDescription != ".NET 10.0.10"
            || RuntimeInformation.ProcessArchitecture is not (Architecture.Arm64 or Architecture.X64)) return false;
        var actual = Environment.GetEnvironmentVariables();
        if (actual.Count != ChildEnvironment.Count) return false;
        foreach (DictionaryEntry item in actual)
            if (item.Key is not string key || item.Value is not string value
                || !ChildEnvironment.TryGetValue(key, out var expected) || value != expected) return false;

        if (File.ReadAllText("/proc/sys/kernel/core_pattern").Trim() is not ("core" or "core.%p")
            || File.ReadAllText("/proc/sys/kernel/core_uses_pid").Trim() is not ("0" or "1")
            || File.ReadAllText("/proc/sys/fs/suid_dumpable").Trim() is not ("0" or "1" or "2")) return false;

        var limit = File.ReadLines("/proc/self/limits")
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .SingleOrDefault(fields => fields.Length >= 7 && fields[0] == "Max" && fields[1] == "core"
                && fields[2] == "file" && fields[3] == "size");
        if (limit is null || limit[4] != "0" || limit[5] != "0") return false;

        var mounts = File.ReadLines("/proc/mounts")
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToArray();
        return mounts.Any(fields => fields.Length >= 4 && fields[1] == "/"
                && fields[3].Split(',').Contains("ro", StringComparer.Ordinal))
            && mounts.Any(fields => fields.Length >= 4 && fields[1] == ProbeRoot && fields[2] == "tmpfs"
                && fields[3].Split(',').Contains("rw", StringComparer.Ordinal));
    }

    private static bool ArtifactsAllowed(string scenario)
    {
        var directories = new HashSet<string>(StringComparer.Ordinal)
        {
            Path.Combine(ProbeRoot, "home"), Path.Combine(ProbeRoot, "tmp"), Path.Combine(ProbeRoot, "benign"),
            Path.Combine(ProbeRoot, scenario),
        };
        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in SentinelNames)
        {
            files.Add(Path.Combine(ProbeRoot, "benign", name));
            files.Add(Path.Combine(ProbeRoot, scenario, name));
        }
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new(ProbeRoot));
        var count = 0;
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if (++count > 32 || entry.LinkTarget is not null
                    || (entry.Attributes & FileAttributes.ReparsePoint) != 0) return false;
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

    private static bool HasSentinel(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        return File.Exists(path) && new FileInfo(path).Length == 4 && File.ReadAllText(path) == "done";
    }

    private static void WriteSentinel(string directory, string name) =>
        File.WriteAllText(Path.Combine(directory, name), "done");

    private static HangfireFatalStage Stage(string scenario) => scenario switch
    {
        "constructor" => HangfireFatalStage.Constructor,
        "startup_unwind" => HangfireFatalStage.StartupUnwind,
        "lifecycle_unwind" or "host_stop_then_fatal" => HangfireFatalStage.LifecycleUnwind,
        _ => throw new ArgumentException("The probe scenario has no fatal stage.", nameof(scenario)),
    };

    private static string Diagnostic(string scenario) => scenario switch
    {
        "constructor" => "LearnStack jobs fatal: transactional:constructor",
        "startup_unwind" => "LearnStack jobs fatal: transactional:startup_unwind",
        "lifecycle_unwind" or "host_stop_then_fatal" => "LearnStack jobs fatal: transactional:lifecycle_unwind",
        _ => string.Empty,
    };

    private static FrozenDictionary<string, string> CreateEnvironment()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = "/usr/share/dotnet:/usr/bin:/bin",
            ["DOTNET_ROOT"] = "/usr/share/dotnet",
            ["HOME"] = "/proof/home",
            ["TMPDIR"] = "/proof/tmp",
            ["LANG"] = "C.UTF-8",
            ["LC_ALL"] = "C.UTF-8",
            ["LEARNSTACK_PROBE_PREFLIGHT"] = "1",
        };
        foreach (var suffix in new[] { "DbgEnableMiniDump", "EnableCrashReport", "EnableCrashReportOnly",
                     "CreateDumpDiagnostics", "CreateDumpVerboseDiagnostics", "EnableDiagnostics" })
        {
            values.Add("DOTNET_" + suffix, "0");
            values.Add("COMPlus_" + suffix, "0");
        }
        return values.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static int Refuse(string error)
    {
        Emit(new { Protocol = 1, Error = error });
        return 78;
    }

    private static void Emit<T>(T receipt)
    {
        var json = JsonSerializer.Serialize(receipt);
        if (json.Length > 4096) json = "{\"Protocol\":1,\"Error\":\"receipt_bound\"}";
        Console.Out.WriteLine(ReceiptPrefix + json);
        Console.Out.Flush();
    }

    private sealed record ChildObservation(bool Exited, int? ExitCode, bool TimedOut, bool Armed,
        bool Diagnostic, bool Finally, bool ProcessExit, bool GateClosedBeforeBoundary, bool BoundaryReturned, long Discarded);

    private sealed record ProbeReceipt(int Protocol, string Error, string Scenario, string Runtime,
        string Architecture, bool Linux, bool SoftCoreZero, bool HardCoreZero, string Collector,
        bool EnvironmentAllowed, bool ChildEnvironmentCleared, bool BenignControlPassed,
        bool NativeExitObserved, int? ChildExitCode, bool TimedOut, bool ExternalKill,
        bool ChildArmed, bool FatalDiagnosticSeen, bool ChildFinally, bool ChildProcessExit,
        bool GateClosedBeforeBoundary, bool BoundaryReturned, bool ArtifactsAllowed, long DiscardedCharacters);

    private sealed class BoundedObservation(string diagnostic)
    {
        private readonly StringBuilder _line = new(256);
        private bool _overflow;
        internal bool Armed { get; private set; }
        internal bool Diagnostic { get; private set; }
        internal bool GateClosedBeforeBoundary { get; private set; }
        internal bool BoundaryReturned { get; private set; }
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
                        if (_line.Length < 2048 && !_overflow) _line.Append(character);
                        else _overflow = true;
                    }
                }
            }
            if (_line.Length > 0 || _overflow) CompleteLine();
        }

        private void CompleteLine()
        {
            if (!_overflow)
            {
                var text = _line.ToString();
                if (text == Program.Armed) Armed = true;
                else if (text == Closed) GateClosedBeforeBoundary = true;
                else if (text == Returned) BoundaryReturned = true;
                else if (diagnostic.Length != 0 && (text == diagnostic || text == "Process terminated. " + diagnostic))
                    Diagnostic = true;
                else Discarded = Math.Min(1_000_000, Discarded + text.Length);
            }
            else Discarded = Math.Min(1_000_000, Discarded + 2049);
            _line.Clear();
            _overflow = false;
        }
    }
}
