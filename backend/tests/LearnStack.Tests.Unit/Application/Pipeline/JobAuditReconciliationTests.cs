using System.Diagnostics.Metrics;
using FluentAssertions;
using LearnStack.Application.Audit;
using LearnStack.Infrastructure.Audit;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Unit.Application.Pipeline;

public sealed class JobAuditReconciliationTests
{
    [Fact]
    public async Task Every_MUST_intent_reports_its_refused_standalone_acquisition_without_replacing_the_business_refusal()
    {
        string[] operations = ["tenancy.tenant.provision", "tenancy.organization.create"];
        var catalog = new FakeCatalog(operations.Select(operation => AuditPipelineHarness.Entry(operation)).ToArray());
        var capture = new AuditStateCapture();
        var owner = JobApplicationSourceOwner.Create();
        using var binding = owner.Enter();
        var context = JobTenantContext.FromValidatedJob(TenantId.From(AuditPipelineHarness.Tenant), null,
            BackgroundJobId.From(Guid.CreateVersion7()), null, null, null,
            "00-11111111111111111111111111111111-1111111111111111-01", "tenancy");
        var auditLogger = new CaptureLogger<PostgresAuditStore>();
        var reconcileLogger = new CaptureLogger<AuditExecutionCoordinator>();
        var health = new AuditHealth();
        using var meters = new AuditMeterFactory();
        var sourceAttempts = 0;
        var store = new PostgresAuditStore(capture, new Lazy<NpgsqlDataSource>(() =>
        {
            sourceAttempts++;
            throw new InvalidOperationException("Reconciliation must not reach another application source.");
        }), auditLogger, meters, health, catalog, new StaticTenantContextAccessor(context), new LearnStack.Infrastructure.Persistence.GlobalIdentityEnrollment());
        var failures = new List<(long Count, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, active) =>
        {
            if (ReferenceEquals(instrument.Meter, meters.Meter)
                && instrument.Name == PostgresAuditStore.StandaloneWriteFailureCounterName)
                active.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, count, tags, _) => failures.Add((count, tags.ToArray())));
        listener.Start();
        var coordinator = new AuditExecutionCoordinator(catalog, new FakeClassifier(), capture, store, context,
            new HarnessClock(DateTimeOffset.UnixEpoch), new HarnessGuidFactory(), reconcileLogger, NSubstitute.Substitute.For<LearnStack.SharedKernel.Persistence.IUnitOfWork>());
        var refusal = Result.Fail<string>(new Error(LocalizedMessage.Of("lockey_forbidden")));
        var actual = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        JobApplicationAuxiliaryResources? retained = null;
        try
        {
            var result = await coordinator.ExecuteAsync(typeof(JobAuditReconciliationTests), null, () =>
            {
                // Synthetic retained loader; the native PostgreSQL companion owns
                // the real cache timeout and actual resource-disposal proof.
                retained = owner.BeginAuditConfigLoad();
                retained.HoldActualTask(actual.Task);
                capture.MarkRolledBack();
                return Task.FromResult(refusal);
            }, CancellationToken.None);

            result.Should().BeSameAs(refusal);
            capture.Intents.Should().BeEmpty();
            capture.HasOpenFrame.Should().BeFalse();
            capture.State.Should().Be(AuditIntentState.None);
            sourceAttempts.Should().Be(0, "the actual store refuses each acquisition before touching its lazy source");
            owner.CleanupConfirmed.Should().BeFalse();
            actual.Task.IsCompleted.Should().BeFalse("reconciliation neither joins nor replaces the held loader");
            var replacement = () => owner.BeginStandaloneAudit();
            replacement.Should().Throw<InvalidOperationException>();
            health.IsHealthy.Should().BeFalse();
            health.FailingOperation.Should().Be(operations[1]);
            auditLogger.Entries.Select(entry => entry.Operation).Should().Equal(operations);
            auditLogger.Entries.Should().OnlyContain(entry => entry.Level == LogLevel.Critical && entry.Exception == null);
            reconcileLogger.Entries.Select(entry => entry.Operation).Should().Equal(operations);
            reconcileLogger.Entries.Should().OnlyContain(entry => entry.Level == LogLevel.Warning && entry.Exception == null);
            foreach (var entry in auditLogger.Entries.Concat(reconcileLogger.Entries))
                entry.Message.Should().NotContain("refuses another acquisition").And.NotContain(nameof(InvalidOperationException));
            failures.Should().HaveCount(2);
            for (var index = 0; index < operations.Length; index++)
            {
                failures[index].Count.Should().Be(1);
                failures[index].Tags.Should().ContainSingle().Which
                    .Should().Be(new KeyValuePair<string, object?>("operation", operations[index]));
            }
        }
        finally
        {
            if (retained is not null) await retained.CleanupAsync();
            actual.TrySetResult();
            (await owner.JoinAuxiliaryCleanupAsync()).Should().BeTrue("the original held loader supplies its own cleanup witness");
            owner.CloseIssuance();
        }
    }

    private sealed class AuditMeterFactory : IMeterFactory
    {
        internal Meter Meter { get; } = new(PostgresAuditStore.MeterName);
        public Meter Create(MeterOptions options) => Meter;
        public void Dispose() => Meter.Dispose();
    }

    private sealed record AuditLog(LogLevel Level, string Operation, string Message, Exception? Exception);

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        internal List<AuditLog> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add(new(logLevel,
                (string)((IEnumerable<KeyValuePair<string, object?>>)state!).Single(property => property.Key == "Operation").Value!,
                formatter(state, exception), exception));
    }
}
