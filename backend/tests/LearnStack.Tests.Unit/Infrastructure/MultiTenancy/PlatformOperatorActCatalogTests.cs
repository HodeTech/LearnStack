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
        // NeedsPlatformRole is false for the rebind, which is what the executor admits; only the catalog keeps it out.
        PlatformOperations.IdentityAuthorityRebind.NeedsPlatformRole.Should().BeFalse();

        var act = () => new PlatformOperatorActCatalog(
            [new KeyValuePair<Type, PlatformOperatorAct>(typeof(Request), new PlatformOperatorAct(PlatformOperations.IdentityAuthorityRebind))]);

        act.Should().Throw<ArgumentException>();
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
