using FluentAssertions;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;
using Xunit;

namespace LearnStack.Tests.Unit.SharedKernel.Tenancy;

public sealed class NativeScopeResolutionTests
{
    private static readonly TenantId Tenant = TenantId.From(Guid.Parse("01930000-0000-7000-8000-00000000bb01"));
    private static readonly OrganizationId Branch = OrganizationId.From(Guid.Parse("01930000-0000-7000-8000-00000000bb02"));
    private static readonly UserId Actor = UserId.From(Guid.Parse("01930000-0000-7000-8000-00000000bb03"));

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Real_host_and_live_native_tuple_produce_the_distinct_ceiling(bool branchHost, bool selectedBranch)
    {
        var result = TenantContextFactory.CreateForNativeScope(Attempt() with
        {
            HostOrganizationId = branchHost ? Branch : null,
            OrganizationId = selectedBranch ? Branch : null,
            OrganizationBelongsToTenant = selectedBranch ? true : null,
        });

        result.IsSuccess.Should().BeTrue();
        result.Value!.Origin.Should().Be(TenantContextOrigin.HostAndNativeSessionScope);
        result.Value.TenantId.Should().Be(Tenant);
        result.Value.OrganizationId.Should().Be(selectedBranch ? Branch : null);
        result.Value.UserId.Should().Be(Actor);
        result.Value.HostScope!.TenantId.Should().Be(Tenant);
        result.Value.HostScope.OrganizationId.Should().Be(branchHost ? Branch : null);
    }

    [Theory]
    [InlineData("platform")]
    [InlineData("foreign-tenant")]
    [InlineData("reserved-tenant")]
    [InlineData("unknown-tenant")]
    [InlineData("empty-host")]
    [InlineData("empty-actor")]
    [InlineData("system-actor")]
    [InlineData("missing-membership")]
    [InlineData("denied-membership")]
    [InlineData("orgless-on-org-host")]
    [InlineData("foreign-branch")]
    [InlineData("unconfirmed-branch")]
    [InlineData("denied-branch")]
    [InlineData("empty-branch")]
    public void Missing_or_disagreeing_authority_never_creates_a_partial_context(string mutation)
    {
        var attempt = mutation switch
        {
            "platform" => Attempt() with { HostTenantId = null },
            "foreign-tenant" => Attempt() with { HostTenantId = TenantId.From(Guid.Parse("01930000-0000-7000-8000-00000000bb99")) },
            "reserved-tenant" => Attempt() with { TenantId = TenantId.PlatformSentinel, HostTenantId = TenantId.PlatformSentinel },
            "unknown-tenant" => Attempt() with { TenantId = (new TenantId[1])[0] },
            "empty-host" => Attempt() with { HostTenantId = TenantId.From(Guid.Empty) },
            "empty-actor" => Attempt() with { ActorUserId = (new UserId[1])[0] },
            "system-actor" => Attempt() with { ActorUserId = UserId.SystemActor },
            "missing-membership" => Attempt() with { MembershipCovers = null },
            "denied-membership" => Attempt() with { MembershipCovers = false },
            "orgless-on-org-host" => Attempt() with { HostOrganizationId = Branch },
            "foreign-branch" => Attempt() with { HostOrganizationId = Branch, OrganizationId = OrganizationId.From(Guid.Parse("01930000-0000-7000-8000-00000000bb98")), OrganizationBelongsToTenant = true },
            "unconfirmed-branch" => Attempt() with { OrganizationId = Branch },
            "denied-branch" => Attempt() with { OrganizationId = Branch, OrganizationBelongsToTenant = false },
            "empty-branch" => Attempt() with { OrganizationId = OrganizationId.From(Guid.Empty), OrganizationBelongsToTenant = true },
            _ => throw new InvalidOperationException("Unknown control."),
        };

        var result = TenantContextFactory.CreateForNativeScope(attempt);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeSameAs(TenantContextFactory.Refused);
    }

    private static NativeScopeResolutionAttempt Attempt() => new()
    {
        HostTenantId = Tenant,
        TenantId = Tenant,
        ActorUserId = Actor,
        MembershipCovers = true,
    };
}
