using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;

namespace LearnStack.Modules.Tenancy.Domain;

/// <summary>
/// A host a tenant claims, and where it is in the verification lifecycle.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is not the resolution index.</b> <c>platform_host_to_tenant</c> is,
/// and the two exist separately because they are read at different moments:
/// this table is read and written <i>under</i> tenant context, as part of a
/// tenant managing its own domains, while the mapping table is read <i>before</i>
/// any tenant context exists, in order to determine the tenant. That difference
/// is exactly why they cannot share one Row Level Security rule — this one is
/// ordinary tenant-owned, the mapping table is the platform-scoped class.
/// </para>
/// <para>
/// A verified row here does not serve traffic on its own; a corresponding
/// mapping row does. The custom-domain lifecycle that keeps the two in step is
/// Hub-owned and lands in
/// <see href="../../../../../docs/roadmap/phase-02c-hub-foundation.md">Phase 02c</see>;
/// what Packet 6 owns is the schema both sides write to.
/// </para>
/// </remarks>
public sealed class TenantDomain : AuditableEntity<TenantDomainId>
{
    private TenantDomain(TenantDomainId id)
        : base(id) => Host = null!;

    // EF materialization.
    private TenantDomain() => Host = null!;

    public TenantId TenantId { get; private set; }

    /// <summary>
    /// The normalized host, lowercase and punycoded.
    /// </summary>
    /// <remarks>
    /// Globally unique, not unique per tenant: a host resolving to two tenants is
    /// unresolvable regardless of who owns it, and the mapping table already
    /// assumes one answer per host.
    /// </remarks>
    public string Host { get; private set; }

    public TenantDomainKind Kind { get; private set; }

    public TenantDomainStatus Status { get; private set; }

    /// <summary>When verification last succeeded. Null until it does.</summary>
    public DateTimeOffset? VerifiedAt { get; private set; }

    /// <summary>How many verification attempts have run, for backoff and for support.</summary>
    public int VerificationAttempts { get; private set; }

    /// <summary>
    /// Why the last attempt failed, in operator-facing terms.
    /// </summary>
    /// <remarks>
    /// Carries no certificate material and no private key. CLAUDE.md forbids cert
    /// material moving by value anywhere; it moves by secret-store replication and
    /// is referenced by path.
    /// </remarks>
    public string? LastVerificationError { get; private set; }

    /// <summary>
    /// Claims a platform subdomain, which is verified by construction.
    /// </summary>
    public static TenantDomain CreateSubdomain(
        TenantDomainId id, TenantId tenantId, string host, IClock clock, UserId createdBy)
    {
        var domain = CreateCore(id, tenantId, host, TenantDomainKind.Subdomain, clock, createdBy);
        domain.Status = TenantDomainStatus.Verified;
        domain.VerifiedAt = clock.UtcNow;
        return domain;
    }

    /// <summary>
    /// Claims a customer-owned domain, which starts unverified.
    /// </summary>
    public static TenantDomain RequestCustomDomain(
        TenantDomainId id, TenantId tenantId, string host, IClock clock, UserId createdBy)
        => CreateCore(id, tenantId, host, TenantDomainKind.Custom, clock, createdBy);

    /// <summary>
    /// Starts a verification attempt, moving the domain into
    /// <see cref="TenantDomainStatus.Verifying"/>.
    /// </summary>
    /// <remarks>
    /// The state the enum defined, the CHECK accepted, the module spec drew — and
    /// no code could produce. Without it a custom domain went
    /// <c>Requested → Verified</c> in one call, a transition no diagram in the
    /// corpus draws, and <c>Verifying</c> was an unreachable value in a closed
    /// set. Who calls it is
    /// <see href="../../../../../docs/roadmap/phase-02c-hub-foundation.md">Phase
    /// 02c</see>'s custom-domain lifecycle; the state machine is this aggregate's
    /// either way.
    /// </remarks>
    public void MarkVerificationStarted(IClock clock, UserId updatedBy)
    {
        ArgumentNullException.ThrowIfNull(clock);
        EnsureVerifiable();
        EnsureStatusIs(
            "start verification",
            TenantDomainStatus.Requested,
            TenantDomainStatus.Failed);

        Status = TenantDomainStatus.Verifying;
        MarkUpdated(clock.UtcNow, updatedBy);
    }

    /// <summary>Records a verification attempt that succeeded.</summary>
    public void MarkVerified(IClock clock, UserId updatedBy)
    {
        ArgumentNullException.ThrowIfNull(clock);
        EnsureVerifiable();
        EnsureStatusIs("record a verification result", TenantDomainStatus.Verifying);

        Status = TenantDomainStatus.Verified;
        VerifiedAt = clock.UtcNow;
        VerificationAttempts++;
        LastVerificationError = null;
        MarkUpdated(clock.UtcNow, updatedBy);
    }

    /// <summary>Records a verification attempt that failed.</summary>
    public void MarkVerificationFailed(string error, IClock clock, UserId updatedBy)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        // The column holds 1000; an unbounded provider message otherwise arrives
        // as 22001 from three layers down, naming neither the property nor the
        // aggregate.
        MappedLength.EnsureAtMost(error, 1000, nameof(error));
        EnsureVerifiable();
        EnsureStatusIs("record a verification result", TenantDomainStatus.Verifying);

        Status = TenantDomainStatus.Failed;
        VerificationAttempts++;
        LastVerificationError = error;
        MarkUpdated(clock.UtcNow, updatedBy);
    }

    /// <summary>
    /// Only a <see cref="TenantDomainKind.Custom"/> domain travels the
    /// verification path.
    /// </summary>
    /// <remarks>
    /// A <see cref="TenantDomainKind.Subdomain"/> is verified by construction —
    /// the platform controls the zone — and every state diagram in the corpus
    /// draws it that way. Without this guard the two verification methods would
    /// move one to <c>Verifying</c> or <c>Failed</c>, a state the module spec says
    /// it cannot reach, and the schema would not object: the kind and status
    /// CHECKs are independent single-column constraints.
    /// </remarks>
    private void EnsureVerifiable()
    {
        if (Kind != TenantDomainKind.Custom)
        {
            throw new InvalidOperationException(
                $"A {Kind} domain is verified by construction and has no verification lifecycle; "
                + "only a Custom domain can be marked verified or failed.");
        }
    }

    /// <summary>
    /// Refuses a transition the module spec's state diagram does not draw.
    /// </summary>
    private void EnsureStatusIs(string operation, params TenantDomainStatus[] allowed)
    {
        if (!allowed.Contains(Status))
        {
            throw new InvalidOperationException(
                $"A domain in {Status} cannot {operation}; expected "
                + $"{string.Join(" or ", allowed)}. The lifecycle is "
                + "Requested → Verifying → Verified | Failed, and Failed may start over.");
        }
    }

    private static TenantDomain CreateCore(
        TenantDomainId id,
        TenantId tenantId,
        string host,
        TenantDomainKind kind,
        IClock clock,
        UserId createdBy)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        if (!id.IsInitialized() || id.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "The identifier was never assigned; construct it through its factory.",
                nameof(id));
        }

        TenantOwned.EnsureRealTenant(
            tenantId, "A domain belongs to a tenant.", nameof(tenantId));

        // The database carries the same rule as ck_tenant_domains_host_normalized;
        // this is the loud half, so a caller that skipped EffectiveHost.Normalize
        // learns it here rather than as a constraint violation three layers down.
        //
        // Asked by running the normalizer, not by testing one of its four
        // properties. An earlier version checked case alone while its message
        // named all four — lowercase, punycoded, no port, no trailing dot — so a
        // host carrying a port or an IDN label passed the aggregate and failed at
        // the CHECK. Normalize returns null for a host it cannot normalize at all,
        // and the input unchanged for one that is already normalized, so
        // inequality is the whole question.
        if (!string.Equals(EffectiveHost.Normalize(host), host, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Host must be normalized before it reaches the aggregate: lowercase, punycoded, no port, no trailing dot. Use EffectiveHost.Normalize.",
                nameof(host));
        }

        var domain = new TenantDomain(id)
        {
            TenantId = tenantId,
            Host = host,
            Kind = kind,
            Status = TenantDomainStatus.Requested,
        };

        domain.MarkCreated(clock.UtcNow, createdBy);
        return domain;
    }
}
