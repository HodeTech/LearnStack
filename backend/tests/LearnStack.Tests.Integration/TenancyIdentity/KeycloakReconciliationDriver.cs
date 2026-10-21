using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace LearnStack.Tests.Integration.TenancyIdentity;

/// <summary>Owns a real Node child and a private pipe; no credential enters argv, inherited environment or disk.</summary>
internal static class KeycloakReconciliationDriver
{
    internal static async Task<ReconciliationOutcome> RunAsync(KeycloakFixture fixture, string scenario = "normal")
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(140));
        var start = new ProcessStartInfo("node")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(Path.Combine(Path.GetDirectoryName(KeycloakFixture.RealmPath("learnstack"))!,
            "..", "..", "..", "backend", "tests", "LearnStack.Tests.Integration", "TenancyIdentity", "reconciliation-driver.mjs"));
        var executablePath = Environment.GetEnvironmentVariable("PATH");
        start.Environment.Clear();
        if (executablePath is not null) start.Environment["PATH"] = executablePath;
        using var child = new Process { StartInfo = start };
        var started = false;
        var failed = false;
        var cleanupFailed = false;
        ReconciliationOutcome? outcome = null;
        Task<string>? output = null;
        Task<string>? errors = null;
        try
        {
            started = child.Start();
            if (!started) throw new InvalidOperationException();
            output = ReadBoundedAsync(child.StandardOutput, deadline.Token);
            errors = ReadBoundedAsync(child.StandardError, deadline.Token);
            await fixture.WriteReconciliationInputAsync(child.StandardInput.BaseStream, scenario, deadline.Token);
            child.StandardInput.Close();
            await Task.WhenAll(child.WaitForExitAsync(deadline.Token), output, errors);
            if ((await errors).Length != 0) throw new InvalidOperationException();
            using var json = JsonDocument.Parse(await output);
            var result = json.RootElement;
            var status = result.GetProperty("outcome").GetString();
            if (status is not ("success" or "failure")) throw new InvalidOperationException();
            var success = status == "success";
            if (child.ExitCode != (success ? 0 : 1)) throw new InvalidOperationException();
            var mutations = result.GetProperty("mutations").GetInt32();
            if (mutations < 0 || mutations > 256) throw new InvalidOperationException();
            var preparationDispatched = result.TryGetProperty("preparationDispatched", out var preparation);
            if (preparationDispatched && preparation.ValueKind != JsonValueKind.True) throw new InvalidOperationException();
            var stage = success ? null : result.GetProperty("stage").GetString();
            var reason = success ? null : result.GetProperty("reason").GetString();
            // Only the driver's closed vocabulary can leave this process owner.
            if (!success && (!Stages.Contains(stage, StringComparer.Ordinal) || !Reasons.Contains(reason, StringComparer.Ordinal)))
                throw new InvalidOperationException();
            outcome = new ReconciliationOutcome(success, mutations, stage, reason, preparationDispatched);
        }
        catch (Exception)
        {
            failed = true;
        }
        finally
        {
            if (started)
            {
                try
                {
                    if (!child.HasExited)
                    {
                        try { child.Kill(entireProcessTree: true); }
                        catch (InvalidOperationException) when (child.HasExited) { }
                    }
                    await child.WaitForExitAsync(CancellationToken.None);
                    // Readers are owned too, even when input serialization or the child failed first.
                    if (output is not null) try { await output; } catch (Exception) { }
                    if (errors is not null) try { await errors; } catch (Exception) { }
                }
                catch (Exception)
                {
                    cleanupFailed = true;
                }
            }
        }
        if (cleanupFailed) throw new InvalidOperationException("Keycloak reconciliation driver owned cleanup failed.");
        if (failed || outcome is null)
            throw new InvalidOperationException("Keycloak reconciliation driver failed after owned child cleanup.");
        return outcome;
    }

    private static readonly string[] Stages =
        ["preflight", "discovery", "authentication", "version", "manifest", "lock", "plan", "prepare", "apply", "readback", "cleanup"];
    private static readonly string[] Reasons =
        ["configuration", "identity_conflict", "mapper_conflict", "secret_conflict", "unsupported", "response", "status",
            "redirect", "deadline", "cancelled", "transport", "lock_busy", "body_limit", "readback_conflict", "cleanup", "missing_web_secret"];

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var buffer = new char[512];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            if (text.Length + count > 4096) throw new InvalidOperationException("Reconciliation child output exceeded its fixed bound.");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }
}

internal sealed record ReconciliationOutcome(bool Success, int Mutations, string? Stage, string? Reason, bool PreparationDispatched = false);
