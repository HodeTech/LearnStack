using System.Data.Common;
using FluentAssertions;
using LearnStack.Api.Tenancy;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry.Metrics;
using Xunit;

namespace LearnStack.Tests.Integration;

public sealed partial class TenantAuthenticationDiagnosticsTests
{
    [Fact]
    public async Task Actual_budget_suppression_exports_the_real_counter_through_production_OTLP()
    {
        await using var collector = await TenantAuthenticationDiagnosticCollector.StartAsync();
        var defendedTenant = Guid.Parse("01975ead-6242-7777-8666-000000000065");
        var actor = UserId.From(Guid.Parse("01975ead-6242-7777-8666-000000000066"));
        const string correlation = "suppression-export-correlation-0065";
        await using (var factory = new SuppressionMetricsFactory(collector.Endpoint))
        {
            var services = factory.Services;
            var budget = services.GetRequiredService<TenantAssertionAuditBudget>();
            // Admission/concurrency has its own production multi-scope proof.
            // Spend the real budget directly so this export proof reaches one
            // real suppressed occurrence without 600 unrelated durable writes.
            for (var index = 0; index < 600; index++) budget.TryAcquire(defendedTenant).Should().BeTrue();
            await using var scope = services.CreateAsyncScope();
            var recorder = scope.ServiceProvider.GetRequiredService<ITenantAssertionRecorder>();
            recorder.Should().BeOfType<AuditingTenantAssertionRecorder>();
            await recorder.RecordRejectionAsync(new TenantAssertionRejection(
                defendedTenant, TenantAssertionDimension.Tenant,
                Guid.Parse("01975ead-6242-7777-8666-000000000067"), true,
                TenantAssertionSource.JwtClaim, actor, correlation, null));

            factory.Store.Calls.Should().Be(0, "suppression must not reach any audit-store operation");
            services.GetRequiredService<MeterProvider>().ForceFlush(5000).Should().BeTrue();
            // The Serilog sink batches independently of MeterProvider. Await
            // the actual correlated log while its host still owns that sink;
            // metrics flushing and factory disposal are not delivery receipts.
            await collector.WaitForLogAsync(correlation, TimeSpan.FromSeconds(8));
        }

        var points = collector.MetricPoints.Where(point =>
            point.ScopeName == LoggingTenantAssertionRecorder.MeterName
            && point.Name == LoggingTenantAssertionRecorder.SuppressedCounterName).ToArray();
        points.Should().NotBeEmpty("the actual LearnStack.Api meter must match the production subscription");
        points.Should().OnlyContain(point => point.Value == 1);
        foreach (var point in points)
            point.Attributes.Should().BeEquivalentTo(new Dictionary<string, object>
            {
                ["dimension"] = "Tenant",
                ["source"] = "jwt-claim",
                ["reason"] = "audit_budget_exhausted",
            });
        collector.Metrics.Should().NotContain(defendedTenant.ToString("D"))
            .And.NotContain(actor.Value.ToString("D")).And.NotContain(correlation);
        collector.Logs.Should().Contain("audit_budget_exhausted").And.Contain(correlation);
    }

    private sealed class SuppressionMetricsFactory(string endpoint) : ConstructionOnlyApiFactory
    {
        internal SuppressionMetricsStore Store { get; } = new();

        protected override void ConfigureTestHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Telemetry:OtlpEndpoint", endpoint);
            builder.ConfigureTestServices(services =>
                services.Replace(ServiceDescriptor.Singleton<IAuditStore>(Store)));
        }
    }

    private sealed class SuppressionMetricsStore : IAuditStore
    {
        internal int Calls { get; private set; }

        private Task Refuse()
        {
            Calls++;
            throw new InvalidOperationException("A suppressed occurrence must not call the audit store.");
        }

        public Task WritePendingAsync(IUnitOfWork unitOfWork, CancellationToken cancellationToken = default) => Refuse();
        public Task WriteStandaloneAsync(AuditEntryDraft entry, CancellationToken cancellationToken = default) => Refuse();
        public Task WriteBestEffortAsync(AuditEntryDraft entry, CancellationToken cancellationToken = default) => Refuse();
        public Task WritePlatformScopeAsync(AuditEntryDraft entry, DbConnection connection, DbTransaction transaction,
            CancellationToken cancellationToken = default) => Refuse();
        public Task WriteEventTerminalAsync(AuditEntryDraft entry, EventTerminalTransition transition, DbConnection connection,
            DbTransaction transaction, CancellationToken cancellationToken = default) => Refuse();
        public Task WriteJobTerminalAsync(AuditEntryDraft entry, JobTerminalTransition transition, DbConnection connection,
            DbTransaction transaction, CancellationToken cancellationToken = default) => Refuse();
    }
}
