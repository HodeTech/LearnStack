using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using LearnStack.Modules.Tenancy.Application.FieldConsents;
using Xunit;

namespace LearnStack.Tests.Unit.Tenancy;

public sealed class MembershipFieldConsentRequestTests
{
    [Fact]
    public void Notice_hash_is_stable_and_binds_the_exact_authored_utf8_text()
    {
        var hash = MembershipFieldConsentNotice.ComputeHash("Notice: é");
        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, MembershipFieldConsentNotice.ComputeHash("Notice: é"));
        Assert.NotEqual(hash, MembershipFieldConsentNotice.ComputeHash("Notice: e\u0301"));
        Assert.NotEqual(hash, MembershipFieldConsentNotice.ComputeHash("Notice: é "));
        Assert.All(hash, c => Assert.True(c is >= '0' and <= '9' or >= 'a' and <= 'f'));
    }

    [Fact]
    public void Grant_requires_confirmation_and_an_exact_well_formed_predecessor()
    {
        var validator = new GrantOwnMembershipFieldConsentCommandValidator();
        var command = new GrantOwnMembershipFieldConsentCommand(Guid.CreateVersion7(), 1, "en",
            MembershipFieldConsentNotice.ComputeHash("Notice"), 0, null, true);
        Assert.True(validator.Validate(command).IsValid);
        Assert.False(validator.Validate(command with { Confirmed = false }).IsValid);
        Assert.False(validator.Validate(command with { ExpectedSequence = 1 }).IsValid);
        Assert.False(validator.Validate(command with { ExpectedEvidenceId = Guid.CreateVersion7() }).IsValid);
        Assert.False(validator.Validate(command with { ExpectedNoticeHash = "invalid" }).IsValid);
        Assert.True(validator.Validate(command with { ExpectedSequence = 1, ExpectedEvidenceId = Guid.CreateVersion7() }).IsValid);
    }

    [Fact]
    public void Withdrawal_needs_retained_evidence_and_an_explicit_nonzero_predecessor()
    {
        var validator = new WithdrawOwnMembershipFieldConsentCommandValidator();
        var id = Guid.CreateVersion7();
        var command = new WithdrawOwnMembershipFieldConsentCommand(id, 1, id, true);
        Assert.True(validator.Validate(command).IsValid);
        Assert.False(validator.Validate(command with { Confirmed = false }).IsValid);
        Assert.False(validator.Validate(command with { ExpectedSequence = 0 }).IsValid);
        Assert.False(validator.Validate(command with { EvidenceId = Guid.Empty }).IsValid);
        Assert.False(validator.Validate(command with { ExpectedEvidenceId = Guid.Empty }).IsValid);
    }
}
