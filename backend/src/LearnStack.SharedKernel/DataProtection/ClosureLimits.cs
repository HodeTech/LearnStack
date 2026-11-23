namespace LearnStack.SharedKernel.DataProtection;

/// <summary>
/// The size a global account closure is admitted for (ADR-0107 § 7). Begin judges it before the person's User leaves
/// Active, because every later step of the closure is bounded by it and Closing cannot be undone: a person past it is
/// refused with nothing changed, never moved to Closing and then stranded.
/// </summary>
/// <remarks>
/// <para>
/// <b>Groups.</b> <see cref="MaximumParticipantGroups"/> bounds the participant groups Begin and Finalize read under the
/// person's User fence, the groups the fan-out is handed in one call, and so the tenant fences one closure holds. It counts
/// every group in which the person holds a Membership of any status, served or not, because Finalize fences and redacts
/// every participant tenant whatever its lifecycle.
/// </para>
/// <para>
/// <b>Memberships.</b> In each participant tenant the person may hold at most
/// <see cref="DataRequestLimits.MaximumSnapshotMemberships"/> Memberships of any status, Revoked tombstones included: the
/// bound of one group's fan-out snapshot and of one tenant pass of Finalize's redaction, and a group never holds more than
/// its tenant.
/// </para>
/// <para>
/// A person past either bound is served by Phase 11's data operations, not by this closure.
/// </para>
/// </remarks>
public static class ClosureLimits
{
    /// <summary>The most participant groups, served or unserved, one closure is admitted for.</summary>
    public const int MaximumParticipantGroups = 256;

    /// <summary>
    /// The most Memberships, of any status, the person may hold in one participant tenant: the snapshot bound one group's
    /// fan-out freezes and one tenant pass of the redaction compares.
    /// </summary>
    public const int MaximumTenantMemberships = DataRequestLimits.MaximumSnapshotMemberships;

    /// <summary>
    /// The most Memberships one admitted closure can hold across its participant tenants: every group in a tenant of its
    /// own, each at the tenant bound. Finalize reads at most this many and treats more as a defect.
    /// </summary>
    public const int MaximumMemberships = MaximumParticipantGroups * MaximumTenantMemberships;

    /// <summary>
    /// Whether a person with these participant groups and this many Memberships in their fullest tenant is within the
    /// bounds a closure is admitted for. A person with no Membership is not a size question: no closure is filed for one.
    /// </summary>
    /// <param name="groups">The participant groups, served or not.</param>
    /// <param name="mostTenantMemberships">The most Memberships of any status the person holds in one participant tenant.</param>
    public static bool Admits(int groups, int mostTenantMemberships) =>
        groups <= MaximumParticipantGroups && mostTenantMemberships <= MaximumTenantMemberships;
}
