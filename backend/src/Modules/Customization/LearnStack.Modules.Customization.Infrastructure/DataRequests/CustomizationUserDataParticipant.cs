using LearnStack.Infrastructure.DataProtection;
using LearnStack.Modules.Customization.Infrastructure.Persistence;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LearnStack.Modules.Customization.Infrastructure.DataRequests;

/// <summary>
/// The Customization module's part in subject data requests: describe-only. Its rows are tenant definitions, not
/// the person's data; the person appears only as creator, updater or deleter, reported as
/// <c>attribution_reference</c> (<see href="../../../../../docs/decisions/0107-data-request-fulfillment-and-account-closure.md">ADR-0107 § 1, § 6</see>).
/// </summary>
/// <remarks>
/// Its export section is empty: definitions are tenant settings and the export excludes them (ADR-0107 § 3). The
/// labels and classification of the person's own custom-field values are exported by Identity, through the exact
/// definition reader contract. Erasure is the default no-op.
/// </remarks>
public sealed class CustomizationUserDataParticipant(CustomizationDbContext db, ITenantContext tenant)
    : DescribeOnlyUserDataParticipant(db, tenant)
{
    public override string Key => "customization";
}

/// <summary>Registers the Customization module's participant from its own boundary (ADR-0107 § 1).</summary>
public static class CustomizationDataRequestRegistration
{
    public static IServiceCollection AddCustomizationUserDataParticipant(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IUserDataParticipant, CustomizationUserDataParticipant>());
        return services;
    }
}
