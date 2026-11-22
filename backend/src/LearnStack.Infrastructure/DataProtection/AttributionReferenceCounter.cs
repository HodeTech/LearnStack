using System.Reflection;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LearnStack.Infrastructure.DataProtection;

/// <summary>
/// Counts a subject's creator, updater and deleter attribution on every mapped tenant-owned aggregate of one
/// module context, for the closed retained kind <c>attribution_reference</c>
/// (<see href="../../../../docs/decisions/0107-data-request-fulfillment-and-account-closure.md">ADR-0107 § 6</see>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Derived from the model, not listed.</b> Every entity type the context maps that derives
/// <see cref="AuditableEntity{TId}"/> and is tenant-owned is counted, so an aggregate a module adds later is
/// counted without a change here.
/// </para>
/// <para>
/// <b>Exactly the group.</b> An organization-scoped row counts for the group whose organization it carries, and a
/// tenant-owned row without an organization counts for the tenant-wide group only, so a person in several groups
/// is never counted twice. Reads run on the module context, inside the ambient tenant transaction and under its
/// query filter and row policy, and return a count: no row, no identifier.
/// </para>
/// </remarks>
public static class AttributionReferenceCounter
{
    private static readonly MethodInfo CountMethod = typeof(AttributionReferenceCounter)
        .GetMethod(nameof(CountForAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <param name="context">The module's own context.</param>
    /// <param name="subject">The subject and group to count for.</param>
    /// <param name="excluded">Entity types reported under another kind, such as a request's own receipt.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public static async Task<long> CountAsync(DbContext context, UserDataSubject subject, IReadOnlySet<Type>? excluded,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(subject);
        long total = 0;
        foreach (var type in context.Model.GetEntityTypes()
                     .Where(entity => !entity.IsOwned() && !entity.HasSharedClrType)
                     .Select(entity => entity.ClrType)
                     .Where(clr => typeof(ITenantOwned).IsAssignableFrom(clr) && IsAuditable(clr) && excluded?.Contains(clr) != true)
                     .Distinct()
                     .OrderBy(clr => clr.FullName, StringComparer.Ordinal))
        {
            var scoped = typeof(IOrganizationScoped).IsAssignableFrom(type);
            if (!scoped && !subject.Group.IsTenantWide)
            {
                continue;
            }

            var count = (Task<long>)CountMethod.MakeGenericMethod(type)
                .Invoke(null, [context, subject.SubjectUserId, subject.Group.OrganizationId, scoped, cancellationToken])!;
            total = checked(total + await count.ConfigureAwait(false));
        }

        return total;
    }

    private static bool IsAuditable(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(AuditableEntity<>))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<long> CountForAsync<T>(DbContext context, UserId subject, OrganizationId? organization,
        bool scoped, CancellationToken cancellationToken)
        where T : class
    {
        IQueryable<T> rows = context.Set<T>().AsNoTracking();
        if (scoped)
        {
            rows = rows.Where(row => EF.Property<OrganizationId?>(row, nameof(IOrganizationScoped.OrganizationId)) == organization);
        }

        UserId? person = subject;
        var created = await rows.LongCountAsync(row => EF.Property<UserId>(row, "CreatedBy") == subject, cancellationToken)
            .ConfigureAwait(false);
        var updated = await rows.LongCountAsync(row => EF.Property<UserId?>(row, "UpdatedBy") == person, cancellationToken)
            .ConfigureAwait(false);
        var deleted = await rows.LongCountAsync(row => EF.Property<UserId?>(row, "DeletedBy") == person, cancellationToken)
            .ConfigureAwait(false);
        return checked(created + updated + deleted);
    }
}
