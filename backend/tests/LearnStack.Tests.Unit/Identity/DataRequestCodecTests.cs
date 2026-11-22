using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using LearnStack.Modules.Identity.Application.DataRequests;
using LearnStack.SharedKernel.Identifiers;
using Xunit;

namespace LearnStack.Tests.Unit.Identity;

/// <summary>ADR-0107 § 4's opaque ticket and the data-request page cursor: canonical spellings only, digests only stored.</summary>
public sealed class DataRequestCodecTests
{
    [Fact]
    public void A_minted_ticket_is_32_random_bytes_in_canonical_base64url_and_only_its_domain_separated_digest_is_kept()
    {
        var (ticket, digest) = DataExportTicketCodec.Mint();
        var (other, otherDigest) = DataExportTicketCodec.Mint();

        ticket.Should().MatchRegex("^[A-Za-z0-9_-]{43}$");
        other.Should().NotBe(ticket);
        digest.Should().MatchRegex("^[0-9a-f]{64}$").And.NotBe(otherDigest);
        DataExportTicketCodec.TryDigest(ticket).Should().Be(digest);
        var raw = Convert.FromBase64String(ticket.Replace('-', '+').Replace('_', '/') + "=");
        raw.Should().HaveCount(32);
        Convert.ToHexStringLower(SHA256.HashData(raw)).Should().NotBe(digest, "the digest is domain-separated, never a bare hash");
        digest.Should().NotContain(ticket);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA/")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA.")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void A_noncanonical_or_malformed_ticket_has_no_digest(string ticket) =>
        DataExportTicketCodec.TryDigest(ticket).Should().BeNull();

    [Fact]
    public void A_one_character_change_is_a_different_well_formed_ticket()
    {
        var (ticket, digest) = DataExportTicketCodec.Mint();
        var forged = (ticket[0] == 'A' ? 'B' : 'A') + ticket[1..];
        DataExportTicketCodec.TryDigest(forged).Should().NotBeNull().And.NotBe(digest);
    }

    [Fact]
    public void A_cursor_round_trips_only_under_the_scope_it_was_minted_for()
    {
        var actor = UserId.From(Guid.CreateVersion7());
        var tenant = TenantId.From(Guid.CreateVersion7());
        var organization = OrganizationId.From(Guid.CreateVersion7());
        var anchor = Guid.CreateVersion7();
        var scope = DataRequestCursorCodec.Scope("identity.data_request.list_own", actor, tenant, null);
        var cursor = DataRequestCursorCodec.Encode(scope, anchor);

        DataRequestCursorCodec.TryDecode(cursor, scope, out var decoded).Should().BeTrue();
        decoded.Should().Be(anchor);
        foreach (var other in new[]
        {
            DataRequestCursorCodec.Scope("identity.data_request.list", actor, tenant, null),
            DataRequestCursorCodec.Scope("identity.data_request.list_own", UserId.From(Guid.CreateVersion7()), tenant, null),
            DataRequestCursorCodec.Scope("identity.data_request.list_own", actor, tenant, organization),
        })
            DataRequestCursorCodec.TryDecode(cursor, other, out _).Should().BeFalse();
        DataRequestCursorCodec.TryDecode(cursor + "A", scope, out _).Should().BeFalse();
        DataRequestCursorCodec.TryDecode(Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"v\":1}")), scope, out _).Should().BeFalse();
        DataRequestCursorCodec.TryDecode(DataRequestCursorCodec.Encode(scope, Guid.NewGuid()), scope, out _).Should().BeFalse(
            "only a UUIDv7 anchors a page");
    }
}
