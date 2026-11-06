using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using MediatR;
using LearnStack.Modules.Tenancy.Application.Contracts.Settings;
using LearnStack.Modules.Tenancy.Application.Settings;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LearnStack.Modules.Tenancy.Infrastructure;

public static class TenantSettingsRegistration
{
    public static IServiceCollection AddTenantSettingsReads(this IServiceCollection services)
    {
        // Registrations are values in an explicit server registry, not individual
        // DI services. Register a complete TenantSettingRegistry before this call
        // to replace the default; the constructor also supports isolated tests.
        services.AddSingleton(new ExplicitReadOnlyRequest(typeof(GetTenantInvitationPolicyQuery)));
        services.TryAddScoped<InvitationPolicyReader>();
        services.TryAddScoped<IInvitationPolicyReader>(provider => provider.GetRequiredService<InvitationPolicyReader>());
        services.TryAddScoped<IInvitationMessageContextReader>(provider => provider.GetRequiredService<InvitationPolicyReader>());
        services.TryAddScoped<InvitationPolicyHandlers>();
        services.TryAddScoped<IRequestHandler<GetTenantInvitationPolicyQuery, Result<InvitationPolicyDto>>>(provider => provider.GetRequiredService<InvitationPolicyHandlers>());
        services.TryAddScoped<IRequestHandler<SetTenantInvitationLifetimeCommand, Result<InvitationPolicyMutationResult>>>(provider => provider.GetRequiredService<InvitationPolicyHandlers>());
        services.TryAddScoped<IRequestHandler<ReadInvitationPolicyReceiptQuery, Result<None>>>(provider => provider.GetRequiredService<InvitationPolicyHandlers>());
        services.TryAddSingleton(TenantSettingRegistry.Default);
        services.TryAddScoped<ITenantSettingsAccessor, TenantSettingsAccessor>();
        return services;
    }
}
