using System.Data;
using System.Data.Common;
using FluentAssertions;
using LearnStack.Infrastructure.Audit;
using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Contracts.Bindings;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class GlobalIdentityBootstrapTests
{
    [Fact]
    public async Task Original_success_evidence_requires_the_complete_captured_key_scope_source_target_and_outcome()
    {
        var observed = new EvidenceObservation();
        await using var provider = BuildProvider(_database.AppConnectionString, Registration,
            new FixtureAdmission(Subject, Actor), services => DecorateEvidence(services, observed));
        await QualifyAsync(provider);
        (await AdoptAsync(provider, Subject, Actor)).Outcome.Should().Be(IdentityBindingOutcome.Created);
        observed.Creation.Should().NotBeNull();
        var creation = observed.Creation!;
        (await EvidenceAsync(creation, creation.Timestamp, "success")).Should().BeTrue();
        foreach (var mismatch in new[] { "id", "timestamp", "authority", "subject", "target", "source", "outcome", "announcement" })
            (await EvidenceAsync(creation, creation.Timestamp, "success", mismatch)).Should().BeFalse(mismatch);
        (await CountsAsync()).Should().Be((1L, 1L));
    }

    [Fact]
    public async Task Duplicate_non_success_is_proved_on_a_clean_transaction_without_a_User_witness()
    {
        await RequireReadOnlyEvidenceAsync();
        var observed = new EvidenceObservation { RefusePending = true, RepeatReconciliation = true };
        await using var provider = BuildProvider(_database.AppConnectionString, Registration,
            new FixtureAdmission(Subject, Actor), services => DecorateEvidence(services, observed));
        await QualifyAsync(provider);
        (await AdoptAsync(provider, Subject, Actor)).Outcome.Should().Be(IdentityBindingOutcome.StorageUnavailable);
        observed.ReconciliationCalls.Should().Be(2);
        observed.Creation.Should().NotBeNull();
        var creation = observed.Creation!;
        (await CountsAsync()).Should().Be((0L, 1L));
        provider.GetRequiredService<IAuditHealth>().IsHealthy.Should().BeTrue();
        var writableEvidence = await FluentActions.Awaiting(() =>
                EvidenceAsync(creation, observed.ReconciliationTimestamp, "failed", readOnly: false))
            .Should().ThrowAsync<PostgresException>();
        writableEvidence.Which.SqlState.Should().Be(PostgresErrorCodes.ReadOnlySqlTransaction);
        (await EvidenceAsync(creation, observed.ReconciliationTimestamp, "failed")).Should().BeTrue();
        (await EvidenceAsync(creation, creation.Timestamp, "success")).Should().BeFalse();
        (await EvidenceAsync(creation, observed.ReconciliationTimestamp, "success")).Should().BeFalse();
        foreach (var mismatch in new[] { "timestamp", "subject", "target", "source", "outcome", "announcement" })
            (await EvidenceAsync(creation, observed.ReconciliationTimestamp, "failed", mismatch)).Should().BeFalse(mismatch);
        await using var clean = BuildProvider(_database.AppConnectionString, Registration, new FixtureAdmission(Subject, Actor));
        await QualifyAsync(clean);
        (await AdoptAsync(clean, Subject, Actor)).Outcome.Should().Be(IdentityBindingOutcome.Created);
        (await CountsAsync()).Should().Be((1L, 2L));
        (await EvidenceAsync(creation, observed.ReconciliationTimestamp, "failed")).Should().BeTrue(
            "failed-row evidence remains valid when a User exists and therefore cannot establish User absence");
    }

    [Fact]
    public async Task Unavailable_duplicate_evidence_keeps_the_original_failure_and_marks_audit_health_unhealthy()
    {
        await using (var connection = await PostgresFixture.OpenAsync(_database.MigrationConnectionString))
        await using (var command = new NpgsqlCommand("""
            REVOKE EXECUTE ON FUNCTION public.fn_identity_audit_evidence(uuid,timestamptz,uuid,bytea,uuid,text,text) FROM learnstack_app
            """, (NpgsqlConnection)connection))
            await command.ExecuteNonQueryAsync();
        var observed = new EvidenceObservation { RefusePending = true, RepeatReconciliation = true };
        await using var provider = BuildProvider(_database.AppConnectionString, Registration,
            new FixtureAdmission(Subject, Actor), services => DecorateEvidence(services, observed));
        await QualifyAsync(provider);
        (await AdoptAsync(provider, Subject, Actor)).Outcome.Should().Be(IdentityBindingOutcome.StorageUnavailable);
        provider.GetRequiredService<IAuditHealth>().IsHealthy.Should().BeFalse();
        provider.GetRequiredService<IAuditHealth>().FailingOperation.Should().Be(GlobalIdentityCreationReference.Operation);
        (await CountsAsync()).Should().Be((0L, 1L));
    }

    [Fact]
    public async Task Reenlisted_context_is_refused_before_snapshot_capture_and_a_fresh_owner_remains_usable()
    {
        await using (var provider = BuildProvider(_database.AppConnectionString, Registration,
            new FixtureAdmission(Subject, Actor), services => services.Replace(ServiceDescriptor.Scoped<IUserBindingStore>(scope =>
            {
                var context = scope.GetRequiredService<IdentityDbContext>();
                return new DetachedIdentityStore(new UserBindingStore(context,
                    scope.GetRequiredService<IGlobalIdentityUnitOfWork>()), context);
            }))))
        {
            await QualifyAsync(provider);
            await FluentActions.Awaiting(() => AdoptAsync(provider, Subject, Actor))
                .Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Global identity capture requires its original physical transaction and connection.");
        }
        (await CountsAsync()).Should().Be((0L, 1L));
        (await AuditRowsAsync()).Should().ContainSingle().Which.Snapshots.Should().BeEmpty();
        await using var clean = BuildProvider(_database.AppConnectionString, Registration, new FixtureAdmission(Subject, Actor));
        await QualifyAsync(clean);
        (await AdoptAsync(clean, Subject, Actor)).Outcome.Should().Be(IdentityBindingOutcome.Created);
        (await CountsAsync()).Should().Be((1L, 2L));
    }

    private static void DecorateEvidence(IServiceCollection services, EvidenceObservation observed) =>
        services.Replace(ServiceDescriptor.Scoped<IAuditStore>(provider =>
            new EvidenceAuditStore(ActivatorUtilities.CreateInstance<PostgresAuditStore>(provider), observed)));

    private async Task RequireReadOnlyEvidenceAsync()
    {
        // Instrument only this test's isolated database, retaining the actual function and grants.
        await using var connection = await OpenConnectionAsync(_database.MigrationConnectionString);
        await using var command = new NpgsqlCommand("""
            SELECT pg_get_functiondef('public.fn_identity_audit_evidence(uuid,timestamptz,uuid,bytea,uuid,text,text)'::regprocedure)
            """, connection);
        var definition = (string)(await command.ExecuteScalarAsync())!;
        var bodyStart = definition.IndexOf("BEGIN", StringComparison.Ordinal);
        bodyStart.Should().BeGreaterThan(0);
        command.CommandText = definition.Insert(bodyStart + "BEGIN".Length, """

                IF current_setting('transaction_read_only') <> 'on' THEN
                    RAISE EXCEPTION 'Evidence requires a physically read-only transaction.' USING ERRCODE='25006';
                END IF;

            """);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<bool> EvidenceAsync(GlobalIdentityCreationReference creation,
        DateTimeOffset timestamp, string outcome, string? mismatch = null, bool readOnly = true)
    {
        await using var connection = await PostgresFixture.OpenAsync(_database.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(connection);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await using var setup = new NpgsqlCommand(readOnly ? "SET TRANSACTION READ ONLY" : "SET TRANSACTION READ WRITE",
            (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        await setup.ExecuteNonQueryAsync();
        setup.CommandText = """
            SELECT set_config('app.identity_authority_id',@authority,true),
                set_config('app.identity_subject_key',@subject,true)
            """;
        setup.Parameters.AddWithValue("authority", creation.AuthorityId.Value.ToString("D"));
        setup.Parameters.AddWithValue("subject", mismatch == "announcement"
            ? IdentitySubjectKey.Compute(creation.AuthorityId, "different-announced-subject").Hex : creation.SubjectKey.Hex);
        await setup.ExecuteNonQueryAsync();
        await using var command = new NpgsqlCommand("""
            SELECT public.fn_identity_audit_evidence(@id,@timestamp,@authority,@subject,@target,@source,@outcome)
            """, (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        command.Parameters.AddWithValue("id", mismatch == "id" ? Guid.CreateVersion7() : creation.AuditId.Value);
        command.Parameters.AddWithValue("timestamp", mismatch == "timestamp" ? timestamp.AddTicks(10) : timestamp);
        command.Parameters.AddWithValue("authority", mismatch == "authority" ? Guid.CreateVersion7() : creation.AuthorityId.Value);
        command.Parameters.AddWithValue("subject", mismatch == "subject"
            ? IdentitySubjectKey.Compute(creation.AuthorityId, "different-evidence-subject").ToByteArray() : creation.SubjectKey.ToByteArray());
        command.Parameters.AddWithValue("target", mismatch == "target" ? Guid.CreateVersion7() : creation.TargetUserId.Value);
        command.Parameters.AddWithValue("source", mismatch == "source" ? "identity-bootstrap" : creation.Source);
        command.Parameters.AddWithValue("outcome", mismatch == "outcome" ? "indeterminate" : outcome);
        var result = await command.ExecuteScalarAsync() is true;
        await transaction.CommitAsync();
        return result;
    }

    private sealed class EvidenceObservation
    {
        public GlobalIdentityCreationReference? Creation { get; set; }
        public bool RefusePending { get; init; }
        public bool RepeatReconciliation { get; init; }
        public DateTimeOffset ReconciliationTimestamp { get; set; }
        public int ReconciliationCalls { get; set; }
    }

    private sealed class DetachedIdentityStore(IUserBindingStore inner, IdentityDbContext context) : IUserBindingStore
    {
        public Task<User?> FindExactAsync(GlobalIdentitySubject subject, CancellationToken cancellationToken = default) =>
            inner.FindExactAsync(subject, cancellationToken);

        public Task AddAsync(User user, CancellationToken cancellationToken = default)
        {
            context.Database.UseTransaction(null);
            return inner.AddAsync(user, cancellationToken);
        }
    }

    private sealed class EvidenceAuditStore(IAuditStore inner, EvidenceObservation observed) : IAuditStore
    {
        public Task WriteGlobalIdentityPendingAsync(IUnitOfWork unit, GlobalIdentityCreationReference creation,
            CancellationToken cancellationToken = default)
        {
            observed.Creation = creation;
            if (observed.RefusePending) throw new AuditWriteFailedException("Deliberate central MUST refusal.");
            return inner.WriteGlobalIdentityPendingAsync(unit, creation, cancellationToken);
        }

        public async Task ReconcileGlobalIdentityAsync(AuditIntent intent, IReadOnlyList<CapturedEntityChange> changes,
            AuditIntentState state, DateTimeOffset timestamp, CancellationToken cancellationToken = default)
        {
            observed.ReconciliationTimestamp = timestamp;
            observed.ReconciliationCalls++;
            await inner.ReconcileGlobalIdentityAsync(intent, changes, state, timestamp, cancellationToken);
            if (observed.RepeatReconciliation)
            {
                observed.ReconciliationCalls++;
                await inner.ReconcileGlobalIdentityAsync(intent, changes, state, timestamp, cancellationToken);
            }
        }

        public Task WritePendingAsync(IUnitOfWork unit, CancellationToken cancellationToken = default) => inner.WritePendingAsync(unit, cancellationToken);
        public Task WriteStandaloneAsync(AuditEntryDraft entry, CancellationToken cancellationToken = default) => inner.WriteStandaloneAsync(entry, cancellationToken);
        public Task WriteBestEffortAsync(AuditEntryDraft entry, CancellationToken cancellationToken = default) => inner.WriteBestEffortAsync(entry, cancellationToken);
        public Task WritePlatformScopeAsync(AuditEntryDraft entry, DbConnection connection, DbTransaction transaction,
            CancellationToken cancellationToken = default) => inner.WritePlatformScopeAsync(entry, connection, transaction, cancellationToken);
        public Task WriteEventTerminalAsync(AuditEntryDraft entry, EventTerminalTransition transition, DbConnection connection,
            DbTransaction transaction, CancellationToken cancellationToken = default) => inner.WriteEventTerminalAsync(entry, transition, connection, transaction, cancellationToken);
        public Task WriteJobTerminalAsync(AuditEntryDraft entry, JobTerminalTransition transition, DbConnection connection,
            DbTransaction transaction, CancellationToken cancellationToken = default) => inner.WriteJobTerminalAsync(entry, transition, connection, transaction, cancellationToken);
    }
}
