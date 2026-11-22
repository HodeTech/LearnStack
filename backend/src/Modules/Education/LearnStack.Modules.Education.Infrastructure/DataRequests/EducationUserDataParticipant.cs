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
public sealed class EducationUserDataParticipant(EducationDbContext db, ITenantContext tenant) : IUserDataParticipant
{
    public string Key => "education";

    public Task ExportAsync(UserDataSubject subject, IUserDataExportWriter writer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(writer);
        RequireGroup(subject);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyCollection<RetainedReferenceCount>> DescribeRetainedAsync(UserDataSubject subject,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        RequireGroup(subject);
        return
        [
            new(RetainedReferenceKind.AttributionReference,
                await AttributionReferenceCounter.CountAsync(db, subject, null, cancellationToken).ConfigureAwait(false)),
        ];
    }

    private void RequireGroup(UserDataSubject subject)
    {
        if (subject.Group.TenantId != tenant.TenantId || subject.Group.OrganizationId != tenant.OrganizationId)
        {
            throw new InvalidOperationException("A participant reads only the announced group of its request.");
        }
    }
}

/// <summary>Registers the Education module's participant from its own boundary (ADR-0107 § 1).</summary>
public static class EducationDataRequestRegistration
{
    public static IServiceCollection AddEducationUserDataParticipant(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IUserDataParticipant, EducationUserDataParticipant>());
        return services;
    }
}
