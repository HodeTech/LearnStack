using FluentAssertions;
using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Administration;
using LearnStack.Modules.Tenancy.Application.Authorization;
using LearnStack.Modules.Tenancy.Application.Branding;
using LearnStack.Modules.Tenancy.Application.Contracts.Branding;
using LearnStack.Modules.Tenancy.Application.Contracts.Locales;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Modules.Tenancy;

public sealed class TenancyAdministrationGuardTests
{
    [Fact]
    public async Task Every_human_handler_without_an_active_application_invocation_refuses_before_lookup_or_lock()
    {
        var reader = Substitute.For<ITenancyAdministrationReader>();
        var organizations = Substitute.For<IOrganizationWriteStore>();
        var tenants = Substitute.For<ITenantWriteStore>();
        var settings = Substitute.For<ITenantSettingWriteStore>();
        var invocations = Substitute.For<IOperationInvocationContext>();
        var context = Substitute.For<ITenantContext>();
        var authorizer = Substitute.For<IProtectedOperationAuthorizer>();
        var refusals = Substitute.For<IAuthorizationRefusalReporter>();
        var locks = Substitute.For<IIdentityMutationLocks>();
        var unit = Substitute.For<IUnitOfWork>();
        var subject = Substitute.For<IAuditSubject>();
        var clock = Substitute.For<IClock>();
        var guids = Substitute.For<IGuidFactory>();
        var catalog = Substitute.For<IOperationPolicyCatalog>();
        var gate = new TenancyAdministrationAuthorization(catalog, invocations, context, authorizer, refusals, reader, locks, unit);
        var id = OrganizationId.From(Guid.Parse("11111111-1111-7111-8111-111111111111"));

        await AssertRefused(new CreateManagedOrganizationCommandHandler(organizations, reader, gate, context, subject, clock, guids),
            new CreateManagedOrganizationCommand("branch", "Branch"));
        await AssertRefused(new RenameOrganizationCommandHandler(organizations, reader, gate, context, subject, clock, guids),
            new RenameOrganizationCommand(id, 0, "Renamed"));
        await AssertRefused(new ArchiveOrganizationCommandHandler(organizations, reader, gate, context, subject, clock, guids),
            new ArchiveOrganizationCommand(id, 0));
        await AssertRefused(new SelectDefaultOrganizationCommandHandler(tenants, reader, gate, context, subject, clock, guids),
            new SelectDefaultOrganizationCommand(id, 0));
        await AssertRefused(new AddManagedTenantLocaleCommandHandler(tenants, reader, gate, context, subject, clock, guids),
            new AddManagedTenantLocaleCommand(0, "en", true, true, 0));
        await AssertRefused(new SetManagedDefaultTenantLocaleCommandHandler(tenants, reader, gate, context, subject, clock, guids),
            new SetManagedDefaultTenantLocaleCommand(0, "en"));
        await AssertRefused(new SetTenantLocaleEnabledCommandHandler(tenants, reader, gate, context, subject, clock, guids),
            new SetTenantLocaleEnabledCommand(0, "en", true));
        await AssertRefused(new RemoveTenantLocaleCommandHandler(tenants, reader, gate, context, subject, clock, guids),
            new RemoveTenantLocaleCommand(0, "en"));
        await AssertRefused(new SetManagedTenantBrandingCommandHandler(settings, reader, gate, context, subject, clock, guids),
            new SetManagedTenantBrandingCommand("{}", null));
        await AssertRefused(new ListOrganizationsQueryHandler(reader, gate), new ListOrganizationsQuery());
        await AssertRefused(new GetOrganizationDetailQueryHandler(reader, gate, context, subject), new GetOrganizationDetailQuery(id));

        foreach (var collaborator in new object[] { reader, organizations, tenants, settings, authorizer, refusals, locks, unit, subject, clock, guids, catalog })
            collaborator.ReceivedCalls().Should().BeEmpty("a direct call cannot manufacture a protected admission");
    }

    [Fact]
    public async Task Direct_cancelled_administration_preserves_cancellation_before_lookup()
    {
        var reader = Substitute.For<ITenancyAdministrationReader>();
        var gate = new TenancyAdministrationAuthorization(Substitute.For<IOperationPolicyCatalog>(),
            Substitute.For<IOperationInvocationContext>(), Substitute.For<ITenantContext>(), Substitute.For<IProtectedOperationAuthorizer>(),
            Substitute.For<IAuthorizationRefusalReporter>(), reader, Substitute.For<IIdentityMutationLocks>(), Substitute.For<IUnitOfWork>());
        var handler = new ListOrganizationsQueryHandler(reader, gate);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var act = () => handler.Handle(new ListOrganizationsQuery(), cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        reader.ReceivedCalls().Should().BeEmpty();
    }

    private static async Task AssertRefused<TRequest, TResponse>(IRequestHandler<TRequest, Result<TResponse>> handler, TRequest request)
        where TRequest : notnull, IRequest<Result<TResponse>>
    {
        var result = await handler.Handle(request, CancellationToken.None);
        result.IsFailure.Should().BeTrue(typeof(TRequest).Name);
        result.Error!.Code.Should().Be("forbidden", typeof(TRequest).Name);
    }
}
