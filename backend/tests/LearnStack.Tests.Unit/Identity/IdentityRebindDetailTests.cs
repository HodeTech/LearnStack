using FluentAssertions;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using Xunit;

namespace LearnStack.Tests.Unit.Identity;

/// <summary>The closed detail tokens of a refusal and the timing invariants the run's fences rely on (ADR-0104).</summary>
public sealed class IdentityRebindDetailTests
{
    [Fact]
    public void Every_Detail_Has_Its_Own_Lowercase_Token_And_None_Has_None()
    {
        var tokens = Enum.GetValues<IdentityRebindDetail>().Where(detail => detail != IdentityRebindDetail.None)
            .Select(IdentityRebindDetails.Token).ToList();

        tokens.Should().OnlyHaveUniqueItems().And.NotContain(string.Empty);
        tokens.Should().OnlyContain(token => token.All(character => char.IsAsciiLetterLower(character) || character == '_'));
        IdentityRebindDetails.Token(IdentityRebindDetail.None).Should().BeEmpty();
    }

    [Fact]
    public void The_Servers_Idle_Fence_Outlasts_The_Run_Deadline_And_The_Statement_Bound_Fits_Inside_It()
    {
        // The fence frees the lock of a tool that is hung, never of one still inside its own deadline.
        IdentityIssuerRebind.IdleTransactionTimeout.Should().BeGreaterThan(
            IdentityIssuerRebind.RunDeadline + IdentityIssuerRebind.DefaultLockWait);
        IdentityIssuerRebind.StatementTimeout.Should().BeLessThan(IdentityIssuerRebind.RunDeadline);
    }
}
