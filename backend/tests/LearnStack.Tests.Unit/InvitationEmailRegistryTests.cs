using System.Text.Json;
using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Modules.Identity.Application.Contracts.Invitations;
using LearnStack.Modules.Identity.Application.Invitations;
using LearnStack.Modules.Identity.Infrastructure.Invitations;
using LearnStack.SharedKernel.Jobs;
using Xunit;

namespace LearnStack.Tests.Unit;

public sealed class InvitationEmailRegistryTests
{
    [Fact]
    public void Actual_Registry_Binds_Exactly_Two_Active_Definitions_And_Four_Scoped_Ordinary_Roots()
    {
        var registry = InvitationEmailRegistration.CreateRegistry();
        registry.DefinitionKeys.Should().BeEquivalentTo(
            "identity.invitation_email_tenant.v1", "identity.invitation_email_organization.v1");
        var definitions = registry.DefinitionKeys.Select(key => registry.Resolve(key).Definition!).ToArray();
        definitions.Should().OnlyContain(definition => definition.Readiness == BackgroundJobDefinitionReadiness.Active
            && definition.Profile == BackgroundJobExecutionProfile.External && definition.Queue == "external"
            && definition.OwnerModule == "identity" && definition.CapacityGroup == "identity.invitation_email"
            && definition.ParameterType == typeof(InvitationEmailParameters));
        definitions.SelectMany(definition => definition.RootRequestTypes).Should().BeEquivalentTo(
            new[] { typeof(PrepareTenantInvitationEmailCommand), typeof(FinalizeTenantInvitationEmailCommand),
                typeof(PrepareOrganizationInvitationEmailCommand), typeof(FinalizeOrganizationInvitationEmailCommand) });
        definitions.SelectMany(definition => definition.NestedRequestTypes).Should().BeEmpty();
        registry.All.Where(request => request.Classification == BackgroundJobRequestClassification.Ordinary)
            .Select(request => request.RequestType).Should().BeEquivalentTo(
                new[] { typeof(PrepareTenantInvitationEmailCommand), typeof(FinalizeTenantInvitationEmailCommand),
                    typeof(PrepareOrganizationInvitationEmailCommand), typeof(FinalizeOrganizationInvitationEmailCommand) });
        foreach (var definition in definitions)
        {
            definition.ProfileRegistration.PlanType.Should().Be<InvitationEmailPlan>();
            definition.ProfileRegistration.OutcomeType.Should().Be<InvitationEmailOutcome>();
            definition.ProfileRegistration.ExternalPortType.Should().Be<IInvitationEmailOperation>();
            var expectedScope = definition.Key == "identity.invitation_email_tenant.v1"
                ? BackgroundJobScope.TenantWide : BackgroundJobScope.Organization;
            definition.Scope.Should().Be(expectedScope);
            foreach (var root in definition.RootRequestTypes)
            {
                registry.TryGet(root, out var request).Should().BeTrue();
                request!.Scope.Should().Be(expectedScope);
                request.Classification.Should().Be(BackgroundJobRequestClassification.Ordinary);
            }
        }
    }

    [Fact]
    public void Actual_Registry_Preserves_Two_External_Workers_And_Four_Tenant_Capacity_Slots()
    {
        var infrastructure = InvitationEmailRegistration.CreateRegistry().Infrastructure;

        infrastructure.Pools.Should().HaveCount(3);
        infrastructure.Pools["transactional"].Workers.Should().Be(1);
        infrastructure.Pools["control"].Workers.Should().Be(1);
        infrastructure.Pools["external"].Workers.Should().Be(2);
        infrastructure.Pools["external"].Kind.Should().Be(BackgroundJobWorkerPoolKind.External);
        infrastructure.Pools["external"].Queues.Should().Equal("external");
        infrastructure.CapacityGroups.Should().ContainSingle();
        var group = infrastructure.CapacityGroups["identity.invitation_email"];
        group.OperationalCeiling.Should().Be(4);
        group.PlanLimit.Should().BeNull();
        group.DeclaringType.Should().Be<InvitationEmailJobHandlers>();
        infrastructure.TryGetAdapter(typeof(IInvitationEmailOperation), out var adapter).Should().BeTrue();
        adapter!.ImplementationType.Should().Be<InvitationSmtpOperation>();
    }

    [Theory]
    [InlineData(BackgroundJobPayloadKind.Parameters)]
    [InlineData(BackgroundJobPayloadKind.Plan)]
    [InlineData(BackgroundJobPayloadKind.Outcome)]
    public void Actual_Contracts_Use_Only_Canonical_Guids_And_Exact_PascalCase_Wire_Members(BackgroundJobPayloadKind kind)
    {
        var invitation = Guid.Parse("019712ac-1234-7000-8000-000000000001");
        var generation = Guid.Parse("019712ac-1234-7000-8000-000000000002");
        var spool = Guid.Parse("019712ac-1234-7000-8000-000000000003");
        var receipt = Guid.Parse("019712ac-1234-7000-8000-000000000004");
        object payload = kind switch
        {
            BackgroundJobPayloadKind.Parameters => new InvitationEmailParameters(invitation, generation, spool),
            BackgroundJobPayloadKind.Plan => new InvitationEmailPlan(invitation, generation, spool),
            BackgroundJobPayloadKind.Outcome => new InvitationEmailOutcome(receipt),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var type = payload.GetType();

        var json = BackgroundJobPayloadContract.Serialize(payload, type, kind);

        BackgroundJobPayloadContract.Read(json, type, kind).Should().Be(payload);
        using var wire = JsonDocument.Parse(json);
        var properties = wire.RootElement.EnumerateObject().ToArray();
        string[] expectedMembers = kind == BackgroundJobPayloadKind.Outcome
            ? ["ReceiptId"] : ["InvitationId", "GenerationId", "SpoolId"];
        properties.Select(property => property.Name).Should().BeEquivalentTo(expectedMembers);
        foreach (var property in properties)
        {
            property.Value.ValueKind.Should().Be(JsonValueKind.String);
            var value = property.Value.GetString();
            Guid.TryParseExact(value, "D", out var parsed).Should().BeTrue();
            parsed.Should().NotBeEmpty();
            value.Should().Be(parsed.ToString("D"));
        }

        var lowerCaseWire = json.Replace("Id\"", "id\"", StringComparison.Ordinal);
        var act = () => BackgroundJobPayloadContract.Read(lowerCaseWire, type, kind);
        act.Should().Throw<ArgumentException>();
    }
}
