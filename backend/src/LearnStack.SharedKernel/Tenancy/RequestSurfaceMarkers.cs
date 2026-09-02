namespace LearnStack.SharedKernel.Tenancy;

/// <summary>
/// Marks a request type that legitimately runs before any tenant is resolved.
/// </summary>
/// <remarks>
/// <para>
/// <b>A deliberate hole, and the point is that it is counted.</b>
/// <c>TenantContextBehavior</c> at pipeline step 4 refuses every request whose
/// <see cref="ITenantContext.IsResolved"/> is <c>false</c>; this marker is the only
/// exemption, and it exists for the narrow set of tenant-provisioning and
/// platform-admin commands that have no tenant to resolve because they are what
/// creates or spans one. <c>AllowsUnresolvedTenantContext_Only_On_Provisioning_Commands</c>
/// holds the set: a hole nobody counts becomes a hole everybody uses.
/// </para>
/// <para>
/// <b>It exempts the assertion, never the ceiling.</b> A marked request that arrives
/// on a <i>resolved</i> context is subject to the authority ceiling like any other —
/// a provisioning command addressed to a live tenant's own hostname resolves
/// <see cref="TenantContextOrigin.HostOnly"/> and is refused, which is exactly the
/// confused deputy the ceiling exists to close. Fusing the two checks so the marker
/// skips both would let an anonymous caller reach provisioning by typing a tenant's
/// hostname.
/// </para>
/// <para>
/// <b>It ships with no users.</b> The first is <c>ProvisionTenantCommand</c>, in
/// Packet 7 step 9 — there is not one production request type in the solution today.
/// The marker lands ahead of it because the behavior that reads it lands now, and a
/// predicate with no attribute to look for is the stub this replaces.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class AllowsUnresolvedTenantContextAttribute : Attribute;

/// <summary>
/// Marks a request type reachable by a caller LearnStack has not authenticated.
/// </summary>
/// <remarks>
/// <para>
/// <b>The marker is the whole of the claim under a host-only context.</b> A tenant
/// context assembled from the host alone carries
/// <see cref="TenantContextOrigin.HostOnly"/> and reaches only request types marked
/// here; a type without it is unreachable from <c>HostOnly</c> whatever its route
/// looks like. That ceiling is what makes a forged <c>Host</c> harmless — with it, a
/// forged host reaches exactly the pages that hostname already serves to anyone who
/// types it, and only while the mapping row is publicly live. Without it the trusted
/// hop is a confused deputy, because the edge derives its own assertion from the same
/// string the visitor chose.
/// </para>
/// <para>
/// <b>It is not a permit to run without a tenant.</b> Rows 13 and 15 of ADR-0036's
/// reconciliation matrix — a platform host, with or without a token — resolve no
/// tenant at all, and those are governed by
/// <see cref="AllowsUnresolvedTenantContextAttribute"/>. The two markers answer
/// different questions and neither implies the other.
/// </para>
/// <para>
/// <b>The set is a table, and it ships empty.</b> Every marked type is enumerated in
/// <see href="../../../../docs/standards/04-api-design.md">Standards 04 § Public
/// surface</see> with its permitted methods — the default is <c>GET</c>/<c>HEAD</c>,
/// a mutating entry states why, no marked type performs a tenant-owned write, and
/// none may be classified MUST-class <c>read-sensitive</c>, which would turn an
/// anonymous <c>GET</c> into a durable standalone audit write. The first rows arrive
/// with Phase 02d's two anonymous read endpoints.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class PublicSurfaceAttribute : Attribute;
