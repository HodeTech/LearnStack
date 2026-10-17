using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Application.Messaging;
using LearnStack.Application.Persistence;
using LearnStack.Application.Pipeline;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Application.Jobs;

public sealed class BackgroundJobFrameworkAdmissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Recipient_attribution_has_no_invented_child_or_ancestry_and_grants_no_framework_authority(bool organization)
    {
        var tenant = TenantId.From(Guid.NewGuid());
        var org = organization ? OrganizationId.From(Guid.NewGuid()) : (OrganizationId?)null;
        var context = JobTenantContext.ForRecipient(tenant, org, BackgroundJobRuntimeFixtureTrace, "tenancy");
        context.TenantId.Should().Be(tenant);
        context.OrganizationId.Should().Be(org);
        context.UserId.Should().Be(UserId.SystemActor);
        context.Origin.Should().Be(TenantContextOrigin.Ambient);
        ((ITenantContext)context).HostScope.Should().BeNull();
        context.JobId.Should().BeNull();
        context.ParentJobId.Should().BeNull();
        context.IncomingEventId.Should().BeNull();
        context.CausalActorUserId.Should().BeNull();
        BackgroundJobExecutionAdmission.PermitsRequest(typeof(SubmitBackgroundJobRecipientCommand), context).Should().BeFalse();
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("sentinel")]
    [InlineData("organization")]
    [InlineData("trace")]
    [InlineData("module")]
    public void Recipient_attribution_refuses_invalid_routes_and_diagnostics(string invalid)
    {
        var tenant = invalid == "tenant" ? TenantId.From(Guid.Empty)
            : invalid == "sentinel" ? TenantId.PlatformSentinel : TenantId.From(Guid.NewGuid());
        var org = invalid == "organization" ? OrganizationId.From(Guid.Empty) : (OrganizationId?)null;
        var create = () => JobTenantContext.ForRecipient(tenant, org,
            invalid == "trace" ? "untrusted-provider-text" : BackgroundJobRuntimeFixtureTrace,
            invalid == "module" ? "Tenancy.Provider" : "tenancy");
        create.Should().Throw<ArgumentException>();
    }

    private const string BackgroundJobRuntimeFixtureTrace = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";

    [Fact]
    public async Task Exact_framework_query_is_refused_without_a_claim_before_its_handler()
    {
        var receipt = Receipt();
        var constructed = false;
        var behavior = new TenantContextBehavior<ReadBackgroundJobEnvelopeQuery, Result<BackgroundJobEnvelope>>(
            UnresolvedTenantContext.Instance);
        var result = await behavior.Handle(new(receipt.Claim), () =>
        {
            constructed = true;
            return Task.FromResult(Result<BackgroundJobEnvelope>.Ok(receipt.Envelope));
        }, CancellationToken.None);
        result.Error!.Code.Should().Be("forbidden");
        constructed.Should().BeFalse();
        BackgroundJobExecutionAdmission.PermitsRequest(typeof(AdmitBackgroundJobAttemptCommand),
            new Context(receipt.Claim.TenantId)).Should().BeFalse();
    }

    [Fact]
    public void Preread_scope_refuses_business_nested_requests_and_accessor_replacement()
    {
        var receipt = Receipt();
        using var scope = Admission().EnterFramework(receipt.Claim);
        BackgroundJobExecutionAdmission.PermitsRequest(typeof(ReadBackgroundJobEnvelopeQuery),
            UnresolvedTenantContext.Instance).Should().BeTrue();
        BackgroundJobExecutionAdmission.PermitsRequest(typeof(BackgroundJobExecutionAdmissionTests.Root),
            UnresolvedTenantContext.Instance).Should().BeFalse();
        BackgroundJobExecutionAdmission.PermitsRequest(typeof(AdmitBackgroundJobAttemptCommand),
            new Context(receipt.Claim.TenantId)).Should().BeFalse();
        BackgroundJobExecutionAdmission.PermitsRequest(typeof(ReadBackgroundJobEnvelopeQuery),
            new Context(receipt.Claim.TenantId)).Should().BeFalse();
        BackgroundJobExecutionAdmission.TryEnterRequest(typeof(ReadBackgroundJobEnvelopeQuery),
            UnresolvedTenantContext.Instance, out var request).Should().BeTrue();
        using (request)
        {
            BackgroundJobExecutionAdmission.PermitsRequest(typeof(ReadBackgroundJobEnvelopeQuery),
                UnresolvedTenantContext.Instance).Should().BeFalse("framework reads cannot nest even themselves");
            var replaced = () => BackgroundJobExecutionAdmission.RequireContext(new Context(receipt.Claim.TenantId));
            replaced.Should().Throw<InvalidOperationException>();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Framework_request_cannot_lend_authority_to_a_legacy_direct_owner(bool start)
    {
        var receipt = Receipt();
        var unit = Substitute.For<IUnitOfWork>();
        var producer = new ProducerExecution(unit);
        using var scope = Admission().EnterFramework(receipt.Claim);
        ITenantContext context = UnresolvedTenantContext.Instance;
        var requestType = typeof(ReadBackgroundJobEnvelopeQuery);
        if (start)
        {
            BackgroundJobExecutionAdmission.InstallValidatedEnvelope(receipt.Claim, receipt.Envelope);
            context = new Context(receipt.Claim.TenantId);
            requestType = typeof(AdmitBackgroundJobAttemptCommand);
        }
        BackgroundJobExecutionAdmission.TryEnterRequest(requestType, context, out var request).Should().BeTrue();
        using (request)
        {
            var coordinator = new TransactionExecutionCoordinator(unit, context, Substitute.For<IAuditStore>(),
                Substitute.For<IAuditStateCapture>(), producer, new DomainEventCollector(producer, Substitute.For<IPublisher>()),
                NullLogger<TransactionExecutionCoordinator>.Instance);
            var constructed = false;
            var run = () => coordinator.ExecuteAsync(null, TransactionMode.ReadWrite, "untyped-probe", () =>
            {
                constructed = true;
                return Task.FromResult(Result<None>.Ok(None.Value));
            });
            await run.Should().ThrowAsync<InvalidOperationException>();
            constructed.Should().BeFalse();
            await unit.DidNotReceiveWithAnyArgs().BeginTransactionAsync(default(TransactionMode), default);
            var swapped = () => BackgroundJobExecutionAdmission.RequireContext(new Context(TenantId.From(Guid.CreateVersion7())));
            swapped.Should().Throw<InvalidOperationException>();
        }
    }

    [Fact]
    public async Task Closed_claim_scope_revokes_retained_tasks_and_cannot_be_reopened()
    {
        var admission = Admission();
        var receipt = Receipt();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> retained;
        using (admission.EnterFramework(receipt.Claim))
        {
            retained = Task.Run(async () =>
            {
                await release.Task;
                var privileged = () => BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
                privileged.Should().Throw<InvalidOperationException>();
                return BackgroundJobExecutionAdmission.PermitsRequest(typeof(ReadBackgroundJobEnvelopeQuery),
                    UnresolvedTenantContext.Instance);
            });
        }
        release.SetResult();
        (await retained).Should().BeFalse();
        var reopen = () => admission.EnterFramework(receipt.Claim);
        reopen.Should().Throw<InvalidOperationException>();
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
    }

    [Fact]
    public void An_ordinary_start_receipt_cannot_activate_twice()
    {
        var fixture = new BackgroundJobExecutionAdmissionTests.Fixture();
        var receipt = JobAdmissionFixtureReceipt.Create(BackgroundJobExecutionAdmissionTests.Fixture.Key,
            fixture.Context.TenantId, null, BackgroundJobExecutionStage.Transactional);
        fixture.EnterReceipt(receipt).Dispose();
        var reuse = () => fixture.EnterReceipt(receipt);
        reuse.Should().Throw<InvalidOperationException>();
    }

    private static BackgroundJobExecutionAdmission Admission()
    {
        var catalog = BackgroundJobDefinitionRegistry.CreateEmpty();
        return new(catalog, catalog, new AuditCatalog([new BackgroundJobAuditCatalogSource()]));
    }

    private static ConfirmedStartReceipt Receipt() => JobAdmissionFixtureReceipt.Create("tenancy.probe.v1",
        TenantId.From(Guid.CreateVersion7()), null, BackgroundJobExecutionStage.Transactional);

    private sealed record Context(TenantId TenantId) : ITenantContext
    {
        public bool IsResolved => true;
        public OrganizationId? OrganizationId => null;
        public UserId? UserId => LearnStack.SharedKernel.Identifiers.UserId.SystemActor;
        public TenantContextOrigin? Origin => TenantContextOrigin.Ambient;
        public string? CorrelationId => null;
        public string? ModuleName => null;
    }
}
