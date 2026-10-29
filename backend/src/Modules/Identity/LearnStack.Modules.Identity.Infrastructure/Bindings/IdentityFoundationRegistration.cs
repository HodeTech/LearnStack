using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Audit;
using LearnStack.Modules.Identity.Application.Bindings;
using LearnStack.Modules.Identity.Application.Authorization;
using LearnStack.Modules.Identity.Application.Contracts.Bindings;
using LearnStack.Modules.Identity.Application.Contracts.Entry;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
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
        services.TryAddScoped<IIdentityMutationLocks, IdentityMutationLocks>();
        services.TryAddSingleton<IFixedSeedPrerequisiteVerifier, DenyAllFixedSeedPrerequisiteVerifier>();
        services.TryAddScoped<ContextualIdentityStorageControl>();
        services.TryAddScoped<IRoleWriteStore, RoleWriteStore>();
        services.TryAddScoped<IRoleReadStore, RoleReadStore>();
        services.TryAddScoped<IMembershipWriteStore, MembershipWriteStore>();
        services.TryAddScoped<ISeedStateReader, SeedStateReader>();
        services.TryAddScoped<IMembershipDirectoryReader, MembershipDirectoryReader>();
        services.TryAddScoped<IMembershipAdmissionReader, MembershipAdmissionReader>();
        services.TryAddScoped<IMembershipAdministrationReader, MembershipAdministrationReader>();
        services.AddSingleton(new ExplicitReadOnlyRequest(typeof(GetOwnMembershipProfileQuery)));
        services.TryAddScoped<Func<RoleGrantAuthorization>>(provider => () => provider.GetRequiredService<RoleGrantAuthorization>());
        return services;
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
        services.TryAddScoped<ISeedIdentityBindingAdopter>(provider => provider.GetRequiredService<GlobalIdentityBindingService>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCatalogSource, IdentityAuditCatalogSource>());
        return services;
    }
}
