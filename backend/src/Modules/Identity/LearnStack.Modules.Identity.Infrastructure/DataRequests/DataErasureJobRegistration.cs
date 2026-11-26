using LearnStack.Modules.Identity.Application.Contracts.DataRequests;
using LearnStack.Modules.Identity.Application.DataRequests;
using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Modules.Identity.Infrastructure.DataRequests;

/// <summary>
/// The tenant-side erasure's two transactional definitions and their two ordinary roots
/// (<see href="../../../../../docs/decisions/0107-data-request-fulfillment-and-account-closure.md">ADR-0107 § 2, § 5</see>):
/// once for the tenant-wide group and once for an organization group, version 1, identifier-only parameters, the
/// transactional queue and no capacity group. Each is submitted with <c>ScheduleAsync</c> at the end of the request's
/// cancellable window.
/// </summary>
/// <remarks>
/// They join the Identity module's one job registry in <c>InvitationEmailRegistration.CreateRegistry</c>, beside the
/// export workflow's, because the runtime holds exactly one registry for every definition.
/// </remarks>
public static class DataErasureJobRegistration
{
    public static IReadOnlyList<BackgroundJobDefinitionRegistration> Definitions { get; } =
    [
        Definition(DataErasureJobKeys.TenantErasure.Value, BackgroundJobScope.TenantWide, typeof(RunTenantDataErasureCommand)),
        Definition(DataErasureJobKeys.OrganizationErasure.Value, BackgroundJobScope.Organization, typeof(RunOrganizationDataErasureCommand)),
    ];

    public static IReadOnlyList<BackgroundJobRequestDescriptor> Requests { get; } =
    [
        new(typeof(RunTenantDataErasureCommand), BackgroundJobRequestClassification.Ordinary, BackgroundJobScope.TenantWide),
        new(typeof(RunOrganizationDataErasureCommand), BackgroundJobRequestClassification.Ordinary, BackgroundJobScope.Organization),
    ];

    private static BackgroundJobDefinitionRegistration Definition(string key, BackgroundJobScope scope, Type root) =>
        new(key, typeof(DataErasureJobHandlers), typeof(DataErasureJobParameters), scope, BackgroundJobDefinitionReadiness.Active,
            "transactional", BackgroundJobProfileRegistration.Transactional(root));
}
