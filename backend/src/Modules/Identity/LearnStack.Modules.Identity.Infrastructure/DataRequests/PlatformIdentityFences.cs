using LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Modules.Identity.Infrastructure.DataRequests;

/// <summary>
/// The fences of a global account closure on the platform connection (ADR-0107 § 7): the person's exclusive User key, then
/// the exclusive key of every tenant in which the person holds a Membership of any status, ascending by key, each a
/// transaction-scoped advisory lock taken through the platform scope's statement facade.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not <see cref="IIdentityMutationLocks"/>.</b> That port fences one tenant inside a tenant-announced application
/// transaction. A closure spans every tenant of the person on a connection that announces nothing, so this is a separate
/// acquirer with the same key derivation (<see cref="IdentityAdvisoryLockKey"/>) and the same canonical order: Users before
/// tenants, each family ascending by its key. Its census leg sits beside
/// <c>Identity_Membership_Invariants_Use_Fresh_Ordered_Locks</c>, and this file is the one place that takes a User key and
/// then tenant keys on a platform connection.
/// </para>
/// <para>
/// <b>The participants are read under the User fence.</b> A Membership is admitted only under the person's exclusive User
/// key and is never deleted, so once that key is held the set of tenants cannot grow before the tenant keys are taken. The
/// database is the witness of all of it: the <c>users</c> trigger exception and the request guard's closure arrows read the
/// keys this connection holds in <c>pg_locks</c>, and refuse the change without them.
/// </para>
/// <para>
/// <b>Bounded, and only for the transaction.</b> The wait for every key is bounded by the transaction's own
/// <c>lock_timeout</c>, set first; a key not granted in time fails the statement with <c>55P03</c>, which leaves the scope
/// to be abandoned, and the owner answers it as the retryable unavailable. Transaction-scoped keys end at the commit or the
/// rollback, so a second transaction (Finalize's) takes them again; no session-level lock is ever taken.
/// </para>
/// </remarks>
internal static class PlatformIdentityFences
{
    /// <summary>The most a closure waits for any one of its keys.</summary>
    internal static readonly TimeSpan LockWait = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The most participant groups one closure is read with: the bound of the platform lifecycle read. A person reaching it
    /// is refused as a defect rather than fenced partially.
    /// </summary>
    internal const int MaximumGroups = IIdentityScopeLifecycleReader.MaximumPlatformScopes;

    private const string BoundTheWait = "SELECT pg_catalog.set_config('lock_timeout', '3s', true)";

    private const string TakeExclusive = "SELECT pg_catalog.pg_advisory_xact_lock(@key)";

    // Every group of the person, a Revoked tombstone's included, from the Membership user index; the platform role reads the
    // six granted columns of memberships and nothing else.
    private const string Groups = """
        SELECT DISTINCT member.tenant_id, member.organization_id FROM public.memberships AS member
        WHERE member.user_id = @subject
        ORDER BY member.tenant_id, member.organization_id NULLS FIRST
        LIMIT @limit
        """;

    /// <summary>
    /// Takes the person's User fence, reads the participant groups under it, and takes every participant tenant's fence
    /// (with the closure request's own tenant) ascending by key, on <paramref name="handle"/>'s transaction.
    /// </summary>
    /// <param name="handle">An entered platform scope; its transaction holds the keys until it ends.</param>
    /// <param name="subject">The person.</param>
    /// <param name="requestTenant">The tenant the closure request lives in, which the request guard also fences.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The groups read under the User fence and the tenants fenced, in the order they were taken.</returns>
    internal static async Task<PlatformIdentityFenceSet> AcquireAsync(
        IPlatformAdminScopeHandle handle, UserId subject, TenantId requestTenant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var userKey = IdentityAdvisoryLockKey.ForUser(subject);
        _ = IdentityAdvisoryLockKey.ForTenant(requestTenant);

        await handle.ExecuteAsync(BoundTheWait, null, cancellationToken);
        await handle.ExecuteAsync(TakeExclusive, [new PlatformSqlParameter("key", userKey)], cancellationToken);

        var groups = await ReadGroupsAsync(handle, subject, cancellationToken);
        var tenants = groups.Select(group => group.TenantId).Append(requestTenant).Distinct()
            .OrderBy(IdentityAdvisoryLockKey.ForTenant).ToArray();
        foreach (var tenant in tenants)
        {
            await handle.ExecuteAsync(
                TakeExclusive, [new PlatformSqlParameter("key", IdentityAdvisoryLockKey.ForTenant(tenant))], cancellationToken);
        }

        return new PlatformIdentityFenceSet(subject, groups, tenants);
    }

    /// <summary>
    /// The person's participant groups. Fenced, inside <see cref="AcquireAsync"/>; unfenced only for a person whose User is
    /// no longer Active, whom no Membership can be admitted for.
    /// </summary>
    /// <exception cref="InvalidOperationException">The person has no Membership, or more groups than the bound.</exception>
    internal static async Task<IReadOnlyList<ClosureParticipantGroup>> ReadGroupsAsync(
        IPlatformAdminScopeHandle handle, UserId subject, CancellationToken cancellationToken)
    {
        var groups = await handle.QueryAsync(
            Groups,
            [new PlatformSqlParameter("subject", subject.Value), new PlatformSqlParameter("limit", MaximumGroups + 1)],
            reader => new ClosureParticipantGroup(
                TenantId.From(reader.GetGuid(0)), reader.IsDBNull(1) ? null : OrganizationId.From(reader.GetGuid(1))),
            cancellationToken);
        if (groups.Count == 0)
        {
            // A closure is filed in a group the person holds a Membership in, and Memberships are never deleted.
            throw new InvalidOperationException("A closure's person holds no Membership; the act is abandoned.");
        }

        if (groups.Count > MaximumGroups)
        {
            throw new InvalidOperationException(
                "A closure's person holds Memberships in more groups than one closure fences; the act is abandoned.");
        }

        return groups;
    }
}

/// <summary>What <see cref="PlatformIdentityFences.AcquireAsync"/> fenced.</summary>
/// <param name="Subject">The person whose User key is held.</param>
/// <param name="Groups">The participant groups, read under the User key.</param>
/// <param name="Tenants">The tenants whose keys are held, ascending by key.</param>
internal sealed record PlatformIdentityFenceSet(
    UserId Subject, IReadOnlyList<ClosureParticipantGroup> Groups, IReadOnlyList<TenantId> Tenants)
{
    /// <summary>Never prints the person.</summary>
    public override string ToString() => nameof(PlatformIdentityFenceSet);
}
