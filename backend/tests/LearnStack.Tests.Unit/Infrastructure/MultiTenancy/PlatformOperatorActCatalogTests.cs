using FluentAssertions;
using LearnStack.Infrastructure.PlatformOperator;
using LearnStack.SharedKernel.Platform;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.MultiTenancy;

/// <summary>The closed act catalog never holds the offline issuer rebind (ADR-0104 § 3).</summary>
public sealed class PlatformOperatorActCatalogTests
{
    private sealed record Request;

    [Fact]
    public void The_Rebind_Operation_Is_Not_A_Pipeline_Act_Even_Though_It_Needs_No_Platform_Role()
    {
        // NeedsPlatformRole is false for the rebind; what keeps it out is that it does not run in the pipeline.
        PlatformOperations.IdentityAuthorityRebind.NeedsPlatformRole.Should().BeFalse();
        PlatformOperations.IdentityAuthorityRebind.RunsInPipeline.Should().BeFalse();

        var act = () => new PlatformOperatorActCatalog(
            [new KeyValuePair<Type, PlatformOperatorAct>(typeof(Request), new PlatformOperatorAct(PlatformOperations.IdentityAuthorityRebind))]);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [MemberData(nameof(NonPipelineOperations))]
    public void An_Operation_That_Runs_Anywhere_But_The_Pipeline_Is_Refused_By_The_Catalog(string auditSlug)
    {
        // The slug, not the operation, is the theory parameter: xUnit cannot serialize a PlatformOperation, and a
        // case it cannot serialize is discovered as one case and executed as five, which hosted CI refuses.
        var operation = PlatformOperations.All.Single(candidate => candidate.AuditSlug == auditSlug);
        operation.RunsInPipeline.Should().BeFalse();

        var act = () => new PlatformOperatorActCatalog(
            [new KeyValuePair<Type, PlatformOperatorAct>(typeof(Request), new PlatformOperatorAct(operation))]);

        act.Should().Throw<ArgumentException>();
    }

    public static TheoryData<string> NonPipelineOperations() => new()
    {
        PlatformOperations.KillswitchToggle.AuditSlug,
        PlatformOperations.IdentityAuthorityRebind.AuditSlug,
        PlatformOperations.DeliveryInspect.AuditSlug,
        PlatformOperations.DeliveryReplay.AuditSlug,
        PlatformOperations.DeliveryReset.AuditSlug,
    };

    [Fact]
    public void Every_Registered_Operation_Runs_In_Exactly_One_Place_And_Only_The_Scope_Needs_The_Platform_Role()
    {
        foreach (var operation in PlatformOperations.All)
        {
            operation.NeedsPlatformRole.Should().Be(operation.Executor == PlatformOperationExecutor.PlatformScope, operation.AuditSlug);
            operation.RunsInPipeline.Should().Be(operation.Executor == PlatformOperationExecutor.Pipeline, operation.AuditSlug);
        }

        PlatformOperations.DeliveryReplay.Executor.Should().Be(PlatformOperationExecutor.RecoveryOwner);
        PlatformOperations.DeliveryReset.Executor.Should().Be(PlatformOperationExecutor.RecoveryOwner);
        PlatformOperations.DeliveryInspect.Executor.Should().Be(PlatformOperationExecutor.PlatformScope);
    }

    [Fact]
    public void An_Onboarding_Operation_Is_Still_Accepted()
    {
        var catalog = new PlatformOperatorActCatalog(
            [new KeyValuePair<Type, PlatformOperatorAct>(typeof(Request), new PlatformOperatorAct(PlatformOperations.HostMap))]);

        catalog.TryGet(typeof(Request), out var found).Should().BeTrue();
        found.Operation.Should().BeSameAs(PlatformOperations.HostMap);
    }
}
