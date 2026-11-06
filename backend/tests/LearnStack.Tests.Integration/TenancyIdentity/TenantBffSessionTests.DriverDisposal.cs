using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace LearnStack.Tests.Integration.TenancyIdentity;

public sealed partial class TenantBffSessionTests
{
    /// <summary>
    /// <c>await using</c> replaces the exception already in flight with whatever disposal throws, so a retained
    /// private tree once hid the fixture's own closed failure. The driver keeps its first failure authoritative and
    /// still refuses a cleanup-only failure.
    /// </summary>
    [Fact]
    public async Task Driver_disposal_keeps_its_first_failure_and_still_refuses_a_cleanup_only_failure()
    {
        await AssertDisposalAsync(failFirst: true);
        await AssertDisposalAsync(failFirst: false);
    }

    private static async Task AssertDisposalAsync(bool failFirst)
    {
        var root = Directory.CreateTempSubdirectory("learnstack-bff-disposal-");
        var receiptPath = Path.Combine(root.FullName, "owned.json");
        string? retained = null;
        var childAbsenceConfirmed = false;
        try
        {
            var scripts = Path.Combine(root.FullName, "frontend/apps/web/scripts");
            Directory.CreateDirectory(scripts);
            // Ready on the first line; the second line is the command under test. Both variants leave the same
            // private directory behind, so only the earlier failure differs.
            await File.WriteAllTextAsync(Path.Combine(scripts, "verify-bff-session.mjs"), $$"""
                import { mkdirSync, writeFileSync } from 'node:fs';
                import { join } from 'node:path';
                let lines = 0;
                process.stdin.setEncoding('utf8');
                process.stdin.on('data', () => {
                  if (++lines === 1) {
                    process.stdout.write(JSON.stringify({ protocol: 1, phase: 'ready' }) + '\n');
                    return;
                  }
                  mkdirSync(join(process.env.TMPDIR, 'retained'));
                  writeFileSync({{JsonSerializer.Serialize(receiptPath)}}, JSON.stringify({ pid: process.pid, temporary: process.env.TMPDIR }));
                  if ({{(failFirst ? "true" : "false")}}) {
                    process.stderr.write('BFF session fixture failed: browser-devtools\nBFF session cleanup failed.\n');
                    process.stdin.destroy();
                    process.exitCode = 1;
                  } else {
                    process.stdout.write(JSON.stringify({ protocol: 1, phase: 'complete' }) + '\n');
                  }
                });
                """);
            var driver = await BffNativeDriver.StartAsync(root.FullName, new { protocol = 1 });
            if (failFirst)
            {
                var failure = await FluentActions.Awaiting(() => driver.InvitationBrowserAsync("tenant.example:3000", "code"))
                    .Should().ThrowAsync<InvalidOperationException>();
                failure.Which.Message.Should()
                    .Be("BFF native fixture did not reach the closed invitation_browser/browser-devtools_cleanup_failed phase.");
                await FluentActions.Awaiting(() => driver.DisposeAsync().AsTask()).Should().NotThrowAsync(
                    "a cleanup error must not replace the failure that already explains the test");
                failure.Which.Data["BffCleanupFailed"].Should().Be(true, "the lost cleanup fact is still recorded on the first failure");
            }
            else
            {
                await driver.CompleteAsync();
                await FluentActions.Awaiting(() => driver.DisposeAsync().AsTask()).Should().ThrowAsync<InvalidOperationException>()
                    .WithMessage("BFF native fixture retained its private TLS/build tree.");
            }
            using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(receiptPath));
            retained = receipt.RootElement.GetProperty("temporary").GetString()!;
            Path.GetFileName(retained).Should().StartWith("learnstack-bff-driver-");
            Directory.Exists(Path.Combine(retained, "retained")).Should().BeTrue("the cleanup failure is real, not simulated");
            var absent = false;
            try
            {
                using var child = Process.GetProcessById(receipt.RootElement.GetProperty("pid").GetInt32());
                absent = child.HasExited;
            }
            catch (ArgumentException) { absent = true; }
            childAbsenceConfirmed = absent;
            absent.Should().BeTrue("the driver joined its actual owned child before reporting");
        }
        finally
        {
            // Only this variant's independently witnessed retained tree is removed, and only once its child is gone.
            if (childAbsenceConfirmed)
            {
                if (retained is not null && Directory.Exists(retained)) Directory.Delete(retained, recursive: true);
                root.Delete(recursive: true);
            }
        }
    }
}
