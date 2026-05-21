using FluentAssertions;
using LearnStack.SharedKernel.Identifiers;
using Xunit;

namespace LearnStack.Tests.Unit.SharedKernel.Identifiers;

public sealed class FixedGuidFactoryTests
{
    private static readonly Guid G1 = Guid.Parse("019712ac-1111-7000-8000-000000000001");
    private static readonly Guid G2 = Guid.Parse("019712ac-2222-7000-8000-000000000002");

    [Fact]
    public void NewUuidV7_ReturnsTheNextSequenceValue()
    {
        var factory = new FixedGuidFactory(G1, G2);

        factory.NewUuidV7().Should().Be(G1);
        factory.NewUuidV7().Should().Be(G2);
    }

    [Fact]
    public void NewUuidV7_AfterSequenceExhausted_Throws()
    {
        var factory = new FixedGuidFactory(G1);
        factory.NewUuidV7();

        var act = () => factory.NewUuidV7();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*exhausted*");
    }

    [Fact]
    public void NewUuidV4_DrawsFromTheSameSequence()
    {
        var factory = new FixedGuidFactory(G1, G2);

        factory.NewUuidV4().Should().Be(G1);
        factory.NewUuidV4().Should().Be(G2);
    }
}
