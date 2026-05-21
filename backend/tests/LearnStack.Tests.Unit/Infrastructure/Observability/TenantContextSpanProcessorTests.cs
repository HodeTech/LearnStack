using System.Diagnostics;
using FluentAssertions;
using LearnStack.Infrastructure.Observability;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Observability;

/// <summary>
/// Backs the catalogue entry
/// TenantContextSpanProcessor_DoesNotThrow_When_Context_Missing
/// (ADR-0032 § Sub-decision 10). The processor must no-op when the
/// singleton accessor is null — auto-instrumentation libraries create
/// warm-up activities before any handler scope opens.
/// </summary>
public sealed class TenantContextSpanProcessorTests
{
    [Fact]
    public void OnStart_DoesNotThrow_When_Accessor_Current_Is_Null()
    {
        var accessor = new TestAccessor(current: null);
        var processor = new TenantContextSpanProcessor(accessor);

        using var activity = new Activity("warm-up");
        activity.Start();

        var act = () => processor.OnStart(activity);

        act.Should().NotThrow();
    }

    [Fact]
    public void OnStart_Enriches_Activity_When_Context_Is_Resolved()
    {
        var tenantId = Guid.Parse("018f4d40-1234-7000-8000-000000000001");
        var organizationId = Guid.Parse("018f4d40-1234-7000-8000-000000000002");
        var userId = UserId.From(Guid.Parse("018f4d40-1234-7000-8000-000000000003"));

        var accessor = new TestAccessor(new TestTenantContext(
            IsResolved: true,
            TenantId: tenantId,
            OrganizationId: organizationId,
            UserId: userId,
            CorrelationId: "00-aabbccdd-eeff0011-01",
            ModuleName: "education"));

        var processor = new TenantContextSpanProcessor(accessor);
        using var activity = new Activity("test-span");
        activity.Start();

        processor.OnStart(activity);

        activity.GetTagItem("tenant.id").Should().Be(tenantId);
        activity.GetTagItem("organization.id").Should().Be(organizationId);
        activity.GetTagItem("user.id").Should().Be(userId.Value);
        activity.GetTagItem("correlation.id").Should().Be("00-aabbccdd-eeff0011-01");
        activity.GetTagItem("module").Should().Be("education");
    }

    [Fact]
    public void OnStart_DoesNotEnrich_TenantTags_When_Context_Is_Unresolved()
    {
        var accessor = new TestAccessor(UnresolvedTenantContext.Instance);
        var processor = new TenantContextSpanProcessor(accessor);

        using var activity = new Activity("unresolved");
        activity.Start();

        var act = () => processor.OnStart(activity);

        act.Should().NotThrow();
        activity.GetTagItem("tenant.id").Should().BeNull();
    }

    private sealed class TestAccessor(ITenantContext? current) : ITenantContextAccessor
    {
        public ITenantContext? Current { get; set; } = current;
    }

    private sealed record TestTenantContext(
        bool IsResolved,
        Guid TenantId,
        Guid? OrganizationId,
        UserId? UserId,
        string? CorrelationId,
        string? ModuleName) : ITenantContext;
}
