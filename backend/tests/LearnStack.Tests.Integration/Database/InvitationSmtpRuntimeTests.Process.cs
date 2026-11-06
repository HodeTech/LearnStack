using System.Diagnostics;
using System.Globalization;
using FluentAssertions;
using Hangfire;
using LearnStack.Infrastructure.Email;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class InvitationSmtpRuntimeTests
{
    [Fact]
    public async Task Actual_Identity_SDK_disposal_failure_retains_owner_connector_and_shutdown_until_owned_process_exit()
    {
        await using var database = await OpenDatabaseAsync();
        await using var smtp = new InvitationSmtpTransportServer(InvitationSmtpTransportBehavior.Accept, TimeSpan.FromSeconds(120));
        var first = await SeedAsync();
        var later = await SeedAsync(tenant: SchemaFixture.TenantB);
        await using var child = StartSmtpProbe("retained-cleanup", smtp.Port, first.Job, later.Job);
        (await child.Boundary.WaitAsync(TimeSpan.FromSeconds(40))).Should().Be("retained-cleanup");
        child.HasExited.Should().BeFalse();
        smtp.Connections.Should().Be(1); smtp.Messages.Should().Be(1);
        smtp.ClientClosed.Task.IsCompleted.Should().BeFalse("the failed actual SDK Dispose retains its socket in the child");
        var before = await ReadProcessFactsAsync(first.Job);
        before.ProviderState.Should().Be("sending");
        before.Receipt.Should().BeNull(); before.Evidence.Should().BeNull();
        before.Unreleased.Should().Be(1); before.Outcomes.Should().Be(0); before.Finalizers.Should().Be(0);
        (await ReadProcessFactsAsync(later.Job, SchemaFixture.TenantB)).ProviderState.Should().BeNull(
            "an unhealthy source refuses even an eligible separate-tenant job before claim or SMTP");
        await child.KillAndJoinAsync();
        child.ExitCode.Should().NotBe(0);
        await smtp.ClientClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        (await ReadProcessFactsAsync(first.Job)).Should().Be(before,
            "process exit cannot create provider evidence or release unresolved occupancy");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_process_loss_after_final250_before_provider_receipt_recovers_Unknown_without_resend(bool organization)
    {
        await using var database = await OpenDatabaseAsync();
        await using var smtp = new InvitationSmtpTransportServer(InvitationSmtpTransportBehavior.Accept, TimeSpan.FromSeconds(120));
        var facts = await SeedAsync(organization ? SchemaFixture.OrgA1 : null);
        ProcessSmtpFacts before;
        await using (var child = StartSmtpProbe("after-final250", smtp.Port, facts.Job))
        {
            (await child.Boundary.WaitAsync(TimeSpan.FromSeconds(40))).Should().Be("after-final250");
            await smtp.ClientClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            smtp.Connections.Should().Be(1); smtp.Messages.Should().Be(1);
            before = await ReadProcessFactsAsync(facts.Job, organization: organization ? SchemaFixture.OrgA1 : null);
            before.ProviderState.Should().Be("sending");
            before.Claim.Should().NotBeNull(); before.Receipt.Should().BeNull(); before.Evidence.Should().BeNull();
            before.Normal.Should().Be(1); before.Unreleased.Should().Be(1); before.Outcomes.Should().Be(0);
            before.Phase.Should().Be("executing");
            child.HasExited.Should().BeFalse();
            await child.KillAndJoinAsync();
            child.ExitCode.Should().NotBe(0);
        }
        // A genuinely new host/source/run cell follows joined process death. Stock
        // native maintenance waits for the existing lease; no fixture UPDATE expires it.
        var bounds = new BackgroundJobRuntimeOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(20),
            RenewalInterval = TimeSpan.FromSeconds(1),
            SqlTimeout = TimeSpan.FromSeconds(3),
            ProbeTimeout = TimeSpan.FromSeconds(3),
            ProgressTimeout = TimeSpan.FromSeconds(35),
            HandoffRepairGrace = TimeSpan.FromSeconds(15),
            ControlPoll = TimeSpan.FromMilliseconds(50),
            RecoveryProbeInterval = TimeSpan.FromSeconds(5),
        };
        await using var host = Host(smtp.Port, configure: services =>
        {
            services.RemoveAll<BackgroundJobRuntimeOptions>(); services.AddSingleton(bounds);
        });
        var registry = host.Services.GetRequiredService<BackgroundJobDefinitionRegistry>();
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(_database!.OutboxConnectionString));
        var gate = new BackgroundJobRuntimeGate(registry.Infrastructure);
        var ids = new SystemGuidFactory();
        using var filters = new ProcessSmtpGlobalFilters();
        var runtime = new BackgroundJobRuntime(source, registry, BackgroundJobRecurrenceRegistry.CreateEmpty(),
            host.Services.GetRequiredService<JobExecutor>(), new(source, registry, bounds, ids, gate), gate,
            bounds, ids, TimeProvider.System, new(false, 100), new HangfireBoundedLogProvider(),
            host.Services.GetRequiredService<ApplicationDataSourceLifetime>());
        runtime.BindMessagingLifetime(Task.CompletedTask, Task.CompletedTask);
        ProcessSmtpFacts? recovered = null;
        try
        {
            using var start = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await runtime.StartAsync(start.Token);
            using var observed = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (true)
            {
                recovered = await ReadProcessFactsAsync(facts.Job, organization: organization ? SchemaFixture.OrgA1 : null);
                if (recovered.Phase == "quarantined" && runtime.Snapshot.Control is { Executions: 0, PendingOutcomes: 0 }
                    && runtime.Snapshot.RetainedInvocations == 0) break;
                await Task.Delay(25, observed.Token);
            }
        }
        finally
        {
            runtime.RequestStop();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            await runtime.StopAsync(cleanup.Token); await runtime.Completion.WaitAsync(cleanup.Token);
        }
        recovered.Should().NotBeNull();
        recovered!.Quarantine.Should().Be("unknown_outcome");
        recovered.Normal.Should().Be(1); recovered.Finalizers.Should().Be(0);
        recovered.Unreleased.Should().Be(1); recovered.Outcomes.Should().Be(0); recovered.NoStarts.Should().Be(0);
        recovered.Attempt.Should().Be(before.Attempt); recovered.Operation.Should().Be(before.Operation);
        recovered.Claim.Should().Be(before.Claim); recovered.ProviderState.Should().Be("sending");
        recovered.Receipt.Should().BeNull(); recovered.Evidence.Should().BeNull();
        recovered.Submission.Should().Be("Queued"); recovered.SubmissionReceipt.Should().BeNull();
        recovered.RecoveryGeneration.Should().BeGreaterThan(before.RecoveryGeneration);
        runtime.Snapshot.CleanupConfirmed.Should().BeTrue();
        runtime.Snapshot.Control!.CleanupConfirmed.Should().BeTrue();
        runtime.Snapshot.RetainedInvocations.Should().Be(0);
        host.Services.GetRequiredService<InvitationSmtpProviderDataSource>().IsHealthy.Should().BeTrue();
        smtp.Connections.Should().Be(1); smtp.Messages.Should().Be(1);
    }

    private async Task<ProcessSmtpFacts> ReadProcessFactsAsync(BackgroundJobId job, Guid? tenant = null, Guid? organization = null)
    {
        string? state = null; Guid? claim = null, receipt = null, evidence = null;
        // Owner-only observation of private operational facts, never an isolation assertion.
        await using (var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database!.MigrationConnectionString))
        await using (var provider = new NpgsqlCommand("SELECT state,claim_id,receipt_id,evidence_id FROM invitation_email_attempt_receipts WHERE job_id=@id", owner))
        {
            provider.Parameters.AddWithValue("id", job.Value);
            await using var row = await provider.ExecuteReaderAsync();
            if (await row.ReadAsync())
            {
                state = row.GetString(0); claim = row.GetGuid(1);
                receipt = row.IsDBNull(2) ? null : row.GetGuid(2); evidence = row.IsDBNull(3) ? null : row.GetGuid(3);
                (await row.ReadAsync()).Should().BeFalse();
            }
        }
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database!.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
        await IdentityMembershipFixture.ScopeAsync(app, transaction, tenant ?? SchemaFixture.TenantA, organization);
        await using var command = new NpgsqlCommand("""
            SELECT j.phase,j.quarantine_reason,j.normal_attempts,j.finalize_attempts,j.current_attempt_id,j.operation_id,j.wake_generation,
                (SELECT count(*) FROM background_job_reservations r WHERE r.job_id=j.id AND r.released_at IS NULL),
                (SELECT count(*) FROM background_job_attempts a WHERE a.job_id=j.id AND a.outcome IS NOT NULL),
                (SELECT count(*) FROM background_job_attempts a WHERE a.job_id=j.id AND a.no_start_at IS NOT NULL),
                i.submission_status,i.submission_receipt_id
            FROM background_jobs j JOIN invitations i ON i.current_delivery_job_id=j.id WHERE j.id=@id
            """, app, transaction);
        command.Parameters.AddWithValue("id", job.Value);
        await using var facts = await command.ExecuteReaderAsync();
        (await facts.ReadAsync()).Should().BeTrue();
        return new(facts.GetString(0), facts.IsDBNull(1) ? null : facts.GetString(1), facts.GetInt32(2), facts.GetInt32(3),
            facts.GetGuid(4), facts.GetGuid(5), facts.GetInt64(6), facts.GetInt64(7), facts.GetInt64(8), facts.GetInt64(9),
            facts.GetString(10), facts.IsDBNull(11) ? null : facts.GetGuid(11), state, claim, receipt, evidence);
    }

    private sealed record ProcessSmtpFacts(string Phase, string? Quarantine, int Normal, int Finalizers, Guid Attempt,
        Guid Operation, long RecoveryGeneration, long Unreleased, long Outcomes, long NoStarts,
        string Submission, Guid? SubmissionReceipt, string? ProviderState, Guid? Claim, Guid? Receipt, Guid? Evidence);

    private SmtpProbeChild StartSmtpProbe(string mode, int port, BackgroundJobId job, BackgroundJobId? later = null)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "invitation-smtp-probe", "LearnStack.Tests.InvitationSmtpProbe.dll");
        File.Exists(path).Should().BeTrue("the separately built SMTP probe must be staged");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(path); start.ArgumentList.Add(mode);
        start.Environment.Clear();
        foreach (var name in new[] { "PATH", "DOTNET_ROOT", "DOTNET_ROOT_ARM64", "TMPDIR", "LANG" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        start.Environment["DOTNET_EnableDiagnostics"] = "0";
        start.Environment["DOTNET_DbgEnableMiniDump"] = "0";
        start.Environment["COMPlus_DbgEnableMiniDump"] = "0";
        start.Environment["LEARNSTACK_SMTP_PROOF_APP"] = _database!.AppConnectionString;
        start.Environment["LEARNSTACK_SMTP_PROOF_DISPATCHER"] = _database.OutboxConnectionString;
        start.Environment["LEARNSTACK_SMTP_PROOF_KEYRING"] = KeyRing;
        start.Environment["LEARNSTACK_SMTP_PROOF_PORT"] = port.ToString(CultureInfo.InvariantCulture);
        start.Environment["LEARNSTACK_SMTP_PROOF_JOB"] = job.Value.ToString("D");
        if (later is { } laterJob) start.Environment["LEARNSTACK_SMTP_PROOF_LATER_JOB"] = laterJob.Value.ToString("D");
        return new(start, Path.GetDirectoryName(path)!);
    }

    private sealed class SmtpProbeChild : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Task _stdout, _stderr;
        private readonly string _profile;
        private readonly TaskCompletionSource<string> _boundary = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal SmtpProbeChild(ProcessStartInfo start, string contentRoot)
        {
            _profile = Path.Combine(Path.GetTempPath(), "learnstack-smtp-probe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_profile);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_profile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            foreach (var key in new[] { "HOME", "APPDATA", "XDG_CONFIG_HOME", "TMPDIR", "TMP", "TEMP" })
                start.Environment[key] = _profile;
            start.Environment["ASPNETCORE_TEST_CONTENTROOT_LEARNSTACK_API"] = contentRoot;
            start.WorkingDirectory = _profile;
            _process = new() { StartInfo = start };
            try
            {
                if (!_process.Start()) throw new InvalidOperationException("The owned SMTP child did not start.");
            }
            catch
            {
                _process.Dispose(); Directory.Delete(_profile, recursive: true); throw;
            }
            _stdout = DrainAsync(_process.StandardOutput, marker: true);
            _stderr = DrainAsync(_process.StandardError, marker: false);
        }
        internal Task<string> Boundary => _boundary.Task;
        internal bool HasExited => _process.HasExited;
        internal int ExitCode => _process.ExitCode;
        private async Task DrainAsync(StreamReader stream, bool marker)
        {
            var length = 0;
            while (await stream.ReadLineAsync() is { } line)
            {
                length += line.Length;
                if (length > 16_384)
                {
                    _boundary.TrySetException(new InvalidOperationException("The owned SMTP child exceeded its output bound."));
                    continue;
                }
                if (marker && line.StartsWith("InvitationSmtpProbe:", StringComparison.Ordinal))
                    _boundary.TrySetResult(line[20..]);
                // All incidental output is discarded, never copied to diagnostics/artifacts.
            }
            if (marker) _boundary.TrySetException(new InvalidOperationException("The owned SMTP child exited before its boundary."));
        }
        internal async Task KillAndJoinAsync()
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAll(_stdout, _stderr).WaitAsync(TimeSpan.FromSeconds(10));
        }
        public async ValueTask DisposeAsync()
        {
            await KillAndJoinAsync();
            _process.Dispose();
            Directory.Delete(_profile, recursive: true);
        }
    }

    private sealed class ProcessSmtpGlobalFilters : IDisposable
    {
        private readonly Hangfire.Common.JobFilter[] _filters = GlobalJobFilters.Filters.ToArray();
        public void Dispose()
        {
            GlobalJobFilters.Filters.Clear();
            foreach (var filter in _filters) GlobalJobFilters.Filters.Add(filter.Instance, filter.Order);
        }
    }
}
