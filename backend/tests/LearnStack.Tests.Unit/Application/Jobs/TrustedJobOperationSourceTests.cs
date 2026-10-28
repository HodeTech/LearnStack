using System.Data;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Application.Jobs;

/// <summary>Synthetic retained claims exercise source admission; these are not database durability proofs.</summary>
public sealed class TrustedJobOperationSourceTests
{
    [Theory]
    [InlineData(typeof(ReadBackgroundJobEnvelopeQuery))]
    [InlineData(typeof(AdmitBackgroundJobAttemptCommand))]
    [InlineData(typeof(ReadBackgroundJobExternalStateQuery))]
    [InlineData(typeof(RecordBackgroundJobArmCommand))]
    [InlineData(typeof(RecordBackgroundJobOutcomeCommand))]
    public void Exact_framework_request_captures_its_existing_live_owner_without_readmission(Type requestType)
    {
        var external = requestType != typeof(ReadBackgroundJobEnvelopeQuery)
            && requestType != typeof(AdmitBackgroundJobAttemptCommand);
        var receipt = JobAdmissionFixtureReceipt.Create("tenancy.source-proof.v1", TenantId.From(Guid.CreateVersion7()),
            null, external ? BackgroundJobExecutionStage.Prepare : BackgroundJobExecutionStage.Transactional);
        using var binding = receipt.SourceOwner.Enter();
        using var source = new BackgroundJobOperationSource();
        var catalog = BackgroundJobDefinitionRegistry.CreateEmpty();
        var admission = new BackgroundJobExecutionAdmission(catalog, catalog, new AuditCatalog([new BackgroundJobAuditCatalogSource()]));
        ITenantContext context = requestType == typeof(ReadBackgroundJobEnvelopeQuery) ? UnresolvedTenantContext.Instance
            : JobTenantContext.FromValidatedJob(receipt.Envelope.TenantId, null, receipt.Envelope.JobId, null, null,
                null, receipt.Envelope.Traceparent, "tenancy");
        using var authority = external ? EnterExternal(admission, requestType, receipt)
            : admission.EnterFramework(receipt.Claim);
        if (requestType == typeof(AdmitBackgroundJobAttemptCommand))
            BackgroundJobExecutionAdmission.InstallValidatedEnvelope(receipt.Claim, receipt.Envelope);
        source.Capture(requestType, context).Should().BeNull("execution is not yet an admitted request");
        BackgroundJobExecutionAdmission.TryEnterRequest(requestType, context, out var request).Should().BeTrue();
        using (request)
        {
            var descriptor = source.Capture(requestType, context);
            descriptor.Should().NotBeNull();
            descriptor!.SourceKind.Should().Be(AuthorizationSourceKind.BackgroundJob);
            descriptor.Matches(requestType, context).Should().BeTrue();
            source.Capture(typeof(SubmitBackgroundJobRecipientCommand), context).Should().BeNull();
            BackgroundJobExecutionAdmission.PermitsRequest(requestType, context).Should().BeFalse(
                "policy capture preserves the already claimed request rather than repeating admission");
            receipt.Run.Close();
            if (external) descriptor.IsActive.Should().BeFalse("external checkpoint lifetime follows its actual run gate");
            request!.Dispose();
            descriptor.IsActive.Should().BeFalse();
        }
    }

    [Fact]
    public void Exact_recipient_request_captures_only_its_armed_stored_claim_and_closes_with_its_gate()
    {
        var owner = JobApplicationSourceOwner.Create();
        using var binding = owner.Enter();
        using var source = new BackgroundJobOperationSource();
        var claim = RecipientSource.Create();
        var context = JobTenantContext.ForRecipient(claim.TenantId, claim.OrganizationId,
            "00-11111111111111111111111111111111-1111111111111111-01", "tenancy");
        var unit = Substitute.For<IUnitOfWork>();
        var completion = new RecipientCompletion(unit, new StaticTenantContextAccessor(context));
        var gate = new BackgroundJobRunGate(new BackgroundJobRunGate.RuntimeStop())
            .OpenBoundary(BackgroundJobCheckpointKind.RecipientSubmission);
        gate.TryIssue().Should().BeTrue();
        var command = new SubmitBackgroundJobRecipientCommand(claim, gate, completion,
            new BackgroundJobLifecycleProjection("Trial", "Active", "Suspended", "Active"));
        completion.Arm(command);
        var catalog = new RecipientCatalog();
        var admission = new BackgroundJobExecutionAdmission(catalog, catalog, new AuditCatalog([new BackgroundJobAuditCatalogSource()]));
        using var authority = admission.EnterRecipient(command, unit);
        source.Capture(typeof(SubmitBackgroundJobRecipientCommand), context).Should().BeNull();
        BackgroundJobExecutionAdmission.TryEnterRequest(typeof(SubmitBackgroundJobRecipientCommand), context, out var request).Should().BeTrue();
        using (request)
        {
            var descriptor = source.Capture(typeof(SubmitBackgroundJobRecipientCommand), context);
            descriptor.Should().NotBeNull();
            descriptor!.IsActive.Should().BeTrue();
            source.Capture(typeof(ReadBackgroundJobEnvelopeQuery), context).Should().BeNull();
            gate.Close();
            descriptor.IsActive.Should().BeFalse();
        }
    }

    private static IDisposable EnterExternal(BackgroundJobExecutionAdmission admission, Type requestType, ConfirmedStartReceipt receipt)
    {
        var unit = Substitute.For<IUnitOfWork>();
        if (requestType == typeof(ReadBackgroundJobExternalStateQuery))
            return admission.EnterExternalCheckpoint(new ReadBackgroundJobExternalStateQuery(receipt), unit);
        var gate = receipt.Run.OpenBoundary(requestType == typeof(RecordBackgroundJobArmCommand)
            ? BackgroundJobCheckpointKind.Arm : BackgroundJobCheckpointKind.Outcome);
        gate.TryIssue().Should().BeTrue();
        return requestType == typeof(RecordBackgroundJobArmCommand)
            ? admission.EnterExternalCheckpoint(new RecordBackgroundJobArmCommand(receipt, gate, new string('a', 64), Guid.NewGuid()), unit)
            : admission.EnterExternalCheckpoint(new RecordBackgroundJobOutcomeCommand(receipt, gate, new string('a', 64), Guid.NewGuid(),
                "{}", BackgroundJobExternalState.Succeeded, null, Guid.NewGuid(), BackgroundJobQuiescenceKind.ProviderFenced), unit);
    }

    private sealed class RecipientCompletion(IUnitOfWork unit, ITenantContextAccessor accessor)
        : BackgroundJobRecipientCompletionParticipant(unit, accessor)
    {
        protected override Task FenceReceiptAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecipientCatalog : IBackgroundJobDefinitionCatalog, IBackgroundJobRequestCatalog
    {
        public IReadOnlyCollection<BackgroundJobRequestDescriptor> All => BackgroundJobFrameworkRequests.All;
        public bool TryGet(Type requestType, [NotNullWhen(true)] out BackgroundJobRequestDescriptor? descriptor)
        {
            descriptor = All.SingleOrDefault(item => item.RequestType == requestType);
            return descriptor is not null;
        }
        public bool TryGet(string key, [NotNullWhen(true)] out BackgroundJobDefinitionDescriptor? definition)
        {
            definition = key == "tenancy.source-proof.v1" ? new(new BackgroundJobDefinitionRegistration(key,
                typeof(LearnStack.Modules.Tenancy.Application.AssemblyMarker), typeof(BackgroundJobOccurrenceParameters),
                BackgroundJobScope.TenantWide, BackgroundJobDefinitionReadiness.Active, "probe",
                BackgroundJobProfileRegistration.Transactional(typeof(BackgroundJobExecutionAdmissionTests.Root)))) : null;
            return definition is not null;
        }
        public BackgroundJobDefinitionResolution Resolve(string? key) => TryGet(key ?? "", out var definition)
            ? new(BackgroundJobDefinitionDisposition.Active, definition) : new(BackgroundJobDefinitionDisposition.Unknown, null);
    }

    private sealed class RecipientSource : BackgroundJobRecipientClaim.MintingSource
    {
        internal static BackgroundJobRecipientClaim Create()
        {
            using var table = new DataTable();
            foreach (var type in new[] { typeof(Guid), typeof(Guid), typeof(Guid), typeof(Guid), typeof(string), typeof(long),
                         typeof(string), typeof(string), typeof(string), typeof(DateTimeOffset), typeof(DateTimeOffset),
                         typeof(Guid), typeof(DateTimeOffset), typeof(DateTimeOffset) })
                table.Columns.Add(new DataColumn { DataType = type });
            var now = DateTimeOffset.UtcNow;
            table.Rows.Add(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), DBNull.Value,
                "tenancy.source-proof", 1L, new string('a', 64), "tenancy.source-proof.v1", "serving_tenants",
                now, now, Guid.NewGuid(), now.AddMinutes(1), now);
            using var row = table.CreateDataReader();
            row.Read().Should().BeTrue();
            return MintRecipient(row, Stopwatch.GetTimestamp(), typeof(TrustedJobOperationSourceTests));
        }
    }
}
