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

/// <summary>ADR-0107 § 2: the fulfilment job of a request's kind and group, and the purge at the artifact's expiry.</summary>
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

        await new DataRequestFulfillmentScheduler(jobs, new Context(organization)).SubmitAsync(request, CancellationToken.None);

        jobs.Submitted.Should().ContainSingle().Which.Should().Be((key, (object)new DataExportJobParameters(request.Id.Value), (DateTimeOffset?)null));
    }

    [Theory]
    [InlineData("erasure")]
    [InlineData("closure")]
    [InlineData("other_group")]
    [InlineData("fulfilling")]
    public async Task Anything_but_a_requested_export_of_the_announced_group_is_a_programming_error(string variant)
    {
        var jobs = new RecordingScheduler();
        var request = variant switch
        {
            "erasure" => Request(DataRequestKind.Erasure, null),
            "closure" => Request(DataRequestKind.Closure, null),
            _ => Request(DataRequestKind.Export, null),
        };
        if (variant == "fulfilling")
        {
            request.StartFulfilling(At, UserId.SystemActor).IsSuccess.Should().BeTrue();
        }

        var context = new Context(variant == "other_group" ? Organization : null);

        await FluentActions.Awaiting(() => new DataRequestFulfillmentScheduler(jobs, context).SubmitAsync(request, CancellationToken.None).AsTask())
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

        await new DataRequestFulfillmentScheduler(jobs, new Context(organization)).SchedulePurgeAsync(artifact, due, CancellationToken.None);

        jobs.Submitted.Should().ContainSingle().Which.Should().Be((key, (object)new DataExportPurgeParameters(artifact.Value), (DateTimeOffset?)due));
        await FluentActions.Awaiting(() => new DataRequestFulfillmentScheduler(jobs, new Context(organization))
            .SchedulePurgeAsync(DataExportArtifactId.From(Guid.NewGuid()), due, CancellationToken.None).AsTask())
            .Should().ThrowAsync<InvalidOperationException>("an artifact identity is a UUIDv7");
    }

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
