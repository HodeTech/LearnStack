using System.Diagnostics.Metrics;
using FluentAssertions;
using LearnStack.Api.Tenancy;
using LearnStack.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Tenancy;

public sealed partial class AuditingTenantAssertionRecorderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_601st_attempt_has_no_store_IO_and_exactly_one_attributed_suppression_warning(bool storeFails)
    {
        var store = new RecordingStore { Fails = storeFails };
        var signal = new CapturingLogger<LoggingTenantAssertionRecorder>();
        var recorder = Recorder(store, out _, signalLogger: signal,
            auditBudget: new TenantAssertionAuditBudget(new AuditBudgetTime()));
        for (var attempt = 0; attempt < 600; attempt++)
        {
            // Changing actors, dimensions and sources cannot multiply the tenant budget.
            await recorder.RecordRejectionAsync(Rejection(true,
                attempt % 2 == 0 ? TenantAssertionDimension.Tenant : TenantAssertionDimension.Organization,
                attempt % 3 == 0 ? TenantAssertionSource.Header : TenantAssertionSource.JwtClaim) with
            { ActorUserId = UserId.From(Guid.NewGuid()) });
        }
        store.Attempts.Should().Be(600);
        signal.EventIds.Should().HaveCount(600).And.OnlyContain(id => id.Id == 4001);
        signal.Entries[0].Properties.Keys.Should().BeEquivalentTo(
            ["Dimension", "ResolvedTenantId", "AssertedValue", "IsAuthenticated", "AssertionSource", "ActorUserId", "CorrelationId", "{OriginalFormat}"],
            "normal assertion warnings must not acquire credential, email or network properties");
        await recorder.RecordRejectionAsync(Rejection(true));
        store.Attempts.Should().Be(600, "a failed write still consumes its admission");
        store.Written.Should().HaveCount(storeFails ? 0 : 600);
        signal.EventIds.Should().HaveCount(601);
        signal.EventIds[^1].Should().Be(new EventId(4006, "AuditBudgetExhausted"));
        var suppressed = signal.Entries[^1];
        suppressed.Level.Should().Be(LogLevel.Warning);
        suppressed.Properties.Keys.Should().BeEquivalentTo(
            ["Dimension", "ResolvedTenantId", "AssertedValue", "Reason", "AssertionSource", "ActorUserId", "CorrelationId", "{OriginalFormat}"],
            "suppression changes the closed reason field without widening diagnostic attribution");
        suppressed.Properties.Should().Contain("Reason", "audit_budget_exhausted")
            .And.Contain("ResolvedTenantId", Resolved).And.Contain("ActorUserId", Actor.Value)
            .And.Contain("CorrelationId", Correlation);

        await recorder.RecordRejectionAsync(Rejection(true) with { ResolvedTenantId = Guid.NewGuid() });
        store.Attempts.Should().Be(601, "independent defended tenants have independent allowance");
        await recorder.RecordRejectionAsync(Rejection(false));
        store.Attempts.Should().Be(601, "authenticated attempts did not spend the anonymous threshold");
        signal.EventIds[^1].Id.Should().Be(4001);
    }

    [Fact]
    public async Task Suppressed_occurrences_have_a_separate_counter_with_only_closed_labels()
    {
        using var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var factory = provider.GetRequiredService<IMeterFactory>();
        var meter = factory.Create(LoggingTenantAssertionRecorder.MeterName);
        var values = new List<(string Name, long Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, observed) =>
        {
            if (ReferenceEquals(instrument.Meter, meter)) observed.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            values.Add((instrument.Name, value, tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value))));
        listener.Start();
        var recorder = Recorder(new RecordingStore(), out _, meterFactory: factory,
            auditBudget: new TenantAssertionAuditBudget(new AuditBudgetTime()));
        for (var attempt = 0; attempt < 601; attempt++) await recorder.RecordRejectionAsync(Rejection(true));
        var suppressed = values.Where(item => item.Name == LoggingTenantAssertionRecorder.SuppressedCounterName).ToArray();
        suppressed.Should().ContainSingle();
        suppressed[0].Value.Should().Be(1);
        suppressed[0].Tags.Should().BeEquivalentTo(new Dictionary<string, object?>
        {
            ["dimension"] = "Tenant",
            ["source"] = "header",
            ["reason"] = "audit_budget_exhausted",
        });
        var mismatches = values.Where(item => item.Name == LoggingTenantAssertionRecorder.MismatchCounterName).ToArray();
        mismatches.Sum(item => item.Value).Should().Be(601);
        mismatches.Should().OnlyContain(item => item.Tags.Count == 3
            && item.Tags.ContainsKey("dimension") && item.Tags.ContainsKey("source")
            && item.Tags.ContainsKey("authenticated"));
    }
}
