using System.Text.Json;
using FluentAssertions;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Tenancy;
using Xunit;

namespace LearnStack.Tests.Unit.Application.Jobs;

public sealed class JobTrustedAttributionTests
{
    [Fact]
    public void Trusted_job_identity_is_distinct_from_parent_event_and_effective_actor_in_both_audit_paths()
    {
        var job = BackgroundJobId.From(Guid.CreateVersion7());
        var parent = BackgroundJobId.From(Guid.CreateVersion7());
        var incoming = Guid.CreateVersion7();
        var actor = UserId.From(Guid.CreateVersion7());
        var context = Context(job, parent, incoming, actor);
        context.UserId.Should().Be(UserId.SystemActor);
        context.CausalActorUserId.Should().Be(actor);
        context.JobId.Should().Be(job);
        context.ParentJobId.Should().Be(parent);
        context.IncomingEventId.Should().Be(incoming);
        AsyncExecutionDiagnostics.IsTrusted(context).Should().BeTrue();
        var intent = new AuditIntent(AuditEntryId.From(Guid.CreateVersion7()), context.TenantId, null,
            context.UserId, context.CorrelationId, "tenancy", "tenancy.organization.create", OperationType.Create,
            OperationClass.Must, null, DateTimeOffset.UnixEpoch)
        {
            JobId = job,
            ParentJobId = parent,
            CausationId = incoming,
            CausalActorUserId = actor,
            Result = AuditIntentResult.Succeeded,
        };
        foreach (var draft in new[] { AuditDraftComposer.InTransaction(intent, []),
                     AuditDraftComposer.Reconciled(intent, [], AuditIntentState.RolledBack, DateTimeOffset.UnixEpoch) })
        {
            using var metadata = JsonDocument.Parse(draft.Metadata!);
            metadata.RootElement.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
                ["job_id", "parent_job_id", "causation_id", "causal_actor_user_id"]);
            metadata.RootElement.GetProperty("job_id").GetGuid().Should().Be(job.Value);
            metadata.RootElement.GetProperty("parent_job_id").GetGuid().Should().Be(parent.Value);
            metadata.RootElement.GetProperty("causation_id").GetGuid().Should().Be(incoming);
            metadata.RootElement.GetProperty("causal_actor_user_id").GetGuid().Should().Be(actor.Value);
            draft.ActorUserId.Should().Be(UserId.SystemActor);
        }
    }

    [Fact]
    public void An_arbitrary_ambient_system_context_does_not_gain_trusted_diagnostic_attribution()
    {
        AsyncExecutionDiagnostics.IsTrusted(new AmbientSystemContext()).Should().BeFalse();
        AsyncExecutionDiagnostics.IsTrusted(null).Should().BeFalse();
    }

    [Fact]
    public void An_unresolved_framework_flow_redacts_using_its_closed_source_provenance()
    {
        var owner = JobApplicationSourceOwner.Create();
        AsyncExecutionDiagnostics.IsTrusted(UnresolvedTenantContext.Instance).Should().BeFalse();
        using (owner.Enter())
        {
            AsyncExecutionDiagnostics.IsTrusted(null).Should().BeTrue();
            AsyncExecutionDiagnostics.IsTrusted(UnresolvedTenantContext.Instance).Should().BeTrue();
            AsyncExecutionDiagnostics.IsTrusted(new AmbientSystemContext()).Should().BeTrue();
        }
        AsyncExecutionDiagnostics.IsTrusted(null).Should().BeFalse();
    }

    [Theory]
    [InlineData("same_parent")]
    [InlineData("v4_job")]
    [InlineData("empty_event")]
    public void Invalid_or_conflated_trusted_job_identifiers_are_refused(string invalid)
    {
        var job = BackgroundJobId.From(invalid == "v4_job" ? Guid.NewGuid() : Guid.CreateVersion7());
        var create = () => Context(job, invalid == "same_parent" ? job : null,
            invalid == "empty_event" ? Guid.Empty : null, null);
        create.Should().Throw<ArgumentException>();
    }

    private static JobTenantContext Context(BackgroundJobId job, BackgroundJobId? parent, Guid? incoming, UserId? actor) =>
        JobTenantContext.FromValidatedJob(TenantId.From(Guid.CreateVersion7()), null, job, parent, incoming, actor,
            "00-11111111111111111111111111111111-1111111111111111-01", "tenancy");

    private sealed class AmbientSystemContext : ITenantContext
    {
        public bool IsResolved => true;
        public TenantId TenantId { get; } = TenantId.From(Guid.CreateVersion7());
        public OrganizationId? OrganizationId => null;
        public UserId? UserId => LearnStack.SharedKernel.Identifiers.UserId.SystemActor;
        public UserId? CausalActorUserId => LearnStack.SharedKernel.Identifiers.UserId.From(Guid.CreateVersion7());
        public TenantContextOrigin? Origin => TenantContextOrigin.Ambient;
        public string? CorrelationId => null;
        public string? ModuleName => "tenancy";
    }
}
