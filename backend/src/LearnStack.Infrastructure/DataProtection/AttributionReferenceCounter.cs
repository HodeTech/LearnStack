using System.Linq.Expressions;
using System.Reflection;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace LearnStack.Infrastructure.DataProtection;

/// <summary>
/// Counts the rows of one module context that retain a subject as an attribution reference, for the closed retained
/// kind <c>attribution_reference</c>
/// (<see href="../../../../docs/decisions/0107-data-request-fulfillment-and-account-closure.md">ADR-0107 § 6</see>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Derived from the model, not listed.</b> An attribution property is every mapped scalar property of CLR type
/// <see cref="UserId"/> or <c>UserId?</c> on a mapped tenant-owned entity type: the creator, updater and deleter
/// columns of every <c>AuditableEntity</c>, and equally an <c>UpdatedBy</c> that is not one
/// (<c>TenantFeatureFlag</c>) or any later actor column, so an aggregate or column a module adds is counted without
/// a change here. The caller names what it reports under another kind instead: whole entity types (a request's own
/// receipt rows) and single properties that are the row's subject or party rather than its author (a Membership's
/// <c>UserId</c>, an invitation's inviter and acceptor). A named property the model does not map as a
/// <see cref="UserId"/> is a programming error, so an exclusion cannot silently go stale.
/// </para>
/// <para>
/// <b>The unit is a row.</b> A row counts once when one or more of its attribution properties name the subject: a row
/// the subject created and later updated is one retained row, not two. Another kind may count the same row for
/// another reason (an invitation the subject issued is also an <c>invitation_record</c>); kinds are reasons, not a
/// partition of rows.
/// </para>
/// <para>
/// <b>Exactly the group.</b> An organization-scoped row counts for the group whose organization it carries, and a
/// tenant-owned row without an organization counts for the tenant-wide group only, so a person in several groups
/// is never counted twice. Reads run on the module context, inside the ambient tenant transaction and under its
/// query filter and row policy, and return a count: no row, no identifier. Owned entity types are part of their
/// owner's row and are not queried on their own.
/// </para>
/// </remarks>
public static class AttributionReferenceCounter
{
    private static readonly MethodInfo CountMethod = typeof(AttributionReferenceCounter)
        .GetMethod(nameof(CountForAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo PropertyMethod = typeof(EF).GetMethod(nameof(EF.Property))!;

    /// <param name="context">The module's own context.</param>
    /// <param name="subject">The subject and group to count for.</param>
    /// <param name="excluded">Entity types reported under another kind, such as a request's own receipt.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public static Task<long> CountAsync(DbContext context, UserDataSubject subject, IReadOnlySet<Type>? excluded,
        CancellationToken cancellationToken) => CountAsync(context, subject, excluded, null, cancellationToken);

    /// <param name="context">The module's own context.</param>
    /// <param name="subject">The subject and group to count for.</param>
    /// <param name="excluded">Entity types reported under another kind, such as a request's own receipt.</param>
    /// <param name="parties">
    /// <see cref="UserId"/> properties that hold the row's subject or a party to it, reported under another kind or
    /// exported as the subject's own record, never attribution.
    /// </param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <exception cref="InvalidOperationException">A named party property is not a mapped <see cref="UserId"/> property.</exception>
    public static async Task<long> CountAsync(DbContext context, UserDataSubject subject, IReadOnlySet<Type>? excluded,
        IReadOnlyCollection<(Type Entity, string Property)>? parties, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(subject);
        return await SumAsync(context, subject, Plan(context.Model, excluded, parties), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Counts the rows of the named entity types in which any mapped <see cref="UserId"/> column names the subject, once
    /// per row and exactly in the group: the rows a caller reports under a kind of its own (a request's receipts, as
    /// <c>request_receipt</c>) and excludes from <c>attribution_reference</c> by passing the same types there.
    /// </summary>
    /// <remarks>
    /// Every <see cref="UserId"/> column counts, the subject, the requester and every author column alike, so a person
    /// who filed, cancelled or revoked a request about someone else is told the row survives, and a column a later
    /// change adds is counted without a change here.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A named type is not a mapped tenant-owned entity with a <see cref="UserId"/> column.</exception>
    public static async Task<long> CountNamingRowsAsync(DbContext context, UserDataSubject subject, IReadOnlyCollection<Type> types,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(subject);
        return await SumAsync(context, subject, NamingPlan(context.Model, types), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The <see cref="UserId"/> columns per named entity type, in a stable order: what
    /// <see cref="CountNamingRowsAsync"/> reads, without reading it.
    /// </summary>
    /// <exception cref="InvalidOperationException">A named type is not a mapped tenant-owned entity with a <see cref="UserId"/> column.</exception>
    public static IReadOnlyList<(Type Entity, IReadOnlyList<string> Properties)> NamingPlan(IModel model, IReadOnlyCollection<Type> types)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(types);
        return
        [
            .. types.Distinct().OrderBy(type => type.FullName, StringComparer.Ordinal).Select(type =>
            {
                var entity = model.FindEntityType(type);
                IReadOnlyList<string> properties = entity is null || entity.IsOwned() || !typeof(ITenantOwned).IsAssignableFrom(type)
                    ? []
                    : [.. entity.GetProperties().Where(property => IsUserId(property.ClrType)).Select(property => property.Name)
                        .Order(StringComparer.Ordinal)];
                return properties.Count == 0
                    ? throw new InvalidOperationException("A named type is a mapped tenant-owned entity with a UserId column.")
                    : (type, properties);
            }),
        ];
    }

    private static async Task<long> SumAsync(DbContext context, UserDataSubject subject,
        IReadOnlyList<(Type Entity, IReadOnlyList<string> Properties)> plan, CancellationToken cancellationToken)
    {
        long total = 0;
        foreach (var (type, properties) in plan)
        {
            var scoped = typeof(IOrganizationScoped).IsAssignableFrom(type);
            if (!scoped && !subject.Group.IsTenantWide)
            {
                continue;
            }

            var count = (Task<long>)CountMethod.MakeGenericMethod(type)
                .Invoke(null, [context, properties, subject.SubjectUserId, subject.Group.OrganizationId, scoped, cancellationToken])!;
            total = checked(total + await count.ConfigureAwait(false));
        }

        return total;
    }

    /// <summary>
    /// The attribution properties per counted entity type, in a stable order: what <see cref="CountAsync(DbContext, UserDataSubject, IReadOnlySet{Type}?, IReadOnlyCollection{ValueTuple{Type, string}}?, CancellationToken)"/>
    /// reads, without reading it.
    /// </summary>
    public static IReadOnlyList<(Type Entity, IReadOnlyList<string> Properties)> Plan(IModel model, IReadOnlySet<Type>? excluded,
        IReadOnlyCollection<(Type Entity, string Property)>? parties)
    {
        ArgumentNullException.ThrowIfNull(model);
        var entities = model.GetEntityTypes().Where(entity => !entity.IsOwned() && !entity.HasSharedClrType).ToList();
        var omitted = new HashSet<(Type, string)>();
        foreach (var party in parties ?? [])
        {
            var property = entities.FirstOrDefault(entity => entity.ClrType == party.Entity)?.FindProperty(party.Property);
            if (property is null || !IsUserId(property.ClrType))
            {
                throw new InvalidOperationException("A party property names a mapped UserId property of the context.");
            }

            omitted.Add((party.Entity, party.Property));
        }

        return
        [
            .. entities
                .Where(entity => typeof(ITenantOwned).IsAssignableFrom(entity.ClrType) && excluded?.Contains(entity.ClrType) != true)
                .Select(entity => (Entity: entity.ClrType, Properties: (IReadOnlyList<string>)
                [
                    .. entity.GetProperties().Where(property => IsUserId(property.ClrType) && !omitted.Contains((entity.ClrType, property.Name)))
                        .Select(property => property.Name).Order(StringComparer.Ordinal),
                ]))
                .Where(entry => entry.Properties.Count > 0)
                .DistinctBy(entry => entry.Entity)
                .OrderBy(entry => entry.Entity.FullName, StringComparer.Ordinal),
        ];
    }

    private static bool IsUserId(Type type) => type == typeof(UserId) || type == typeof(UserId?);

    private static async Task<long> CountForAsync<T>(DbContext context, IReadOnlyList<string> properties, UserId subject,
        OrganizationId? organization, bool scoped, CancellationToken cancellationToken)
        where T : class
    {
        IQueryable<T> rows = context.Set<T>().AsNoTracking();
        if (scoped)
        {
            rows = rows.Where(row => EF.Property<OrganizationId?>(row, nameof(IOrganizationScoped.OrganizationId)) == organization);
        }

        // One predicate per row: any attribution property naming the subject. The subject travels as a query
        // parameter through the closure, never as a literal in the SQL text.
        var person = new Person(subject);
        var row = Expression.Parameter(typeof(T), "row");
        Expression? names = null;
        var entity = context.Model.FindEntityType(typeof(T))!;
        foreach (var name in properties)
        {
            var type = entity.FindProperty(name)!.ClrType;
            var access = Expression.Call(PropertyMethod.MakeGenericMethod(type), row, Expression.Constant(name));
            var value = Expression.Property(Expression.Constant(person), type == typeof(UserId) ? nameof(Person.Value) : nameof(Person.Nullable));
            var match = Expression.Equal(access, value);
            names = names is null ? match : Expression.OrElse(names, match);
        }

        return await rows.LongCountAsync(Expression.Lambda<Func<T, bool>>(names!, row), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The subject as the closure the predicate reads, so EF binds it as a parameter.</summary>
    private sealed class Person(UserId value)
    {
        public UserId Value { get; } = value;

        public UserId? Nullable { get; } = value;
    }
}
