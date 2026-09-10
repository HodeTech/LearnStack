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
public sealed class AuditingTenantAssertionRecorderTests
{
    private static readonly Guid Resolved = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid Asserted = Guid.Parse("99999999-9999-7999-8999-999999999999");

    [Fact]
    public async Task An_authenticated_mismatch_is_a_row_every_time()
    {
        // No coalescing and no per-tenant ceiling. The tier is bounded by token issuance
        // and the actor is the finding, so suppressing repeats loses the signal — and a
        // ceiling would be worse than the flood it prevents: ten cheap requests against a
        // tenant of the attacker's choosing would silence every later authenticated
        // mismatch against that tenant for the window.
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

        // The resolver sets this and runs BEFORE the assertion middleware, so the row, the
        // response header, the Problem Details body and the Warning line carry one value.
        // The comment that used to justify a null here said the correlation was the
        // pipeline's and unavailable; it was neither.
        row.CorrelationId.Should().Be(HarnessContext.Correlation);

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
    }

    [Fact]
    public async Task An_unresolved_occurrence_still_reaches_the_metric()
    {
        // The decorator's ONLY job on this path is to forward, and nothing checked that it
        // did: `An_unresolved_request_writes_nothing` asserts the absence of a row, which
        // a recorder that dropped the call entirely also satisfies — silently killing
        // learnstack_tenant_assertion_unresolved_total.
        var unresolved = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, active) =>
        {
            if (instrument.Meter.Name == LoggingTenantAssertionRecorder.MeterName
                && instrument.Name == LoggingTenantAssertionRecorder.UnresolvedCounterName)
            {
                active.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => unresolved += measurement);
        listener.Start();

        Recorder(new RecordingStore(), out _).RecordUnresolved(TenantAssertionDimension.Tenant);

        unresolved.Should().Be(1, "counted, never recorded — and counted is half of that rule");
    }

    [Theory]
    [MemberData(nameof(UntranslatedFailures))]
    public async Task A_store_failure_of_any_kind_leaves_the_refusal_alone(Exception failure)
    {
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

        // And it is not swallowed silently. A wide catch with no log is the one shape that
        // would be worse than the 500 it replaces: an operation refused, unrecorded, and
        // invisible. Critical because nothing downstream will say it again — the store
        // could not translate this failure, so no counter and no health signal fired for
        // it there either.
        logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Critical)
            .Which.Message.Should().Contain(AuditingTenantAssertionRecorder.RejectOperation);
    }

    public static TheoryData<Exception> UntranslatedFailures()
    {
        var failures = new TheoryData<Exception>();

        // The measured one: a Lazy data source with no credential behind it.
        failures.Add(new InvalidOperationException("no credential"));

        // The physical-connection initializer refusing a role that can bypass RLS.
        failures.Add(new InvalidOperationException("the role bypasses row level security"));

        // And a shape nobody predicted, which is the point of catching by width.
        failures.Add(new TimeoutException("the pool was exhausted"));

        return failures;
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
            .Should().BeEquivalentTo(["dimension", "assertedOrganizationId", "authenticated"]);
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
        row.ActorUserId.Should().BeNull("there is no principal in this process until Phase 02b");
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
        var mismatches = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, active) =>
        {
            if (instrument.Meter.Name == LoggingTenantAssertionRecorder.MeterName
                && instrument.Name == LoggingTenantAssertionRecorder.MismatchCounterName)
            {
                active.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => mismatches += measurement);
        listener.Start();

        var store = new RecordingStore();
        var recorder = Recorder(store, out _, threshold: 100);

        await recorder.RecordRejectionAsync(Rejection(authenticated: false));
        await recorder.RecordRejectionAsync(Rejection(authenticated: false));

        store.Written.Should().BeEmpty("two is far below the threshold");
        mismatches.Should().Be(2, "the counter moves on every occurrence, row or not");
    }

    private static TenantAssertionRejection Rejection(
        bool authenticated,
        TenantAssertionDimension dimension = TenantAssertionDimension.Tenant) =>
        new(Resolved, dimension, Asserted, authenticated);

    private static AuditingTenantAssertionRecorder Recorder(
        RecordingStore store,
        out MovableClock clock,
        int threshold = 10,
        IAuditCatalog? catalog = null,
        ILogger<AuditingTenantAssertionRecorder>? logger = null)
    {
        clock = new MovableClock(DateTimeOffset.UnixEpoch);

        var meterFactory = new ServiceCollection().AddMetrics()
            .BuildServiceProvider().GetRequiredService<IMeterFactory>();

        return new AuditingTenantAssertionRecorder(
            new LoggingTenantAssertionRecorder(
                NullLogger<LoggingTenantAssertionRecorder>.Instance, meterFactory),
            catalog ?? new AuditCatalog([new TenancyAuditCatalogSource()]),
            store,
            new TenantAssertionBurstDetector(
                Options.Create(new AssertionBurstOptions
                {
                    Threshold = threshold,
                    Window = TimeSpan.FromMinutes(5),
                }),
                clock),
            new HarnessContext(),
            clock,
            new SystemGuidFactory(),
            logger ?? NullLogger<AuditingTenantAssertionRecorder>.Instance);
    }

    /// <summary>A resolved context, so the row can carry the correlation the resolver set.</summary>
    private sealed class HarnessContext : ITenantContext
    {
        public const string Correlation = "00-harness-assertion-01";

        public bool IsResolved => true;

        public TenantId TenantId => TenantId.From(Resolved);

        public OrganizationId? OrganizationId => null;

        public UserId? UserId => null;

        public string? CorrelationId => Correlation;

        public string? ModuleName => "tenancy";
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

        public bool Fails { get; init; }

        /// <summary>A failure the store does NOT translate, which is the escaping kind.</summary>
        public Exception? FailsWith { get; init; }

        public Task WriteStandaloneAsync(
            AuditEntryDraft entry, CancellationToken cancellationToken = default)
        {
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

        public Task WritePlatformScopeAsync(
            AuditEntryDraft entry,
            System.Data.Common.DbConnection connection,
            System.Data.Common.DbTransaction transaction,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("that path has its own caller");
    }

    private sealed class CapturingLogger : ILogger<AuditingTenantAssertionRecorder>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _entries.Add((logLevel, formatter(state, exception)));
    }
}
