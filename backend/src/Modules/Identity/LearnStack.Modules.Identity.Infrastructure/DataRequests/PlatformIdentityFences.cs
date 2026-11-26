using LearnStack.Modules.Identity.Application.DataRequests;
using LearnStack.SharedKernel.DataProtection;
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
/// key and is never deleted, so once that key is held the set of tenants, and the Memberships counted in each, cannot grow
/// before the tenant keys are taken. That holds only for a statement whose snapshot is taken after the key was granted,
/// which is what READ COMMITTED gives every statement; the acquirer refuses any other isolation before it takes a key. The
/// database is the witness of the rest: the <c>users</c> trigger exception and the request guard's closure arrows read the
/// keys this connection holds in <c>pg_locks</c>, and refuse the change without them.
/// </para>
/// <para>
/// <b>The size is read with the participants.</b> One grouped statement over the person's own Memberships answers every
/// participant group and how many Memberships of any status it holds, so the closure's bounds
/// (<see cref="ClosureLimits"/>) are judged under the User fence, before any tenant key: a person past them takes no tenant
/// key at all.
/// </para>
/// <para>
/// <b>Two forms.</b> <see cref="AcquireAsync"/> takes every key, for a transaction that changes the User or a closure's
/// request, which the database admits only under all of them. <see cref="AcquireUserAsync"/> takes the User key alone, for
/// Finalize's first transaction, which changes neither and only redacts: the participant tenants' own identity writes do
/// not wait for its redaction.
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
    /// The most participant groups one closure is admitted for and reads (<see cref="ClosureLimits.MaximumParticipantGroups"/>);
    /// the read returns one more, so a person past it is seen and never fenced partially.
    /// </summary>
    internal const int MaximumGroups = ClosureLimits.MaximumParticipantGroups;

    private const string ReadCommitted = "SELECT pg_catalog.current_setting('transaction_isolation')";

    private const string BoundTheWait = "SELECT pg_catalog.set_config('lock_timeout', '3s', true)";

    private const string TakeExclusive = "SELECT pg_catalog.pg_advisory_xact_lock(@key)";

    /// <summary>
    /// Every group of the person, a Revoked tombstone's included, with how many Memberships of any status the person holds
    /// there: one grouped statement over the person's own rows through <c>ix_memberships_user_id</c>, whose cost is those
    /// rows, never a group's or a tenant's. The platform role reads the six granted columns of memberships and nothing else.
    /// Exposed so that a test can ask for its plan.
    /// </summary>
    internal static string ParticipantsSql { get; } = """
        SELECT member.tenant_id, member.organization_id, count(*) FROM public.memberships AS member
        WHERE member.user_id = @subject
        GROUP BY member.tenant_id, member.organization_id
        ORDER BY member.tenant_id, member.organization_id NULLS FIRST
        LIMIT @limit
        """;

    /// <summary>
    /// Takes the person's User fence, reads the participant groups under it, and, when the closure is within its bounds,
    /// takes every participant tenant's fence (with the closure request's own tenant) ascending by key, on
    /// <paramref name="handle"/>'s transaction. Past the bounds it takes no tenant key and says so.
    /// </summary>
    /// <param name="handle">An entered platform scope; its transaction holds the keys until it ends.</param>
    /// <param name="subject">The person.</param>
    /// <param name="requestTenant">The tenant the closure request lives in, which the request guard also fences.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The groups read under the User fence and the tenants fenced, in the order they were taken.</returns>
    internal static Task<PlatformIdentityFenceSet> AcquireAsync(
        IPlatformAdminScopeHandle handle, UserId subject, TenantId requestTenant, CancellationToken cancellationToken) =>
        AcquireCoreAsync(handle, subject, requestTenant, cancellationToken);

    /// <summary>
    /// Takes the person's User fence alone and reads the participant groups under it: for a transaction that moves no User
    /// and no closure request, so no participant tenant waits for it.
    /// </summary>
    /// <param name="handle">An entered platform scope; its transaction holds the key until it ends.</param>
    /// <param name="subject">The person.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The groups read under the User fence; no tenant is fenced.</returns>
    internal static Task<PlatformIdentityFenceSet> AcquireUserAsync(
        IPlatformAdminScopeHandle handle, UserId subject, CancellationToken cancellationToken) =>
        AcquireCoreAsync(handle, subject, requestTenant: null, cancellationToken);

    /// <summary>
    /// The one body of both forms: the isolation check, the bound, the User key, the participants under it, and, for a
    /// request tenant within the bounds, the tenant keys ascending.
    /// </summary>
    private static async Task<PlatformIdentityFenceSet> AcquireCoreAsync(
        IPlatformAdminScopeHandle handle, UserId subject, TenantId? requestTenant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var userKey = IdentityAdvisoryLockKey.ForUser(subject);
        if (requestTenant is { } own)
        {
            _ = IdentityAdvisoryLockKey.ForTenant(own);
        }

        var isolation = await handle.QueryAsync(ReadCommitted, null, reader => reader.GetString(0), cancellationToken);
        if (isolation.Count != 1 || !string.Equals(isolation[0], "read committed", StringComparison.Ordinal))
        {
            // A snapshot taken before the User key was granted could miss a Membership admitted just before it, and that
            // tenant would be neither fenced nor judged.
            throw new InvalidOperationException("A closure's fences are taken only in a READ COMMITTED transaction; the act is abandoned.");
        }

        await handle.ExecuteAsync(BoundTheWait, null, cancellationToken);
        await handle.ExecuteAsync(TakeExclusive, [new PlatformSqlParameter("key", userKey)], cancellationToken);

        var participants = await ReadParticipantsAsync(handle, subject, cancellationToken);
        if (requestTenant is not { } request || !participants.WithinBounds)
        {
            return new PlatformIdentityFenceSet(subject, participants.Groups, [], participants.WithinBounds);
        }

        var tenants = participants.Groups.Select(group => group.TenantId).Append(request).Distinct()
            .OrderBy(IdentityAdvisoryLockKey.ForTenant).ToArray();
        foreach (var tenant in tenants)
        {
            await handle.ExecuteAsync(
                TakeExclusive, [new PlatformSqlParameter("key", IdentityAdvisoryLockKey.ForTenant(tenant))], cancellationToken);
        }

        return new PlatformIdentityFenceSet(subject, participants.Groups, tenants, WithinBounds: true);
    }

    /// <summary>
    /// The person's participant groups and whether the closure is within its bounds: at most one group past
    /// <see cref="MaximumGroups"/> is read, so a person past it is seen without reading every group. Fenced, inside the
    /// acquirer; unfenced only for a person whose User is no longer Active, whom no Membership can be admitted for.
    /// </summary>
    /// <exception cref="InvalidOperationException">The person has no Membership.</exception>
    internal static async Task<ClosureParticipantRead> ReadParticipantsAsync(
        IPlatformAdminScopeHandle handle, UserId subject, CancellationToken cancellationToken)
    {
        var rows = await handle.QueryAsync(
            ParticipantsSql,
            [new PlatformSqlParameter("subject", subject.Value), new PlatformSqlParameter("limit", MaximumGroups + 1)],
            reader => (Group: new ClosureParticipantGroup(
                    TenantId.From(reader.GetGuid(0)), reader.IsDBNull(1) ? null : OrganizationId.From(reader.GetGuid(1))),
                Memberships: reader.GetInt64(2)),
            cancellationToken);
        if (rows.Count == 0)
        {
            // A closure is filed in a group the person holds a Membership in, and Memberships are never deleted.
            throw new InvalidOperationException("A closure's person holds no Membership; the act is abandoned.");
        }

        var fullest = rows.GroupBy(row => row.Group.TenantId).Max(tenant => tenant.Sum(row => row.Memberships));
        var within = ClosureLimits.Admits(rows.Count, (int)Math.Min(fullest, int.MaxValue));
        return new ClosureParticipantRead([.. rows.Select(row => row.Group)], within);
    }
}

/// <summary>The participant groups read under the User fence, and whether the closure is within its bounds.</summary>
/// <param name="Groups">The groups in key order: every one when within the bounds, one past the group bound otherwise.</param>
/// <param name="WithinBounds">
/// Whether the groups and the Memberships of the fullest participant tenant are within <see cref="ClosureLimits"/>.
/// </param>
internal sealed record ClosureParticipantRead(IReadOnlyList<ClosureParticipantGroup> Groups, bool WithinBounds)
{
    /// <summary>Never prints a group, which is the person's Membership.</summary>
    public override string ToString() => nameof(ClosureParticipantRead);
}

/// <summary>What the acquirer fenced.</summary>
/// <param name="Subject">The person whose User key is held.</param>
/// <param name="Groups">The participant groups, read under the User key.</param>
/// <param name="Tenants">
/// The tenants whose keys are held, ascending by key; none for the User-only form, or when the closure is past its bounds.
/// </param>
/// <param name="WithinBounds">Whether the closure is within <see cref="ClosureLimits"/>; past them no tenant key was taken.</param>
internal sealed record PlatformIdentityFenceSet(
    UserId Subject, IReadOnlyList<ClosureParticipantGroup> Groups, IReadOnlyList<TenantId> Tenants, bool WithinBounds)
{
    /// <summary>Never prints the person.</summary>
    public override string ToString() => nameof(PlatformIdentityFenceSet);
}
