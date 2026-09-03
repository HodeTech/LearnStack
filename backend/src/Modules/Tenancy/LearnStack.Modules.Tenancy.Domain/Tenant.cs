using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Time;

namespace LearnStack.Modules.Tenancy.Domain;

/// <summary>
/// The root of a customer's data. Every other tenant-owned row keys on its id.
/// </summary>
/// <remarks>
/// <para>
/// <b>Self-keyed, and that shapes its whole lifecycle.</b> <c>tenants</c> has no
/// <c>tenant_id</c> column — its primary key <i>is</i> the tenant id — so its Row
/// Level Security policy keys on <c>id</c>
/// (<see href="../../../../../docs/standards/05-database.md">Database Standards
/// § Table classes</see>). Two consequences follow, and both are binding:
/// </para>
/// <para>
/// <b>The id is never minted here.</b> The registry that owns the tenant assigns
/// it — the Hub in SaaS / Dedicated, configuration in Self-Hosted, the fixture in
/// a seed — and the provisioning transaction sets <c>app.tenant_id</c> to that
/// value before the <c>INSERT</c>, so <c>WITH CHECK</c> passes. A factory that
/// generated its own id could not satisfy its own policy.
/// </para>
/// <para>
/// <b>Enumerating tenants needs the platform role.</b> <c>SELECT … FROM tenants</c>
/// with no <c>app.tenant_id</c> returns zero rows, so every operator list screen
/// and every cross-tenant sweep goes through <c>EnterPlatformAdminScope(reason)</c>.
/// That is the intended cost: the application role cannot enumerate the customer
/// list.
/// </para>
/// </remarks>
[TenantOwned(SelfKeyed = true)]
public sealed class Tenant : AuditableEntity<TenantId>, IAggregateRoot<TenantId>
{
    private Tenant(TenantId id)
        : base(id)
    {
        Slug = null!;
        DisplayName = null!;
    }

    // EF materialization.
    private Tenant()
    {
        Slug = null!;
        DisplayName = null!;
    }

    /// <summary>
    /// Globally unique, URL-safe handle. Appears in hostnames.
    /// </summary>
    /// <remarks>
    /// Unique across the whole table rather than per tenant, and PostgreSQL
    /// enforces unique indexes with row security bypassed — so a duplicate-slug
    /// insert reveals that <i>some</i> tenant already holds the slug. Accepted
    /// here because slugs appear in hostnames and are public by construction. It
    /// is accepted nowhere else, which is why every other natural key is
    /// <c>UNIQUE (tenant_id, …)</c>.
    /// </remarks>
    public string Slug { get; private set; }

    /// <summary>Human-facing name.</summary>
    public string DisplayName { get; private set; }

    /// <summary>Lifecycle state.</summary>
    public TenantStatus Status { get; private set; }

    /// <summary>
    /// The organization a request falls back to when it names none.
    /// </summary>
    /// <remarks>
    /// Nullable, and null only inside the provisioning transaction. The foreign
    /// key is composite — <c>(id, default_organization_id) REFERENCES
    /// organizations (tenant_id, id)</c> — so it cannot point at another tenant's
    /// organization even though referential-integrity checks run with row
    /// security bypassed. Under <c>MATCH SIMPLE</c> the check is skipped entirely
    /// while the column is null, which is what makes the three-statement
    /// provisioning sequence work: insert the tenant, insert its organization,
    /// then <see cref="AssignDefaultOrganization"/>.
    /// </remarks>
    public OrganizationId? DefaultOrganizationId { get; private set; }

    /// <summary>
    /// Creates a tenant under an id its registry has already assigned.
    /// </summary>
    public static Tenant Create(
        TenantId id,
        string slug,
        string displayName,
        IClock clock,
        UserId createdBy)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        MappedLength.EnsureAtMost(slug, UrlSlug.MaxLength, nameof(slug));
        MappedLength.EnsureAtMost(displayName, MappedLength.DisplayName, nameof(displayName));
        UrlSlug.EnsureUrlSafe(slug, nameof(slug));

        TenantOwnership.EnsureRealTenant(
            id,
            "A tenant id is assigned by the registry that owns the tenant, never minted here.",
            nameof(id));

        var tenant = new Tenant(id)
        {
            Slug = slug,
            DisplayName = displayName,
            Status = TenantStatus.Trial,
        };

        tenant.MarkCreated(clock.UtcNow, createdBy);
        return tenant;
    }

    /// <summary>
    /// Points the tenant at its default organization, completing provisioning.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Create"/> because the organization cannot exist
    /// before its tenant: the composite foreign key would have nothing to
    /// reference. Both statements run in one transaction, so a tenant is never
    /// observable without a default organization.
    /// </remarks>
    public void AssignDefaultOrganization(OrganizationId organizationId, IClock clock, UserId updatedBy)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (!organizationId.IsInitialized() || organizationId.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "The default organization must be a real organization.", nameof(organizationId));
        }

        MarkUpdated(clock.UtcNow, updatedBy);
        DefaultOrganizationId = organizationId;
    }

    /// <summary>Moves the tenant to a new lifecycle state.</summary>
    /// <remarks>
    /// The transitions the module spec's state diagram draws, and no others.
    /// A bare assignment took <c>Archived → Active</c>, <c>Active → Trial</c> and
    /// <c>(TenantStatus)999</c>; the column's CHECK stops only the third, because
    /// it can see the value and not where the row came from. This is the same
    /// argument <c>TenantDomain.EnsureVerifiable</c> makes for the sibling
    /// aggregate — "the schema would not object, so the aggregate is where the
    /// invariant lives".
    /// </remarks>
    private readonly List<TenantLocale> _locales = [];

    /// <summary>
    /// The locales this tenant publishes in.
    /// </summary>
    /// <remarks>
    /// A navigation rather than its own aggregate: a locale row is
    /// <c>PRIMARY KEY (tenant_id, locale)</c> with no surrogate id, and a composite
    /// natural key is not an <c>IAggregateRoot&lt;TId&gt;</c> under any reading. It is
    /// read-only here because the root is the only way in — see the mutators below, and
    /// the reason they exist.
    /// </remarks>
    public IReadOnlyCollection<TenantLocale> Locales => _locales.AsReadOnly();

    private readonly List<TenantFeatureFlag> _featureFlags = [];

    /// <summary>This tenant's feature-flag overrides.</summary>
    public IReadOnlyCollection<TenantFeatureFlag> FeatureFlags => _featureFlags.AsReadOnly();

    /// <summary>Adds a locale this tenant publishes in.</summary>
    /// <remarks>
    /// <b>Every child write goes through a root method, and that is what bumps
    /// <c>row_version</c>.</b> The version advances only inside
    /// <c>AuditableEntity.Touch</c>, which nothing but <c>MarkUpdated</c> and
    /// <c>SoftDelete</c> reach — so the bump the roadmap requires for a locale or flag
    /// write is a consequence of containment rather than a second mechanism someone has
    /// to remember. A caller holding a locale directly could not have produced it.
    /// </remarks>
    public void AddLocale(
        string locale,
        bool isDefault,
        IClock clock,
        UserId updatedBy,
        bool isEnabled = true,
        short sort = 0)
    {
        ArgumentNullException.ThrowIfNull(clock);

        var added = TenantLocale.Create(Id, locale, isDefault: false, isEnabled, sort);

        if (_locales.Any(existing => string.Equals(
                existing.Locale, added.Locale, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"This tenant already publishes in '{added.Locale}'. A locale row is "
                + "identified by (tenant_id, locale), so a second one is a duplicate "
                + "rather than a second locale.");
        }

        MarkUpdated(clock.UtcNow, updatedBy);
        _locales.Add(added);

        if (isDefault)
        {
            PromoteDefault(added);
        }
    }

    /// <summary>Makes an existing locale this tenant's default.</summary>
    public void SetDefaultLocale(string locale, IClock clock, UserId updatedBy)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);

        var canonical = LocaleTag.Canonicalize(locale);
        var target = _locales.FirstOrDefault(existing => string.Equals(
                         existing.Locale, canonical, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"This tenant does not publish in '{canonical}', so it cannot be the default.");

        MarkUpdated(clock.UtcNow, updatedBy);
        PromoteDefault(target);
    }

    /// <summary>Removes a locale, which must not be the default.</summary>
    public void RemoveLocale(string locale, IClock clock, UserId updatedBy)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);

        var canonical = LocaleTag.Canonicalize(locale);
        var target = _locales.FirstOrDefault(existing => string.Equals(
                         existing.Locale, canonical, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"This tenant does not publish in '{canonical}'.");

        if (target.IsDefault)
        {
            throw new InvalidOperationException(
                $"'{canonical}' is this tenant's default locale. Promote another locale "
                + "first: a tenant that publishes in nothing has no fallback to render.");
        }

        MarkUpdated(clock.UtcNow, updatedBy);
        _locales.Remove(target);
    }

    /// <summary>Sets a feature-flag override, adding it when it is new.</summary>
    public void SetFeatureFlag(string key, string value, IClock clock, UserId updatedBy)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        MarkUpdated(clock.UtcNow, updatedBy);

        var existing = _featureFlags.FirstOrDefault(
            flag => string.Equals(flag.Key, key, StringComparison.Ordinal));

        if (existing is null)
        {
            _featureFlags.Add(
                TenantFeatureFlag.Create(Id, key, value, clock.UtcNow, updatedBy));
            return;
        }

        existing.SetValue(value, clock.UtcNow, updatedBy);
    }

    /// <summary>Removes a feature-flag override.</summary>
    public void RemoveFeatureFlag(string key, IClock clock, UserId updatedBy)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var target = _featureFlags.FirstOrDefault(
                         flag => string.Equals(flag.Key, key, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"No feature flag '{key}' is set.");

        MarkUpdated(clock.UtcNow, updatedBy);
        _featureFlags.Remove(target);
    }

    /// <summary>
    /// Clears the incumbent default before setting the new one.
    /// </summary>
    /// <remarks>
    /// <b>The order is not cosmetic.</b> A partial unique index
    /// <c>UNIQUE (tenant_id) WHERE is_default</c> backs this invariant in the database,
    /// and EF emits one UPDATE per changed row: measured, setting the new default first
    /// fails 23505, and clearing the incumbent first succeeds. The guard here exists for
    /// the error message; the index exists because an aggregate invariant does not hold
    /// across concurrent transactions.
    /// </remarks>
    private void PromoteDefault(TenantLocale target)
    {
        foreach (var incumbent in _locales.Where(locale => locale.IsDefault))
        {
            incumbent.ClearDefault();
        }

        target.MakeDefault();
    }

    public void ChangeStatus(TenantStatus status, IClock clock, UserId updatedBy)
    {
        ArgumentNullException.ThrowIfNull(clock);
        EnsureTransitionAllowed(status);

        // Stamped before the field moves. MarkUpdated is the only statement here
        // that can throw — a sentinel timestamp or an unreal actor — and an
        // aggregate left mutated by a call that failed is a state no guard above
        // it can see.
        MarkUpdated(clock.UtcNow, updatedBy);
        Status = status;
    }

    private void EnsureTransitionAllowed(TenantStatus target)
    {
        if (!Enum.IsDefined(target))
        {
            throw new ArgumentOutOfRangeException(
                nameof(target), target, "Not a defined TenantStatus.");
        }

        // Archived is terminal for serving: a tenant is retained for audit and
        // retention obligations and never served again, so nothing leaves it.
        var allowed = Status switch
        {
            TenantStatus.Trial => target is TenantStatus.Active or TenantStatus.Suspended
                or TenantStatus.Archived,
            TenantStatus.Active => target is TenantStatus.Suspended or TenantStatus.Archived,
            TenantStatus.Suspended => target is TenantStatus.Active or TenantStatus.Archived,
            _ => false,
        };

        if (!allowed && target != Status)
        {
            throw new InvalidOperationException(
                $"A tenant cannot move from {Status} to {target}. "
                + "Trial goes to Active, Suspended or Archived; Active and Suspended swap and "
                + "both archive; Archived is terminal.");
        }
    }
}
