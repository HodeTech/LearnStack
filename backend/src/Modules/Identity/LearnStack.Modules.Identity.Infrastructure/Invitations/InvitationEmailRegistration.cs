using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Modules.Identity.Application.Contracts.Invitations;
using LearnStack.Modules.Identity.Application.Invitations;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Results;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LearnStack.Modules.Identity.Infrastructure.Invitations;

public static class InvitationEmailRegistration
{
    public static IServiceCollection AddInvitationEmailJobs(this IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton(_ => CreateRegistry()));
        services.TryAddScoped<IInvitationAdministrationStore, InvitationAdministrationStore>();
        services.TryAddScoped<InvitationAdministrationAuthorization>();
        services.TryAddScoped<InvitationAdministrationHandlers>();
        services.TryAddScoped<IRequestHandler<CreateInvitationCommand, Result<InvitationMutationResult>>>(provider => provider.GetRequiredService<InvitationAdministrationHandlers>());
        services.TryAddScoped<IRequestHandler<RevokeInvitationCommand, Result<InvitationMutationResult>>>(provider => provider.GetRequiredService<InvitationAdministrationHandlers>());
        services.TryAddScoped<IRequestHandler<ReissueInvitationCommand, Result<InvitationMutationResult>>>(provider => provider.GetRequiredService<InvitationAdministrationHandlers>());
        services.TryAddScoped<IRequestHandler<GetInvitationQuery, Result<InvitationDto>>>(provider => provider.GetRequiredService<InvitationAdministrationHandlers>());
        services.TryAddScoped<IRequestHandler<ListInvitationsQuery, Result<LearnStack.SharedKernel.Pagination.Page<InvitationDto>>>>(provider => provider.GetRequiredService<InvitationAdministrationHandlers>());
        services.TryAddScoped<IRequestHandler<ListInvitableRolesQuery, Result<InvitableRoleDirectory>>>(provider => provider.GetRequiredService<InvitationAdministrationHandlers>());
        services.TryAddScoped<IRequestHandler<ReadInvitationReceiptQuery, Result<None>>>(provider => provider.GetRequiredService<InvitationAdministrationHandlers>());
        services.TryAddScoped<IInvitationEmailOperation, InvitationSmtpOperation>();
        services.TryAddScoped<IInvitationEmailJobStore, InvitationEmailJobStore>();
        services.TryAddScoped<IInvitationEmailStager, InvitationEmailStager>();
        services.TryAddScoped<InvitationEmailJobHandlers>();
        services.TryAddScoped<IRequestHandler<PrepareTenantInvitationEmailCommand, Result<InvitationEmailPlan>>>(provider => provider.GetRequiredService<InvitationEmailJobHandlers>());
        services.TryAddScoped<IRequestHandler<PrepareOrganizationInvitationEmailCommand, Result<InvitationEmailPlan>>>(provider => provider.GetRequiredService<InvitationEmailJobHandlers>());
        services.TryAddScoped<IRequestHandler<FinalizeTenantInvitationEmailCommand, Result<None>>>(provider => provider.GetRequiredService<InvitationEmailJobHandlers>());
        services.TryAddScoped<IRequestHandler<FinalizeOrganizationInvitationEmailCommand, Result<None>>>(provider => provider.GetRequiredService<InvitationEmailJobHandlers>());
        return services;
    }
    public static BackgroundJobDefinitionRegistry CreateRegistry()
    {
        var modules = new[] { new BackgroundJobModuleRegistration(typeof(InvitationEmailParameters).Assembly,
            typeof(InvitationEmailJobHandlers).Assembly, typeof(InvitationSmtpOperation).Assembly) };
        var infrastructure = BackgroundJobInfrastructureRegistry.Create(
            [new("transactional", BackgroundJobWorkerPoolKind.Transactional, 1, ["transactional"]),
             new("external", BackgroundJobWorkerPoolKind.External, 2, ["external"]),
             new("control", BackgroundJobWorkerPoolKind.Control, 1, ["control"])],
            [new("identity.invitation_email", typeof(InvitationEmailJobHandlers), 4)],
            [new(typeof(IInvitationEmailOperation), typeof(InvitationSmtpOperation))], modules);
        return BackgroundJobDefinitionRegistry.Create(
            [Definition("identity.invitation_email_tenant.v1", BackgroundJobScope.TenantWide, typeof(PrepareTenantInvitationEmailCommand), typeof(FinalizeTenantInvitationEmailCommand)),
             Definition("identity.invitation_email_organization.v1", BackgroundJobScope.Organization, typeof(PrepareOrganizationInvitationEmailCommand), typeof(FinalizeOrganizationInvitationEmailCommand))],
            [.. BackgroundJobFrameworkRequests.All,
             new(typeof(PrepareTenantInvitationEmailCommand), BackgroundJobRequestClassification.Ordinary, BackgroundJobScope.TenantWide),
             new(typeof(FinalizeTenantInvitationEmailCommand), BackgroundJobRequestClassification.Ordinary, BackgroundJobScope.TenantWide),
             new(typeof(PrepareOrganizationInvitationEmailCommand), BackgroundJobRequestClassification.Ordinary, BackgroundJobScope.Organization),
             new(typeof(FinalizeOrganizationInvitationEmailCommand), BackgroundJobRequestClassification.Ordinary, BackgroundJobScope.Organization)], modules, infrastructure: infrastructure);
    }
    private static BackgroundJobDefinitionRegistration Definition(string key, BackgroundJobScope scope, Type prepare, Type finalize) =>
        new(key, typeof(InvitationEmailJobHandlers), typeof(InvitationEmailParameters), scope, BackgroundJobDefinitionReadiness.Active, "external",
            BackgroundJobProfileRegistration.External(prepare, finalize, typeof(InvitationEmailPlan), typeof(InvitationEmailOutcome), typeof(IInvitationEmailOperation)),
            capacityGroup: "identity.invitation_email");
}
