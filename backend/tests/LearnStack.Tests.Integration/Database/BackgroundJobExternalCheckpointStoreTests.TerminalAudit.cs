using System.Text.Json;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobExternalCheckpointStoreTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(6, false)]
    [InlineData(1, true)]
    public async Task No_plan_terminal_store_waits_for_actual_no_start_then_commits_one_real_MUST_atomically(int ordinal, bool auditFault)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var host = NoPlanRefusalHost(database);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var control = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var terminal = new PostgresBackgroundJobTerminalStore(source, host.Services.GetRequiredService<IServiceScopeFactory>(),
            new(), new SystemGuidFactory(), runtime);
        var normal = await RequestedTerminalGuardFixtureAsync(database, host.Services, ordinal, control, runtime);
        var early = await terminal.TryClaimTerminalAsync([]);
        early.ThrowIfFailed();
        early.Value.Should().BeNull("an occupied no-start request grants no terminal audit authority");
        var cleanup = await control.AcquireExternalTerminalNoStartAsync([], CancellationToken.None);
        cleanup.ThrowIfFailed();
        cleanup.CommitAcknowledged.Should().BeTrue();
        await CompleteTerminalGuardNoStartAsync(host.Services, cleanup, runtime);
        var liveCleanup = await terminal.TryClaimTerminalAsync([]);
        liveCleanup.ThrowIfFailed();
        liveCleanup.Value.Should().BeNull("a live cleanup lease is not another terminal acquisition");
        await ExpireCleanTerminalOwnerForFixtureAsync(database, normal);
        var acquired = await terminal.TryClaimTerminalAsync([]);
        acquired.ThrowIfFailed();
        acquired.CommitAcknowledged.Should().BeTrue();
        acquired.CleanupConfirmed.Should().BeTrue();
        var claim = acquired.Value!;
        claim.Profile.Should().Be("external");
        claim.NormalAttempts.Should().Be(ordinal);
        claim.Reason.Should().Be("permanent_failure");
        claim.Token.Should().NotBe(cleanup.Value!.RecoveryToken).And.NotBe(normal.Claim.Token);
        if (auditFault)
        {
            await using var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
            await SchemaQueries.ExecuteAsync(owner, null, "REVOKE INSERT ON audit_log FROM learnstack_outbox_admin");
            var failed = await terminal.TryTerminalizeAsync(claim, normal.Envelope.Traceparent);
            failed.Failed.Should().BeTrue();
            failed.KnownRollback.Should().BeTrue();
            failed.CleanupConfirmed.Should().BeTrue();
            failed.CommitAcknowledged.Should().BeFalse();
            (await ReadNoPlanTerminalAuditAsync(database, normal)).Should().BeNull();
            (await ReadNoPlanTerminalStateAsync(database, normal)).Should().BeFalse();
            var proof = await control.ProbeCommitAsync(failed.Probes.Single());
            proof.ThrowIfFailed();
            proof.Value!.Disposition.Should().Be(BackgroundJobProbeDisposition.RolledBack);
            await SchemaQueries.ExecuteAsync(owner, null, "GRANT INSERT ON audit_log TO learnstack_outbox_admin");
        }
        var completed = await terminal.TryTerminalizeAsync(claim, normal.Envelope.Traceparent);
        completed.ThrowIfFailed();
        completed.CommitAcknowledged.Should().BeTrue();
        completed.CleanupConfirmed.Should().BeTrue();
        await AssertNoPlanTerminalAuditAsync(database, normal, claim);
        var duplicate = await terminal.TryTerminalizeAsync(claim, normal.Envelope.Traceparent);
        duplicate.ThrowIfFailed();
        duplicate.Value.Should().BeNull();
        await AssertNoPlanTerminalAuditAsync(database, normal, claim);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task No_plan_terminal_commit_loss_confirms_exact_history_without_rewriting_ack_or_a_second_audit(bool committed)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var host = NoPlanRefusalHost(database);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var control = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var scopes = host.Services.GetRequiredService<IServiceScopeFactory>();
        var terminal = new PostgresBackgroundJobTerminalStore(source, scopes, new(), new SystemGuidFactory(), runtime);
        var normal = await RequestedTerminalGuardFixtureAsync(database, host.Services, 6, control, runtime);
        var cleanup = await control.AcquireExternalTerminalNoStartAsync([], CancellationToken.None);
        cleanup.ThrowIfFailed();
        await CompleteTerminalGuardNoStartAsync(host.Services, cleanup, runtime);
        await ExpireCleanTerminalOwnerForFixtureAsync(database, normal);
        var acquired = await terminal.TryClaimTerminalAsync([]);
        acquired.ThrowIfFailed();
        acquired.CommitAcknowledged.Should().BeTrue();
        var claim = acquired.Value!;
        await using var proxy = new CommitResponseDroppingProxy(database.OutboxConnectionString, dropCommitRequest: !committed);
        await using var broken = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(proxy.ConnectionString));
        var unknown = await new PostgresBackgroundJobTerminalStore(broken, scopes, new(), new SystemGuidFactory(), runtime)
            .TryTerminalizeAsync(claim, normal.Envelope.Traceparent);
        unknown.CommitUncertain.Should().BeTrue();
        unknown.CleanupConfirmed.Should().BeTrue();
        unknown.Value.Should().BeNull();
        proxy.DroppedCommitRequests.Should().Be(committed ? 0 : 1);
        proxy.DroppedCommitResponses.Should().Be(committed ? 1 : 0);
        var descriptor = unknown.Probes.Single();
        var observed = await control.ProbeCommitAsync(descriptor);
        observed.ThrowIfFailed();
        observed.Value!.Disposition.Should().Be(committed
            ? BackgroundJobProbeDisposition.TerminalCompleted : BackgroundJobProbeDisposition.RolledBack);
        unknown.CommitAcknowledged.Should().BeFalse();
        foreach (string? profile in new string?[] { null, "transactional" })
        {
            var substituted = await control.ProbeCommitAsync(descriptor with { Profile = profile });
            substituted.ThrowIfFailed();
            substituted.Value!.Disposition.Should().Be(BackgroundJobProbeDisposition.Unresolved,
                "the locked external row must still use exact validation when the supplied profile is substituted");
        }
        var wrong = await control.ProbeCommitAsync(descriptor with { ExpectedTerminalReason = "retry_exhausted" });
        wrong.ThrowIfFailed();
        wrong.Value!.Disposition.Should().Be(BackgroundJobProbeDisposition.Unresolved,
            "the same sixth admission cannot certify a different frozen terminal reason");
        if (!committed)
        {
            (await ReadNoPlanTerminalAuditAsync(database, normal)).Should().BeNull();
            (await ReadNoPlanTerminalStateAsync(database, normal)).Should().BeFalse();
            var retry = await terminal.TryTerminalizeAsync(claim, normal.Envelope.Traceparent);
            retry.ThrowIfFailed();
            retry.CommitAcknowledged.Should().BeTrue();
        }
        else
        {
            var duplicate = await terminal.TryTerminalizeAsync(claim, normal.Envelope.Traceparent);
            duplicate.ThrowIfFailed();
            duplicate.Value.Should().BeNull();
        }
        await AssertNoPlanTerminalAuditAsync(database, normal, claim);
    }

    private static async Task ExpireCleanTerminalOwnerForFixtureAsync(DisposableSchemaDatabase database, ConfirmedStartReceipt normal)
    {
        // The actual app task and its source cleanup are joined by the helper
        // above. This fixture-only status change does not prove quiescence.
        await using var control = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        await SchemaQueries.ExecuteAsync(control, null,
            "UPDATE background_jobs SET ownership_until=clock_timestamp() WHERE id=@id", ("id", normal.Envelope.JobId.Value));
    }

    private static async Task<string?> ReadNoPlanTerminalAuditAsync(DisposableSchemaDatabase database, ConfirmedStartReceipt normal)
    {
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
        await EducationSchemaSeed.AnnounceAsync(app, transaction, normal.Envelope.TenantId.Value, normal.Envelope.OrganizationId?.Value);
        await using var command = new NpgsqlCommand("""
            SELECT a.metadata::text FROM audit_log a
            WHERE a.metadata->>'job_id'=@id::text AND a.operation='platform.job.dead_letter'
            """, (NpgsqlConnection)app, (NpgsqlTransaction)transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("id", normal.Envelope.JobId.Value);
        await using var row = await command.ExecuteReaderAsync();
        if (!await row.ReadAsync()) return null;
        var metadata = row.GetString(0);
        (await row.ReadAsync()).Should().BeFalse("one terminal request can commit only one MUST audit");
        return metadata;
    }

    private static async Task<bool> ReadNoPlanTerminalStateAsync(DisposableSchemaDatabase database, ConfirmedStartReceipt normal)
    {
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
        await EducationSchemaSeed.AnnounceAsync(app, transaction, normal.Envelope.TenantId.Value, normal.Envelope.OrganizationId?.Value);
        await using var command = new NpgsqlCommand("SELECT dead_lettered_at IS NOT NULL FROM background_jobs WHERE id=@id",
            (NpgsqlConnection)app, (NpgsqlTransaction)transaction)
        { CommandTimeout = 5 };
        command.Parameters.AddWithValue("id", normal.Envelope.JobId.Value);
        var actual = await command.ExecuteScalarAsync();
        actual.Should().BeOfType<bool>("missing state is never a proof of rollback");
        return (bool)actual!;
    }

    private static async Task AssertNoPlanTerminalAuditAsync(DisposableSchemaDatabase database, ConfirmedStartReceipt normal,
        BackgroundJobTerminalClaim claim)
    {
        var metadata = await ReadNoPlanTerminalAuditAsync(database, normal);
        metadata.Should().NotBeNull();
        (await ReadNoPlanTerminalStateAsync(database, normal)).Should().BeTrue();
        using var parsed = JsonDocument.Parse(metadata!);
        parsed.RootElement.GetProperty("job_id").GetGuid().Should().Be(normal.Envelope.JobId.Value);
        parsed.RootElement.GetProperty("attempt_id").GetGuid().Should().Be(normal.AttemptId.Value);
        parsed.RootElement.GetProperty("normal_attempts").GetInt32().Should().Be(normal.NormalAttemptOrdinal);
        parsed.RootElement.GetProperty("reason").GetString().Should().Be("permanent_failure");
        parsed.RootElement.GetProperty("acquisition_token").GetGuid().Should().Be(claim.Token);
        parsed.RootElement.TryGetProperty("failure_policy", out _).Should().BeFalse("no provider outcome was invented");
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, normal.Envelope.JobId))
            .Should().Be(normal.NormalAttemptOrdinal);
    }
}
