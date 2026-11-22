using LearnStack.Modules.Identity.Application.Contracts.DataRequests;
using LearnStack.Modules.Identity.Application.DataRequests;
using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Modules.Identity.Infrastructure.DataRequests;

/// <summary>
/// The export workflow's four transactional definitions and their four ordinary roots
/// (<see href="../../../../../docs/decisions/0107-data-request-fulfillment-and-account-closure.md">ADR-0107 § 2</see>):
/// export and artifact purge, each once for the tenant-wide group and once for an organization group, version 1,
/// identifier-only parameters, the transactional queue and no capacity group.
/// </summary>
/// <remarks>
/// They join the Identity module's one job registry in <c>InvitationEmailRegistration.CreateRegistry</c>, because
/// the runtime holds exactly one registry for every definition. The erasure definitions are P03-10 Step 2's.
/// </remarks>
public static class DataExportJobRegistration
{
    public static IReadOnlyList<BackgroundJobDefinitionRegistration> Definitions { get; } =
    [
        Definition(DataExportJobKeys.TenantExport.Value, BackgroundJobScope.TenantWide, typeof(DataExportJobParameters), typeof(RunTenantDataExportCommand)),
        Definition(DataExportJobKeys.OrganizationExport.Value, BackgroundJobScope.Organization, typeof(DataExportJobParameters), typeof(RunOrganizationDataExportCommand)),
        Definition(DataExportJobKeys.TenantPurge.Value, BackgroundJobScope.TenantWide, typeof(DataExportPurgeParameters), typeof(PurgeTenantDataExportCommand)),
        Definition(DataExportJobKeys.OrganizationPurge.Value, BackgroundJobScope.Organization, typeof(DataExportPurgeParameters), typeof(PurgeOrganizationDataExportCommand)),
    ];

    public static IReadOnlyList<BackgroundJobRequestDescriptor> Requests { get; } =
    [
        new(typeof(RunTenantDataExportCommand), BackgroundJobRequestClassification.Ordinary, BackgroundJobScope.TenantWide),
        new(typeof(RunOrganizationDataExportCommand), BackgroundJobRequestClassification.Ordinary, BackgroundJobScope.Organization),
        new(typeof(PurgeTenantDataExportCommand), BackgroundJobRequestClassification.Ordinary, BackgroundJobScope.TenantWide),
        new(typeof(PurgeOrganizationDataExportCommand), BackgroundJobRequestClassification.Ordinary, BackgroundJobScope.Organization),
    ];

    private static BackgroundJobDefinitionRegistration Definition(string key, BackgroundJobScope scope, Type parameters, Type root) =>
        new(key, typeof(DataExportJobHandlers), parameters, scope, BackgroundJobDefinitionReadiness.Active, "transactional",
            BackgroundJobProfileRegistration.Transactional(root));
}
