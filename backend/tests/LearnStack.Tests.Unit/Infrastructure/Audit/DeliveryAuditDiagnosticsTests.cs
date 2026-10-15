using System.Diagnostics.Metrics;
using FluentAssertions;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.Audit.Capture;
using LearnStack.Infrastructure.Caching;
using LearnStack.Modules.Tenancy.Application.Contracts.IntegrationEvents;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Messaging;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Audit;

/// <summary>Actual audit failures retain their posture while delivery diagnostics exclude provider details.</summary>
public sealed class DeliveryAuditDiagnosticsTests
{
    private const string Canary = "private-provider-detail-must-not-leave-delivery";
    private const string Operation = "tenancy.tenant.provision";
    private const string Traceparent = "00-12345678901234567890123456789012-1234567890123456-01";

    [Fact]
    public void Store_requires_an_accessor_even_when_no_context_is_currently_resolved()
    {
        using var meters = new IsolatedMeterFactory();
        var create = () => new PostgresAuditStore(
            new AuditStateCapture(),
            new Lazy<NpgsqlDataSource>(() => throw new InvalidOperationException("No database is needed.")),
            new CaptureLogger<PostgresAuditStore>(), meters, new AuditHealth(), new AuditCatalog([]), null!);

        create.Should().Throw<ArgumentNullException>().WithParameterName("tenantAccessor");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Override_read_failure_preserves_the_declared_tier_and_reports_with_context_appropriate_detail(
        bool delivery)
    {
        var tenant = TenantId.From(Guid.CreateVersion7());
        var failure = new IOException(Canary);
        var logger = new CaptureLogger<AuditConfigService>();
        using var meters = new IsolatedMeterFactory();
        var cache = new InMemoryCacheService(new FixedClock(), meters);
        var attempted = 0;
        var source = new Lazy<NpgsqlDataSource>(() =>
        {
            attempted++;
            throw failure;
        });
        var classifier = new AuditConfigService(cache, source, logger, Accessor(tenant, delivery));
        var declaration = new AuditCatalogEntry(
            "tenancy", Operation, OperationType.Create, OperationClass.Should, null);

        var classified = await classifier.ClassifyAsync(tenant, declaration);

        attempted.Should().Be(1, "a real cache miss must reach the lazy database loader");
        classified.Should().Be(AuditClassification.Should,
            "an unavailable override cannot lower or refuse the declared coverage");
        var log = logger.Entries.Should().ContainSingle().Subject;
        log.Level.Should().Be(LogLevel.Error, "override fallback retains its established severity");
        AssertDiagnostic(log, delivery, failure);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task Store_failure_preserves_durability_health_and_counter_posture_while_minimizing_delivery_logs(
        bool delivery, bool standaloneMust)
    {
        var tenant = TenantId.From(Guid.CreateVersion7());
        var failure = new IOException(Canary);
        var logger = new CaptureLogger<PostgresAuditStore>();
        var health = new AuditHealth();
        using var meters = new IsolatedMeterFactory();
        var sourceAttempts = 0;
        var store = new PostgresAuditStore(
            new AuditStateCapture(),
            new Lazy<NpgsqlDataSource>(() =>
            {
                sourceAttempts++;
                throw failure;
            }),
            logger, meters, health, new AuditCatalog([]), Accessor(tenant, delivery));
        var measurements = new List<(long Count, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, active) =>
        {
            // Meter identity excludes measurements from concurrently running tests.
            if (ReferenceEquals(instrument.Meter, meters.Meter)
                && instrument.Name == PostgresAuditStore.StandaloneWriteFailureCounterName)
                active.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, count, tags, _) =>
            measurements.Add((count, tags.ToArray())));
        listener.Start();
        var draft = Draft(tenant, standaloneMust ? OperationClass.Must : OperationClass.Should);

        if (standaloneMust)
        {
            var thrown = await Assert.ThrowsAsync<IOException>(() => store.WriteStandaloneAsync(draft));
            thrown.Should().BeSameAs(failure, "diagnostic minimization must not change the caller's failure contract");
            health.IsHealthy.Should().BeFalse();
            health.FailingOperation.Should().Be(Operation);
            var measurement = measurements.Should().ContainSingle().Subject;
            measurement.Count.Should().Be(1);
            measurement.Tags.Should().ContainSingle().Which
                .Should().Be(new KeyValuePair<string, object?>("operation", Operation));
        }
        else
        {
            await store.WriteBestEffortAsync(draft);
            health.IsHealthy.Should().BeTrue("a best-effort loss does not mark MUST durability unavailable");
            health.FailingOperation.Should().BeNull();
            measurements.Should().BeEmpty("only failed MUST standalone writes increment the failure counter");
        }

        sourceAttempts.Should().Be(1, "the actual store must attempt its standalone database path");
        var log = logger.Entries.Should().ContainSingle().Subject;
        log.Level.Should().Be(standaloneMust ? LogLevel.Critical : LogLevel.Error);
        log.Message.Should().Contain(Operation);
        AssertDiagnostic(log, delivery, failure);
    }

    private static void AssertDiagnostic(LogEntry log, bool delivery, Exception failure)
    {
        log.Message.Should().NotBeNullOrWhiteSpace("bounded diagnostics must still report the failure");
        log.Message.Should().NotContain(Canary).And.NotContain(typeof(IOException).FullName!);
        if (delivery)
        {
            log.Exception.Should().BeNull("the logger must not receive provider text, stack or raw exception type");
        }
        else
        {
            log.Exception.Should().BeSameAs(failure, "ordinary request diagnostics retain the original exception");
        }
    }

    private static StaticTenantContextAccessor Accessor(TenantId tenant, bool delivery)
    {
        if (!delivery) return new(new RequestContext(tenant));
        var fact = new TenantCreatedIntegrationEventV1
        {
            EventId = Guid.CreateVersion7(),
            TenantId = tenant.Value,
            OccurredAt = DateTimeOffset.UnixEpoch,
        };
        return new(EventTenantContext.FromEnvelope(new IntegrationEventEnvelope(fact, Traceparent)));
    }

    private static AuditEntryDraft Draft(TenantId tenant, OperationClass classification) => new()
    {
        Id = AuditEntryId.From(Guid.CreateVersion7()),
        TenantId = tenant,
        OrganizationId = null,
        ActorUserId = UserId.SystemActor,
        ActorEmail = null,
        ModuleName = "tenancy",
        Operation = Operation,
        OperationType = OperationType.Create,
        OperationClass = classification,
        EntityType = null,
        EntityId = null,
        Outcome = AuditOutcome.Failed,
        ErrorKey = null,
        Reason = null,
        BeforeState = null,
        AfterState = null,
        Changes = "[]",
        CorrelationId = Traceparent,
        IpAddress = null,
        UserAgent = null,
        Timestamp = DateTimeOffset.UnixEpoch,
        Metadata = null,
    };

    private sealed class RequestContext(TenantId tenant) : ITenantContext
    {
        public bool IsResolved => true;
        public TenantId TenantId => tenant;
        public OrganizationId? OrganizationId => null;
        public UserId? UserId { get; } = LearnStack.SharedKernel.Identifiers.UserId.From(Guid.CreateVersion7());
        public string? CorrelationId => Traceparent;
        public string? ModuleName => "tenancy";
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
    }

    private sealed class IsolatedMeterFactory : IMeterFactory
    {
        public Meter Meter { get; } = new(PostgresAuditStore.MeterName);
        public Meter Create(MeterOptions options) => Meter;
        public void Dispose() => Meter.Dispose();
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add(new(logLevel, formatter(state, exception), exception));
    }
}
