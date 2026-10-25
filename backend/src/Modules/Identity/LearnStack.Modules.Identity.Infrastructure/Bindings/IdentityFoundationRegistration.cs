using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Audit;
using LearnStack.Modules.Identity.Application.Bindings;
using LearnStack.Modules.Identity.Application.Contracts.Bindings;
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
        services.TryAddScoped<GlobalIdentityBindingService>();
        services.TryAddScoped<IIdentityBindingReader>(provider => provider.GetRequiredService<GlobalIdentityBindingService>());
        services.TryAddScoped<ISeedIdentityBindingAdopter>(provider => provider.GetRequiredService<GlobalIdentityBindingService>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCatalogSource, IdentityAuditCatalogSource>());
        return services;
    }
}
