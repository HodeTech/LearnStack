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

    [Fact]
    public void MixedV7AndV4_PreservesVersionPerSeed()
    {
        // The fixture does not synthesise version-7/-4 shapes — callers
        // seed version-appropriate Guids and the fixture returns them
        // verbatim. Locks the documented shared-queue contract.
        var v7 = Guid.CreateVersion7();
        var v4 = Guid.NewGuid();
        var factory = new FixedGuidFactory(v7, v4);

        factory.NewUuidV7().Version.Should().Be(7);
        factory.NewUuidV4().Version.Should().Be(4);
    }
}
