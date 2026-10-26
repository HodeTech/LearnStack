using FluentAssertions;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using Xunit;

namespace LearnStack.Tests.Unit.Identity;

public sealed class GlobalIdentityKeyTests
{
    private static readonly IdentityAuthorityId Authority =
        IdentityAuthorityId.From(Guid.Parse("019a1929-5450-7bbb-8df1-516e8e002501"));

    [Theory]
    [InlineData("019a1929-5450-7bbb-8df1-516e8e002501", 6536232317004839961L, 8764202873088396187L)]
    [InlineData("01930000-0000-7000-8000-00000000a001", 7247098387317121321L, -9009402356915246337L)]
    [InlineData("ffffffff-ffff-7fff-bfff-ffffffffffff", -2062124468225165546L, 7755294501392127735L)]
    public void User_and_tenant_keys_have_independent_signed_network_order_vectors(
        string identifier, long userKey, long tenantKey)
    {
        var id = Guid.Parse(identifier);
        IdentityAdvisoryLockKey.ForUser(UserId.From(id)).Should().Be(userKey);
        IdentityAdvisoryLockKey.ForTenant(TenantId.From(id)).Should().Be(tenantKey);
        userKey.Should().NotBe(tenantKey);
    }

    [Fact]
    public void A_contextual_lock_plan_copies_deduplicates_and_orders_actual_users_and_keeps_mode()
    {
        var users = new[] { UserId.From(Authority.Value), UserId.From(Guid.Parse("ffffffff-ffff-7fff-bfff-ffffffffffff")) };
        var expected = users.OrderBy(IdentityAdvisoryLockKey.ForUser).ToArray();
        var plan = new IdentityMutationLockPlan(users.Concat(users), TenantId.From(Authority.Value), IdentityTenantLockMode.Shared);
        users[0] = UserId.SystemActor;
        plan.UserIds.Should().Equal(expected);
        plan.TenantMode.Should().Be(IdentityTenantLockMode.Shared);
        var system = () => new IdentityMutationLockPlan([UserId.SystemActor], plan.TenantId, IdentityTenantLockMode.Shared);
        system.Should().Throw<ArgumentException>();
        var sentinel = () => new IdentityMutationLockPlan([], TenantId.PlatformSentinel, IdentityTenantLockMode.Shared);
        sentinel.Should().Throw<ArgumentException>();
    }

    // Independently calculated from length-prefixed UTF-8/RFC UUID bytes, not from the C# helper.
    [Theory]
    [InlineData("opaque-subject", "e766989ed59e5e7cc8dcaed58d7d9ca36a53d3d6320cbd4b11da510075b9b61e", -6382331710714775529L)]
    [InlineData("  I/ı/🙂  ", "27681d98c78fb11e3cb9b4b61a0388e7916a5dfd861f99a40c1b5b63f8043d9b", 137252104885486171L)]
    public void Subject_and_lock_keys_use_the_exact_network_order_vectors(string subject, string digest, long lockKey)
    {
        var value = new GlobalIdentitySubject(Authority, "https://identity.example/realms/learnstack", subject);
        value.SubjectKey.Hex.Should().Be(digest);
        IdentityAdvisoryLockKey.ForSubject(value).Should().Be(lockKey);
        value.Subject.Should().Be(subject);
    }

    [Fact]
    public void Issuer_address_does_not_change_a_stable_authority_key_but_exact_subject_and_authority_do()
    {
        var original = new GlobalIdentitySubject(Authority, "https://old.example/realms/learnstack", "subject");
        var address = new GlobalIdentitySubject(Authority, "https://new.example/realms/learnstack", "subject");
        original.SubjectKey.Should().Be(address.SubjectKey);
        original.SubjectKey.Should().NotBe(IdentitySubjectKey.Compute(Authority, "Subject"));
        original.SubjectKey.Should().NotBe(IdentitySubjectKey.Compute(Authority, " subject"));
        original.SubjectKey.Should().NotBe(IdentitySubjectKey.Compute(IdentityAuthorityId.From(Guid.CreateVersion7()), "subject"));
    }

    [Fact]
    public void Retained_key_bytes_cannot_be_changed_through_input_or_output_arrays()
    {
        var bytes = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var key = IdentitySubjectKey.FromBytes(bytes);
        var expected = key.Hex;
        bytes[0] = 255;
        key.ToByteArray()[1] = 255;
        key.Hex.Should().Be(expected);
        key.Should().Be(IdentitySubjectKey.FromBytes(Convert.FromHexString(expected)));
        key.GetHashCode().Should().Be(IdentitySubjectKey.FromBytes(Convert.FromHexString(expected)).GetHashCode());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void A_key_requires_exactly_32_bytes(int length)
    {
        var invalid = () => IdentitySubjectKey.FromBytes(new byte[length]);
        invalid.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Invalid_facts_and_unbounded_subjects_refuse_without_echoing_values()
    {
        var canary = "private-identity-canary" + new string('x', 256);
        var invalidSubject = () => new GlobalIdentitySubject(Authority, "https://identity.example", canary);
        invalidSubject.Should().Throw<ArgumentException>().Which.ToString().Should().NotContain(canary);
        var invalidAuthority = () => IdentitySubjectKey.Compute(IdentityAuthorityId.From(Guid.Empty), "subject");
        invalidAuthority.Should().Throw<ArgumentException>();
        var invalidIssuer = () => new GlobalIdentitySubject(Authority, "https://user:private-identity-canary@identity.example", "subject");
        invalidIssuer.Should().Throw<ArgumentException>().Which.ToString().Should().NotContain("private-identity-canary");
        var value = new GlobalIdentitySubject(Authority, "https://identity.example", "private-identity-canary");
        value.ToString().Should().Be(nameof(GlobalIdentitySubject));
        value.SubjectKey.ToString().Should().Be(nameof(IdentitySubjectKey));
    }

    [Theory]
    [InlineData("nul")]
    [InlineData("high")]
    [InlineData("low")]
    public void Nonstorable_subjects_never_reach_hashing_or_storage(string variant)
    {
        var subject = variant switch { "nul" => "private\0subject", "high" => "private\ud800subject", _ => "private\udc00subject" };
        var facts = () => new GlobalIdentitySubject(Authority, "https://identity.example", subject);
        facts.Should().Throw<ArgumentException>().Which.ToString().Should().NotContain(subject);
        var digest = () => IdentitySubjectKey.Compute(Authority, subject);
        digest.Should().Throw<ArgumentException>().Which.ToString().Should().NotContain(subject);
    }
}
