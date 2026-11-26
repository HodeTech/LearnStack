using LearnStack.Infrastructure.DataProtection;
using LearnStack.Modules.Education.Infrastructure.Persistence;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LearnStack.Modules.Education.Infrastructure.DataRequests;

/// <summary>
/// The Education module's part in subject data requests: describe-only. Courses and lessons are tenant content;
/// the person appears only as their creator, updater or deleter, reported as <c>attribution_reference</c>
/// (<see href="../../../../../docs/decisions/0107-data-request-fulfillment-and-account-closure.md">ADR-0107 § 1, § 6</see>).
/// </summary>
/// <remarks>
/// Its export section is empty: no Education table holds the person's own data yet. Phase 07's first
/// learner-behaviour writer extends this participant before it ships (ADR-0107 § 1). Erasure is the default no-op.
/// </remarks>
public sealed class EducationUserDataParticipant(EducationDbContext db, ITenantContext tenant)
    : DescribeOnlyUserDataParticipant(db, tenant)
{
    public override string Key => "education";
}

/// <summary>Registers the Education module's participant and user-reference locator from its own boundary (ADR-0107 § 1, ADR-0108 § 7).</summary>
public static class EducationDataRequestRegistration
{
    public static IServiceCollection AddEducationUserDataParticipant(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IUserDataParticipant, EducationUserDataParticipant>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IUserReferenceLocator, EducationUserReferenceLocator>());
        return services;
    }
}
