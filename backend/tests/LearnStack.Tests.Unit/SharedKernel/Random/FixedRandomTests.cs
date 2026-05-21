using FluentAssertions;
using LearnStack.SharedKernel.Random;
using Xunit;

namespace LearnStack.Tests.Unit.SharedKernel.Random;

public sealed class FixedRandomTests
{
    [Fact]
    public void SameSeed_ProducesSameSequence()
    {
        var a = new FixedRandom(seed: 42);
        var b = new FixedRandom(seed: 42);

        Enumerable.Range(0, 10).Select(_ => a.Next(1_000_000))
            .Should().Equal(
                Enumerable.Range(0, 10).Select(_ => b.Next(1_000_000)));
    }

    [Fact]
    public void Next_BoundedByMaxExclusive()
    {
        var random = new FixedRandom(seed: 1);

        for (var i = 0; i < 100; i++)
        {
            random.Next(maxExclusive: 5).Should().BeInRange(0, 4);
        }
    }

    [Fact]
    public void NextBytes_FillsTheDestinationSpan()
    {
        var random = new FixedRandom(seed: 7);
        var buffer = new byte[16];

        random.NextBytes(buffer);

        buffer.Should().NotEqual(new byte[16], "the seeded random fills bytes");
    }
}
