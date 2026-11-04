using LearnStack.Modules.Identity.Application.Contracts.Invitations;
using LearnStack.Modules.Tenancy.Application.Contracts.Settings;
using LearnStack.Modules.Tenancy.Application.Audit;
using LearnStack.SharedKernel.Audit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class InvitationApplicationAdmissionTests(MembershipAdministrationFixture fixture) : IClassFixture<MembershipAdministrationFixture>
{
    [Fact]
    public async Task Direct_application_invitation_dispatch_denies_before_unregistered_write_or_disclosure_collaborators()
    {
        var learner = await fixture.AddMemberAsync();
        var create = await fixture.SendAsync(new CreateInvitationCommand("direct@example.test", Guid.CreateVersion7(), 0),
            "invitation-direct-create-denied", learner.Actor);
        Assert.True(create.IsFailure); Assert.Equal("forbidden", create.Error.Code);
        var detail = await fixture.SendAsync(new GetInvitationQuery(Guid.CreateVersion7()), "invitation-direct-detail-denied", learner.Actor);
        Assert.True(detail.IsFailure); Assert.Equal("forbidden", detail.Error.Code);
        var receipt = await fixture.SendAsync(new ReadInvitationReceiptQuery(Guid.CreateVersion7()), "invitation-direct-receipt-denied", learner.Actor);
        Assert.True(receipt.IsFailure); Assert.Equal("forbidden", receipt.Error.Code);
    }
    [Fact]
    public async Task Direct_v1_policy_write_cannot_bypass_existing_setting_pair()
    {
        await using var harness = await RoleGrantPipelineHarness.CreateAsync(fixture.AppConnectionString, IdentityMembershipFixture.Authority,
            LearnStack.SharedKernel.Identifiers.TenantId.From(SchemaFixture.TenantA),
            LearnStack.SharedKernel.Identifiers.UserId.From(IdentityMembershipFixture.AdminA), "invitation-direct-policy-denied", configure:
            services => services.AddSingleton<IAuditCatalogSource, TenancyAuditCatalogSource>());
        var result = await harness.Sender.Send(new SetTenantInvitationLifetimeCommand(7, false, null));
        Assert.True(result.IsFailure); Assert.Equal("forbidden", result.Error.Code);
    }
}
