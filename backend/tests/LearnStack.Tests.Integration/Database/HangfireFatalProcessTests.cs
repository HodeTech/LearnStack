using System.Buffers;
using System.Text.Json;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// Real Linux termination of the production gate. Stage selection here is
/// synthetic input, not evidence of HangfireGeneration phase dominance.
/// </summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class HangfireFatalProcessTests(ITestOutputHelper output)
{
    private const string Image = "mcr.microsoft.com/dotnet/aspnet:10.0.10";
    private const string ReceiptPrefix = "LearnStackLifecycleReceipt:";
    private static readonly SearchValues<char> LowerHex = SearchValues.Create("0123456789abcdef");
    private static readonly string[] ReceiptFields =
    [
        "Protocol", "Error", "Scenario", "Runtime", "Architecture", "Linux", "SoftCoreZero", "HardCoreZero",
        "Collector", "EnvironmentAllowed", "ChildEnvironmentCleared", "BenignControlPassed", "NativeExitObserved",
        "ChildExitCode", "TimedOut", "ExternalKill", "ChildArmed", "FatalDiagnosticSeen", "ChildFinally",
        "ChildProcessExit", "GateClosedBeforeBoundary", "BoundaryReturned", "ArtifactsAllowed", "DiscardedCharacters",
    ];
    private static readonly string[] Refusals =
    [
        "none", "argument_refused", "collector_unreadable", "collector_pipe", "collector_unsupported",
        "soft_core_not_zero", "hard_core_not_zero", "soft_core_refused", "hard_core_refused",
        "limits_unreadable", "scratch_refused", "managed_preflight_refused", "probe_failed",
        "benign_control_failed", "benign_artifact_refused", "receipt_bound",
    ];

    [Theory]
    [InlineData("benign", false, false, false)]
    [InlineData("constructor", true, false, false)]
    [InlineData("startup_unwind", true, false, false)]
    [InlineData("lifecycle_unwind", true, false, false)]
    [InlineData("host_stop_then_fatal", true, true, false)]
    [InlineData("final_exit_then_fatal", false, true, true)]
    public async Task The_direct_production_gate_has_native_exit_and_truthful_cleanup_evidence(
        string scenario, bool fatal, bool gateClosedBeforeBoundary, bool boundaryReturned)
    {
        var staged = Path.Combine(AppContext.BaseDirectory, "job-lifecycle-probe");
        (Directory.Exists(staged)
            && File.Exists(Path.Combine(staged, "LearnStack.Tests.JobLifecycleProbe.dll"))
            && File.Exists(Path.Combine(staged, "preflight.sh"))).Should().BeTrue(
            "the solution build must stage the portable probe; this test never builds or restores");

        await using var container = new ContainerBuilder(Image)
            .WithLogger(NullLogger.Instance)
            .WithBindMount(staged, "/opt/probe", AccessMode.ReadOnly)
            .WithEntrypoint("/bin/sh", "/opt/probe/preflight.sh")
            .WithCommand(scenario)
            .WithWorkingDirectory("/proof")
            .WithCreateParameterModifier(parameters =>
            {
                parameters.User = "1654:1654";
                parameters.HostConfig ??= new HostConfig();
                parameters.HostConfig.NetworkMode = "none";
                parameters.HostConfig.ReadonlyRootfs = true;
                parameters.HostConfig.CapDrop = ["ALL"];
                parameters.HostConfig.SecurityOpt = ["no-new-privileges:true"];
                parameters.HostConfig.Ulimits = [new Ulimit { Name = "core", Soft = 0, Hard = 0 }];
                parameters.HostConfig.Tmpfs ??= new Dictionary<string, string>(StringComparer.Ordinal);
                parameters.HostConfig.Tmpfs["/proof"] = "rw,noexec,nosuid,size=16777216,uid=1654,gid=1654,mode=0700";
            })
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged(ReceiptPrefix,
                strategy => strategy.WithMode(WaitStrategyMode.OneShot).WithTimeout(TimeSpan.FromSeconds(90))))
            .Build();

        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await container.StartAsync(deadline.Token);
        }
        catch (Exception)
        {
            var reason = "container_start";
            if (!deadline.IsCancellationRequested && !string.IsNullOrEmpty(container.Id))
            {
                using var diagnosticBudget = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    var startupLogs = await container.GetLogsAsync(timestampsEnabled: false, ct: diagnosticBudget.Token);
                    reason = StartupRefusal(startupLogs.Stdout);
                }
                catch (Exception) { }
            }
            // Only a fixed known preflight code may leave this boundary. Never
            // attach Docker/provider text, raw logs or a fallback image.
            throw new InvalidOperationException("The isolated Linux fatal probe refused: " + reason);
        }

        using var docker = TestcontainersSettings.OS.DockerEndpointAuthConfig
            .GetDockerClientBuilder(Guid.NewGuid()).Build();
        ContainerWaitResponse stopped;
        ContainerInspectResponse actualContainer;
        try
        {
            // A logged receipt proves message readiness only. Observe actual
            // supervisor exit before any owned-container cleanup can supply it.
            stopped = await docker.Containers.WaitContainerAsync(container.Id, deadline.Token);
            actualContainer = await docker.Containers.InspectContainerAsync(container.Id, deadline.Token);
        }
        catch (Exception)
        {
            throw new InvalidOperationException("The Linux probe did not establish actual supervisor exit within its deadline.");
        }
        var actualState = actualContainer.State ?? throw new InvalidOperationException("The Linux probe returned no process state.");
        var actualConfiguration = actualContainer.HostConfig ?? throw new InvalidOperationException("The Linux probe returned no isolation configuration.");
        (actualState.Status == "exited" && !actualState.Running
            && !actualState.Restarting && !actualState.OOMKilled).Should().BeTrue(
            "receipt readiness, timeout cleanup and a running or externally interrupted supervisor are not exit evidence");
        actualState.ExitCode.Should().Be(stopped.StatusCode);
        stopped.StatusCode.Should().Be(0, "the supervisor must stop normally before test cleanup");

        var logs = await container.GetLogsAsync(timestampsEnabled: false, ct: deadline.Token);
        (logs.Stderr.Length == 0 && logs.Stdout.Length <= 8192).Should().BeTrue(
            "only a bounded sanitized supervisor receipt may leave the container");
        var receipt = ParseReceipt(logs.Stdout);
        ReceiptRefusal(receipt, scenario, fatal, gateClosedBeforeBoundary, boundaryReturned).Should().Be("none",
            "the shared receipt predicate requires native exit, armed/diagnostic evidence and qualified cleanup/environment");
        var actualImage = await docker.Images.InspectImageAsync(actualContainer.Image, deadline.Token);
        IsImageId(actualImage.ID).Should().BeTrue();
        actualImage.Os.Should().Be("linux");
        actualImage.Architecture.Should().Be(receipt.Architecture == "Arm64" ? "arm64" : "amd64");
        actualConfiguration.ReadonlyRootfs.Should().BeTrue();
        actualConfiguration.NetworkMode.Should().Be("none");
        actualContainer.Mounts.Should().ContainSingle(mount => mount.Destination == "/opt/probe" && !mount.RW,
            "the only host input is the read-only staged proof binary directory");
        var core = actualConfiguration.Ulimits.Single(limit => limit.Name == "core");
        (core.Soft == 0 && core.Hard == 0).Should().BeTrue();

        // Only validated metadata and the closed receipt enter TRX/artifact output.
        output.WriteLine(JsonSerializer.Serialize(new
        {
            Image,
            ImageId = actualImage.ID,
            Architecture = actualImage.Architecture,
            Receipt = receipt,
            Coverage = "direct production gate; synthetic stage; no generation phase proof",
        }));
    }

    [Fact]
    public void The_real_receipt_predicate_rejects_false_fatal_and_unqualified_process_evidence()
    {
        StartupRefusal(ReceiptPrefix + "{\"Protocol\":1,\"Error\":\"collector_pipe\"}")
            .Should().Be("collector_pipe");
        foreach (var invalid in new[]
        {
            "{\"Protocol\":\"1\",\"Error\":\"collector_pipe\"}",
            "{\"Protocol\":1,\"Error\":\"provider detail\"}",
            "{\"Protocol\":1,\"Error\":\"collector_pipe\",\"Detail\":\"provider detail\"}",
            "{\"Protocol\":1,\"Error\":\"none\"}",
            "{",
        })
            StartupRefusal(ReceiptPrefix + invalid).Should().Be("container_start");
        var clean = CleanFatalReceipt();
        ReceiptRefusal(clean, "constructor", true, false, false).Should().Be("none");
        var normal = clean with
        {
            Scenario = "benign",
            ChildExitCode = 0,
            FatalDiagnosticSeen = false,
            ChildFinally = true,
            ChildProcessExit = true,
        };
        ReceiptRefusal(normal, "benign", false, false, false).Should().Be("none");
        var finalExit = normal with
        {
            Scenario = "final_exit_then_fatal",
            GateClosedBeforeBoundary = true,
            BoundaryReturned = true,
        };
        ReceiptRefusal(finalExit, "final_exit_then_fatal", false, true, true).Should().Be("none");
        ReceiptRefusal(finalExit with { BoundaryReturned = false }, "final_exit_then_fatal", false, true, true)
            .Should().NotBe("none", "final teardown must actually return from its suppressed fatal selection");
        var dirty = new (string Name, ProbeReceipt Receipt)[]
        {
            ("suppressed fatal boundary", clean with
            {
                ChildExitCode = 0, FatalDiagnosticSeen = false, ChildFinally = true, ChildProcessExit = true,
            }),
            ("returned fatal boundary", clean with { BoundaryReturned = true }),
            ("missing diagnostic", clean with { FatalDiagnosticSeen = false }),
            ("missing armed marker", clean with { ChildArmed = false }),
            ("nonzero startup refusal", clean with { ChildExitCode = 78, FatalDiagnosticSeen = false }),
            ("wrong native exit despite diagnostic", clean with { ChildExitCode = 79 }),
            ("no actual native exit", clean with { NativeExitObserved = false }),
            ("cleanup supplied the result", clean with { ExternalKill = true }),
            ("wait expired", clean with { TimedOut = true }),
            ("artifact inventory failed", clean with { ArtifactsAllowed = false }),
            ("environment inherited", clean with { EnvironmentAllowed = false }),
            ("child environment not cleared", clean with { ChildEnvironmentCleared = false }),
            ("bad normal twin", clean with { BenignControlPassed = false }),
            ("fatal ran finally", clean with { ChildFinally = true }),
            ("fatal ran process exit", clean with { ChildProcessExit = true }),
            ("soft dump limit enabled", clean with { SoftCoreZero = false }),
            ("hard dump limit enabled", clean with { HardCoreZero = false }),
            ("unqualified collector", clean with { Collector = "unsupported" }),
            ("unqualified runtime", clean with { Runtime = ".NET unsupported" }),
            ("unexpected preclosed marker", clean with { GateClosedBeforeBoundary = true }),
        };
        foreach (var (name, receipt) in dirty)
        {
            // These are in-memory receipts, not unsafe dump/process experiments.
            // They call the exact predicate used by the actual Docker result.
            var serialized = ReceiptPrefix + JsonSerializer.Serialize(receipt);
            ReceiptRefusal(ParseReceipt(serialized), "constructor", true, false, false)
                .Should().NotBe("none", name);
        }
    }

    private static ProbeReceipt CleanFatalReceipt() => new(1, "none", "constructor", ".NET 10.0.10",
        "Arm64", true, true, true, "core", true, true, true, true, 134, false, false,
        true, true, false, false, false, false, true, 0);

    private static string ReceiptRefusal(ProbeReceipt receipt, string scenario, bool fatal,
        bool gateClosedBeforeBoundary, bool boundaryReturned)
    {
        if (receipt.Protocol != 1 || receipt.Error != "none" || receipt.Scenario != scenario)
            return "receipt_identity";
        if (receipt.Runtime != ".NET 10.0.10" || receipt.Architecture is not ("Arm64" or "X64")
            || !receipt.Linux || !receipt.SoftCoreZero || !receipt.HardCoreZero
            || receipt.Collector is not ("core" or "core.%p")) return "preflight";
        if (!receipt.EnvironmentAllowed || !receipt.ChildEnvironmentCleared || !receipt.ArtifactsAllowed)
            return "environment_or_artifact";
        if (!receipt.BenignControlPassed) return "normal_twin";
        if (!receipt.NativeExitObserved || receipt.TimedOut || receipt.ExternalKill
            || receipt.ChildExitCode != (fatal ? 134 : 0)) return "native_exit";
        if (!receipt.ChildArmed || receipt.FatalDiagnosticSeen != fatal) return "termination_markers";
        if (receipt.ChildFinally != !fatal || receipt.ChildProcessExit != !fatal) return "cleanup_witness";
        if (receipt.GateClosedBeforeBoundary != gateClosedBeforeBoundary
            || receipt.BoundaryReturned != boundaryReturned) return "boundary_markers";
        return receipt.DiscardedCharacters is >= 0 and <= 1_000_000 ? "none" : "receipt_bound";
    }

    private static ProbeReceipt ParseReceipt(string stdout)
    {
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length != 1 || !lines[0].StartsWith(ReceiptPrefix, StringComparison.Ordinal)
            || lines[0].Length > 4096 + ReceiptPrefix.Length)
            throw new InvalidOperationException("The fatal probe emitted an unsupported receipt.");
        try
        {
            var json = lines[0][ReceiptPrefix.Length..];
            using var parsed = JsonDocument.Parse(json);
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Error", out var error)
                || error.ValueKind != JsonValueKind.String || !Refusals.Contains(error.GetString(), StringComparer.Ordinal))
                throw new InvalidOperationException("The fatal probe emitted an unsupported receipt.");
            if (error.GetString() != "none")
                throw new InvalidOperationException("The fatal probe preflight refused: " + error.GetString());
            if (!root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)
                    .SequenceEqual(ReceiptFields.Order(StringComparer.Ordinal)))
                throw new InvalidOperationException("The fatal probe emitted an unsupported receipt.");
            return JsonSerializer.Deserialize<ProbeReceipt>(json)
                ?? throw new InvalidOperationException("The fatal probe emitted no receipt.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("The fatal probe emitted invalid receipt data.");
        }
    }

    private static string StartupRefusal(string stdout)
    {
        if (stdout.Length > 4096 || !stdout.StartsWith(ReceiptPrefix, StringComparison.Ordinal)) return "container_start";
        try
        {
            using var parsed = JsonDocument.Parse(stdout[ReceiptPrefix.Length..]);
            var root = parsed.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.EnumerateObject().Count() == 2
                && root.TryGetProperty("Protocol", out var protocol) && protocol.ValueKind == JsonValueKind.Number
                && protocol.TryGetInt32(out var version) && version == 1
                && root.TryGetProperty("Error", out var error) && error.ValueKind == JsonValueKind.String
                && error.GetString() is { } code && code != "none" && Refusals.Contains(code, StringComparer.Ordinal))
                return code;
        }
        catch (JsonException) { }
        return "container_start";
    }

    private static bool IsImageId(string value) => value.Length == 71
        && value.StartsWith("sha256:", StringComparison.Ordinal)
        && value.AsSpan(7).IndexOfAnyExcept(LowerHex) < 0;

    private sealed record ProbeReceipt(int Protocol, string Error, string Scenario, string Runtime,
        string Architecture, bool Linux, bool SoftCoreZero, bool HardCoreZero, string Collector,
        bool EnvironmentAllowed, bool ChildEnvironmentCleared, bool BenignControlPassed,
        bool NativeExitObserved, int? ChildExitCode, bool TimedOut, bool ExternalKill,
        bool ChildArmed, bool FatalDiagnosticSeen, bool ChildFinally, bool ChildProcessExit,
        bool GateClosedBeforeBoundary, bool BoundaryReturned, bool ArtifactsAllowed, long DiscardedCharacters);
}
