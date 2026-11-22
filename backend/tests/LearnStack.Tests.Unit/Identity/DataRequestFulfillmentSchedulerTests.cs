using FluentAssertions;
using LearnStack.Modules.Identity.Application.Contracts.DataRequests;
using LearnStack.Modules.Identity.Application.DataRequests;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Tenancy;
using Xunit;

namespace LearnStack.Tests.Unit.Identity;

/// <summary>
/// ADR-0107 § 2, § 7: the fulfilment job of a request's kind and group (an export's at once, an erasure's at the end of
/// its window, a closure's none), and the purge at the artifact's expiry.
/// </summary>
public sealed class DataRequestFulfillmentSchedulerTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = TenantId.From(Guid.CreateVersion7());
    private static readonly OrganizationId Organization = OrganizationId.From(Guid.CreateVersion7());

    [Theory]
    [InlineData("tenant", "identity.data_export_tenant.v1")]
    [InlineData("organization", "identity.data_export_organization.v1")]
    public async Task A_requested_export_submits_the_export_definition_of_its_group(string scope, string key)
    {
        var organization = scope == "tenant" ? (OrganizationId?)null : Organization;
        var jobs = new RecordingScheduler();
        var request = Request(DataRequestKind.Export, organization);

        await new DataRequestFulfillmentScheduler(jobs, new Context(organization)).SubmitAsync(Fulfillment(request), CancellationToken.None);

        jobs.Submitted.Should().ContainSingle().Which.Should().Be((key, (object)new DataExportJobParameters(request.Id.Value), (DateTimeOffset?)null));
    }

    [Theory]
    [InlineData("tenant", "identity.data_erasure_tenant.v1")]
    [InlineData("organization", "identity.data_erasure_organization.v1")]
    public async Task A_scheduled_erasure_schedules_the_erasure_definition_of_its_group_at_the_end_of_its_window(string scope, string key)
    {
        var organization = scope == "tenant" ? (OrganizationId?)null : Organization;
        var jobs = new RecordingScheduler();
        var request = Request(DataRequestKind.Erasure, organization);

        await new DataRequestFulfillmentScheduler(jobs, new Context(organization)).SubmitAsync(Fulfillment(request), CancellationToken.None);

        jobs.Submitted.Should().ContainSingle().Which.Should().Be((key, (object)new DataErasureJobParameters(request.Id.Value),
            (DateTimeOffset?)(At + DataRequestLimits.CancellationWindow)), "the job is due when the cancellable window ends, never before");
    }

    /// <summary>
    /// ADR-0107 § 7: a closure has no job. Its execution is the operator's Begin, an act in the platform scope no job holds
    /// the authority for, so a scheduled closure is an accepted fulfilment that submits nothing.
    /// </summary>
    [Theory]
    [InlineData("tenant")]
    [InlineData("organization")]
    public async Task A_scheduled_closure_is_accepted_and_submits_no_job(string scope)
    {
        var organization = scope == "tenant" ? (OrganizationId?)null : Organization;
        var jobs = new RecordingScheduler();
        var request = Request(DataRequestKind.Closure, organization);
        request.ExecuteAt.Should().Be(At + DataRequestLimits.CancellationWindow);

        await new DataRequestFulfillmentScheduler(jobs, new Context(organization)).SubmitAsync(Fulfillment(request), CancellationToken.None);

        jobs.Submitted.Should().BeEmpty("a closure is executed by the operator's Begin, never by a job");
    }

    [Theory]
    [InlineData("other_group")]
    [InlineData("fulfilling")]
    [InlineData("erasure_without_due_time")]
    [InlineData("export_with_due_time")]
    [InlineData("closure_without_due_time")]
    [InlineData("closure_fulfilling")]
    [InlineData("closure_of_another_group")]
    public async Task Anything_but_a_requested_export_or_scheduled_erasure_or_closure_of_the_announced_group_is_a_programming_error(string variant)
    {
        var jobs = new RecordingScheduler();
        var request = variant switch
        {
            "erasure_without_due_time" => Request(DataRequestKind.Erasure, null),
            "closure_without_due_time" or "closure_fulfilling" or "closure_of_another_group" => Request(DataRequestKind.Closure, null),
            _ => Request(DataRequestKind.Export, null),
        };
        if (variant == "fulfilling")
        {
            request.StartFulfilling(At, UserId.SystemActor).IsSuccess.Should().BeTrue();
        }
        else if (variant == "closure_fulfilling")
        {
            request.StartFulfilling(At + DataRequestLimits.CancellationWindow, UserId.SystemActor).IsSuccess.Should().BeTrue();
        }

        var context = new Context(variant is "other_group" or "closure_of_another_group" ? Organization : null);
        var fulfillment = variant switch
        {
            "erasure_without_due_time" or "closure_without_due_time" => Fulfillment(request) with { ExecuteAt = null },
            "export_with_due_time" => Fulfillment(request) with { ExecuteAt = At },
            _ => Fulfillment(request),
        };

        await FluentActions.Awaiting(() => new DataRequestFulfillmentScheduler(jobs, context).SubmitAsync(fulfillment, CancellationToken.None).AsTask())
            .Should().ThrowAsync<InvalidOperationException>();
        jobs.Submitted.Should().BeEmpty();
    }

    [Theory]
    [InlineData("tenant", "identity.data_export_purge_tenant.v1")]
    [InlineData("organization", "identity.data_export_purge_organization.v1")]
    public async Task A_purge_is_scheduled_for_its_artifact_at_the_due_time(string scope, string key)
    {
        var organization = scope == "tenant" ? (OrganizationId?)null : Organization;
        var jobs = new RecordingScheduler();
        var artifact = DataExportArtifactId.From(Guid.CreateVersion7());
        var due = At + DataRequestLimits.ArtifactLifetime;

        await new DataRequestFulfillmentScheduler(jobs, new Context(organization)).SchedulePurgeAsync(artifact.Value, due, CancellationToken.None);

        jobs.Submitted.Should().ContainSingle().Which.Should().Be((key, (object)new DataExportPurgeParameters(artifact.Value), (DateTimeOffset?)due));
        await FluentActions.Awaiting(() => new DataRequestFulfillmentScheduler(jobs, new Context(organization))
            .SchedulePurgeAsync(Guid.NewGuid(), due, CancellationToken.None).AsTask())
            .Should().ThrowAsync<InvalidOperationException>("an artifact identity is a UUIDv7");
    }

    private static DataRequestFulfillment Fulfillment(DataSubjectRequest request) =>
        new(request.Id.Value, request.Kind, request.Status, request.Group, request.ExecuteAt);

    private static DataSubjectRequest Request(DataRequestKind kind, OrganizationId? organization) =>
        DataSubjectRequest.FileForSelf(DataSubjectRequestId.From(Guid.CreateVersion7()), kind, UserId.From(Guid.CreateVersion7()),
            new DataRequestScopeGroup(Tenant, organization), [MembershipId.From(Guid.CreateVersion7())], At).Value!;

    private sealed class Context(OrganizationId? organization) : ITenantContext
    {
        public bool IsResolved => true;
        public TenantContextOrigin? Origin => TenantContextOrigin.Ambient;
        public TenantId TenantId => Tenant;
        public OrganizationId? OrganizationId => organization;
        public UserId? UserId => null;
        public string? CorrelationId => null;
        public string? ModuleName => null;
    }

    private sealed class RecordingScheduler : IBackgroundJobScheduler
    {
        public List<(string Key, object Parameters, DateTimeOffset? Due)> Submitted { get; } = [];

        public Task<BackgroundJobId> EnqueueAsync<TParameters>(BackgroundJobKey<TParameters> definition, TParameters parameters,
            CancellationToken cancellationToken = default) where TParameters : IBackgroundJobParameters
        {
            Submitted.Add((definition.Value, parameters, null));
            return Task.FromResult(BackgroundJobId.From(Guid.CreateVersion7()));
        }

        public Task<BackgroundJobId> ScheduleAsync<TParameters>(BackgroundJobKey<TParameters> definition, TParameters parameters,
            DateTimeOffset dueAt, CancellationToken cancellationToken = default) where TParameters : IBackgroundJobParameters
        {
            Submitted.Add((definition.Value, parameters, dueAt));
            return Task.FromResult(BackgroundJobId.From(Guid.CreateVersion7()));
        }
    }
}
