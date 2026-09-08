using FluentAssertions;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using Xunit;

namespace LearnStack.Tests.Unit.SharedKernel.Audit;

/// <summary>
/// The reserved platform tenant id, and the guards that keep it out of everything but
/// the row it exists for.
/// </summary>
/// <remarks>
/// <see href="../../../../../docs/decisions/0044-audit-write-path.md">ADR-0044 § 1</see>
/// states the invariant — "never written by a tenant request path, never announced by
/// <c>SetTenantContextAsync</c>" — and its Amendment 3 § 5 assigns it an enforcer,
/// because before that nothing enforced it and the sentence read as though something
/// did. These are the cases that make the enforcement observable.
/// </remarks>
public sealed class PlatformSentinelTests
{
    [Fact]
    public void The_sentinel_is_not_the_nil_uuid()
    {
        // The whole of ADR-0044 § 1's argument in one assertion. All-zero is what three
        // shipped mechanisms read as "no tenant"; choosing it would have made one value
        // mean both "no tenant" and "the platform's tenant", in the one component that
        // writes audit rows.
        TenantId.PlatformSentinel.Value.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void The_sentinel_reads_as_assigned()
    {
        // Not decoration: the standalone writer announces this value as app.tenant_id and
        // puts it on the row. A sentinel that every validator in the solution reported
        // "not supplied" would be unusable by the one writer it exists for — which is
        // exactly what the nil uuid would have been.
        StronglyTypedId.IsAssigned(TenantId.PlatformSentinel).Should().BeTrue();
    }

    [Fact]
    public void The_sentinel_is_uuid_v7_shaped_like_the_actor_sentinel()
    {
        // Version nibble 7, variant bits 10. The shape is the precedent UserId.SystemActor
        // set for the actor column; a tenant sentinel that looked unlike the actor
        // sentinel would be the surprising outcome.
        var bytes = TenantId.PlatformSentinel.Value.ToByteArray(bigEndian: true);

        (bytes[6] >> 4).Should().Be(7);
        (bytes[8] >> 6).Should().Be(0b10);
    }

    [Fact]
    public void An_aggregate_cannot_be_owned_by_the_sentinel()
    {
        // One guard, eight factories. EnsureRealTenant is the rule every tenant-owned
        // aggregate's factory calls, so refusing the sentinel here refuses it in all of
        // them — rather than in whichever of the eight someone remembered.
        var refuse = () => TenantOwnership.EnsureRealTenant(
            TenantId.PlatformSentinel, "message", "tenantId");

        refuse.Should().Throw<ArgumentException>().WithParameterName("tenantId");
    }

    [Fact]
    public void The_guard_still_refuses_the_nil_uuid()
    {
        // The sentinel is a third refusal beside two that already existed, and adding an
        // arm to a boolean guard is exactly how the earlier arms stop being checked.
        var refuse = () => TenantOwnership.EnsureRealTenant(
            TenantId.From(Guid.Empty), "message", "tenantId");

        refuse.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_guard_still_refuses_an_uninitialized_id()
    {
        // Not `default(TenantId)`: Vogen refuses that at compile time (VOG009), which is
        // why this arm looks unreachable and is not. A default-valued array element is
        // one of the shapes the analyzer cannot see, and an unset field on a
        // deserialized DTO is the one that happens in production — so the arm has a
        // caller even though no line of ordinary code can spell it.
        var slot = new TenantId[1];
        var uninitialized = slot[0];

        var refuse = () => TenantOwnership.EnsureRealTenant(uninitialized, "message", "tenantId");

        refuse.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_real_tenant_still_passes()
    {
        // The arm that stops this from being a guard that refuses everything.
        var refuse = () => TenantOwnership.EnsureRealTenant(
            TenantId.From(Guid.CreateVersion7()), "message", "tenantId");

        refuse.Should().NotThrow();
    }
}
