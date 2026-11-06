using FluentAssertions;
using LearnStack.SharedKernel.Identity;
using Xunit;

namespace LearnStack.Tests.Unit;

public sealed class InvitationVerifierTests
{
    [Fact]
    public void Canonical_vector_uses_three_uint32_big_endian_lengths_network_uuid_and_raw_secret()
    {
        // Independently computed from struct.pack('>I', len(part)) + part for each of the three byte strings.
        var digest = InvitationVerifier.Compute(Guid.Parse("019712ac-1234-7000-8000-000000000001"),
            Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
        Convert.ToHexStringLower(digest).Should().Be("77c326c42f1a3d37197563697a672e3b993bd4ae2cd875c39b9de68e33496fe7");
        InvitationVerifier.Compute(Guid.Parse("019712ac-1234-7000-8000-000000000002"), new byte[32]).Should().NotEqual(digest);
        Action malformed = () => InvitationVerifier.Compute(Guid.Empty, new byte[32]);
        malformed.Should().Throw<ArgumentException>();
        Action shortSecret = () => InvitationVerifier.Compute(Guid.Parse("019712ac-1234-7000-8000-000000000001"), new byte[31]);
        shortSecret.Should().Throw<ArgumentException>();
    }
}
