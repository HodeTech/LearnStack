using Vogen;

namespace LearnStack.SharedKernel.Identifiers;

/// <summary>
/// The tenant a row belongs to — the identifier every isolation layer keys on.
/// </summary>
/// <remarks>
/// <para>
/// Lives in <see cref="LearnStack.SharedKernel"/> rather than in the Tenancy
/// module, per ADR-0023 Amendment 2's cross-cutting placement rule: it appears in
/// <c>ITenantContext</c>, on every <c>[TenantOwned]</c> entity, in cache keys, in
/// job payloads and in integration-event envelopes, so a module-owned type would
/// make every one of those a reference to Tenancy.
/// </para>
/// <para>
/// <b>There is no <c>TenantId.New()</c>, and that is a constraint rather than an
/// omission.</b> A tenant id is never minted inside a handler: the registry that
/// owns the <c>Tenant</c> aggregate assigns it — the Hub in SaaS / Dedicated,
/// configuration in Self-Hosted, the fixture in a seed — and the provisioning
/// transaction sets <c>app.tenant_id</c> to that value before the <c>INSERT</c>,
/// so the self-keyed policy's <c>WITH CHECK</c> passes. A handler that generated
/// its own could not satisfy its own policy. See
/// <see href="../../../../docs/standards/05-database.md">Database Standards
/// § Table classes</see>.
/// </para>
/// <para>
/// <b>The platform sentinel is deliberately absent.</b> The corpus refers to a
/// "sentinel platform tenant id" for the one row that has no tenant of its own —
/// the audit row written inside
/// <c>EnterPlatformAdminScope</c>, which describes a cross-tenant operation. Its
/// *value* is fixed nowhere. Packet 7 is the first to emit it — a <c>Warning</c>
/// log line from <c>EnterPlatformAdminScope</c> — but a log line is not a
/// one-way door and can carry the reason and the caller without a minted id. The
/// irreversible consumer is <c>audit_log</c>'s <c>tenant_id</c> column, which
/// <see href="../../../../docs/roadmap/phase-02a-kernel-tenancy.md">Phase 02a
/// Packet 9</see> owns; choosing the value here would fix a one-way-door
/// identifier for a table that does not exist yet, so Packet 9 chooses it with
/// the schema that stores it. Note that ADR-0036 forbids the sentinel on a different
/// path — an unauthenticated tenant-assertion rejection must never write under it
/// — and the two rules do not conflict: one is an audited operator action, the
/// other an anonymous request.
/// </para>
/// </remarks>
[ValueObject<Guid>(LearnStackVogenDefaults.IdMask)]
public readonly partial record struct TenantId : IStronglyTypedId<Guid>;
