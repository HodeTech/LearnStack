using System.Data;
using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class AuthorizationEvidenceSourceTests(SchemaFixture schema)
{
    [Fact]
    public async Task Two_retained_evidence_connectors_refuse_excess_without_waiting_and_release_only_after_actual_cleanup()
    {
        await using var source = await AuthorizationEvidenceFixture.SourceAsync(schema);
        var occurrence = AuthorizationEvidenceFixture.Occurrence();
        var first = (await source.OpenAsync(occurrence))!;
        var second = (await source.OpenAsync(occurrence))!;
        try
        {
            ((NpgsqlConnection)first.Unit.Connection).ProcessID.Should().NotBe(((NpgsqlConnection)second.Unit.Connection).ProcessID);
            var excess = source.OpenAsync(occurrence);
            excess.IsCompleted.Should().BeTrue("there is no admission queue");
            (await excess).Should().BeNull();
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await FluentActions.Awaiting(() => source.OpenAsync(occurrence, canceled.Token)).Should().ThrowAsync<OperationCanceledException>();
            await first.CompleteAsync();
            (await source.OpenAsync(occurrence)).Should().BeNull("commit does not confirm connector cleanup");
            await first.DisposeAsync();
            await using var replacement = await source.OpenAsync(occurrence);
            replacement.Should().NotBeNull();
        }
        finally { await first.DisposeAsync(); await second.DisposeAsync(); }
    }

    [Fact]
    public async Task Verification_disposes_the_aborted_INSERT_transaction_before_fresh_physical_READ_ONLY_on_the_same_connector()
    {
        await using var source = await AuthorizationEvidenceFixture.SourceAsync(schema, 1);
        var occurrence = AuthorizationEvidenceFixture.Occurrence();
        await using var session = (await source.OpenAsync(occurrence))!;
        var connection = (NpgsqlConnection)session.Unit.Connection;
        var original = (NpgsqlTransaction)session.Unit.Transaction!;
        var announcement = session.Unit.GetTenantContextAnnouncement(original)!;
        var originalServerTransaction = await ScalarAsync(session, "SELECT pg_current_xact_id()::text");
        (await ScalarAsync(session, "SHOW transaction_read_only")).Should().Be("off");
        var failedInsert = () => ScalarAsync(session, "SELECT 1 / 0");
        await failedInsert.Should().ThrowAsync<PostgresException>();
        await session.BeginVerificationAsync();
        // Npgsql reuses its transaction object: Connection and object identity
        // cannot witness retirement. A fresh server transaction on this one
        // connector after a real aborted statement proves the old one ended.
        (await ScalarAsync(session, "SELECT pg_current_xact_id()::text")).Should().NotBe(originalServerTransaction);
        ((NpgsqlConnection)session.Unit.Connection).Should().BeSameAs(connection);
        session.Unit.Mode.Should().Be(TransactionMode.ReadOnly);
        (await ScalarAsync(session, "SHOW transaction_read_only")).Should().Be("on");
        (await ScalarAsync(session, "SHOW transaction_isolation")).Should().Be("read committed");
        var fresh = session.Unit.GetTenantContextAnnouncement(session.Unit.Transaction)!;
        fresh.Should().NotBe(announcement, "the retired announcement cannot vouch for the new transaction even when its driver object is reused");
        fresh.Generation.Should().BeGreaterThan(announcement.Generation);
        fresh.TenantId.Should().Be(occurrence.Draft.TenantId);
        fresh.OrganizationId.Should().Be(occurrence.Draft.OrganizationId);
        foreach (var name in new[] { "scope", "resolving_host", "identity_authority_id", "identity_subject_key" })
            (await ScalarAsync(session, $"SELECT current_setting('app.{name}', true)")).Should().Be(string.Empty);
        await FluentActions.Awaiting(session.BeginVerificationAsync).Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Awaiting(() => session.Unit.BeginTransactionAsync()).Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Awaiting(() => session.Unit.SetProvisioningTenantContextAsync(TenantId.From(SchemaFixture.TenantB)))
            .Should().ThrowAsync<InvalidOperationException>();
        await session.CompleteAsync();
        session.Unit.GetTenantContextAnnouncement(session.Unit.Transaction).Should().BeNull();
    }

    [Fact]
    public async Task Verification_reuses_the_original_deadline_and_expiry_retains_capacity_until_cleanup()
    {
        await using var source = await AuthorizationEvidenceFixture.SourceAsync(schema, 1);
        var occurrence = AuthorizationEvidenceFixture.Occurrence();
        await using var session = (await source.OpenAsync(occurrence))!;
        await ScalarAsync(session, "SELECT pg_sleep(2.6)");
        await session.BeginVerificationAsync();
        await FluentActions.Awaiting(() => ScalarAsync(session, "SELECT pg_sleep(3)"))
            .Should().ThrowAsync<OperationCanceledException>();
        session.DeadlineToken.IsCancellationRequested.Should().BeTrue();
        session.Unit.HasActiveTransaction.Should().BeFalse();
        session.Unit.Transaction.Should().BeNull();
        (await source.OpenAsync(occurrence)).Should().BeNull();
        await FluentActions.Awaiting(session.CompleteAsync).Should().ThrowAsync<OperationCanceledException>();
        await session.DisposeAsync();
        await using var replacement = await source.OpenAsync(occurrence);
        replacement.Should().NotBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_connection_cleanup_spans_both_transactions_and_controls_source_and_job_shutdown(bool fault)
    {
        var source = await AuthorizationEvidenceFixture.SourceAsync(schema, 1);
        var occurrence = AuthorizationEvidenceFixture.Occurrence();
        var job = JobApplicationSourceOwner.Create();
        using var binding = job.Enter();
        var session = (await source.OpenAsync(occurrence))!;
        var originalServerTransaction = await ScalarAsync(session, "SELECT pg_current_xact_id()::text");
        await session.BeginVerificationAsync();
        (await ScalarAsync(session, "SELECT pg_current_xact_id()::text")).Should().NotBe(originalServerTransaction);
        (await ScalarAsync(session, "SHOW transaction_read_only")).Should().Be("on");
        var connection = (NpgsqlConnection)session.Unit.Connection;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        StateChangeEventHandler disposal = (_, change) =>
        {
            if (change.CurrentState != ConnectionState.Closed) return;
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            if (fault) throw new InvalidOperationException("Deliberate actual evidence connection cleanup fault.");
        };
        connection.StateChange += disposal;
        var cleanup = Task.Run(() => session.DisposeAsync().AsTask());
        Task? shutdown = null;
        Task<bool>? jobJoin = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cleanup.IsCompleted.Should().BeFalse();
            (await source.OpenAsync(occurrence)).Should().BeNull();
            job.CloseIssuance();
            jobJoin = job.JoinAuxiliaryCleanupAsync();
            shutdown = source.DisposeAsync().AsTask();
            jobJoin.IsCompleted.Should().BeFalse();
            shutdown.IsCompleted.Should().BeFalse();
        }
        finally { release.TrySetResult(); }
        if (fault)
        {
            await FluentActions.Awaiting(() => cleanup).Should().ThrowAsync<InvalidOperationException>();
            await FluentActions.Awaiting(() => shutdown!).Should().ThrowAsync<InvalidOperationException>();
            await FluentActions.Awaiting(() => source.DisposeAsync().AsTask()).Should().ThrowAsync<InvalidOperationException>();
        }
        else { await cleanup; await shutdown!; }
        (await jobJoin!).Should().Be(!fault);
        job.CleanupConfirmed.Should().Be(!fault);
        connection.StateChange -= disposal;
    }

    [Fact]
    public async Task Reported_statement_cleanup_fault_prevents_verification_and_retains_the_slot_after_other_cleanup()
    {
        var source = await AuthorizationEvidenceFixture.SourceAsync(schema, 1);
        var occurrence = AuthorizationEvidenceFixture.Occurrence();
        var session = (await source.OpenAsync(occurrence))!;
        session.MarkCleanupFailed();
        await FluentActions.Awaiting(session.BeginVerificationAsync).Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Awaiting(() => session.DisposeAsync().AsTask()).Should().ThrowAsync<InvalidOperationException>();
        (await source.OpenAsync(occurrence)).Should().BeNull();
        await FluentActions.Awaiting(() => source.DisposeAsync().AsTask()).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Startup_qualification_refuses_another_authority_without_enabling_evidence_issuance()
    {
        await using var source = new AuthorizationEvidenceSource(schema.Postgres.AppConnectionString, 1);
        await FluentActions.Awaiting(() => source.QualifyAsync(IdentityAuthorityId.From(Guid.CreateVersion7()),
            IdentityMembershipFixture.Authority.Issuer)).Should().ThrowAsync<InvalidOperationException>();
        source.IsQualified.Should().BeFalse();
        await FluentActions.Awaiting(() => source.OpenAsync(AuthorizationEvidenceFixture.Occurrence()))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    private static async Task<object?> ScalarAsync(IAuthorizationEvidenceSession session, string sql)
    {
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)session.Unit.Connection,
            (NpgsqlTransaction?)session.Unit.Transaction);
        return await command.ExecuteScalarAsync(session.DeadlineToken);
    }
}
