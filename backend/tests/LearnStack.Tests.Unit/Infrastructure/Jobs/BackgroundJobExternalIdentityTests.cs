using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Jobs;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Jobs;

public sealed class BackgroundJobExternalIdentityTests
{
    // Independent Python hashlib+uuid network-byte vectors. The expected values
    // are fixed data, not values regenerated through the production derivation.
    [Theory]
    [InlineData("01234567-89ab-7cde-8f01-23456789abcd", "00", "57c11dd7-0e65-896b-8611-95ca95f91e2f")]
    [InlineData("01234567-89ab-7cde-8f01-23456789abcd", "ab", "5f947ea1-a33e-8ec2-9c51-646520cb7c94")]
    [InlineData("fedcba98-7654-7321-a012-3456789abcde", "00", "eeb005c2-0b69-8f2b-9338-172f0b1f6087")]
    public void Operation_identity_pins_domain_network_byte_order_hash_and_UUIDv8_bits(string job, string hexByte, string expected)
    {
        var identity = BackgroundJobId.From(Guid.Parse(job));
        var hash = string.Concat(Enumerable.Repeat(hexByte, 32));
        var actual = BackgroundJobExternalIdentity.OperationId(identity, hash);
        actual.Should().Be(Guid.Parse(expected));
        actual.Version.Should().Be(8);
        BackgroundJobExternalIdentity.OperationId(identity, hash).Should().Be(actual,
            "a fresh attempt or process must not alter the logical provider identity");
    }

    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("ABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABAB")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void Noncanonical_or_missing_fingerprint_cannot_create_an_operation_identity(string fingerprint)
    {
        var derive = () => BackgroundJobExternalIdentity.OperationId(BackgroundJobId.From(Guid.CreateVersion7()), fingerprint);
        derive.Should().Throw<ArgumentException>();
    }
}
