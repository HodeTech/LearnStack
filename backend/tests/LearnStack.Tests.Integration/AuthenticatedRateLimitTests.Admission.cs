using System.Net;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using FluentAssertions;
using LearnStack.Api.Authentication;
using LearnStack.Api.Tenancy;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.Tools.Seeder;
using LearnStack.Tests.Integration.Database;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry.Metrics;
using Serilog.Core;
using Xunit;

namespace LearnStack.Tests.Integration;

public sealed partial class AuthenticatedRateLimitTests
{
    private static readonly TimeSpan AdmissionDeadline = TimeSpan.FromSeconds(15);
    private const string UnknownAdmissionPath = "/api/v1/admission-unknown";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Signed_admission_exports_quota_capacity_and_sticky_fault_without_resolution_or_identity_labels(bool timerFault)
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var collector = await TenantAuthenticationDiagnosticCollector.StartAsync();
        using var scenario = new AdmissionScenario();
        AssertKnownFailureTreeControls(scenario.Failure);
        AssertNoStoreHeaderControls();
        var observed = new SubjectQuotaObservation();
        var memberships = new AdmissionMembershipObservation();
        HttpClient? client = AdmissionClient(issuer, scenario, observed, memberships, collector.Endpoint);
        try
        {
            var audits = await fixture.AuditCountAsync(SeedData.English);
            var reads = fixture.Observation.Reads;
            // Both actual durable writes are positive controls. A moved subject
            // gate would reach this same recorder on the later rejected paths.
            using (var membershipControl = await AdmissionSend(client, issuer, "admission-subject-A", organization: true))
                await TenantAuthenticationHttpTests.Problem(membershipControl, HttpStatusCode.NotFound, "not_found");
            memberships.Calls.Should().Be(1, "the admitted signed organization request must exercise the resolution observer");
            using (var mismatch = await AdmissionSend(client, issuer, "admission-subject-A", mismatch: true))
                await TenantAuthenticationHttpTests.Problem(mismatch, HttpStatusCode.NotFound, "tenant_mismatch");
            using (var healthy = await AdmissionSend(client, issuer, "admission-subject-B"))
                healthy.StatusCode.Should().Be(HttpStatusCode.OK);
            observed.Rejections.Should().HaveCount(2);
            (await fixture.AuditCountAsync(SeedData.English)).Should().Be(audits + 2);
            var meter = observed.Services.GetRequiredService<MeterProvider>();
            meter.ForceFlush(5000).Should().BeTrue();
            AdmissionPoints(collector).Should().BeEmpty("admitted signed requests emit no rejection or cleanup-fault point");

            // Claimless admission would emit the fixed resolver Debug event if
            // the refused request crossed the subject gate.
            var claimlessPayload = JsonNode.Parse(issuer.Payload(("sub", "admission-subject-A")))!.AsObject();
            claimlessPayload.Remove("tenant_id");
            claimlessPayload.Remove("organization_id");
            var claimlessA = issuer.Token(claimlessPayload.ToJsonString());
            using (var quota = await TenantAuthenticationHttpRequest.Send(client, SeedData.English.Host,
                       TenantAuthenticationHttpRequest.PublicPath, claimlessA))
                await AdmissionProblem(quota, HttpStatusCode.TooManyRequests, "rate_limited", 60);
            using (var unknownQuota = await TenantAuthenticationHttpRequest.Send(client, SeedData.English.Host,
                       UnknownAdmissionPath, claimlessA))
                await AdmissionProblem(unknownQuota, HttpStatusCode.TooManyRequests, "rate_limited", 60);
            using (var capacity = await AdmissionSend(client, issuer, "admission-subject-C", organization: true))
                await AdmissionProblem(capacity, HttpStatusCode.TooManyRequests, "rate_limited", 60);
            var capacityToken = AccountSiteHttpTests.Token(issuer, SeedData.English,
                organization: true, subject: "admission-subject-C");
            using (var unknownCapacity = await TenantAuthenticationHttpRequest.Send(client, SeedData.English.Host,
                       UnknownAdmissionPath, capacityToken))
                await AdmissionProblem(unknownCapacity, HttpStatusCode.TooManyRequests, "rate_limited", 60);

            // The injected partition delegates real quota/retry metadata to the
            // stock limiter. Only its idle/disposal boundary is controlled so
            // both fault origins can be observed without waiting a minute.
            scenario.Budgets[0].ForceRetirementFailure();
            if (timerFault)
            {
                scenario.Clock.Timer.Fire();
                meter.ForceFlush(5000).Should().BeTrue();
                var transition = AdmissionPoints(collector).Where(point =>
                    point.Name == TenantSubjectAdmissionMetrics.CleanupFailedCounterName).ToArray();
                transition.Should().NotBeEmpty("timer failure signals before another request is made");
                transition.Should().OnlyContain(point => point.Value == 1 && point.Attributes.Count == 0);
                AdmissionPoints(collector).Where(point => point.Name == TenantSubjectAdmissionMetrics.RejectedCounterName)
                    .Should().NotContain(point => point.Attributes.Values.Contains("registry_fault"));
            }
            using (var fault = await AdmissionSend(client, issuer, "admission-subject-C", organization: true))
                await AdmissionProblem(fault, HttpStatusCode.ServiceUnavailable, "dependency_unavailable", 30);
            using (var unknownFault = await TenantAuthenticationHttpRequest.Send(client, SeedData.English.Host,
                       UnknownAdmissionPath, capacityToken))
                await AdmissionProblem(unknownFault, HttpStatusCode.ServiceUnavailable, "dependency_unavailable", 30);

            var beforeReplay = scenario.Budgets.Sum(budget => budget.Attempts);
            var replay = new DefaultHttpContext { RequestServices = observed.Services };
            replay.Response.Body = new MemoryStream();
            replay.Features.Set(HostClassification.Platform("admission-replay-canary.example"));
            replay.Features.Set(new TenantCredentialFeature
            {
                Identity = new ValidatedTenantIdentity(
                issuer.Issuer, "replay-subject-canary", UserId.From(TenantAuthenticationIssuer.Actor), null, null)
            });
            var middleware = new TenantSubjectAdmissionMiddleware(
                _ => throw new InvalidOperationException("A fault replay cannot dispatch."), scenario.Owner);
            await middleware.InvokeAsync(replay);
            await middleware.InvokeAsync(replay);
            replay.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
            replay.Response.Headers.RetryAfter.ToString().Should().Be("30");
            scenario.Budgets.Sum(budget => budget.Attempts).Should().Be(beforeReplay,
                "the cached refused outcome cannot reacquire or debit on re-execution");

            using (var retained = await AdmissionSend(client, issuer, "admission-subject-B"))
            {
                retained.StatusCode.Should().Be(HttpStatusCode.OK, "sticky failure stops new partitions, not a retained healthy key");
                TenantAuthenticationHttpTests.NoStore(retained);
            }
            scenario.Clock.Timer.Fire();
            scenario.Budgets.Should().HaveCount(2, "neither fault refusal nor replay allocates a new partition");
            scenario.Budgets[0].Disposals.Should().Be(1, "the failed retirement is never retried");
            memberships.Calls.Should().Be(1, "only the positive control reaches resolution membership; refused signed selectors must not");
            observed.ClaimlessEvents.Should().Be(0);
            observed.Rejections.Should().HaveCount(2);
            fixture.Observation.Reads.Should().Be(reads + 2);
            (await fixture.AuditCountAsync(SeedData.English)).Should().Be(audits + 2);
            meter.ForceFlush(5000).Should().BeTrue();
            AssertAdmissionMetrics(collector, quota: 2, capacity: 2, fault: 3);
            foreach (var canary in new[] { issuer.Issuer, "admission-subject-A", "admission-subject-B",
                         "admission-subject-C", "replay-subject-canary", "admission-replay-canary.example",
                         TenantAuthenticationIssuer.Actor.ToString("D"), SeedData.English.TenantId.Value.ToString("D") })
                collector.Metrics.Should().NotContain(canary);
            await AssertFaultedShutdown(scenario);
            DisposeFaultedClient(client, scenario);
            client = null;
        }
        finally { DisposeFaultedClient(client, scenario); }
    }

    [Fact]
    public async Task Blocked_acquisition_cleanup_holds_its_signed_request_until_fault_without_blocking_other_subjects()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        using var scenario = new AdmissionScenario();
        AssertKnownFailureTreeControls(scenario.Failure);
        AssertNoStoreHeaderControls();
        var observed = new SubjectQuotaObservation();
        var memberships = new AdmissionMembershipObservation();
        HttpClient? client = AdmissionClient(issuer, scenario, observed, memberships);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<HttpResponseMessage>? pending = null;
        try
        {
            using (var first = await AdmissionSend(client, issuer, "blocked-retiring-subject"))
                first.StatusCode.Should().Be(HttpStatusCode.OK);
            using (var existing = await AdmissionSend(client, issuer, "blocked-retained-subject"))
                existing.StatusCode.Should().Be(HttpStatusCode.OK);
            var reads = fixture.Observation.Reads;
            var audits = await fixture.AuditCountAsync(SeedData.English);
            scenario.Budgets[0].ForceRetirementFailure(() =>
            {
                entered.TrySetResult();
                release.Wait();
            });
            pending = Task.Run(() => AdmissionSend(client, issuer, "blocked-owning-subject", organization: true));
            await entered.Task.WaitAsync(AdmissionDeadline);
            pending.IsCompleted.Should().BeFalse("the owning request must join actual disposal");
            scenario.Budgets.Should().HaveCount(2, "the cleanup owner has not allocated or debited its new key");

            using (var existing = await AdmissionSend(client, issuer, "blocked-retained-subject").WaitAsync(AdmissionDeadline))
                existing.StatusCode.Should().Be(HttpStatusCode.OK);
            using (var competitor = await AdmissionSend(client, issuer, "blocked-competing-subject").WaitAsync(AdmissionDeadline))
                competitor.StatusCode.Should().Be(HttpStatusCode.OK, "a different request can take the freed retained slot");
            using (var full = await AdmissionSend(client, issuer, "blocked-capacity-subject", organization: true).WaitAsync(AdmissionDeadline))
                await AdmissionProblem(full, HttpStatusCode.TooManyRequests, "rate_limited", 60);
            scenario.Budgets.Should().HaveCount(3);
            scenario.Budgets.Sum(budget => budget.IdleReads).Should().Be(1, "no second batch starts while acquisition owns disposal");
            pending.IsCompleted.Should().BeFalse();
            release.Set();
            using (var fault = await pending.WaitAsync(AdmissionDeadline))
                await AdmissionProblem(fault, HttpStatusCode.ServiceUnavailable, "dependency_unavailable", 30);
            pending = null;
            memberships.Calls.Should().Be(0);
            observed.Rejections.Should().BeEmpty();
            fixture.Observation.Reads.Should().Be(reads + 2);
            (await fixture.AuditCountAsync(SeedData.English)).Should().Be(audits);
            scenario.Budgets.Should().HaveCount(3, "the failed owner cannot allocate after its disposal returns");
            await AssertFaultedShutdown(scenario);
            DisposeFaultedClient(client, scenario);
            client = null;
        }
        finally
        {
            release.Set();
            try
            {
                if (pending is not null) (await pending.WaitAsync(AdmissionDeadline)).Dispose();
            }
            finally { DisposeFaultedClient(client, scenario); }
        }
    }

    private HttpClient AdmissionClient(TenantAuthenticationIssuer issuer, AdmissionScenario scenario,
        SubjectQuotaObservation observed, AdmissionMembershipObservation memberships, string? endpoint = null) =>
        fixture.ClientFor(SeedData.English.Host, services =>
        {
            services.RemoveAll<TenantSubjectRateLimiter>();
            services.AddSingleton(_ => scenario.Owner);
            services.AddScoped<AuditingTenantAssertionRecorder>();
            services.AddScoped<ITenantAssertionRecorder>(provider => new ObservedQuotaRecorder(
                provider.GetRequiredService<AuditingTenantAssertionRecorder>(), observed));
            services.AddSingleton<ITenantMembershipReader>(memberships);
            services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter>(observed);
            services.AddSingleton<ILogEventSink>(observed);
        }, builder =>
        {
            builder.UseSetting(TenantAuthenticationOptions.IssuerConfigurationKey, issuer.Issuer);
            builder.UseSetting("Serilog:MinimumLevel:Default", "Debug");
            if (endpoint is not null) builder.UseSetting("Telemetry:OtlpEndpoint", endpoint);
        });

    private static Task<HttpResponseMessage> AdmissionSend(HttpClient client, TenantAuthenticationIssuer issuer,
        string subject, bool mismatch = false, bool organization = false) =>
        TenantAuthenticationHttpRequest.Send(client, SeedData.English.Host, token: AccountSiteHttpTests.Token(
            issuer, SeedData.English, organization: organization, subject: subject,
            claimedTenant: mismatch ? SeedData.Yoga.TenantId.Value : null));

    private static void AssertNoStoreHeaderControls()
    {
        using var clean = new HttpResponseMessage();
        clean.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoStore = true };
        TenantAuthenticationHttpTests.NoStore(clean);
        using var missing = new HttpResponseMessage();
        Record.Exception(() => TenantAuthenticationHttpTests.NoStore(missing)).Should().NotBeNull(
            "a missing Cache-Control header must fail rather than short-circuit its assertion");
        clean.Headers.CacheControl.NoStore = false;
        Record.Exception(() => TenantAuthenticationHttpTests.NoStore(clean)).Should().NotBeNull(
            "presence alone does not satisfy the no-store policy");
    }

    private static async Task AdmissionProblem(HttpResponseMessage response, HttpStatusCode status, string code, int retry)
    {
        await TenantAuthenticationHttpTests.Problem(response, status, code);
        (response.Headers.RetryAfter?.Delta).Should().Be(TimeSpan.FromSeconds(retry));
        response.Headers.WwwAuthenticate.Should().BeEmpty();
    }

    private static DiagnosticMetricPoint[] AdmissionPoints(TenantAuthenticationDiagnosticCollector collector) =>
        collector.MetricPoints.Where(point => point.ScopeName == TenantSubjectAdmissionMetrics.MeterName
            && point.Name is TenantSubjectAdmissionMetrics.RejectedCounterName or TenantSubjectAdmissionMetrics.CleanupFailedCounterName).ToArray();

    private static void AssertAdmissionMetrics(TenantAuthenticationDiagnosticCollector collector, int quota, int capacity, int fault)
    {
        var points = AdmissionPoints(collector);
        var rejected = points.Where(point => point.Name == TenantSubjectAdmissionMetrics.RejectedCounterName).ToArray();
        rejected.Should().NotBeEmpty();
        rejected.Select(point => (string)point.Attributes["reason"]).Distinct()
            .Should().BeEquivalentTo(["quota", "registry_capacity", "registry_fault"]);
        foreach (var point in rejected)
            point.Attributes.Keys.Should().Equal("reason");
        foreach (var (reason, value) in new[] { ("quota", quota), ("registry_capacity", capacity), ("registry_fault", fault) })
            rejected.Where(point => (string)point.Attributes["reason"] == reason).Max(point => point.Value).Should().Be(value);
        var transitions = points.Where(point => point.Name == TenantSubjectAdmissionMetrics.CleanupFailedCounterName).ToArray();
        transitions.Should().NotBeEmpty();
        transitions.Should().OnlyContain(point => point.Value == 1 && point.Attributes.Count == 0);
        points.Should().OnlyContain(point => point.Exemplars.Length == 0);
    }

    private static async Task AssertFaultedShutdown(AdmissionScenario scenario)
    {
        Func<Task> shutdown = () => scenario.Owner.DisposeAsync().AsTask();
        var exception = (await shutdown.Should().ThrowAsync<AggregateException>()).Which;
        exception.Flatten().InnerExceptions.Should().ContainSingle().Which.Should().BeSameAs(scenario.Failure,
            "joined owner shutdown must expose exactly the injected failure, with no additional cleanup fault");
        scenario.Budgets.Should().OnlyContain(budget => budget.Disposals == 1,
            "shutdown drains the retained partitions without retrying the failed retirement");
    }

    private static void DisposeFaultedClient(HttpClient? client, AdmissionScenario scenario)
    {
        if (client is null) return;
        try { client.Dispose(); }
        catch (Exception error) when (IsKnownFailureTree(error, scenario.Failure))
        {
            // Preserve a primary assertion failure while joining the known
            // faulted owner. Successful paths assert the failure above.
        }
    }

    private static bool IsKnownFailureTree(Exception error, Exception expected) => ReferenceEquals(error, expected)
        || error is AggregateException { InnerExceptions.Count: > 0 } aggregate
            && aggregate.InnerExceptions.All(nested => IsKnownFailureTree(nested, expected));

    private static void AssertKnownFailureTreeControls(Exception expected)
    {
        IsKnownFailureTree(expected, expected).Should().BeTrue();
        IsKnownFailureTree(new AggregateException(new AggregateException(expected)), expected).Should().BeTrue();
        IsKnownFailureTree(new AggregateException(expected, new InvalidOperationException("unexpected-cleanup-control")), expected)
            .Should().BeFalse("an additional cleanup failure must never be hidden behind the expected one");
        IsKnownFailureTree(new InvalidOperationException("unexpected-wrapper-control", expected), expected)
            .Should().BeFalse("an arbitrary wrapper is not a known cleanup outcome");
        IsKnownFailureTree(new AggregateException(), expected).Should().BeFalse();
    }

    private sealed class AdmissionMembershipObservation : ITenantMembershipReader
    {
        private readonly DenyAllTenantMembershipReader _inner = new();
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        public Task<bool> CoversAsync(UserId userId, TenantId tenantId, OrganizationId? organizationId,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return _inner.CoversAsync(userId, tenantId, organizationId, cancellationToken);
        }
    }

    private sealed class AdmissionScenario : IDisposable
    {
        internal AdmissionClock Clock { get; } = new();
        internal List<AdmissionBudget> Budgets { get; } = [];
        internal Exception Failure { get; } = new InvalidOperationException("synthetic-admission-cleanup-fault");
        internal TenantSubjectRateLimiter Owner { get; }
        internal AdmissionScenario() => Owner = new TenantSubjectRateLimiter(() =>
        {
            var budget = new AdmissionBudget(Failure);
            Budgets.Add(budget);
            return budget;
        }, TenantSubjectRateLimiter.Window, Clock, maximumPartitions: 2);
        public void Dispose()
        {
            try
            {
                try { Owner.Dispose(); }
                catch (Exception error) when (IsKnownFailureTree(error, Failure)) { }
            }
            finally
            {
                // An injected throwing Dispose is deliberately not a production
                // cleanup witness. The test creator separately owns its stock child.
                foreach (var budget in Budgets) budget.ReleaseTestBackingResource();
            }
        }
    }

    private sealed class AdmissionBudget(Exception failure) : RateLimiter
    {
        private readonly FixedWindowRateLimiter _inner = TenantSubjectRateLimiter.CreateBudget(2, autoReplenishment: false);
        private Action? _beforeFailure;
        private int _forcedRetirement;
        internal int Attempts { get; private set; }
        internal int IdleReads { get; private set; }
        internal int Disposals { get; private set; }
        internal void ForceRetirementFailure(Action? beforeFailure = null)
        {
            _beforeFailure = beforeFailure;
            Volatile.Write(ref _forcedRetirement, 1);
        }
        public override TimeSpan? IdleDuration
        {
            get
            {
                IdleReads++;
                // Only the named fault injection overrides actual stock eligibility.
                // The production stock/full-quota rule is proved separately by unit controls.
                return Volatile.Read(ref _forcedRetirement) == 0
                    ? _inner.IdleDuration : TenantSubjectRateLimiter.Window;
            }
        }
        public override RateLimiterStatistics? GetStatistics() => _inner.GetStatistics();
        protected override RateLimitLease AttemptAcquireCore(int permitCount)
        {
            Attempts++;
            return _inner.AttemptAcquire(permitCount);
        }
        protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Subject admission never queues.");
        protected override async ValueTask DisposeAsyncCore()
        {
            try { Dispose(true); }
            finally { await base.DisposeAsyncCore().ConfigureAwait(false); }
        }
        protected override void Dispose(bool disposing)
        {
            try
            {
                if (!disposing) return;
                Disposals++;
                if (Volatile.Read(ref _forcedRetirement) != 0)
                {
                    _beforeFailure?.Invoke();
                    throw failure;
                }
                _inner.Dispose();
            }
            finally { base.Dispose(disposing); }
        }
        internal void ReleaseTestBackingResource() => _inner.Dispose();
    }

    private sealed class AdmissionClock : TimeProvider
    {
        internal AdmissionTimer Timer { get; private set; } = null!;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            Timer = new AdmissionTimer(callback, state);
    }

    private sealed class AdmissionTimer(TimerCallback callback, object? state) : ITimer
    {
        private bool _disposed;
        internal void Fire() { if (!_disposed) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;
        public void Dispose() => _disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
