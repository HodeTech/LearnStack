using System.Diagnostics.Metrics;
using System.Net;
using FluentAssertions;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class AuthorizationEvidenceStoreTests(SchemaFixture schema)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pending_evidence_is_atomic_with_its_actual_RW_owner_and_reconciliation_reuses_the_fixed_microsecond_draft(bool commit)
    {
        await using var source = await AuthorizationEvidenceFixture.SourceAsync(schema);
        await using var business = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        using var meters = new AuthorizationEvidenceFixture.Meters();
        var store = AuthorizationEvidenceFixture.Store(business, meters, source);
        var occurrence = AuthorizationEvidenceFixture.Occurrence();
        try
        {
            await using (var unit = new NpgsqlUnitOfWork(business, NullLogger<NpgsqlUnitOfWork>.Instance))
            {
                await using var frame = await unit.BeginTransactionAsync();
                await unit.SetTenantContextAsync(new Context(occurrence.Draft.TenantId, occurrence.Draft.OrganizationId));
                await store.WriteAuthorizationPendingAsync(unit, occurrence);
                (await CountAsync(occurrence.Draft.Id)).Should().Be(0, "the row is invisible before actual business commit");
                if (commit) await frame.CompleteAsync();
            }
            (await CountAsync(occurrence.Draft.Id)).Should().Be(commit ? 1 : 0);
            await store.WriteAuthorizationStandaloneAsync(occurrence);
            (await CountAsync(occurrence.Draft.Id)).Should().Be(1);
            await using var observer = (NpgsqlConnection)await PostgresFixture.OpenAsync(schema.Postgres.PlatformConnectionString);
            await using var command = new NpgsqlCommand("SELECT timestamp, outcome, metadata::text FROM audit_log WHERE id=@id", observer);
            command.Parameters.AddWithValue("id", occurrence.Draft.Id.Value);
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetFieldValue<DateTimeOffset>(0).Should().Be(occurrence.Draft.Timestamp);
            occurrence.Draft.Timestamp.Offset.Should().Be(TimeSpan.Zero);
            (occurrence.Draft.Timestamp.Ticks % 10).Should().Be(0);
            reader.GetString(1).Should().Be("denied");
            reader.GetString(2).Should().Contain("authorization");
        }
        finally { await AuthorizationEvidenceFixture.DeleteAsync(schema, occurrence.Draft.Id); }
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("organization")]
    [InlineData("readonly")]
    [InlineData("unannounced")]
    public async Task Pending_evidence_refuses_foreign_or_unannounced_scope_and_physical_read_only_owners(string mismatch)
    {
        await using var business = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        using var meters = new AuthorizationEvidenceFixture.Meters();
        var store = AuthorizationEvidenceFixture.Store(business, meters);
        var occurrence = AuthorizationEvidenceFixture.Occurrence();
        await using var unit = new NpgsqlUnitOfWork(business, NullLogger<NpgsqlUnitOfWork>.Instance);
        await using var frame = await unit.BeginTransactionAsync(mismatch == "readonly" ? TransactionMode.ReadOnly : TransactionMode.ReadWrite);
        if (mismatch != "unannounced")
            await unit.SetTenantContextAsync(new Context(TenantId.From(mismatch == "tenant" ? SchemaFixture.TenantB : SchemaFixture.TenantA),
                OrganizationId.From(mismatch == "organization" ? SchemaFixture.OrgA2 : SchemaFixture.OrgA1)));
        await FluentActions.Awaiting(() => store.WriteAuthorizationPendingAsync(unit, occurrence)).Should().ThrowAsync<ArgumentException>();
        (await CountAsync(occurrence.Draft.Id)).Should().Be(0);
    }

    [Theory]
    [InlineData(AuthorizationEvidenceKind.Denial)]
    [InlineData(AuthorizationEvidenceKind.ClaimAssertion)]
    public async Task Exact_duplicate_evidence_uses_one_connector_and_fresh_RO_verification_with_JSONB_value_equality(AuthorizationEvidenceKind kind)
    {
        await using var source = await AuthorizationEvidenceFixture.SourceAsync(schema, 1);
        await using var business = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        using var meters = new AuthorizationEvidenceFixture.Meters();
        var factory = new ObservedFactory(source);
        var health = new AuditHealth();
        var store = AuthorizationEvidenceFixture.Store(business, meters, factory, health);
        var occurrence = AuthorizationEvidenceFixture.Occurrence(kind: kind);
        var reordered = occurrence.Draft with { Metadata = """{ "authorization": { "policy": "Tenant:identity.membership.read", "version": 1 } }""" };
        try
        {
            // A fixture write plants the same JSONB value with different source text.
            // The new event seam never calls this generic path as a fallback.
            await store.WriteStandaloneAsync(reordered);
            await store.WriteAuthorizationStandaloneAsync(occurrence);
            factory.Verifications.Should().Be(1);
            factory.VerificationBackend.Should().Be(factory.InsertBackend);
            factory.VerificationReadOnly.Should().Be("on");
            health.IsHealthy.Should().BeTrue();
            (await CountAsync(occurrence.Draft.Id)).Should().Be(1);
        }
        finally { await AuthorizationEvidenceFixture.DeleteAsync(schema, occurrence.Draft.Id); }
    }

    [Theory]
    [InlineData("tenant_id")]
    [InlineData("organization_id")]
    [InlineData("actor_user_id")]
    [InlineData("actor_email")]
    [InlineData("module")]
    [InlineData("operation")]
    [InlineData("operation_type")]
    [InlineData("operation_class")]
    [InlineData("entity_type")]
    [InlineData("entity_id")]
    [InlineData("outcome")]
    [InlineData("error_key")]
    [InlineData("reason")]
    [InlineData("before_state")]
    [InlineData("after_state")]
    [InlineData("changes")]
    [InlineData("correlation_id")]
    [InlineData("ip_address")]
    [InlineData("user_agent")]
    [InlineData("metadata")]
    public async Task A_primary_key_collision_with_any_other_persisted_field_changed_is_not_durable_evidence(string column)
    {
        await using var source = await AuthorizationEvidenceFixture.SourceAsync(schema, 1);
        await using var business = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        using var meters = new AuthorizationEvidenceFixture.Meters();
        var health = new AuditHealth();
        var factory = new ObservedFactory(source);
        var store = AuthorizationEvidenceFixture.Store(business, meters, factory, health);
        var occurrence = AuthorizationEvidenceFixture.Occurrence();
        var draft = occurrence.Draft;
        var changed = column switch
        {
            "tenant_id" => draft with { TenantId = TenantId.From(SchemaFixture.TenantB) },
            "organization_id" => draft with { OrganizationId = OrganizationId.From(SchemaFixture.OrgA2) },
            "actor_user_id" => draft with { ActorUserId = UserId.From(IdentityMembershipFixture.PersonA) },
            "actor_email" => draft with { ActorEmail = "private@example.invalid" },
            "module" => draft with { ModuleName = "tenancy" },
            "operation" => draft with { Operation = "tenancy.assertion.reject_claim_context" },
            "operation_type" => draft with { OperationType = OperationType.ReadSensitive },
            "operation_class" => draft with { OperationClass = OperationClass.Should },
            "entity_type" => draft with { EntityType = "OtherSubject" },
            "entity_id" => draft with { EntityId = Guid.CreateVersion7().ToString() },
            "outcome" => draft with { Outcome = AuditOutcome.Failed },
            "error_key" => draft with { ErrorKey = null },
            "reason" => draft with { Reason = "other.reason" },
            "before_state" => draft with { BeforeState = "{}" },
            "after_state" => draft with { AfterState = "{}" },
            "changes" => draft with { Changes = "[]" },
            "correlation_id" => draft with { CorrelationId = null },
            "ip_address" => draft with { IpAddress = IPAddress.Parse("192.0.2.2") },
            "user_agent" => draft with { UserAgent = null },
            "metadata" => draft with { Metadata = """{"authorization":{"version":2}}""" },
            _ => throw new InvalidOperationException("Unknown persisted field."),
        };
        try
        {
            await store.WriteStandaloneAsync(changed);
            await FluentActions.Awaiting(() => store.WriteAuthorizationStandaloneAsync(occurrence)).Should().ThrowAsync<AuditWriteFailedException>();
            factory.Verifications.Should().Be(1);
            health.IsHealthy.Should().BeFalse();
            health.FailingOperation.Should().Be(draft.Operation);
        }
        finally { await AuthorizationEvidenceFixture.DeleteAsync(schema, draft.Id); }
    }

    [Theory]
    [InlineData("audit_log_pkey", 1)]
    [InlineData("different_unique_key", 0)]
    public async Task Missing_row_or_another_unique_constraint_never_turns_23505_into_durability(string constraint, int verifications)
    {
        await using var source = await AuthorizationEvidenceFixture.SourceAsync(schema, 1);
        await using var business = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        using var meters = new AuthorizationEvidenceFixture.Meters();
        var factory = new ObservedFactory(source);
        var health = new AuditHealth();
        var logger = new Logs();
        var store = AuthorizationEvidenceFixture.Store(business, meters, factory, health, logger);
        var occurrence = AuthorizationEvidenceFixture.Occurrence();
        var suffix = Guid.NewGuid().ToString("N");
        var function = "evidence_fault_" + suffix;
        var trigger = "evidence_fault_" + suffix;
        await schema.Postgres.ExecuteAsSuperuserAsync($"""
            CREATE FUNCTION public.{function}() RETURNS trigger LANGUAGE plpgsql AS $body$
            BEGIN
                IF NEW.id = '{occurrence.Draft.Id.Value:D}'::uuid THEN
                    RAISE EXCEPTION 'private-evidence-provider-canary' USING ERRCODE = '23505',
                        CONSTRAINT = '{constraint}', SCHEMA = 'public', TABLE = 'audit_log';
                END IF;
                RETURN NEW;
            END;
            $body$;
            CREATE TRIGGER {trigger} BEFORE INSERT ON public.audit_log
                FOR EACH ROW EXECUTE FUNCTION public.{function}();
            """);
        try
        {
            var failure = await FluentActions.Awaiting(() => store.WriteAuthorizationStandaloneAsync(occurrence))
                .Should().ThrowAsync<AuditWriteFailedException>();
            failure.Which.InnerException.Should().BeNull();
            failure.Which.ToString().Should().NotContain("private-evidence-provider-canary");
            factory.Verifications.Should().Be(verifications);
            health.IsHealthy.Should().BeFalse();
            logger.Items.Should().ContainSingle(item => item.Level == LogLevel.Critical);
            logger.Items.Should().OnlyContain(item => item.Exception == null && !item.Text.Contains("private-evidence-provider-canary", StringComparison.Ordinal));
            (await CountAsync(occurrence.Draft.Id)).Should().Be(0);
        }
        finally
        {
            await schema.Postgres.ExecuteAsSuperuserAsync($"DROP TRIGGER {trigger} ON public.audit_log; DROP FUNCTION public.{function}();");
        }
    }

    [Fact]
    public async Task Lost_commit_acknowledgement_can_reconcile_the_same_event_without_claiming_a_business_outcome()
    {
        await using var source = await AuthorizationEvidenceFixture.SourceAsync(schema, 1);
        await using var business = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        using var meters = new AuthorizationEvidenceFixture.Meters();
        var factory = new ObservedFactory(source) { LoseFirstCommitAcknowledgement = true };
        var logger = new Logs();
        var health = new AuditHealth();
        var store = AuthorizationEvidenceFixture.Store(business, meters, factory, health, logger);
        var occurrence = AuthorizationEvidenceFixture.Occurrence();
        var failures = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, active) =>
        {
            if (ReferenceEquals(instrument.Meter, meters.Meter) && instrument.Name == PostgresAuditStore.StandaloneWriteFailureCounterName)
                active.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, count, _, _) => failures += count);
        listener.Start();
        try
        {
            await FluentActions.Awaiting(() => store.WriteAuthorizationStandaloneAsync(occurrence)).Should().ThrowAsync<AuditWriteFailedException>();
            health.IsHealthy.Should().BeFalse();
            (await CountAsync(occurrence.Draft.Id)).Should().Be(1, "the physical evidence commit succeeded despite lost acknowledgement");
            await store.WriteAuthorizationStandaloneAsync(occurrence);
            health.IsHealthy.Should().BeTrue();
            failures.Should().Be(1);
            factory.Verifications.Should().Be(1);
            logger.Items.Should().ContainSingle(item => item.Level == LogLevel.Critical);
            logger.Items.Should().ContainSingle(item => item.Level == LogLevel.Warning && item.Text.Contains("no business transaction outcome", StringComparison.Ordinal));
            logger.Items.Should().OnlyContain(item => item.Exception == null && !item.Text.Contains("private-evidence-provider-canary", StringComparison.Ordinal));
            (await CountAsync(occurrence.Draft.Id)).Should().Be(1);
        }
        finally { await AuthorizationEvidenceFixture.DeleteAsync(schema, occurrence.Draft.Id); }
    }

    private async Task<long> CountAsync(AuditEntryId id)
    {
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(schema.Postgres.PlatformConnectionString);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM audit_log WHERE id=@id", connection);
        command.Parameters.AddWithValue("id", id.Value);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private sealed record Context(TenantId TenantId, OrganizationId? OrganizationId) : ITenantContext
    {
        public bool IsResolved => true;
        public UserId? UserId => LearnStack.SharedKernel.Identifiers.UserId.From(IdentityMembershipFixture.AdminA);
        public string? CorrelationId => null;
        public string? ModuleName => "identity";
    }

    private sealed class ObservedFactory(IAuthorizationEvidenceSessionFactory source) : IAuthorizationEvidenceSessionFactory
    {
        internal int Verifications { get; set; }
        internal int InsertBackend { get; private set; }
        internal int VerificationBackend { get; set; }
        internal string? VerificationReadOnly { get; set; }
        internal bool LoseFirstCommitAcknowledgement { get; init; }
        private bool _lost;
        public async Task<IAuthorizationEvidenceSession?> OpenAsync(AuthorizationEvidenceOccurrence occurrence, CancellationToken cancellationToken = default)
        {
            var session = await source.OpenAsync(occurrence, cancellationToken);
            if (session is null) return null;
            InsertBackend = ((NpgsqlConnection)session.Unit.Connection).ProcessID;
            return new ObservedSession(session, this);
        }

        private sealed class ObservedSession(IAuthorizationEvidenceSession inner, ObservedFactory owner) : IAuthorizationEvidenceSession
        {
            public IUnitOfWork Unit => inner.Unit;
            public CancellationToken DeadlineToken => inner.DeadlineToken;
            public async Task BeginVerificationAsync()
            {
                await inner.BeginVerificationAsync();
                owner.Verifications++;
                owner.VerificationBackend = ((NpgsqlConnection)Unit.Connection).ProcessID;
                await using var command = new NpgsqlCommand("SHOW transaction_read_only", (NpgsqlConnection)Unit.Connection, (NpgsqlTransaction?)Unit.Transaction);
                owner.VerificationReadOnly = (string?)await command.ExecuteScalarAsync(DeadlineToken);
            }
            public async Task CompleteAsync()
            {
                await inner.CompleteAsync();
                if (!owner.LoseFirstCommitAcknowledgement || owner._lost) return;
                owner._lost = true;
                throw new IOException("private-evidence-provider-canary");
            }
            public void MarkCleanupFailed() => inner.MarkCleanupFailed();
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    private sealed class Logs : ILogger<PostgresAuditStore>
    {
        internal List<(LogLevel Level, string Text, Exception? Exception)> Items { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Items.Add((logLevel, formatter(state, exception), exception));
    }
}
