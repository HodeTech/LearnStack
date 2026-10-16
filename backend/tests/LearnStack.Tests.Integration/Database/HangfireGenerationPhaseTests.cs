using System.Text.Json;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// Synthetic placement in unchanged pinned Core, actual production generation
/// phase selection, real PostgreSQL, and independently observed native exit.
/// These cases do not reproduce genuine resource exhaustion.
/// </summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class HangfireGenerationPhaseTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("gen_constructor_before")]
    [InlineData("gen_constructor_after")]
    [InlineData("gen_required_before_capture")]
    [InlineData("gen_supplied_after_capture")]
    [InlineData("gen_post_builder")]
    [InlineData("gen_notify_succeeded")]
    [InlineData("gen_wait_entry")]
    [InlineData("gen_dispose_second_announce")]
    [InlineData("gen_dispose_outer_complete")]
    [InlineData("gen_announce_log")]
    [InlineData("gen_host_stop")]
    [InlineData("gen_final_exit")]
    [InlineData("gen_process_exit")]
    [InlineData("gen_stall_constructor")]
    [InlineData("gen_stall_required")]
    [InlineData("gen_stall_supplied")]
    [InlineData("gen_stall_postbuilder")]
    public async Task Actual_generation_phase_observations_dominate_the_native_process_result(string scenario)
    {
        var evidence = await HangfireGenerationPhaseHarness.RunAsync(scenario);
        // The evidence contains only already parsed finite protocol values,
        // image identities and booleans; raw runtime/provider logs never escape.
        output.WriteLine(JsonSerializer.Serialize(evidence));
        evidence.SupervisorExitCode.Should().Be(0, "the supervisor must exit normally before owned-container cleanup");
        evidence.InternalTopologyQualified.Should().BeTrue();
        evidence.OriginalVendorBinaries.Should().BeTrue();
        Refusal(evidence.Receipt, scenario).Should().Be("none",
            "native termination is decisive only with the actual Core phase and qualified normal twin");
    }

    [Theory]
    [InlineData("sink", "gen_post_builder", "fatal_gate_returned", "native_result", 0)]
    [InlineData("constructor", "gen_constructor_before", "constructor_boundary_returned", "native_result", 0)]
    [InlineData("capture_dispose", "gen_post_builder", "capture_dispose_returned", "capture_returned", 134)]
    [InlineData("second_announce", "gen_dispose_second_announce", "second_announcement_returned", "second_announcement_returned", 134)]
    [InlineData("outer_completion", "gen_dispose_outer_complete", "outer_completion_returned", "native_result", 0)]
    public async Task Removing_the_actual_fatal_call_or_phase_observer_makes_the_same_child_control_fail(
        string mutation, string scenario, string returnedMarker, string refusal, int nativeExit)
    {
        var evidence = await HangfireGenerationPhaseHarness.RunAsync(scenario, mutation);
        output.WriteLine(JsonSerializer.Serialize(evidence));
        evidence.SupervisorExitCode.Should().Be(0);
        evidence.Mutation.Should().Be(mutation);
        evidence.InternalTopologyQualified.Should().BeTrue();
        evidence.OriginalVendorBinaries.Should().BeTrue();
        var receipt = evidence.Receipt;
        receipt.Error.Should().Be("none");
        receipt.BenignControlPassed.Should().BeTrue();
        receipt.ArtifactsAllowed.Should().BeTrue();
        receipt.BusinessRowsAfter.Should().Be(0);
        receipt.Child.Should().NotBeNull();
        var child = receipt.Child!;
        child.Exited.Should().BeTrue();
        child.TimedOut.Should().BeFalse();
        child.ProtocolInvalid.Should().BeFalse();
        child.Events.Should().Contain("armed").And.Contain("fault_site").And.Contain(returnedMarker);
        child.ExitCode.Should().Be(nativeExit);
        child.Diagnostic.Should().Be(nativeExit == 134);
        child.Finally.Should().Be(nativeExit == 0);
        child.ProcessExit.Should().Be(nativeExit == 0);
        child.Events.Should().NotContain("normal_cleanup");
        receipt.ServerRowsAfter.Should().Be(mutation == "constructor" ? 0 : 1);
        if (mutation != "constructor")
            child.Events.Should().Contain("first_announcement").And.Contain("required_thread_held");
        if (mutation is "sink" or "capture_dispose")
            child.Events.Should().Contain("capture_dispose");
        if (mutation is "second_announce" or "outer_completion")
        {
            child.Events.Should().Contain("required_dispose_fault").And.NotContain("capture_dispose").And.NotContain("removal_entered");
            child.Events.Should().Contain(mutation == "second_announce" ? "second_announcement" : "outer_dispatcher_fault");
        }
        Refusal(receipt, scenario).Should().Be(refusal,
            "the exact normal predicate must reject the actual modified child's result, including a later fallback fatal branch");
    }

    [Fact]
    public void The_phase_predicate_rejects_missing_fault_sites_and_wrong_observation_branches()
    {
        HangfireGenerationPhaseHarness.StartupRefusal("LearnStackLifecycleReceipt:{\"Protocol\":1,\"Error\":\"collector_pipe\"}")
            .Should().Be("collector_pipe");
        HangfireGenerationPhaseHarness.StartupRefusal("LearnStackLifecycleReceipt:{\"Protocol\":1,\"Error\":\"phase_envelope_json\"}")
            .Should().Be("phase_envelope_json");
        foreach (var unsupported in new[]
        {
            "{\"Protocol\":\"1\",\"Error\":\"collector_pipe\"}",
            "{\"Protocol\":1,\"Error\":\"provider detail\"}",
            "{\"Protocol\":1,\"Error\":\"phase_envelope_provider detail\"}",
            "{\"Protocol\":1,\"Error\":\"collector_pipe\",\"Detail\":\"provider detail\"}",
            "{\"Protocol\":1,\"Error\":\"none\"}", "{",
        }) HangfireGenerationPhaseHarness.StartupRefusal("LearnStackLifecycleReceipt:" + unsupported).Should().Be("unknown");
        var twin = new GenerationPhaseChild(true, 0, false, false, true, true, false,
            ["armed", "first_announcement", "required_thread_held", "fault_site", "capture_dispose", "removal_entered", "normal_cleanup"], 0);
        var child = new GenerationPhaseChild(true, 134, false, true, false, false, false,
            ["armed", "first_announcement", "required_thread_held", "fault_site", "required_dispose_fault", "second_announcement"], 0);
        var clean = new GenerationPhaseReceipt(1, "none", "gen_dispose_second_announce", ".NET 10.0.10", "Arm64",
            "core", true, twin, child, true, 1, 0);
        Refusal(clean, clean.Scenario).Should().Be("none");
        foreach (var dirty in new[]
        {
            clean with { Child = child with { Events = child.Events.Where(value => value != "fault_site").ToArray() } },
            clean with { Child = child with { Events = child.Events.Where(value => value != "required_dispose_fault").ToArray() } },
            clean with { Child = child with { Events = child.Events.Where(value => value != "second_announcement").ToArray() } },
            clean with { Child = child with { Events = [.. child.Events, "capture_dispose"] } },
            clean with { Child = child with { Events = [.. child.Events, "removal_entered"] } },
            clean with { Child = child with { Events = [.. child.Events, "outer_dispatcher_fault"] } },
            clean with { Child = child with { Events = [.. child.Events, "second_announcement_returned"] } },
            clean with { Child = child with { ExitCode = 0, Diagnostic = false, Finally = true, ProcessExit = true } },
            clean with { Child = child with { Exited = false, TimedOut = true } },
            clean with { ArtifactsAllowed = false },
            clean with { ServerRowsAfter = 2 },
            clean with { BusinessRowsAfter = 1 },
            clean with { BenignControlPassed = false },
            clean with { Twin = twin with { Events = twin.Events.Where(value => value != "normal_cleanup").ToArray() } },
        }) Refusal(dirty, clean.Scenario).Should().NotBe("none");
        // Predicate controls are deliberately labelled separately from an
        // actual same-child suppression mutation, which owns its own run.
    }

    internal static string Refusal(GenerationPhaseReceipt receipt, string scenario)
    {
        if (receipt.Protocol != 1 || receipt.Error != "none" || receipt.Scenario != scenario) return "identity";
        if (receipt.Runtime != ".NET 10.0.10" || receipt.Architecture is not ("Arm64" or "X64")
            || receipt.Collector is not ("core" or "core.%p")) return "runtime_preflight";
        if (!receipt.BenignControlPassed || !Normal(receipt.Twin) || !Has(receipt.Twin, "normal_cleanup")) return "normal_twin";
        if (!receipt.ArtifactsAllowed || receipt.BusinessRowsAfter != 0 || receipt.ServerRowsAfter is < 0 or > 1) return "artifacts_or_rows";
        var child = receipt.Child;
        if (child is null || child.ProtocolInvalid || !child.Exited || child.TimedOut
            || child.Discarded is < 0 or > 1_000_000 || !Has(child, "armed") || !Has(child, "fault_site")) return "phase_or_exit";
        var stalled = scenario.StartsWith("gen_stall_", StringComparison.Ordinal);
        var fatal = scenario is not ("gen_final_exit" or "gen_process_exit") && !stalled;
        // A fallback fatal sink cannot rescue an observer that already returned
        // through the unsafe phase. These are actual Core post-return markers.
        if (fatal && child.ExitCode == 134 && Has(child, "capture_dispose_returned")) return "capture_returned";
        if (fatal && Has(child, "second_announcement_returned")) return "second_announcement_returned";
        if (child.ExitCode != (fatal ? 134 : 0) || child.Diagnostic != fatal
            || child.Finally != !fatal || child.ProcessExit != !fatal) return "native_result";
        if (fatal && Has(child, "normal_cleanup")) return "false_cleanup";
        if (scenario is not ("gen_constructor_before" or "gen_constructor_after" or "gen_stall_constructor")
            && !Has(child, "first_announcement")) return "announcement";
        if (scenario is not ("gen_constructor_before" or "gen_constructor_after" or "gen_announce_log" or "gen_stall_constructor")
            && !Has(child, "required_thread_held")) return "held_stock_thread";
        if (scenario is not ("gen_constructor_before" or "gen_constructor_after") && !stalled && receipt.ServerRowsAfter != 1)
            return "confirmed_metadata";
        if (stalled)
            return Normal(child) && receipt.ServerRowsAfter == 0 && Has(child, "startup_stalled") && Has(child, "late_cleanup")
                && Has(child, scenario == "gen_stall_constructor" ? "late_handle" : "late_progress")
                && Before(child, "startup_stalled", "late_cleanup") ? "none" : "stalled_ownership";

        return scenario switch
        {
            "gen_constructor_before" => receipt.ServerRowsAfter == 0
                && !Has(child, "first_announcement") && !Has(child, "capture_dispose") ? "none" : "constructor_before",
            "gen_constructor_after" => !Has(child, "normal_cleanup") ? "none" : "constructor_after",
            "gen_required_before_capture" => Has(child, "removal_entered") && !Has(child, "capture_dispose")
                && !Has(child, "second_announcement") ? "none" : "before_capture",
            "gen_supplied_after_capture" or "gen_post_builder" or "gen_wait_entry" =>
                SuppliedUnwind(child) ? "none" : "supplied_unwind",
            "gen_notify_succeeded" => SuppliedUnwind(child) && Has(child, "preannounce_fault") && Has(child, "notify_entered")
                && Before(child, "preannounce_fault", "first_announcement")
                && Before(child, "notify_entered", "fault_site") ? "none" : "notify_succeeded",
            "gen_dispose_second_announce" => SkippedRemoval(child) && Has(child, "second_announcement")
                && !Has(child, "outer_failure") && !Has(child, "outer_dispatcher_fault")
                && Before(child, "required_dispose_fault", "second_announcement") ? "none" : "second_announcement",
            "gen_dispose_outer_complete" => SkippedRemoval(child) && Has(child, "outer_failure")
                && Has(child, "outer_dispatcher_fault") && !Has(child, "second_announcement")
                && Before(child, "required_dispose_fault", "outer_failure")
                && Before(child, "outer_failure", "outer_dispatcher_fault") ? "none" : "outer_completion",
            "gen_announce_log" => Has(child, "second_announcement") && !Has(child, "capture_dispose")
                && !Has(child, "removal_entered") && Before(child, "fault_site", "second_announcement") ? "none" : "announcement_log",
            "gen_host_stop" => SuppliedUnwind(child) && Has(child, "host_stopped")
                && Before(child, "host_stopped", "fault_site") && !Has(child, "final_selected") ? "none" : "host_stop",
            "gen_final_exit" => Normal(child) && Has(child, "final_selected") && Has(child, "terminal_retained")
                && !Has(child, "normal_cleanup") && Before(child, "final_selected", "fault_site") ? "none" : "final_priority",
            "gen_process_exit" => Normal(child) && Has(child, "final_observed") && !Has(child, "final_selected")
                && !Has(child, "normal_cleanup") ? "none" : "process_exit_order",
            _ => "unknown_scenario",
        };
    }

    private static bool Normal(GenerationPhaseChild child) => child.Exited && child.ExitCode == 0 && !child.TimedOut
        && !child.Diagnostic && child.Finally && child.ProcessExit && !child.ProtocolInvalid
        && Has(child, "armed") && Has(child, "fault_site");
    private static bool Has(GenerationPhaseChild child, string marker) => child.Events.Contains(marker, StringComparer.Ordinal);
    private static bool Before(GenerationPhaseChild child, string first, string second) =>
        Array.IndexOf(child.Events, first) >= 0 && Array.IndexOf(child.Events, first) < Array.IndexOf(child.Events, second);
    private static bool SuppliedUnwind(GenerationPhaseChild child) => Has(child, "capture_dispose")
        && !Has(child, "removal_entered") && !Has(child, "second_announcement");
    private static bool SkippedRemoval(GenerationPhaseChild child) => Has(child, "required_dispose_fault")
        && !Has(child, "capture_dispose") && !Has(child, "removal_entered");
}
