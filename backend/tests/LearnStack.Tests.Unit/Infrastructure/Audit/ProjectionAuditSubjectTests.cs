using FluentAssertions;
using LearnStack.Infrastructure.Audit;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Audit;

public sealed class ProjectionAuditSubjectTests
{
    [Fact]
    public void An_unchanged_projection_has_its_actual_key_without_a_fabricated_capture_or_parent_designation()
    {
        var capture = new AuditStateCapture();
        var mapping = PlatformHostMapping.Create("manage.example.test", TenantId.From(Guid.CreateVersion7()), null, true, true);
        capture.OpenFrame();
        capture.DeclareIntent(Intent(mapping.TenantId));
        capture.OpenFrame();
        capture.DeclareIntent(Intent(mapping.TenantId));
        ((IAuditSubject)capture).DesignateProjection(mapping);
        capture.Intents[0].SubjectId.Should().BeNull();
        capture.Intents[1].SubjectId.Should().Be(mapping.Host);
        var row = AuditDraftComposer.InTransaction(capture.Intents[1], []);
        row.EntityId.Should().Be(mapping.Host);
        row.BeforeState.Should().BeNull();
        row.AfterState.Should().BeNull();
        row.Changes.Should().BeNull();
        capture.Changes.Should().BeEmpty();
    }

    private static AuditIntent Intent(TenantId tenant) => new(AuditEntryId.From(Guid.CreateVersion7()), tenant,
        null, null, null, "tenancy", "tenancy.hostmapping.write", OperationType.Create, OperationClass.Must,
        typeof(PlatformHostMapping), DateTimeOffset.UnixEpoch);
}
