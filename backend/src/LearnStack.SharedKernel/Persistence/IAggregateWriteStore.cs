using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.Persistence;

/// <summary>
/// The write side of one aggregate root, for a handler that cannot see a
/// <c>DbContext</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>It exists because the dependency rules leave no alternative.</b>
/// <see href="../../../../docs/standards/01-architecture.md">Standards 01</see> lists
/// <c>Application → Infrastructure</c> under forbidden edges, every module's
/// <c>DbContext</c> and its <c>DbSet</c>s live in that module's Infrastructure project,
/// and Infrastructure already references Application — so the reverse reference is a
/// project cycle the compiler refuses. A handler reaches persistence through a port
/// declared beside it and implemented across the boundary, which is what that standard's
/// own parenthetical prescribes.
/// </para>
/// <para>
/// <b>Typed rather than named, so a rule can count it.</b>
/// <c>Cross_Aggregate_Writes_Are_Confined_To_Tenant_Provisioning</c> counts how many
/// parameters of this shape a handler takes; a naming convention would be satisfied by
/// what an author calls a class, which is not a decision anybody reviewed.
/// </para>
/// <para>
/// <b>Write-only, deliberately.</b> There is no read member, because the first caller
/// cannot use one: a provisioning transaction announces the tenant it is about to create,
/// so every query filter and every Row Level Security predicate matches a tenant that
/// does not exist yet. A read surface invented for a caller that cannot use it is a
/// surface nobody reviewed. Reads arrive with the first handler that has something to
/// read.
/// </para>
/// <para>
/// <b>This is the first persistence abstraction in the solution</b>, and six modules
/// inherit its shape. Each method persists on its own rather than deferring to a shared
/// <c>SaveChanges</c>: the EF model carries no relationships between these aggregates, so
/// the order writes reach PostgreSQL is otherwise unspecified — and provisioning depends
/// on that order.
/// </para>
/// </remarks>
public interface IAggregateWriteStore<TRoot, TId>
    where TRoot : class, IAggregateRoot<TId>
    where TId : struct, IStronglyTypedId<Guid>
{
    /// <summary>Persists a newly created aggregate.</summary>
    Task AddAsync(TRoot aggregate, CancellationToken cancellationToken = default);

    /// <summary>Persists a change to an aggregate already stored.</summary>
    Task UpdateAsync(TRoot aggregate, CancellationToken cancellationToken = default);
}
