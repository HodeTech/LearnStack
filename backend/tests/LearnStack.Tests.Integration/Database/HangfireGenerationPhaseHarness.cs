using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// Owns a separate, explicitly internal Docker topology. Fatal children receive
/// only scoped fixture material over stdin after actual isolation inspection.
/// </summary>
internal static class HangfireGenerationPhaseHarness
{
    private const string RuntimeImage = "mcr.microsoft.com/dotnet/aspnet:10.0.10";
    private const string PostgresImage = "postgres:18.4-alpine";
    private const string ReceiptPrefix = "LearnStackGenerationReceipt:";
    private static readonly SearchValues<char> HexDigits = SearchValues.Create("0123456789abcdef");
    private static readonly string[] PhaseFailureStages = ["envelope", "child", "twin", "fault", "rows", "receipt"];
    private static readonly string[] PhaseFailureCategories =
        ["authentication", "privilege", "table", "postgres", "npgsql", "io", "json", "timeout", "canceled", "argument", "invalid_operation", "other"];
    private static readonly (string Token, string Label)[] StandardErrorTokens =
    [
        ("warning", "warning"), ("error", "error"), ("System.", "system"), ("Hangfire", "hangfire"),
        ("Npgsql", "npgsql"), ("Microsoft", "microsoft"), ("SSL", "ssl"), ("cgroup", "cgroup"),
        ("dotnet", "dotnet"), ("Read-only", "readonly"), ("permission", "permission"),
        ("No such", "missing"), ("file", "file"), ("pipe", "pipe"),
        ("gss", "gss"), ("krb", "krb"), ("kerberos", "kerberos"), ("klist", "klist"),
        ("shared library", "shared_library"), ("shared object", "shared_object"),
    ];

    internal static async Task<GenerationPhaseEvidence> RunAsync(string scenario, string mutation = "none")
    {
        var stage = "staged_binaries";
        try
        {
            var staged = Path.Combine(AppContext.BaseDirectory, "job-lifecycle-probe");
            Require(Directory.Exists(staged) && File.Exists(Path.Combine(staged, "LearnStack.Tests.JobLifecycleProbe.dll"))
                && File.Exists(Path.Combine(staged, "preflight.sh")));
            await VerifyVendorBinariesAsync(staged);
            stage = "child_instruction_mutation";
            using var copied = await HangfireGenerationPhaseMutation.CreateAsync(staged, mutation);
            staged = copied.Directory;
            await VerifyVendorBinariesAsync(staged);
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            using var docker = TestcontainersSettings.OS.DockerEndpointAuthConfig.GetDockerClientBuilder(Guid.NewGuid()).Build();
            await using var network = new NetworkBuilder()
                .WithName("learnstack-phase-" + Guid.NewGuid().ToString("N"))
                .WithCreateParameterModifier(parameters => parameters.Internal = true)
                .Build();
            stage = "owned_internal_network";
            await network.CreateAsync(deadline.Token);
            var createdNetwork = await docker.Networks.InspectNetworkAsync(network.Name, deadline.Token);
            var networkId = createdNetwork.ID;
            Require(createdNetwork.Name == network.Name && !string.IsNullOrEmpty(networkId)
                && createdNetwork.Internal && createdNetwork.Containers.Count == 0);

            // Only this owned setup container receives ephemeral role passwords
            // in the environment required by the canonical psql roles script.
            // The fatal supervisor/child environment remains the literal allowlist.
            var migrationPassword = Guid.NewGuid().ToString("N");
            var outboxPassword = Guid.NewGuid().ToString("N");
            await using var postgres = new PostgreSqlBuilder(PostgresImage)
                .WithLogger(NullLogger.Instance)
                .WithDatabase("learnstack")
                .WithUsername("postgres")
                .WithPassword(Guid.NewGuid().ToString("N"))
                .WithEnvironment("LEARNSTACK_MIGRATION_PW", migrationPassword)
                .WithEnvironment("LEARNSTACK_APP_PW", Guid.NewGuid().ToString("N"))
                .WithEnvironment("LEARNSTACK_PLATFORM_PW", Guid.NewGuid().ToString("N"))
                .WithEnvironment("LEARNSTACK_OUTBOX_PW", outboxPassword)
                // Module commands append to its existing PostgreSQL arguments;
                // an extra executable token would become an invalid argument.
                .WithCommand("-c", "log_min_messages=panic", "-c", "log_min_error_statement=panic",
                    "-c", "log_statement=none", "-c", "log_connections=off", "-c", "log_disconnections=off")
                .WithCreateParameterModifier(parameters =>
                {
                    var ports = Required(Required(parameters.HostConfig).PortBindings);
                    Require(ports.Count == 1 && ports.Single().Key == "5432/tcp");
                    var bindings = Required(ports.Single().Value);
                    Require(bindings.Count == 1);
                    bindings[0].HostIP = "127.0.0.1";
                })
                .Build();
            stage = "postgres_start";
            await postgres.StartAsync(deadline.Token);
            stage = "postgres_setup_binding";
            QualifySetupBinding(await docker.Containers.InspectContainerAsync(postgres.Id, deadline.Token));
            stage = "postgres_role_script_read";
            var script = await File.ReadAllBytesAsync(RepositoryPath.RolesScript(), deadline.Token);
            stage = "postgres_role_script_copy";
            await postgres.CopyAsync(script, "/tmp/phase-roles.sql", ct: deadline.Token);
            stage = "postgres_role_exec";
            var roles = await postgres.ExecAsync(
                ["psql", "-v", "ON_ERROR_STOP=1", "-U", "postgres", "-d", "learnstack", "-f", "/tmp/phase-roles.sql"],
                deadline.Token);
            stage = roles.ExitCode switch
            {
                0 => "postgres_role_result",
                1 => "postgres_role_exit_1",
                2 => "postgres_role_exit_2",
                3 => "postgres_role_exit_3",
                _ => "postgres_role_exit_other",
            };
            Require(roles.ExitCode == 0);
            // Setup uses the canonical migration inventory, never a vendor-only
            // handwritten schema or elevated credentials in the fatal child.
            stage = "postgres_migration_input";
            var migration = new NpgsqlConnectionStringBuilder(postgres.GetConnectionString())
            {
                Username = "learnstack_migration",
                Password = migrationPassword,
                Pooling = false,
                IncludeErrorDetail = false,
                LogParameters = false,
            };
            stage = "postgres_migrations";
            await MigrationChains.ApplyAllAsync(migration.ConnectionString).WaitAsync(deadline.Token);

            // Preparation uses the parent's existing Docker host endpoint. Before
            // fatal launch, detach the DB from that bridge and qualify that the
            // only remaining network is our internal two-container topology.
            stage = "postgres_internal_only";
            var before = await docker.Containers.InspectContainerAsync(postgres.Id, deadline.Token);
            QualifySetupBinding(before);
            var preparationSettings = Required(before.NetworkSettings);
            Require(preparationSettings.Networks.Count == 1);
            var preparationNetwork = preparationSettings.Networks.Single();
            Require(preparationNetwork.Key == "bridge");
            await docker.Networks.ConnectNetworkAsync(networkId, new NetworkConnectParameters
            {
                Container = postgres.Id,
                EndpointConfig = new EndpointSettings { Aliases = ["phase-postgres"] },
            }, deadline.Token);
            await docker.Networks.DisconnectNetworkAsync(preparationNetwork.Value.NetworkID,
                new NetworkDisconnectParameters { Container = postgres.Id, Force = true }, deadline.Token);
            var databaseConfiguration = await docker.Containers.InspectContainerAsync(postgres.Id, deadline.Token);
            QualifySetupBinding(databaseConfiguration);
            var databaseSettings = Required(databaseConfiguration.NetworkSettings);
            Require(databaseSettings.Networks.Count == 1
                && databaseSettings.Networks.Single().Value.NetworkID == networkId);

            await using var probe = new ContainerBuilder(RuntimeImage)
                .WithLogger(NullLogger.Instance)
                .WithNetwork(network)
                .WithBindMount(staged, "/opt/probe", AccessMode.ReadOnly)
                .WithEntrypoint("/bin/sh", "/opt/probe/preflight.sh")
                .WithCommand(mutation == "none" ? [scenario] : [scenario, mutation])
                .WithWorkingDirectory("/proof")
                .WithWaitStrategy(Wait.ForUnixContainer())
                .WithCreateParameterModifier(parameters =>
                {
                    parameters.User = "1654:1654";
                    parameters.OpenStdin = true;
                    parameters.StdinOnce = false;
                    parameters.AttachStdin = true;
                    parameters.HostConfig ??= new HostConfig();
                    parameters.HostConfig.ReadonlyRootfs = true;
                    parameters.HostConfig.CapDrop = ["ALL"];
                    parameters.HostConfig.SecurityOpt = ["no-new-privileges:true"];
                    parameters.HostConfig.Ulimits = [new Ulimit { Name = "core", Soft = 0, Hard = 0 }];
                    parameters.HostConfig.Tmpfs ??= new Dictionary<string, string>(StringComparer.Ordinal);
                    parameters.HostConfig.Tmpfs["/proof"] = "rw,noexec,nosuid,size=16777216,uid=1654,gid=1654,mode=0700";
                }).Build();
            stage = "supervisor_preflight";
            try { await probe.StartAsync(deadline.Token); }
            catch (Exception)
            {
                // Only exact, finite pre-credential refusal codes may leave
                // this boundary. Never export Docker/provider logs or text.
                if (!deadline.IsCancellationRequested && !string.IsNullOrEmpty(probe.Id))
                {
                    using var inspectionBudget = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try
                    {
                        var startupLogs = await probe.GetLogsAsync(timestampsEnabled: false, ct: inspectionBudget.Token);
                        stage = "supervisor_preflight_" + StartupRefusal(startupLogs.Stdout);
                    }
                    catch (Exception) { }
                }
                throw;
            }
            stage = "supervisor_topology_inspection";
            var actualProbe = await docker.Containers.InspectContainerAsync(probe.Id, deadline.Token);
            var actualNetwork = await docker.Networks.InspectNetworkAsync(networkId, deadline.Token);
            stage = "supervisor_topology_membership";
            var expectedMembership = actualNetwork.Internal && actualNetwork.Containers.Keys.ToHashSet(StringComparer.Ordinal)
                .SetEquals([postgres.Id, probe.Id]);
            if (!expectedMembership)
            {
                // This is still before private stdin. A failed membership check
                // is never retried into success, and diagnostics cannot replace
                // its failure or export endpoint identities/provider text.
                stage += "_" + TopologySummary(actualNetwork, actualProbe, postgres.Id, probe.Id);
                try
                {
                    using var diagnosticBudget = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    var startupLogs = await probe.GetLogsAsync(timestampsEnabled: false, ct: diagnosticBudget.Token);
                    stage += "_preflight_" + StartupRefusal(startupLogs.Stdout);
                }
                catch (Exception) { }
                Require(false);
            }
            stage = "supervisor_configuration";
            QualifyProbeConfiguration(actualProbe, networkId);
            stage = "supervisor_running_state";
            var runningState = Required(actualProbe.State);
            Require(runningState.Running && !runningState.OOMKilled);

            var connection = new NpgsqlConnectionStringBuilder
            {
                Host = "phase-postgres",
                Port = 5432,
                Database = "learnstack",
                Username = "learnstack_outbox_admin",
                Password = outboxPassword,
                // This isolated fixture uses its ephemeral password, not a
                // Kerberos identity or optional native GSS library discovery.
                GssEncryptionMode = GssEncryptionMode.Disable,
                IncludeErrorDetail = false,
                LogParameters = false,
                PersistSecurityInfo = false,
            };
            var input = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Protocol = 1, ConnectionString = connection.ConnectionString }) + "\n");
            Require(input.Length <= 4096);
            stage = "private_stdin";
            using (var stdin = await docker.Containers.AttachContainerAsync(probe.Id, new ContainerAttachParameters
            {
                Stream = true,
                Stdin = true,
                Stdout = false,
                Stderr = false,
            }, deadline.Token))
            {
                await stdin.WriteAsync(input, 0, input.Length, deadline.Token);
            }
            Array.Clear(input);

            stage = "actual_supervisor_exit";
            var exit = await docker.Containers.WaitContainerAsync(probe.Id, deadline.Token);
            var stopped = await docker.Containers.InspectContainerAsync(probe.Id, deadline.Token);
            var stoppedState = Required(stopped.State);
            Require(stoppedState.Status == "exited" && !stoppedState.Running && !stoppedState.Restarting
                && !stoppedState.OOMKilled && stoppedState.ExitCode == exit.StatusCode);
            // The supervisor's own exit is observed before any container cleanup;
            // no receipt readiness wait or external kill can supply that evidence.
            stage = "sanitized_receipt";
            var logs = await probe.GetLogsAsync(timestampsEnabled: false, ct: deadline.Token);
            var exitCategory = exit.StatusCode switch { 0 => "zero", 78 => "refused", 79 => "failed", _ => "other" };
            var prefixCategory = logs.Stdout.StartsWith(ReceiptPrefix, StringComparison.Ordinal) ? "generation"
                : logs.Stdout.StartsWith("LearnStackLifecycleReceipt:", StringComparison.Ordinal) ? "lifecycle" : "other";
            // Lengths have finite caps; content classification emits literals
            // only. No line, provider text or credential reaches the exception.
            stage = "sanitized_log_bounds_" + exitCategory
                + "_out_" + Math.Min(logs.Stdout.Length, 12_001).ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "_err_" + Math.Min(logs.Stderr.Length, 12_001).ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "_prefix_" + prefixCategory + "_stderr_" + StandardErrorCategory(logs.Stderr);
            if (logs.Stderr.Length != 0 || logs.Stdout.Length > 12_000)
            {
                stage += "_proof_" + ReceiptSummary(logs.Stdout);
                Require(false);
            }
            var refusal = StartupRefusal(logs.Stdout);
            if (refusal != "unknown")
            {
                stage = "supervisor_" + exitCategory + "_" + refusal;
                Require(false);
            }
            stage = "generation_receipt_parse_" + exitCategory;
            var receipt = GenerationPhaseReceipt.Parse(logs.Stdout, ReceiptPrefix);
            stage = "runtime_identity";
            var runtime = await docker.Images.InspectImageAsync(stopped.Image, deadline.Token);
            var databaseImage = await docker.Images.InspectImageAsync(databaseConfiguration.Image, deadline.Token);
            Require(runtime.Os == "linux" && databaseImage.Os == "linux"
                && IsImageId(runtime.ID) && IsImageId(databaseImage.ID)
                && runtime.Architecture == (receipt.Architecture == "Arm64" ? "arm64" : "amd64"));
            return new(receipt, checked((int)exit.StatusCode), RuntimeImage, runtime.ID, PostgresImage, databaseImage.ID,
                InternalTopologyQualified: true, OriginalVendorBinaries: true, Mutation: mutation);
        }
        catch (Exception failure)
        {
            // Provider/Docker text can contain input material. Only this fixed
            // finite stage/category escapes; neither raw logs nor an inner
            // exception does. SQLSTATE is mapped to a closed finite allowlist.
            throw new InvalidOperationException("The isolated generation phase fixture refused: " + stage
                + ":" + FailureCategory(failure));
        }
    }

    private static void QualifySetupBinding(ContainerInspectResponse container)
    {
        var host = Required(container.HostConfig);
        var configured = Required(host.PortBindings);
        Require(configured.Count == 1 && configured.Single().Key == "5432/tcp");
        var bindings = Required(configured.Single().Value);
        Require(bindings.Count == 1 && bindings[0].HostIP == "127.0.0.1");
        // Detaching the preparation bridge does not erase Docker's published
        // port configuration. Its only permitted setup binding stays loopback.
        var actual = Required(Required(container.NetworkSettings).Ports);
        Require(actual.Keys.All(key => key == "5432/tcp"));
        foreach (var entries in actual.Values)
            Require(entries is null || entries.Count == 1 && entries[0].HostIP == "127.0.0.1");
    }

    private static string FailureCategory(Exception failure)
    {
        Exception? current = failure;
        for (var depth = 0; current is not null && depth < 5; depth++, current = current.InnerException)
            if (current is PostgresException postgres)
                return postgres.SqlState switch
                {
                    "28P01" => "postgres_authentication",
                    "28000" => "postgres_authorization",
                    "42501" => "postgres_privilege",
                    "3D000" => "postgres_database_missing",
                    "42P01" => "postgres_table_missing",
                    "42704" => "postgres_object_missing",
                    "42710" or "42P07" => "postgres_object_duplicate",
                    "23505" => "postgres_unique",
                    "22023" => "postgres_parameter",
                    "08001" or "08006" => "postgres_connection",
                    "57P03" => "postgres_not_ready",
                    _ => "postgres_other",
                };
        return failure switch
        {
            InspectionRefusal refusal => "configuration_" + refusal.Reason.ToString().ToLowerInvariant(),
            NpgsqlException => "npgsql",
            DockerApiException => "docker",
            TimeoutException => "timeout",
            OperationCanceledException => "canceled",
            IOException => "io",
            ArgumentException => "argument",
            InvalidOperationException => "invalid_operation",
            _ => "other",
        };
    }

    private static void QualifyProbeConfiguration(ContainerInspectResponse container, string networkId)
    {
        var configuration = Required(container.Config);
        var host = Required(container.HostConfig);
        var network = Required(container.NetworkSettings);
        Inspect(configuration.User == "1654:1654", InspectionReason.User);
        Inspect(configuration.OpenStdin, InspectionReason.Stdin);
        Inspect(host.ReadonlyRootfs, InspectionReason.Root);
        Inspect(host.CapDrop.Contains("ALL", StringComparer.Ordinal), InspectionReason.Capabilities);
        Inspect(host.SecurityOpt.Contains("no-new-privileges:true", StringComparer.Ordinal), InspectionReason.Privileges);
        Inspect(network.Networks.Count == 1 && network.Networks.Single().Value.NetworkID == networkId, InspectionReason.Network);
        var inputs = container.Mounts.Where(mount => mount.Type == "bind").ToArray();
        Inspect(inputs.Length == 1 && inputs[0].Destination == "/opt/probe" && !inputs[0].RW, InspectionReason.Input);
        // Docker's legacy HostConfig.Tmpfs mount is not consistently repeated
        // in Mounts. Qualify its actual inspected configuration here; managed
        // preflight independently verifies /proc/mounts before reading stdin.
        var tmpfs = host.Tmpfs;
        Inspect(tmpfs is not null && tmpfs.Count == 1 && tmpfs.TryGetValue("/proof", out var scratch) && scratch is not null
            && scratch.Split(',').ToHashSet(StringComparer.Ordinal).SetEquals(
                ["rw", "noexec", "nosuid", "size=16777216", "uid=1654", "gid=1654", "mode=0700"])
            && container.Mounts.Where(mount => mount.Destination == "/proof")
                .All(mount => mount.Type == "tmpfs" && mount.RW), InspectionReason.Scratch);
        Inspect(host.PortBindings.Count == 0, InspectionReason.Ports);
        var core = host.Ulimits.Where(limit => limit.Name == "core").ToArray();
        Inspect(core.Length == 1 && core[0].Soft == 0 && core[0].Hard == 0, InspectionReason.Core);
    }

    internal static string StartupRefusal(string stdout)
    {
        const string prefix = "LearnStackLifecycleReceipt:";
        if (stdout.Length > 4096 || !stdout.StartsWith(prefix, StringComparison.Ordinal)) return "unknown";
        try
        {
            using var document = JsonDocument.Parse(stdout[prefix.Length..]);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2
                || !root.TryGetProperty("Protocol", out var protocol) || protocol.ValueKind != JsonValueKind.Number
                || !protocol.TryGetInt32(out var version) || version != 1
                || !root.TryGetProperty("Error", out var error) || error.ValueKind != JsonValueKind.String) return "unknown";
            var code = error.GetString();
            if (PhaseFailureStages.Any(phase => PhaseFailureCategories.Any(category => code == "phase_" + phase + "_" + category)))
                return code!;
            return code switch
            {
                "argument_refused" or "collector_unreadable" or "collector_pipe" or "collector_unsupported"
                    or "soft_core_not_zero" or "hard_core_not_zero" or "soft_core_refused" or "hard_core_refused"
                    or "limits_unreadable" or "scratch_refused" or "managed_preflight_refused" or "probe_failed"
                    => error.GetString()!,
                _ => "unknown",
            };
        }
        catch (JsonException) { return "unknown"; }
    }

    private static string StandardErrorCategory(string stderr)
    {
        if (stderr.Length == 0) return "empty";
        if (string.IsNullOrWhiteSpace(stderr)) return "whitespace";
        if (stderr.Contains("Process terminated. LearnStack jobs fatal: transactional:", StringComparison.Ordinal)) return "fatal";
        if (stderr.StartsWith("LearnStackLifecycleReceipt:", StringComparison.Ordinal)) return "lifecycle";
        if (stderr.StartsWith("LearnStackGenerationReceipt:", StringComparison.Ordinal)) return "generation";
        if (stderr.Contains("/bin/sh:", StringComparison.Ordinal) || stderr.Contains("/opt/probe/preflight.sh:", StringComparison.Ordinal)) return "shell";
        if (stderr.Contains("Welcome to .NET", StringComparison.Ordinal)) return "dotnet_welcome";
        var labels = StandardErrorTokens.Where(item => stderr.Contains(item.Token, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Label).ToArray();
        return labels.Length == 0 ? "other" : string.Join('_', labels);
    }

    private static string ReceiptSummary(string stdout)
    {
        if (stdout.Length > 12_000) return "oversized";
        try
        {
            var receipt = GenerationPhaseReceipt.Parse(stdout, ReceiptPrefix);
            var child = receipt.Child;
            return receipt.Error + "_twin_" + NativeExitCategory(receipt.Twin.ExitCode)
                + "_events_" + receipt.Twin.Events.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "_child_" + NativeExitCategory(child?.ExitCode)
                + "_events_" + (child?.Events.Length ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "_fault_" + (child?.Events.Contains("fault_site", StringComparer.Ordinal) == true ? "yes" : "no")
                + "_diagnostic_" + (child?.Diagnostic == true ? "yes" : "no");
        }
        catch (Exception failure) when (failure is JsonException or InvalidOperationException) { return "invalid"; }
    }

    private static string NativeExitCategory(long? code) => code switch
    {
        null => "absent",
        0 => "zero",
        134 => "abort",
        78 => "refused",
        79 => "failed",
        _ => "other",
    };

    internal static string TopologySummary(NetworkResponse network, ContainerInspectResponse probe,
        string postgresId, string probeId)
    {
        var members = network.Containers;
        var state = probe.State;
        return "internal_" + (network.Internal ? "yes" : "no")
            + "_members_" + Math.Min(members?.Count ?? 0, 3).ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "_database_" + (members?.ContainsKey(postgresId) == true ? "yes" : "no")
            + "_supervisor_" + (members?.ContainsKey(probeId) == true ? "yes" : "no")
            + "_running_" + (state?.Running == true ? "yes" : "no")
            + "_oom_" + (state?.OOMKilled == true ? "yes" : "no")
            + "_exit_" + NativeExitCategory(state?.ExitCode);
    }

    private enum InspectionReason { User, Stdin, Root, Capabilities, Privileges, Network, Input, Scratch, Ports, Core }
    private sealed class InspectionRefusal(InspectionReason reason) : Exception
    {
        internal InspectionReason Reason { get; } = reason;
    }
    private static void Inspect(bool condition, InspectionReason reason)
    {
        if (!condition) throw new InspectionRefusal(reason);
    }

    private static async Task VerifyVendorBinariesAsync(string staged)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(RepositoryPath.RolesScript())!, "../../.."));
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "third-party/hangfire/original-binaries.json")));
        foreach (var name in new[] { "Hangfire.Core.dll", "Hangfire.PostgreSql.dll" })
        {
            var expected = manifest.RootElement.GetProperty(name).GetProperty("sha256").GetString();
            await using var file = File.OpenRead(Path.Combine(staged, name));
            var digest = await SHA256.HashDataAsync(file);
            Require(Convert.ToHexStringLower(digest) == expected);
        }
    }

    private static bool IsImageId(string value) => value.Length == 71 && value.StartsWith("sha256:", StringComparison.Ordinal)
        && value.AsSpan(7).IndexOfAnyExcept(HexDigits) < 0;

    private static T Required<T>(T? value) where T : class => value
        ?? throw new InvalidOperationException("The owned generation proof inspection was incomplete.");

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("The owned generation proof prerequisite failed.");
    }
}

internal sealed record GenerationPhaseEvidence(GenerationPhaseReceipt Receipt, int SupervisorExitCode,
    string RuntimeImage, string RuntimeImageId, string PostgresImage, string PostgresImageId,
    bool InternalTopologyQualified, bool OriginalVendorBinaries, string Mutation);

internal sealed record GenerationPhaseChild(bool Exited, int? ExitCode, bool TimedOut, bool Diagnostic,
    bool Finally, bool ProcessExit, bool ProtocolInvalid, string[] Events, long Discarded,
    bool ExternalKill = false, bool OwnedProcessJoined = false);

internal sealed record GenerationPhaseReceipt(int Protocol, string Error, string Scenario, string Runtime,
    string Architecture, string Collector, bool BenignControlPassed, GenerationPhaseChild Twin,
    GenerationPhaseChild? Child, bool ArtifactsAllowed, long ServerRowsAfter, long BusinessRowsAfter)
{
    private static readonly string[] Fields =
    [
        "Protocol", "Error", "Scenario", "Runtime", "Architecture", "Collector", "BenignControlPassed", "Twin", "Child",
        "ArtifactsAllowed", "ServerRowsAfter", "BusinessRowsAfter",
    ];
    private static readonly string[] ChildFields =
    [
        "Exited", "ExitCode", "TimedOut", "Diagnostic", "Finally", "ProcessExit", "ProtocolInvalid", "Events", "Discarded",
        "ExternalKill", "OwnedProcessJoined",
    ];
    internal static readonly string[] EventNames =
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

    internal static GenerationPhaseReceipt Parse(string stdout, string prefix)
    {
        var lines = stdout.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != 1 || !lines[0].StartsWith(prefix, StringComparison.Ordinal) || lines[0].Length > 10_000)
            throw new InvalidOperationException("The generation proof emitted an unsupported receipt.");
        using var parsed = JsonDocument.Parse(lines[0][prefix.Length..]);
        var root = parsed.RootElement;
        ExactFields(root, Fields);
        ExactFields(root.GetProperty(nameof(Twin)), ChildFields);
        if (root.GetProperty(nameof(Child)).ValueKind != JsonValueKind.Null) ExactFields(root.GetProperty(nameof(Child)), ChildFields);
        var receipt = JsonSerializer.Deserialize<GenerationPhaseReceipt>(root.GetRawText())
            ?? throw new InvalidOperationException("The generation proof emitted no receipt.");
        if (receipt.Error is not ("none" or "benign_control_failed") || !ValidChild(receipt.Twin)
            || receipt.Child is not null && !ValidChild(receipt.Child))
            throw new InvalidOperationException("The generation proof emitted unsupported phase data.");
        return receipt;
    }

    private static bool ValidChild(GenerationPhaseChild child) => child.Events is not null
        && child.Events.Length <= EventNames.Length && child.Events.Distinct(StringComparer.Ordinal).Count() == child.Events.Length
        && child.Events.All(value => EventNames.Contains(value, StringComparer.Ordinal)) && child.Discarded is >= 0 and <= 1_000_000;

    private static void ExactFields(JsonElement element, string[] fields)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.EnumerateObject().Select(property => property.Name)
                .Order(StringComparer.Ordinal).SequenceEqual(fields.Order(StringComparer.Ordinal)))
            throw new InvalidOperationException("The generation proof receipt fields changed.");
    }
}
