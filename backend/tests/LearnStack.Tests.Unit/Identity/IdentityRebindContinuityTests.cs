using FluentAssertions;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using Xunit;

namespace LearnStack.Tests.Unit.Identity;

/// <summary>The continuity proof of a controlled issuer rebind as a pure function (ADR-0104 § 6).</summary>
public sealed class IdentityRebindContinuityTests
{
    private static readonly IdentityAuthorityId Authority =
        IdentityAuthorityId.From(Guid.Parse("019a1929-5450-7bbb-8df1-516e8e002501"));

    private static IdentityRebindStoredUser Active(string subject) =>
        new(IdentitySubjectKey.Compute(Authority, subject), subject, UserStatus.Active);

    private static IdentityRebindStoredUser Closed(string subject) =>
        new(IdentitySubjectKey.Compute(Authority, subject), null, UserStatus.Closed);

    private static IdentityRebindStoredUser Closing(string subject, bool retained) =>
        new(IdentitySubjectKey.Compute(Authority, subject), retained ? subject : null, UserStatus.Closing);

    [Fact]
    public void A_Realm_That_Reports_Every_Active_Subject_Holds_And_Counts_The_Population()
    {
        var result = IdentityRebindContinuity.Evaluate(Authority, [Active("a"), Active("b")], ["a", "b"]);

        result.Should().Be(new IdentityRebindContinuityResult(2, 0, 0, 2, 0, 0, 0, 0));
        result.Holds.Should().BeTrue();
    }

    [Fact]
    public void An_Active_Subject_The_Realm_Does_Not_Report_Is_Counted_And_Fails_The_Proof()
    {
        var result = IdentityRebindContinuity.Evaluate(Authority, [Active("a"), Active("b")], ["a"]);

        result.MissingActive.Should().Be(1);
        result.Holds.Should().BeFalse();
    }

    [Theory]
    [InlineData("A")]
    [InlineData(" a")]
    [InlineData("a ")]
    [InlineData("á")]
    public void Presence_Is_The_Exact_Ordinal_Subject_Not_A_Normalized_One(string reported)
    {
        IdentityRebindContinuity.Evaluate(Authority, [Active("a")], [reported]).MissingActive.Should().Be(1);
    }

    [Fact]
    public void A_Reported_User_With_No_Stored_Key_Is_A_Person_Who_Has_Not_Signed_In_And_Is_Allowed()
    {
        var result = IdentityRebindContinuity.Evaluate(Authority, [Active("a")], ["a", "new-1", "new-2"]);

        result.Unbootstrapped.Should().Be(2);
        result.Holds.Should().BeTrue();
    }

    [Fact]
    public void A_Closed_Persons_Key_That_A_Reported_Subject_Would_Claim_Fails_The_Proof()
    {
        var result = IdentityRebindContinuity.Evaluate(Authority, [Active("a"), Closed("gone")], ["a", "gone"]);

        result.ReappearingClosed.Should().Be(1);
        result.StoredClosed.Should().Be(1);
        result.Holds.Should().BeFalse();
    }

    [Fact]
    public void A_Closed_Persons_Absence_Holds_And_Never_Counts_Against_The_Active_Rule()
    {
        var result = IdentityRebindContinuity.Evaluate(Authority, [Active("a"), Closed("gone")], ["a"]);

        result.ReappearingClosed.Should().Be(0);
        result.MissingActive.Should().Be(0);
        result.Holds.Should().BeTrue();
    }

    [Fact]
    public void A_Closing_User_That_Retains_Its_Subject_Is_Judged_By_Neither_Rule()
    {
        var absent = IdentityRebindContinuity.Evaluate(Authority, [Closing("leaving", retained: true)], []);
        var present = IdentityRebindContinuity.Evaluate(Authority, [Closing("leaving", retained: true)], ["leaving"]);

        absent.Holds.Should().BeTrue("closure owns the transition, so its absence is not a lost person");
        present.Holds.Should().BeTrue("and its presence is not a reappearance");
        absent.StoredClosing.Should().Be(1);
    }

    [Fact]
    public void A_Closing_User_That_Retains_Only_Its_Key_Is_Judged_Like_A_Closed_One()
    {
        IdentityRebindContinuity.Evaluate(Authority, [Closing("leaving", retained: false)], ["leaving"])
            .ReappearingClosed.Should().Be(1);
        IdentityRebindContinuity.Evaluate(Authority, [Closing("leaving", retained: false)], [])
            .ReappearingClosed.Should().Be(0);
    }

    [Fact]
    public void A_Subject_That_Cannot_Be_A_LearnStack_Subject_Is_Counted_And_Matches_Nothing()
    {
        var overlong = new string('x', 256);

        var result = IdentityRebindContinuity.Evaluate(Authority, [Active("a")], ["a", "", overlong, "ok"]);

        result.Unkeyable.Should().Be(2);
        result.Unbootstrapped.Should().Be(1, "only the one reported subject that could be one and matches nothing stored");
        result.Holds.Should().BeTrue();
    }

    [Fact]
    public void Every_Refusal_Class_Has_Its_Own_Closed_Outcome_Label()
    {
        var labels = Enum.GetValues<IdentityRebindRefusal>().Where(refusal => refusal != IdentityRebindRefusal.None)
            .Select(refusal => IdentityRebindMetrics.Label(new IdentityRebindOutcome(IdentityRebindResult.Refused, refusal)))
            .ToList();

        labels.Should().OnlyHaveUniqueItems().And.NotContain("refused", "no class falls through to the default label");
        labels.Should().OnlyContain(label => label.StartsWith("refused_", StringComparison.Ordinal));
        IdentityRebindMetrics.Label(new IdentityRebindOutcome(IdentityRebindResult.Applied)).Should().Be("applied");
        IdentityRebindMetrics.Label(new IdentityRebindOutcome(IdentityRebindResult.AlreadyApplied)).Should().Be("already_applied");
        IdentityRebindMetrics.Label(new IdentityRebindOutcome(IdentityRebindResult.Unknown)).Should().Be("unknown");
    }

    [Fact]
    public void A_Subject_Containing_Text_That_Cannot_Be_Stored_Is_Counted_Unkeyable()
    {
        var result = IdentityRebindContinuity.Evaluate(Authority, [], ["ok", "nul\0inside", "\ud800lone-surrogate"]);

        result.Unkeyable.Should().Be(2);
        result.Unbootstrapped.Should().Be(1);
    }

    [Fact]
    public void Duplicate_Reported_Subjects_Are_One_Subject()
    {
        IdentityRebindContinuity.Evaluate(Authority, [Active("a")], ["a", "a", "a"]).Enumerated.Should().Be(1);
    }

    [Fact]
    public void An_Empty_Realm_Fails_Any_Active_Population_And_Holds_An_Empty_One()
    {
        IdentityRebindContinuity.Evaluate(Authority, [Active("a")], []).Holds.Should().BeFalse();
        IdentityRebindContinuity.Evaluate(Authority, [], []).Holds.Should().BeTrue();
    }

    [Fact]
    public void A_Stored_Active_Row_Without_A_Subject_Cannot_Be_Present_And_Is_Counted_Missing()
    {
        var corrupt = new IdentityRebindStoredUser(IdentitySubjectKey.Compute(Authority, "a"), null, UserStatus.Active);

        IdentityRebindContinuity.Evaluate(Authority, [corrupt], ["a"]).MissingActive.Should().Be(1);
    }

    [Fact]
    public void The_Key_Of_A_Reported_Subject_Is_Computed_Under_The_Stored_Authority_Only()
    {
        var other = IdentityAuthorityId.From(Guid.Parse("019a1929-5450-7bbb-8df1-516e8e002502"));
        var foreignClosed = new IdentityRebindStoredUser(IdentitySubjectKey.Compute(other, "gone"), null, UserStatus.Closed);

        IdentityRebindContinuity.Evaluate(Authority, [foreignClosed], ["gone"]).ReappearingClosed
            .Should().Be(0, "a key under another authority is not the same person");
    }
}
