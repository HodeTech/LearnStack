using FluentAssertions;
using LearnStack.SharedKernel.Identifiers;
using Xunit;

namespace LearnStack.Tests.Unit.SharedKernel.Identifiers;

public sealed class SystemGuidFactoryTests
{
    [Fact]
    public void NewUuidV7_ProducesVersion7Guid()
    {
        var factory = new SystemGuidFactory();

        var guid = factory.NewUuidV7();

        guid.Version.Should().Be(7);
    }

    [Fact]
    public void NewUuidV4_ProducesVersion4Guid()
    {
        var factory = new SystemGuidFactory();

        var guid = factory.NewUuidV4();

        guid.Version.Should().Be(4);
    }

    [Fact]
    public void NewUuidV7_TwoCalls_ProduceDifferentGuids()
    {
        var factory = new SystemGuidFactory();

        var a = factory.NewUuidV7();
        var b = factory.NewUuidV7();

        a.Should().NotBe(b);
    }
}
