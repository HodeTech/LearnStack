using System.Diagnostics.Metrics;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Api.Tenancy;
using LearnStack.Infrastructure.Audit;
using LearnStack.Modules.Tenancy.Application.Audit;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Tenancy;

/// <summary>
/// The row a rejected tenant assertion leaves, and the two tiers that decide whether
/// there is one.
/// </summary>
/// <remarks>
/// <para>
/// The catalogue is the real one, merged from the shipped
/// <c>TenancyAuditCatalogSource</c>, because half of what these cases assert is that the
/// row carries what the module DECLARED. A fake declaring the slugs would keep them green
/// after the source that has to declare them stopped.
/// </para>
/// <para>
/// The store is a double, and the write is proved against the real table by
/// <c>AuditStoreTests</c> — the two halves are separate on purpose: whether a row is
/// written and what it says is this file's, whether PostgreSQL accepts it is that one's.
/// </para>
/// </remarks>
public sealed partial class AuditingTenantAssertionRecorderTests : IDisposable
{
    private readonly List<ServiceProvider> _providers = [];

    public void Dispose()
    {
        foreach (var provider in _providers) provider.Dispose();
    }

    private static readonly Guid Resolved = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid Asserted = Guid.Parse("99999999-9999-7999-8999-999999999999");

    private static readonly UserId Actor = UserId.From(Guid.Parse("22222222-2222-7222-8222-222222222222"));
    private static readonly OrganizationId TrustedOrganization =
        OrganizationId.From(Guid.Parse("33333333-3333-7333-8333-333333333333"));
    private const string Correlation = "00-harness-assertion-01";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_invalid_defended_tenant_is_refused_before_signal_or_store(bool sentinel)
    {
        var store = new RecordingStore();
        var recorder = Recorder(store, out _);
        var rejected = Rejection(authenticated: true) with
        { ResolvedTenantId = sentinel ? TenantId.PlatformSentinel.Value : Guid.Empty };
        var invoke = () => recorder.RecordRejectionAsync(rejected);
        await invoke.Should().ThrowAsync<ArgumentException>();
        store.Attempts.Should().Be(0);
    }

    [Fact]
    public async Task Unassigned_trusted_organization_is_not_dereferenced_or_written_as_metadata()
    {
        var store = new RecordingStore();
        var recorder = Recorder(store, out _);
        await recorder.RecordRejectionAsync(Rejection(authenticated: true) with
        { TrustedOrganizationId = (new OrganizationId[1])[0] });
        using var metadata = JsonDocument.Parse(store.Written.Single().Metadata!);
        metadata.RootElement.GetProperty("trustedOrganizationId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Each_admitted_authenticated_mismatch_has_its_own_row()
    {
        // Admitted events are individual rows; the 600-attempt boundary is proved separately.
        var store = new RecordingStore();
        var recorder = Recorder(store, out _);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await recorder.RecordRejectionAsync(Rejection(authenticated: true));
        }

        store.Written.Should().HaveCount(5);
        store.Written.Should().OnlyContain(
            row => row.Operation == AuditingTenantAssertionRecorder.RejectOperation);
    }

    [Fact]
    public async Task An_anonymous_mismatch_is_a_row_only_when_the_window_crosses()
    {
        // An anonymous mismatch is not itself an audited operation; the burst is. Without
        // this an anonymous caller chooses how much a tenant's audit log grows.
        var store = new RecordingStore();
        var recorder = Recorder(store, out _, threshold: 3);

        await recorder.RecordRejectionAsync(Rejection(authenticated: false));
        await recorder.RecordRejectionAsync(Rejection(authenticated: false));

        store.Written.Should().BeEmpty("two is a mistake, not a run");

        await recorder.RecordRejectionAsync(Rejection(authenticated: false));
        await recorder.RecordRejectionAsync(Rejection(authenticated: false));
        await recorder.RecordRejectionAsync(Rejection(authenticated: false));

        store.Written.Should().ContainSingle()
            .Which.Operation.Should().Be(AuditingTenantAssertionRecorder.BurstOperation);
    }

    [Fact]
    public async Task A_burst_whose_write_fails_stays_consumed_for_its_window()
    {
        // The crossing is consumed BEFORE the write and stays consumed when the write fails.
        // Releasing it would retry the write on every later anonymous occurrence in the window —
        // one database round trip per request, the amplification the burst row exists to bound.
        // Only the detector pinned it; every recorder failure case was authenticated (the fifth
        // review of Packet 9).
        var store = new RecordingStore { Fails = true };
        var recorder = Recorder(store, out var clock, threshold: 3);

        for (var occurrence = 0; occurrence < 8; occurrence++)
        {
            await recorder.RecordRejectionAsync(Rejection(authenticated: false));
        }

        store.Attempts.Should().Be(1, "one crossing is one attempt, whatever came of it");

        clock.Advance(TimeSpan.FromMinutes(5));

        for (var occurrence = 0; occurrence < 3; occurrence++)
        {
            await recorder.RecordRejectionAsync(Rejection(authenticated: false));
        }

        store.Attempts.Should().Be(2, "the next window crosses, and writes, again");
    }

    [Fact]
    public async Task An_authenticated_mismatch_does_not_spend_the_anonymous_budget()
    {
        // Separate tiers, separate counters. Feeding authenticated occurrences into the
        // anonymous window would let a token holder cross it — and then the burst row
        // would say "anonymous" about traffic that was not.
        var store = new RecordingStore();
        var recorder = Recorder(store, out _, threshold: 3);

        await recorder.RecordRejectionAsync(Rejection(authenticated: true));
        await recorder.RecordRejectionAsync(Rejection(authenticated: true));
        await recorder.RecordRejectionAsync(Rejection(authenticated: false));

        store.Written.Should().HaveCount(2, "the two authenticated rows, and no burst");
        store.Written.Should().OnlyContain(
            row => row.Operation == AuditingTenantAssertionRecorder.RejectOperation);
    }

    [Fact]
    public async Task The_row_carries_the_resolved_tenant_and_the_asserted_value_is_metadata()
    {
        // The whole reason this row is safe to write. audit_log is tenant-owned and the
        // standalone write announces app.tenant_id FROM THE DRAFT, so writing the asserted
        // id would set that GUC to an attacker-chosen value — handing an anonymous caller
        // a primitive that writes rows into a tenant of its choosing.
        var store = new RecordingStore();
        var recorder = Recorder(store, out _);

        await recorder.RecordRejectionAsync(Rejection(authenticated: true));

        var row = store.Written.Should().ContainSingle().Subject;

        row.TenantId.Value.Should().Be(Resolved);
        row.TenantId.Should().NotBe(TenantId.PlatformSentinel,
            "nothing is ever written under a sentinel platform tenant");
        row.OrganizationId.Should().BeNull(
            "the finding is about the tenant boundary, and a tenant-wide row is the one a "
            + "tenant-scoped read sees");

        var metadata = JsonDocument.Parse(row.Metadata!).RootElement;
        metadata.GetProperty("assertedTenantId").GetString().Should().Be(Asserted.ToString());
        metadata.GetProperty("dimension").GetString().Should().Be("Tenant");
        metadata.GetProperty("authenticated").GetBoolean().Should().BeTrue();
        metadata.GetProperty("assertionSource").GetString().Should().Be("header");
        metadata.GetProperty("trustedOrganizationId").ValueKind.Should().Be(JsonValueKind.Null);

        // The resolver sets this and runs BEFORE the assertion middleware, so the row, the
        // response header, the Problem Details body and the Warning line carry one value.
        // The comment that used to justify a null here said the correlation was the
        // pipeline's and unavailable; it was neither.
        row.CorrelationId.Should().Be(Correlation);

        // Read from the clock, not from any earlier reading: `ix_audit_log_timestamp` and
        // every retention window are ordered by it.
        row.Timestamp.Should().Be(DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public async Task A_burst_row_does_not_claim_the_traffic_was_authenticated()
    {
        // The mirror of the assertion above, and it was missing: `authenticated` was
        // pinned true on the per-occurrence row and pinned nowhere on the burst row, so a
        // hard-coded `true` shipped a security row claiming a validated principal about
        // traffic that had none. That is the one field an investigator reads to decide
        // whether a token was involved.
        var store = new RecordingStore();
        var recorder = Recorder(store, out _, threshold: 2);

        await recorder.RecordRejectionAsync(Rejection(authenticated: false));
        await recorder.RecordRejectionAsync(Rejection(authenticated: false));

        var row = store.Written.Should().ContainSingle().Subject;

        row.Operation.Should().Be(AuditingTenantAssertionRecorder.BurstOperation);
        JsonDocument.Parse(row.Metadata!).RootElement
            .GetProperty("authenticated").GetBoolean().Should().BeFalse();
        row.ActorUserId.Should().BeNull();
    }

    [Theory]
    [InlineData(TenantAssertionSource.Header, "header")]
    [InlineData(TenantAssertionSource.JwtClaim, "jwt-claim")]
    public async Task A_rejection_carries_its_captured_attribution_without_an_ambient_context(
        TenantAssertionSource source, string label)
    {
        var store = new RecordingStore();
        var recorder = Recorder(store, out _);
        const string capturedCorrelation = "captured-before-resolution-refusal";
        var rejection = Rejection(authenticated: true, source: source,
            trustedOrganizationId: TrustedOrganization) with
        { CorrelationId = capturedCorrelation };

        await recorder.RecordRejectionAsync(rejection);

        var row = store.Written.Should().ContainSingle().Subject;
        row.TenantId.Value.Should().Be(Resolved);
        row.ActorUserId.Should().Be(Actor);
        row.CorrelationId.Should().Be(capturedCorrelation);
        row.OrganizationId.Should().BeNull("trusted organization is evidence, not the security row's scope");
        var metadata = JsonDocument.Parse(row.Metadata!).RootElement;
        metadata.GetProperty("assertionSource").GetString().Should().Be(label);
        metadata.GetProperty("trustedOrganizationId").GetGuid().Should().Be(TrustedOrganization.Value);
    }

    [Theory]
    [InlineData(TenantAssertionSource.Header, "header")]
    [InlineData(TenantAssertionSource.JwtClaim, "jwt-claim")]
    public async Task Each_rejection_source_reaches_the_counter_without_actor_or_correlation_labels(
        TenantAssertionSource source, string label)
    {
        using var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meterFactory = provider.GetRequiredService<IMeterFactory>();
        var meter = meterFactory.Create(LoggingTenantAssertionRecorder.MeterName);
        var measurements = new List<(long Count, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, active) =>
        {
            if (ReferenceEquals(instrument.Meter, meter)
                && instrument.Name == LoggingTenantAssertionRecorder.MismatchCounterName)
                active.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, count, tags, _) =>
            measurements.Add((count, tags.ToArray())));
        listener.Start();
        var signalLogger = new CapturingLogger<LoggingTenantAssertionRecorder>();
        var recorder = Recorder(new RecordingStore(), out _, meterFactory: meterFactory,
            signalLogger: signalLogger);

        // A different recorder's same-name instrument must not make this proof pass.
        using var unrelated = new Meter(LoggingTenantAssertionRecorder.MeterName);
        unrelated.CreateCounter<long>(LoggingTenantAssertionRecorder.MismatchCounterName).Add(9);
        measurements.Should().BeEmpty();

        await recorder.RecordRejectionAsync(Rejection(authenticated: true, source: source));

        var measurement = measurements.Should().ContainSingle().Subject;
        measurement.Count.Should().Be(1);
        measurement.Tags.Should().BeEquivalentTo(new Dictionary<string, object?>
        {
            ["dimension"] = "Tenant",
            ["source"] = label,
            ["authenticated"] = true,
        });
        var warning = signalLogger.Entries.Should().ContainSingle().Subject;
        warning.Level.Should().Be(LogLevel.Warning);
        warning.Properties.Should().ContainKey("AssertionSource").WhoseValue.Should().Be(label);
        warning.Properties.Should().ContainKey("ActorUserId").WhoseValue.Should().Be(Actor.Value);
        warning.Properties.Should().ContainKey("CorrelationId").WhoseValue.Should().Be(Correlation);
    }

    [Fact]
    public void A_no_tenant_membership_refusal_counts_and_diagnoses_without_touching_the_store()
    {
        using var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meterFactory = provider.GetRequiredService<IMeterFactory>();
        var meter = meterFactory.Create(LoggingTenantAssertionRecorder.MeterName);
        var measurements = new List<(long Count, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, active) =>
        {
            if (ReferenceEquals(instrument.Meter, meter)
                && instrument.Name == LoggingTenantAssertionRecorder.UnresolvedCounterName)
                active.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, count, tags, _) =>
            measurements.Add((count, tags.ToArray())));
        listener.Start();
        var store = new RecordingStore { FailsWith = new InvalidOperationException("must not be touched") };
        var signalLogger = new CapturingLogger<LoggingTenantAssertionRecorder>();
        var recorder = Recorder(store, out _, meterFactory: meterFactory, signalLogger: signalLogger);

        using var unrelated = new Meter(LoggingTenantAssertionRecorder.MeterName);
        var unrelatedCounter = unrelated.CreateCounter<long>(LoggingTenantAssertionRecorder.UnresolvedCounterName);
        var unrelatedCount = 0L;
        using var unrelatedListener = new MeterListener();
        unrelatedListener.InstrumentPublished = (instrument, active) =>
        {
            if (ReferenceEquals(instrument, unrelatedCounter)) active.EnableMeasurementEvents(instrument);
        };
        unrelatedListener.SetMeasurementEventCallback<long>((_, count, _, _) => unrelatedCount += count);
        unrelatedListener.Start();
        unrelatedCounter.Add(9);
        unrelatedCount.Should().Be(9);
        measurements.Should().BeEmpty("same-name counters belonging to another recorder are excluded");

        recorder.RecordNoTenantRefusal(new TenantNoTenantRefusal(
            TenantAssertionSource.JwtClaim, TenantAssertionDimension.Tenant, Actor, Correlation));

        store.Attempts.Should().Be(0);
        store.Written.Should().BeEmpty();
        var measurement = measurements.Should().ContainSingle().Subject;
        measurement.Count.Should().Be(1);
        measurement.Tags.Should().BeEquivalentTo(new Dictionary<string, object?>
        {
            ["source"] = "jwt-claim",
            ["dimension"] = "Tenant",
        });
        var diagnostic = signalLogger.Entries.Should().ContainSingle().Subject;
        diagnostic.Level.Should().Be(LogLevel.Debug);
        diagnostic.Message.Should().StartWith("membership_unconfirmed");
        diagnostic.Properties.Keys.Should().BeEquivalentTo(
            ["AssertionSource", "Dimension", "ActorUserId", "CorrelationId", "{OriginalFormat}"]);
        diagnostic.Properties["ActorUserId"].Should().Be(Actor.Value);
        diagnostic.Properties["CorrelationId"].Should().Be(Correlation);
    }

    [Fact]
    public async Task An_unresolved_occurrence_still_reaches_the_metric()
    {
        // The decorator's ONLY job on this path is to forward, and nothing checked that it
        // did: `An_unresolved_request_writes_nothing` asserts the absence of a row, which
        // a recorder that dropped the call entirely also satisfies — silently killing
        // learnstack_tenant_assertion_unresolved_total.
        using var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meterFactory = provider.GetRequiredService<IMeterFactory>();
        var meter = meterFactory.Create(LoggingTenantAssertionRecorder.MeterName);
        var unresolved = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, active) =>
        {
            if (ReferenceEquals(instrument.Meter, meter)
                && instrument.Name == LoggingTenantAssertionRecorder.UnresolvedCounterName)
            {
                active.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => unresolved += measurement);
        listener.Start();

        Recorder(new RecordingStore(), out _, meterFactory: meterFactory)
            .RecordUnresolved(TenantAssertionDimension.Tenant);

        unresolved.Should().Be(1, "counted, never recorded — and counted is half of that rule");
    }

    [Theory]
    [InlineData("no credential", false)]
    [InlineData("the role bypasses row level security", false)]
    [InlineData("the pool was exhausted", true)]
    public async Task A_store_failure_of_any_kind_leaves_the_refusal_alone(string message, bool timeout)
    {
        // Primitive inputs give all three rows stable discovery IDs. Exception
        // instances are not xUnit-serializable and collapse to one discovered case.
        Exception failure = timeout ? new TimeoutException(message) : new InvalidOperationException(message);
        // The catch used to name AuditWriteFailedException alone, and its test threw
        // exactly that type — so the two agreed and neither constrained anything.
        // Measured on the shipped code: a host with no application credential answered an
        // anonymous caller's tenth wrong header with a 500, because the Lazy data source's
        // InvalidOperationException is not a DbException and so is never translated.
        //
        // SEVERAL unrelated types, because the WIDTH is the fix. One type proves the catch
        // names that type; it does not stop the catch being narrowed back to it, which is
        // measured — `catch (InvalidOperationException)` left the whole suite green.
        var store = new RecordingStore { FailsWith = failure };
        var logger = new CapturingLogger();
        var recorder = Recorder(store, out _, logger: logger);

        var act = async () => await recorder.RecordRejectionAsync(Rejection(authenticated: true));

        await act.Should().NotThrowAsync(
            "a failed record does not change a response that is already a refusal");

        // And not swallowed silently — but not a second alert either. The store reports
        // every failure it throws (IAuditStore), so this line says only that the refusal
        // stands; the case below composes the real store to prove the first half.
        logger.Entries.Should().NotContain(entry => entry.Level == LogLevel.Critical);
        logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Warning)
            .Which.Message.Should().Contain(AuditingTenantAssertionRecorder.RejectOperation);
    }

    [Fact]
    public async Task The_real_store_reports_a_failure_once_and_the_recorder_does_not_repeat_it()
    {
        // The fourth review of Packet 9 composed the two for real: a data source that cannot
        // be built made ONE failure produce two Critical lines, the second saying the store
        // had not counted it — beside a counter of 1 and an unhealthy check. The stub above
        // throws without reporting, so only the real store can pin the whole of it.
        var health = new AuditHealth();
        var storeLogger = new CapturingLogger<PostgresAuditStore>();
        var recorderLogger = new CapturingLogger();
        using var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meterFactory = provider.GetRequiredService<IMeterFactory>();
        var meter = meterFactory.Create(PostgresAuditStore.MeterName);

        var store = new PostgresAuditStore(
            new AuditStateCapture(),
            new Lazy<NpgsqlDataSource>(
                () => throw new InvalidOperationException("ConnectionStrings:Default is absent.")),
            storeLogger,
            meterFactory,
            health,
            new AuditCatalog([]), StaticTenantContextAccessor.Unresolved);

        var counted = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, active) =>
        {
            if (ReferenceEquals(instrument.Meter, meter)
                && instrument.Name == PostgresAuditStore.StandaloneWriteFailureCounterName)
            {
                active.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => counted += measurement);
        listener.Start();

        // A concurrent store can publish the same instrument name from another meter.
        using var unrelatedMeter = new Meter(PostgresAuditStore.MeterName);
        var unrelatedCounter = unrelatedMeter.CreateCounter<long>(PostgresAuditStore.StandaloneWriteFailureCounterName);
        var unrelatedCount = 0L;
        using var unrelatedListener = new MeterListener();
        unrelatedListener.InstrumentPublished = (instrument, active) =>
        {
            if (ReferenceEquals(instrument, unrelatedCounter)) active.EnableMeasurementEvents(instrument);
        };
        unrelatedListener.SetMeasurementEventCallback<long>((_, measurement, _, _) => unrelatedCount += measurement);
        unrelatedListener.Start();
        unrelatedCounter.Add(7);
        unrelatedCount.Should().Be(7, "the unrelated instrument must actually emit its planted measurement");
        counted.Should().Be(0, "another store's meter must not contribute to this store's failure count");

        var recorder = Recorder(store, out _, logger: recorderLogger);

        var act = async () => await recorder.RecordRejectionAsync(Rejection(authenticated: true));

        await act.Should().NotThrowAsync("the refusal stays a refusal");

        counted.Should().Be(1);
        health.IsHealthy.Should().BeFalse();
        storeLogger.Entries.Count(entry => entry.Level == LogLevel.Critical)
            .Should().Be(1, "the store owns the alert");
        recorderLogger.Entries.Should().NotContain(entry => entry.Level == LogLevel.Critical,
            "one failure is one alert, and the store has raised it");
    }

    [Fact]
    public async Task A_draft_the_store_refuses_before_writing_is_the_recorder_s_alert()
    {
        // The store reports every failure of the write, and none before it: an
        // ArgumentException is a draft it refused as a caller error (IAuditStore), so the
        // recorder is the only one who can raise it — at Critical, with the response unchanged.
        var store = new RecordingStore { FailsWith = new ArgumentException("a platform-scope draft", "entry") };
        var logger = new CapturingLogger();
        var recorder = Recorder(store, out _, logger: logger);

        var act = async () => await recorder.RecordRejectionAsync(Rejection(authenticated: true));

        await act.Should().NotThrowAsync("a failed record does not change a response that is already a refusal");
        logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Critical)
            .Which.Message.Should().Contain(AuditingTenantAssertionRecorder.RejectOperation);
    }

    [Fact]
    public async Task An_organization_mismatch_names_the_organization_key()
    {
        // The key names the dimension it came from, so a reader does not consult a second
        // column to learn which header lied.
        var store = new RecordingStore();
        var recorder = Recorder(store, out _);

        await recorder.RecordRejectionAsync(
            Rejection(authenticated: true, dimension: TenantAssertionDimension.Organization));

        var metadata = JsonDocument.Parse(store.Written.Single().Metadata!).RootElement;

        metadata.TryGetProperty("assertedOrganizationId", out var asserted).Should().BeTrue();
        asserted.GetString().Should().Be(Asserted.ToString());
        metadata.TryGetProperty("assertedTenantId", out _).Should().BeFalse();

        // The key set is CLOSED, not merely a superset. `metadata` is jsonb on an
        // append-only table, so a key added here is permanent and a key that duplicates a
        // column can disagree with it — an `event` key mirroring `operation` was removed
        // for that reason, and nothing noticed when it came back.
        metadata.EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo(["dimension", "assertedOrganizationId", "authenticated",
                "assertionSource", "trustedOrganizationId"]);
    }

    [Fact]
    public async Task The_row_is_the_tier_and_the_type_the_module_declared()
    {
        // Not literals repeated here: the catalogue is what the Tenancy matrix is joined
        // against, so a row composed from anything else can disagree with the document
        // that promises it.
        var store = new RecordingStore();
        var recorder = Recorder(store, out _);

        await recorder.RecordRejectionAsync(Rejection(authenticated: true));

        var row = store.Written.Should().ContainSingle().Subject;

        row.ModuleName.Should().Be("tenancy");
        row.OperationClass.Should().Be(OperationClass.Must,
            "the Security row of the baseline a tenant AuditConfig may never narrow");
        row.OperationType.Should().Be(OperationType.SecurityEvent);
        row.Outcome.Should().Be(AuditOutcome.Denied,
            "the assertion was refused, which is what the 404 already says; `failed` would "
            + "claim the platform could not answer");
        row.ActorUserId.Should().Be(Actor);
        row.IpAddress.Should().BeNull("the source IP is attacker-chosen and stays out");
    }

    [Fact]
    public async Task An_unresolved_request_writes_nothing()
    {
        // audit_log is tenant-owned; with no resolved tenant the only way to write is to
        // invent one, and a sentinel tenant is an unauthenticated, unbounded write target
        // no tenant admin watches. Counted, never recorded — the rule, not a gap.
        var store = new RecordingStore();
        var recorder = Recorder(store, out _);

        recorder.RecordUnresolved(TenantAssertionDimension.Tenant);
        recorder.RecordUnresolved(TenantAssertionDimension.Organization);

        store.Written.Should().BeEmpty();
    }

    [Fact]
    public async Task A_write_that_fails_does_not_change_the_response()
    {
        // A rejected assertion has no uncommitted-but-unaudited state change and no
        // ungranted-but-unaudited disclosure, so fail-closed protects nothing here — while
        // a 503 under load an anonymous caller can generate is a remotely triggerable
        // availability signal that same caller controls (ADR-0036, ADR-0033 Amendment 1).
        var store = new RecordingStore { Fails = true };
        var recorder = Recorder(store, out _);

        var act = async () => await recorder.RecordRejectionAsync(Rejection(authenticated: true));

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task An_undeclared_slug_is_Critical_and_still_does_not_change_the_response()
    {
        // A deployment whose catalogue lost the slug has the detector switched off, which
        // is worth shouting about — and still is not a reason to turn a refusal into a
        // 503, because the response is already a refusal.
        var store = new RecordingStore();
        var logger = new CapturingLogger();
        var recorder = Recorder(store, out _, catalog: new AuditCatalog([]), logger: logger);

        var act = async () => await recorder.RecordRejectionAsync(Rejection(authenticated: true));

        await act.Should().NotThrowAsync();

        store.Written.Should().BeEmpty();
        logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Critical)
            .Which.Message.Should().Contain(AuditingTenantAssertionRecorder.RejectOperation);
    }

    [Fact]
    public async Task The_metric_and_the_warning_happen_whatever_the_tier_decides()
    {
        // The real-time half is unconditional and costs no I/O. An anonymous mismatch
        // below the threshold writes no row — and must still be counted, or the counter an
        // operator alerts on would only ever move once per window.
        using var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meterFactory = provider.GetRequiredService<IMeterFactory>();
        var meter = meterFactory.Create(LoggingTenantAssertionRecorder.MeterName);
        var mismatches = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, active) =>
        {
            if (ReferenceEquals(instrument.Meter, meter)
                && instrument.Name == LoggingTenantAssertionRecorder.MismatchCounterName)
            {
                active.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => mismatches += measurement);
        listener.Start();

        var store = new RecordingStore();
        var recorder = Recorder(store, out _, threshold: 100, meterFactory: meterFactory);

        await recorder.RecordRejectionAsync(Rejection(authenticated: false));
        await recorder.RecordRejectionAsync(Rejection(authenticated: false));

        store.Written.Should().BeEmpty("two is far below the threshold");
        mismatches.Should().Be(2, "the counter moves on every occurrence, row or not");
    }

    private static TenantAssertionRejection Rejection(
        bool authenticated,
        TenantAssertionDimension dimension = TenantAssertionDimension.Tenant,
        TenantAssertionSource source = TenantAssertionSource.Header,
        OrganizationId? trustedOrganizationId = null) =>
        new(Resolved, dimension, Asserted, authenticated, source,
            authenticated ? Actor : null, Correlation, trustedOrganizationId);

    private AuditingTenantAssertionRecorder Recorder(
        IAuditStore store,
        out MovableClock clock,
        int threshold = 10,
        IAuditCatalog? catalog = null,
        ILogger<AuditingTenantAssertionRecorder>? logger = null,
        IMeterFactory? meterFactory = null,
        ILogger<LoggingTenantAssertionRecorder>? signalLogger = null,
        TenantAssertionAuditBudget? auditBudget = null)
    {
        clock = new MovableClock(DateTimeOffset.UnixEpoch);

        if (meterFactory is null)
        {
            var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
            _providers.Add(provider);
            meterFactory = provider.GetRequiredService<IMeterFactory>();
        }

        return new AuditingTenantAssertionRecorder(
            new LoggingTenantAssertionRecorder(
                signalLogger ?? NullLogger<LoggingTenantAssertionRecorder>.Instance, meterFactory),
            catalog ?? new AuditCatalog([new TenancyAuditCatalogSource()]),
            store,
            new TenantAssertionBurstDetector(
                Options.Create(new AssertionBurstOptions
                {
                    Threshold = threshold,
                    Window = TimeSpan.FromMinutes(5),
                }),
                clock),
            auditBudget ?? new TenantAssertionAuditBudget(TimeProvider.System),
            clock,
            new SystemGuidFactory(),
            logger ?? NullLogger<AuditingTenantAssertionRecorder>.Instance);
    }

    private sealed class MovableClock(DateTimeOffset start) : IClock
    {
        private DateTimeOffset _now = start;

        public DateTimeOffset UtcNow => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    /// <summary>Records the drafts, and fails on demand.</summary>
    private sealed class RecordingStore : IAuditStore
    {
        public List<AuditEntryDraft> Written { get; } = [];

        /// <summary>Every write attempted, whether or not it failed.</summary>
        public int Attempts { get; private set; }

        public bool Fails { get; init; }

        /// <summary>A failure the store does NOT translate, which is the escaping kind.</summary>
        public Exception? FailsWith { get; init; }

        public Task WriteStandaloneAsync(
            AuditEntryDraft entry, CancellationToken cancellationToken = default)
        {
            Attempts++;

            if (FailsWith is not null)
            {
                throw FailsWith;
            }

            if (Fails)
            {
                throw new AuditWriteFailedException("the standalone write failed");
            }

            Written.Add(entry);
            return Task.CompletedTask;
        }

        public Task WritePendingAsync(
            LearnStack.SharedKernel.Persistence.IUnitOfWork unitOfWork,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("a rejected assertion has no business transaction");

        public Task WriteBestEffortAsync(
            AuditEntryDraft entry, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("both slugs are MUST");

        public Task WriteJobTerminalAsync(AuditEntryDraft entry, JobTerminalTransition transition,
            System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction,
            CancellationToken cancellationToken = default) => throw new NotSupportedException("The job-terminal path has its own cases.");

        public Task WriteEventTerminalAsync(
            AuditEntryDraft entry,
            EventTerminalTransition transition,
            System.Data.Common.DbConnection connection,
            System.Data.Common.DbTransaction transaction,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The terminal-event path has its own cases.");

        public Task WritePlatformScopeAsync(
            AuditEntryDraft entry,
            System.Data.Common.DbConnection connection,
            System.Data.Common.DbTransaction transaction,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("that path has its own caller");
    }

    private sealed class CapturingLogger : CapturingLogger<AuditingTenantAssertionRecorder>;

    private class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message, IReadOnlyDictionary<string, object?> Properties)> _entries = [];
        public List<EventId> EventIds { get; } = [];

        public IReadOnlyList<(LogLevel Level, string Message, IReadOnlyDictionary<string, object?> Properties)> Entries => _entries;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            EventIds.Add(eventId);
            _entries.Add((logLevel, formatter(state, exception),
                state is IEnumerable<KeyValuePair<string, object?>> properties
                    ? properties.ToDictionary(property => property.Key, property => property.Value)
                    : new Dictionary<string, object?>()));
        }
    }
}
