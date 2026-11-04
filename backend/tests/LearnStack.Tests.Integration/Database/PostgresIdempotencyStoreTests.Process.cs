using System.Diagnostics;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Idempotency;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class PostgresIdempotencyStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SIGKILL_preserves_committed_claims_and_a_new_process_replays_completed_original_targets(bool complete)
    {
        var tenant = Tenant();
        var target = Guid.CreateVersion7();
        const string key = "actual-process-key";
        using var child = Probe(complete ? "complete" : "claim", tenant, target, key);
        Assert.True(child.Start());
        var errors = ReadBoundedAsync(child.StandardError);
        try
        {
            Assert.Equal("IdempotencyProbe:" + (complete ? "complete" : "claim"),
                await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)));
            Assert.False(child.HasExited);
            var before = await SnapshotAsync(tenant);
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotEqual(0, child.ExitCode);
            Assert.DoesNotContain("finally", await ReadBoundedAsync(child.StandardOutput), StringComparison.Ordinal);
            Assert.Empty(await errors);
            Assert.Equal(before, await SnapshotAsync(tenant));
            if (complete)
            {
                using var restarted = Probe("verify", tenant, target, key);
                Assert.True(restarted.Start());
                try
                {
                    var restartedErrors = ReadBoundedAsync(restarted.StandardError);
                    Assert.Equal("IdempotencyProbe:verified", await restarted.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)));
                    await restarted.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.Equal(0, restarted.ExitCode);
                    Assert.Empty(await restartedErrors);
                }
                finally
                {
                    if (!restarted.HasExited) { restarted.Kill(entireProcessTree: true); await restarted.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                }
                Assert.Equal(before, await SnapshotAsync(tenant));
            }
            else
            {
                await using var source = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
                var store = Store(source);
                Assert.Equal(IdempotencyClaim.InFlight,
                    (await store.TryClaimAsync(tenant, key, "process-fingerprint", CancellationToken.None)).Outcome);
                await ExpireAsync(tenant, key);
                Assert.Equal(IdempotencyClaim.Acquired,
                    (await store.TryClaimAsync(tenant, key, "process-fingerprint", CancellationToken.None)).Outcome);
            }
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            await errors;
        }
    }

    private Process Probe(string mode, TenantId tenant, Guid target, string key)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "idempotency-store-probe", "LearnStack.Tests.IdempotencyStoreProbe.dll");
        Assert.True(File.Exists(executable), "The separately built store probe must be staged by the Integration project.");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(executable);
        start.ArgumentList.Add(mode);
        start.Environment.Clear();
        foreach (var name in new[] { "PATH", "DOTNET_ROOT", "DOTNET_ROOT_ARM64", "TMPDIR", "LANG" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        start.Environment["DOTNET_EnableDiagnostics"] = "0";
        start.Environment["DOTNET_DbgEnableMiniDump"] = "0";
        start.Environment["COMPlus_DbgEnableMiniDump"] = "0";
        start.Environment["LEARNSTACK_IDEMPOTENCY_PROOF_APP"] = schema.Postgres.AppConnectionString;
        start.Environment["LEARNSTACK_IDEMPOTENCY_PROOF_TENANT"] = tenant.Value.ToString("D");
        start.Environment["LEARNSTACK_IDEMPOTENCY_PROOF_TARGET"] = target.ToString("D");
        start.Environment["LEARNSTACK_IDEMPOTENCY_PROOF_KEY"] = key;
        return new Process { StartInfo = start };
    }

    private static async Task<string> ReadBoundedAsync(StreamReader stream)
    {
        var buffer = new char[1024];
        var text = new System.Text.StringBuilder();
        int read;
        while ((read = await stream.ReadAsync(buffer)) != 0)
        {
            if (text.Length + read > 8192) throw new InvalidOperationException("The process proof exceeded its output bound.");
            text.Append(buffer, 0, read);
        }
        return text.ToString();
    }
}
