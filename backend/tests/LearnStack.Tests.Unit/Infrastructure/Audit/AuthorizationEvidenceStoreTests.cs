using System.Diagnostics.Metrics;
using FluentAssertions;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Audit;

public sealed class AuthorizationEvidenceStoreTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("capacity")]
    [InlineData("provider")]
    public async Task Independent_evidence_failure_reports_once_without_generic_fallback_or_provider_payload(string failure)
    {
        using var meters = new Meters();
        var logger = new Logs();
        var health = new AuditHealth();
        var genericAttempted = false;
        var factory = failure == "missing" ? null : new FailingFactory(failure);
        var store = new PostgresAuditStore(new AuditStateCapture(), new Lazy<NpgsqlDataSource>(() =>
        {
            genericAttempted = true;
            throw new InvalidOperationException("private-generic-source-canary");
        }), logger, meters, health, new AuditCatalog([new Catalog()]), StaticTenantContextAccessor.Unresolved,
            new GlobalIdentityEnrollment(), factory);
        var counts = new List<(long Count, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, active) =>
        {
            if (ReferenceEquals(instrument.Meter, meters.Meter) && instrument.Name == PostgresAuditStore.StandaloneWriteFailureCounterName)
                active.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, count, tags, _) => counts.Add((count, tags.ToArray())));
        listener.Start();
        var occurrence = Source.Mint();
        var thrown = await FluentActions.Awaiting(() => store.WriteAuthorizationStandaloneAsync(occurrence))
            .Should().ThrowAsync<AuditWriteFailedException>();
        thrown.Which.InnerException.Should().BeNull();
        thrown.Which.ToString().Should().NotContain("private-");
        genericAttempted.Should().BeFalse();
        if (factory is not null) factory.Attempts.Should().Be(1);
        health.IsHealthy.Should().BeFalse();
        health.FailingOperation.Should().Be("identity.authorization.deny");
        logger.Items.Should().ContainSingle().Which.Should().Be(LogLevel.Critical);
        logger.Exception.Should().BeNull();
        var signal = counts.Should().ContainSingle().Subject;
        signal.Count.Should().Be(1);
        signal.Tags.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, object?>("operation", "identity.authorization.deny"));
    }

    [Fact]
    public async Task Caller_cancellation_keeps_its_token_and_the_failure_signal_without_trying_another_lane()
    {
        using var meters = new Meters();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var health = new AuditHealth();
        var logger = new Logs();
        var factory = new FailingFactory("cancel");
        var store = new PostgresAuditStore(new AuditStateCapture(), new Lazy<NpgsqlDataSource>(() => throw new InvalidOperationException()),
            logger, meters, health, new AuditCatalog([new Catalog()]), StaticTenantContextAccessor.Unresolved,
            new GlobalIdentityEnrollment(), factory);
        var failure = await FluentActions.Awaiting(() => store.WriteAuthorizationStandaloneAsync(Source.Mint(), cancellation.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        failure.Which.CancellationToken.Should().Be(cancellation.Token);
        factory.Attempts.Should().Be(1);
        health.IsHealthy.Should().BeFalse();
        logger.Items.Should().ContainSingle().Which.Should().Be(LogLevel.Critical);
    }

    [Fact]
    public async Task An_unclassified_event_refuses_before_any_source_admission()
    {
        using var meters = new Meters();
        var factory = new FailingFactory("provider");
        var store = new PostgresAuditStore(new AuditStateCapture(), new Lazy<NpgsqlDataSource>(() => throw new InvalidOperationException()),
            new Logs(), meters, new AuditHealth(), new AuditCatalog([]), StaticTenantContextAccessor.Unresolved,
            new GlobalIdentityEnrollment(), factory);
        await FluentActions.Awaiting(() => store.WriteAuthorizationStandaloneAsync(Source.Mint()))
            .Should().ThrowAsync<ArgumentException>();
        factory.Attempts.Should().Be(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void A_test_capacity_cannot_widen_the_two_slot_production_bound(int capacity)
    {
        var create = () => new AuthorizationEvidenceSource(Configuration("learnstack_app"), capacity);
        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Invalid_source_configuration_is_refused_without_credential_diagnostics(bool invalidSyntax)
    {
        var connection = invalidSyntax ? "not-a-connection-string=private-canary" : Configuration("learnstack_platform");
        var create = () => new AuthorizationEvidenceSource(connection);
        var failure = create.Should().Throw<InvalidOperationException>().Which;
        failure.Message.Should().Contain("ConnectionStrings:AuthorizationEvidence").And.NotContain("private-canary");
        failure.InnerException.Should().BeNull();
    }

    private static string Configuration(string role) => new NpgsqlConnectionStringBuilder
    {
        Host = "localhost",
        Database = "learnstack",
        Username = role,
        Password = "private-canary",
    }.ConnectionString;

    private sealed class FailingFactory(string failure) : IAuthorizationEvidenceSessionFactory
    {
        internal int Attempts { get; private set; }
        public Task<IAuthorizationEvidenceSession?> OpenAsync(AuthorizationEvidenceOccurrence occurrence, CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (failure == "cancel") cancellationToken.ThrowIfCancellationRequested();
            return failure == "capacity" ? Task.FromResult<IAuthorizationEvidenceSession?>(null)
                : throw new IOException("private-evidence-provider-canary");
        }
    }

    private sealed class Source : AuthorizationEvidenceOccurrence.MintingSource
    {
        internal static AuthorizationEvidenceOccurrence Mint() => ForOccurrence(AuthorizationEvidenceKind.Denial, new AuditEntryDraft
        {
            Id = AuditEntryId.From(Guid.CreateVersion7()),
            TenantId = TenantId.From(Guid.CreateVersion7()),
            OrganizationId = null,
            ActorUserId = UserId.From(Guid.CreateVersion7()),
            ActorEmail = null,
            ModuleName = "identity",
            Operation = "identity.authorization.deny",
            OperationType = OperationType.SecurityEvent,
            OperationClass = OperationClass.Must,
            EntityType = null,
            EntityId = null,
            Outcome = AuditOutcome.Denied,
            ErrorKey = "authorization.denied",
            Reason = null,
            BeforeState = null,
            AfterState = null,
            Changes = null,
            CorrelationId = null,
            IpAddress = null,
            UserAgent = null,
            Timestamp = DateTimeOffset.UtcNow,
            Metadata = "{}",
        });
    }

    private sealed class Catalog : IAuditCatalogSource
    {
        public string ModuleName => "identity";
        public void Describe(IAuditCatalogBuilder builder) => builder.DeclareOffPath("identity.authorization.deny", OperationType.SecurityEvent, OperationClass.Must);
    }

    private sealed class Meters : IMeterFactory
    {
        internal Meter Meter { get; } = new(PostgresAuditStore.MeterName);
        public Meter Create(MeterOptions options) => Meter;
        public void Dispose() => Meter.Dispose();
    }

    private sealed class Logs : ILogger<PostgresAuditStore>
    {
        internal List<LogLevel> Items { get; } = [];
        internal Exception? Exception { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Exception = exception;
            formatter(state, exception).Should().NotContain("private-");
            Items.Add(logLevel);
        }
    }
}
