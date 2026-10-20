using System.Collections.Concurrent;
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
using Xunit;

namespace LearnStack.Tests.Integration;

/// <summary>ADR-0065's production DI owner shares one pre-I/O admission budget across real request scopes.</summary>
[Collection(TenantAuthenticationDiagnosticsTestGroup.Name)]
public sealed class TenantAssertionAuditAdmissionTests
{
    private const int Requests = 601;
    private const int AllowedAttempts = 600;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private static readonly Guid DefendedTenant = Guid.Parse("01975ead-6242-7777-8666-000000000001");

    [Fact]
    public async Task Production_scoped_recorders_share_exactly_600_concurrent_audit_attempts_before_IO()
    {
        await using var production = new AdmissionFactory();
        await AssertSharedAdmission(production);

        // Only the dirty control changes this registration. The production fixture
        // above uses the singleton installed by Program without replacing it.
        await using var scoped = new AdmissionFactory(scopedBudget: true);
        Func<Task> perRequestBudget = () => AssertSharedAdmission(scoped);
        await perRequestBudget.Should().ThrowAsync<Xunit.Sdk.XunitException>(
            "the same multi-scope proof must detect a budget accidentally registered per request");
    }

    private static async Task AssertSharedAdmission(AdmissionFactory factory)
    {
        var services = factory.Services;
        var begin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decisions = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invoked = 0;
        var recorders = new ConcurrentBag<ITenantAssertionRecorder>();
        var calls = Enumerable.Range(0, Requests).Select(index => Task.Run(async () =>
        {
            await using var scope = services.CreateAsyncScope();
            var recorder = scope.ServiceProvider.GetRequiredService<ITenantAssertionRecorder>();
            recorders.Add(recorder);
            await begin.Task;
            var pending = recorder.RecordRejectionAsync(new TenantAssertionRejection(
                DefendedTenant,
                index % 2 == 0 ? TenantAssertionDimension.Tenant : TenantAssertionDimension.Organization,
                Guid.CreateVersion7(), true,
                index % 4 < 2 ? TenantAssertionSource.Header : TenantAssertionSource.JwtClaim,
                UserId.From(Guid.CreateVersion7()), "audit-admission-" + index, null));
            if (Interlocked.Increment(ref invoked) == Requests) decisions.TrySetResult();
            await pending;
        })).ToArray();
        begin.SetResult();
        try
        {
            await decisions.Task.WaitAsync(Timeout);
            recorders.Should().HaveCount(Requests).And.OnlyContain(recorder => recorder is AuditingTenantAssertionRecorder);
            recorders.Distinct(ReferenceEqualityComparer.Instance).Should().HaveCount(Requests,
                "this proof must exercise different real scoped recorder instances");
            factory.Store.Attempts.Should().Be(AllowedAttempts,
                "actors, sources, dimensions and request scopes share one fixed target-tenant allowance before store I/O finishes");
            var suppressed = await Task.WhenAny(calls).WaitAsync(Timeout);
            suppressed.IsCompletedSuccessfully.Should().BeTrue("the 601st decision completes without waiting for store I/O");
            factory.Store.Completed.Should().Be(0, "none of the admitted writes has been allowed to complete yet");
        }
        finally
        {
            factory.Store.Release();
            await Task.WhenAll(calls).WaitAsync(Timeout);
        }
        factory.Store.Completed.Should().Be(AllowedAttempts);
        factory.Store.Drafts.Should().HaveCount(AllowedAttempts).And.OnlyContain(draft =>
            draft.Operation == AuditingTenantAssertionRecorder.RejectOperation
            && draft.TenantId == TenantId.From(DefendedTenant) && draft.OrganizationId == null);
        factory.Store.Drafts.Select(draft => draft.ActorUserId).Distinct().Should().HaveCount(AllowedAttempts,
            "admission retains the actor of each individual permitted occurrence");
        factory.Store.Drafts.Select(draft => draft.CorrelationId).Distinct().Should().HaveCount(AllowedAttempts);
    }

    private sealed class AdmissionFactory(bool scopedBudget = false) : ConstructionOnlyApiFactory
    {
        internal GatedAuditStore Store { get; } = new();
        protected override void ConfigureTestHost(IWebHostBuilder builder) => builder.ConfigureTestServices(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IAuditStore>(Store));
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(new FrozenTimeProvider()));
            if (scopedBudget) services.Replace(ServiceDescriptor.Scoped<TenantAssertionAuditBudget, TenantAssertionAuditBudget>());
        });
    }

    private sealed class FrozenTimeProvider : TimeProvider
    {
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => 0;
    }

    private sealed class GatedAuditStore : IAuditStore
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _attempts;
        private int _completed;
        internal int Attempts => Volatile.Read(ref _attempts);
        internal int Completed => Volatile.Read(ref _completed);
        internal ConcurrentQueue<AuditEntryDraft> Drafts { get; } = new();
        internal void Release() => _release.TrySetResult();

        public async Task WriteStandaloneAsync(AuditEntryDraft entry, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _attempts);
            Drafts.Enqueue(entry);
            await _release.Task.WaitAsync(cancellationToken);
            Interlocked.Increment(ref _completed);
        }

        public Task WritePendingAsync(IUnitOfWork unitOfWork, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Assertion rejection owns no business transaction.");
        public Task WriteBestEffortAsync(AuditEntryDraft entry, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("An admitted authenticated rejection remains MUST-class.");
        public Task WritePlatformScopeAsync(AuditEntryDraft entry, DbConnection connection, DbTransaction transaction,
            CancellationToken cancellationToken = default) => throw new NotSupportedException("No platform-scope write is admitted.");
        public Task WriteEventTerminalAsync(AuditEntryDraft entry, EventTerminalTransition transition, DbConnection connection,
            DbTransaction transaction, CancellationToken cancellationToken = default) => throw new NotSupportedException("No event terminal write is admitted.");
        public Task WriteJobTerminalAsync(AuditEntryDraft entry, JobTerminalTransition transition, DbConnection connection,
            DbTransaction transaction, CancellationToken cancellationToken = default) => throw new NotSupportedException("No job terminal write is admitted.");
    }
}
