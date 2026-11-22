using LearnStack.SharedKernel.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LearnStack.Application.DataProtection;

/// <summary>
/// The composition-root seam for subject data requests: the participant collection and the
/// demand-gated hold source default.
/// </summary>
/// <remarks>
/// <para>
/// A module registers its participant with
/// <c>services.TryAddEnumerable(ServiceDescriptor.Scoped&lt;IUserDataParticipant, TParticipant&gt;())</c>
/// from its own registration, as a module registers its <c>IAuditCatalogSource</c>; the registry is
/// scoped because a participant reads through the request's module contexts
/// (<see href="../../../../docs/decisions/0107-data-request-fulfillment-and-account-closure.md">ADR-0107 § 1</see>).
/// No participant is registered here.
/// </para>
/// <para>
/// <see cref="NoHoldSource"/> is the working default of <see cref="IDataRequestHoldSource"/>
/// (ADR-0106 § 7: owner Phase 09, trigger the first aggregate that can carry a hold); a real
/// source replaces it by registering first.
/// </para>
/// </remarks>
public static class UserDataProtectionRegistration
{
    public static IServiceCollection AddLearnStackUserDataProtection(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<IUserDataParticipantRegistry>(provider =>
            new UserDataParticipantRegistry(provider.GetServices<IUserDataParticipant>()));
        services.TryAddSingleton<IDataRequestHoldSource, NoHoldSource>();
        return services;
    }
}
