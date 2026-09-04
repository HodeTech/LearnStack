using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Time;

namespace LearnStack.Modules.Customization.Domain;

/// <summary>
/// Where a customization definition sits in its lifecycle.
/// </summary>
/// <remarks>
/// <c>draft → active → deprecated</c>, one way, per
/// <see href="../../../../../docs/roadmap/phase-04-cms-media-pages.md">Phase 04
/// § Customization Key Shape and Immutable Schema Versions</see>. Stored as
/// <c>text</c> with a <c>CHECK</c>, never a PostgreSQL <c>enum</c>
/// (<see href="../../../../../docs/standards/05-database.md">Database Standards
/// § Constraints</see>).
/// </remarks>
public enum CustomizationStatus
{
    /// <summary>Being authored. The definition body is still mutable.</summary>
    Draft = 0,

    /// <summary>Live. At most one revision per concept is here at a time.</summary>
    Active = 1,

    /// <summary>Superseded. Kept while any instance still references it.</summary>
    Deprecated = 2,
}

/// <summary>
/// The shape every versioned customization aggregate carries: a tenant-scoped key,
/// an immutable revision number, a lifecycle, and a localized label.
/// </summary>
/// <remarks>
/// <para>
/// <b>One base, because the rules must not differ between the two.</b>
/// <see cref="TenantContentType"/> and <see cref="TenantLevelTaxonomy"/> both ship
/// the key shape
/// <see href="../../../../../docs/roadmap/phase-04-cms-media-pages.md">Phase 04</see>
/// fixes — <c>UNIQUE (tenant_id, key, schema_version)</c> for the revision,
/// <c>UNIQUE (tenant_id, key) WHERE status = 'active'</c> for the live definition —
/// and the same transitions. Two copies of a lifecycle is two lifecycles as soon
/// as one of them is edited.
/// </para>
/// <para>
/// <b>Tenant-wide, never organization-scoped.</b>
/// <see href="../../../../../docs/architecture/02-domain-model.md">The domain
/// model</see> enumerates the customization aggregates that MAY carry an
/// <c>organization_id</c> — <c>TenantTemplateLibrary</c> and
/// <c>TenantCustomFieldDef</c> — and neither of these is among them. That puts
/// both tables in the "tenant-owned, tenant-wide" class, which takes no
/// restrictive write guards because there is no organization to guard.
/// </para>
/// <para>
/// <b>The body is frozen at publish; presentation is not.</b> Phase 04's
/// authority says "after first publish, <c>status</c> and presentation metadata
/// are the only mutable columns", and this base enforces exactly that.
/// <see cref="EnsureBodyMutable"/> is what a derived aggregate calls before
/// touching the part a stored instance was validated against. The rule is
/// deliberately the strict reading of a question Phase 04 leaves open: loosening
/// it later changes no stored row, while shipping the loose form and tightening
/// it later would invalidate rows already written.
/// </para>
/// </remarks>
public abstract class CustomizationDefinition<TId>
    : AuditableEntity<TId>, ITenantOwned
    where TId : struct, IStronglyTypedId<Guid>, IEquatable<TId>
{
    /// <remarks>
    /// A constructor rather than a <c>protected void Initialize…</c> a derived
    /// factory has to remember to call. The base's own fields are what the base
    /// exists to guarantee, and an initializer method leaves a fully constructed
    /// object whose non-nullable <see cref="Key"/> is null and whose
    /// <see cref="SchemaVersion"/> is the zero its own guard refuses — reachable by
    /// forgetting one line, and reachable silently.
    /// </remarks>
    protected CustomizationDefinition(
        TId id,
        TenantId tenantId,
        string key,
        int schemaVersion,
        LocalizedText displayName)
        : base(id)
    {
        TenantOwnership.EnsureRealTenant(
            tenantId, "A customization definition belongs to a tenant.", nameof(tenantId));
        CustomizationKey.EnsureValid(key, nameof(key));
        ArgumentNullException.ThrowIfNull(displayName);

        if (schemaVersion < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(schemaVersion),
                schemaVersion,
                "A schema version starts at 1; there is no revision zero to reference.");
        }

        TenantId = tenantId;
        Key = key;
        SchemaVersion = schemaVersion;
        SchemaRevision = 0;
        Status = CustomizationStatus.Draft;
        DisplayName = displayName;
    }

    // EF materialization.
    protected CustomizationDefinition()
    {
        Key = null!;
        DisplayName = null!;
    }

    public TenantId TenantId { get; private set; }

    /// <summary>The concept this revision defines, e.g. <c>vocabulary-card</c>.</summary>
    public string Key { get; private set; }

    /// <summary>
    /// The revision's identity, raised only by a change a stored instance could
    /// fail.
    /// </summary>
    public int SchemaVersion { get; private set; }

    /// <summary>
    /// Strictly additive edits within <see cref="SchemaVersion"/>. Not part of any
    /// unique key: instances pin the version, never the revision.
    /// </summary>
    public int SchemaRevision { get; private set; }

    public CustomizationStatus Status { get; private set; }

    /// <summary>The tenant's label for this concept, per authored locale.</summary>
    public LocalizedText DisplayName { get; private set; }

    /// <summary>
    /// Makes this revision the live definition of its concept.
    /// </summary>
    /// <remarks>
    /// The partial index <c>UNIQUE (tenant_id, key) WHERE status = 'active'</c> is
    /// what actually holds "one live revision per concept"; the aggregate cannot
    /// see its siblings. The command that publishes a successor deprecates the
    /// incumbent in the same transaction, and the index is what catches the case
    /// where it did not.
    /// </remarks>
    public void Publish(IClock clock, UserId updatedBy)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (Status != CustomizationStatus.Draft)
        {
            throw new InvalidOperationException(
                $"Only a draft is published; this revision is {Status.ToString().ToLowerInvariant()}. "
                + "A deprecated revision is history, and an active one is already live.");
        }

        EnsurePublishable();

        // Stamped first — a guard that runs after the mutation has already lost.
        MarkUpdated(clock.UtcNow, updatedBy);
        Status = CustomizationStatus.Active;
    }

    /// <summary>
    /// Retires this revision. Rows that pinned it keep resolving to it.
    /// </summary>
    public void Deprecate(IClock clock, UserId updatedBy)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (Status != CustomizationStatus.Active)
        {
            throw new InvalidOperationException(
                $"Only an active revision is deprecated; this one is {Status.ToString().ToLowerInvariant()}. "
                + "A draft that is not wanted is deleted, not deprecated — nothing references it.");
        }

        MarkUpdated(clock.UtcNow, updatedBy);
        Status = CustomizationStatus.Deprecated;
    }

    /// <summary>
    /// Replaces the label. Presentation metadata, so it stays mutable after publish.
    /// </summary>
    public void Rename(LocalizedText displayName, IClock clock, UserId updatedBy)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(displayName);

        MarkUpdated(clock.UtcNow, updatedBy);
        DisplayName = displayName;
    }

    /// <summary>
    /// Refuses a change to the part a stored instance was validated against once
    /// this revision is live.
    /// </summary>
    protected void EnsureBodyMutable()
    {
        if (Status != CustomizationStatus.Draft)
        {
            throw new InvalidOperationException(
                "The body of a published revision is frozen: a stored instance was "
                + "validated against it and cannot be re-validated retroactively. A "
                + "change a stored instance could fail raises schema_version; an "
                + "additive one is made while the revision is still a draft.");
        }
    }

    /// <summary>Counts one additive edit. The caller has already validated it.</summary>
    protected void RaiseRevision() => SchemaRevision++;

    /// <summary>
    /// What a derived aggregate requires before it may go live — a taxonomy needs
    /// items, a content type needs a schema it already has by construction.
    /// </summary>
    protected virtual void EnsurePublishable()
    {
    }
}
