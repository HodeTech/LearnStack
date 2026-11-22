using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Infrastructure.Invitations;
using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Audit;
using LearnStack.Modules.Identity.Application.Bindings;
using LearnStack.Modules.Identity.Application.Authorization;
using LearnStack.Modules.Identity.Application.Contracts.Bindings;
using LearnStack.Modules.Identity.Application.Contracts.Invitations;
using LearnStack.Modules.Identity.Application.Invitations;
using LearnStack.Modules.Identity.Application.Contracts.Entry;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Application.Contracts.CustomFields;
using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Identity.Application.Contracts.Federation;
using LearnStack.Modules.Identity.Application.Federation;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Application.DataRequests;
using LearnStack.Modules.Identity.Infrastructure.DataRequests;
using LearnStack.Modules.Identity.Application.Contracts.DataRequests;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Results;
using MediatR;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace LearnStack.Modules.Identity.Infrastructure.Bindings;

public static class IdentityFoundationRegistration
{
    public static IServiceCollection AddIdentityContextualFoundation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddInvitationEmailJobs();
        services.TryAddScoped<IInvitationEntryLookup, InvitationEntryLookup>();
        services.TryAddScoped<InvitationAcceptanceReadStore>();
        services.TryAddScoped<IInvitationAcceptanceReadStore>(provider => provider.GetRequiredService<InvitationAcceptanceReadStore>());
        services.TryAddScoped<IInvitationDelegationReader>(provider => provider.GetRequiredService<InvitationAcceptanceReadStore>());
        services.TryAddScoped<IInvitationWriteStore, InvitationWriteStore>();
        services.TryAddScoped<InvitationEntryAuthorization>();
        services.AddSingleton(new ExplicitReadOnlyRequest(typeof(InspectInvitationQuery)));
        services.AddSingleton(new ExplicitReadOnlyRequest(typeof(GetOwnInvitationResultQuery)));
        services.TryAddScoped<IIdentityMutationLocks, IdentityMutationLocks>();
        services.TryAddSingleton<IFixedSeedPrerequisiteVerifier, DenyAllFixedSeedPrerequisiteVerifier>();
        services.TryAddScoped<ContextualIdentityStorageControl>();
        services.TryAddScoped<IRoleWriteStore, RoleWriteStore>();
        services.TryAddScoped<IRoleReadStore, RoleReadStore>();
        services.TryAddScoped<IMembershipWriteStore, MembershipWriteStore>();
        services.TryAddScoped<IOperatorSubjectResolver, OperatorSubjectResolver>();
        services.TryAddScoped<ISeedStateReader, SeedStateReader>();
        services.TryAddScoped<IMembershipDirectoryReader, MembershipDirectoryReader>();
        services.TryAddScoped<IMembershipAdmissionReader, MembershipAdmissionReader>();
        services.TryAddScoped<IMembershipAdministrationReader, MembershipAdministrationReader>();
        services.TryAddScoped<IIdentityReceiptReader, IdentityReceiptReader>();
        services.TryAddScoped<IIdentityAdministrationReadStore, IdentityAdministrationReadStore>();
        // ADR-0099: the connection registry, its authorization and the intent-first runner. Both ports default to
        // fail-closed implementations (every call unavailable); the API composition root replaces them with the
        // confined adapter and guarded egress fetcher only when the federation admin client is configured.
        services.TryAddScoped<IFederationConnectionStore, FederationConnectionStore>();
        services.TryAddScoped<IPublicSignInReader, PublicSignInReader>();
        services.TryAddScoped<FederationAuthorization>();
        services.TryAddScoped<FederationOperationRunner>();
        services.TryAddSingleton(FederationOperationBudgets.Default);
        services.TryAddSingleton<IFederationProvider, UnconfiguredFederationProvider>();
        // The operator's port (Amendment 10) is its own: the tenant runner never holds it, and it too fails closed.
        services.TryAddSingleton<IFederationOperator, UnconfiguredFederationOperator>();
        services.TryAddSingleton<IFederationEgressFetcher, UnconfiguredFederationEgressFetcher>();
        services.TryAddSingleton<IIdentityProviderAccountRemoval, RefusingIdentityProviderAccountRemoval>();
        services.AddSingleton(new ExplicitReadOnlyRequest(typeof(GetFederationConnectionQuery)));
        services.AddSingleton(new ExplicitReadOnlyRequest(typeof(GetOwnMembershipProfileQuery)));
        services.AddSingleton(new ExplicitReadOnlyRequest(typeof(GetOwnMembershipContextsQuery)));
        services.AddSingleton(new ExplicitReadOnlyRequest(typeof(GetOwnCapabilitiesQuery)));
        services.AddSingleton(new ExplicitReadOnlyRequest(typeof(GetOwnMembershipCustomFieldsQuery)));
        // ADR-0106 § 9: retained discovery and status reads run on a physically read-only owner, as their witness requires.
        services.AddSingleton(new ExplicitReadOnlyRequest(typeof(ListRetainedDataRequestScopesQuery)));
        services.AddSingleton(new ExplicitReadOnlyRequest(typeof(ListRetainedDataRequestsQuery)));
        services.TryAddScoped<Func<RoleGrantAuthorization>>(provider => () => provider.GetRequiredService<RoleGrantAuthorization>());
        services.AddIdentityDataRequests();
        return services;
    }

    /// <summary>
    /// ADR-0107 § 1–§ 5: the request store, the default artifact store, the export, purge and erasure job roots, the
    /// fulfilment scheduler, the failure reconciler, the erasure service and the Identity participant. The cipher that
    /// seals the chunks and the bundle builder are the composition root's.
    /// </summary>
    private static void AddIdentityDataRequests(this IServiceCollection services)
    {
        services.TryAddScoped<IDataRequestStore, DataRequestStore>();
        services.TryAddScoped<IDataExportArtifactStore, PostgresDataExportArtifactStore>();
        services.TryAddSingleton<DataExportCustodyRefusals>();
        services.TryAddScoped<IDataRequestJobPhaseReader, DataRequestJobPhaseReader>();
        services.TryAddScoped<IDataRequestFulfillmentScheduler, DataRequestFulfillmentScheduler>();
        services.TryAddScoped<DataRequestFailureReconciler>();
        services.TryAddScoped<DataExportJobHandlers>();
        services.TryAddScoped<IRequestHandler<RunTenantDataExportCommand, Result<None>>>(provider => provider.GetRequiredService<DataExportJobHandlers>());
        services.TryAddScoped<IRequestHandler<RunOrganizationDataExportCommand, Result<None>>>(provider => provider.GetRequiredService<DataExportJobHandlers>());
        services.TryAddScoped<IRequestHandler<PurgeTenantDataExportCommand, Result<None>>>(provider => provider.GetRequiredService<DataExportJobHandlers>());
        services.TryAddScoped<IRequestHandler<PurgeOrganizationDataExportCommand, Result<None>>>(provider => provider.GetRequiredService<DataExportJobHandlers>());
        // ADR-0107 § 5: the erasure job's two roots, and the Identity participant's erasure it runs.
        services.TryAddScoped<DataErasureJobHandlers>();
        services.TryAddScoped<IRequestHandler<RunTenantDataErasureCommand, Result<None>>>(provider => provider.GetRequiredService<DataErasureJobHandlers>());
        services.TryAddScoped<IRequestHandler<RunOrganizationDataErasureCommand, Result<None>>>(provider => provider.GetRequiredService<DataErasureJobHandlers>());
        services.TryAddScoped<DataErasureService>();
        services.TryAddScoped<MembershipFieldRevisionReader>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IUserDataParticipant, IdentityUserDataParticipant>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IUserReferenceLocator, IdentityUserReferenceLocator>());
        // ADR-0106 § 7, ADR-0107 § 4: the live-session request API. The default eligibility checks structure only; the
        // hold source is the composition root's NoHoldSource. The session binding answers nothing until the API
        // composition replaces it with the validated-credential binding, so no other host mints or honours a ticket.
        services.TryAddScoped<IDataRequestReader, DataRequestReader>();
        services.TryAddScoped<IDataRequestEligibility, DataRequestEligibility>();
        services.TryAddSingleton<IDataExportSessionBinding, UnavailableDataExportSessionBinding>();
        services.TryAddScoped<DataRequestAuthorization>();
        services.TryAddScoped<LiveDataRequestAdmission>();
        services.TryAddScoped<RetainedDataRequestAdmission>();
        services.TryAddScoped<DataRequestService>();
        services.TryAddScoped<IDataExportTicketStore, DataExportTicketStore>();
        services.TryAddScoped<DataExportDownloadService>();
    }

    /// <summary>Registers internal consumers only; qualification is an explicit startup act before admission activation.</summary>
    public static IServiceCollection AddIdentityGlobalFoundation(this IServiceCollection services,
        IdentityAuthorityRegistration? registration = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<IGlobalIdentityUnitOfWork>(provider =>
            provider.GetRequiredService<IUnitOfWork>() as IGlobalIdentityUnitOfWork
            ?? throw new InvalidOperationException("The registered unit of work lacks global identity ownership."));
        services.TryAddScoped<IGlobalIdentityScopeAccessor, GlobalIdentityScopeAccessor>();
        services.AddModuleDbContext<IdentityDbContext>();
        services.TryAddScoped<IUserBindingStore, UserBindingStore>();
        services.TryAddScoped<CreateUserBindingHandler>();
        services.TryAddScoped<Func<IUserBindingStore>>(provider => () => provider.GetRequiredService<IUserBindingStore>());
        services.TryAddScoped<Func<CreateUserBindingHandler>>(provider => () => provider.GetRequiredService<CreateUserBindingHandler>());
        services.TryAddSingleton(provider => new IdentityAuthorityQualification(
            provider.GetRequiredService<Lazy<NpgsqlDataSource>>(), registration));
        services.TryAddSingleton<ISeedIdentityFoundationAdmission, DenyAllSeedIdentityFoundationAdmission>();
        services.TryAddSingleton<IIdentitySelfEntryAdmission, DenyAllIdentitySelfEntryAdmission>();
        services.TryAddSingleton<INativeSessionAssociationSource, DenyAllNativeSessionAssociationSource>();
        services.TryAddScoped<GlobalIdentityBindingService>();
        services.TryAddScoped<IIdentityBindingReader>(provider => provider.GetRequiredService<GlobalIdentityBindingService>());
        services.TryAddScoped<IIdentitySelfBootstrapper>(provider => provider.GetRequiredService<GlobalIdentityBindingService>());
        services.TryAddScoped<IIdentitySelfStateReader>(provider => provider.GetRequiredService<GlobalIdentityBindingService>());
        services.TryAddScoped<IIdentitySelfDiscovery>(provider => provider.GetRequiredService<GlobalIdentityBindingService>());
        // ADR-0099 Amendment 6 (H): the self read's one host-only, read-only federation alias setter.
        services.TryAddScoped<IFederationHostAliasReader, FederationHostAliasReader>();
        services.TryAddScoped<ISeedIdentityBindingAdopter>(provider => provider.GetRequiredService<GlobalIdentityBindingService>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCatalogSource, IdentityAuditCatalogSource>());
        return services;
    }
}
