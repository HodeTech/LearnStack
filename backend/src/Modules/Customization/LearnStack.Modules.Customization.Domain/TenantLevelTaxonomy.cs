using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Time;

namespace LearnStack.Modules.Customization.Domain;

/// <summary>
/// A tenant's level, grade or difficulty vocabulary — CEFR bands, yoga difficulty,
/// kyu/dan ranks — declared as data.
/// </summary>
/// <remarks>
/// <para>
/// The second half of ADR-0018's claim, and the one
/// <see href="../../../../../docs/roadmap/phase-02d-walking-skeleton.md">Phase 02d</see>
/// puts on screen: two hosts, two taxonomies, one binary.
/// </para>
/// <para>
/// <b>Items are rows, not a JSON array.</b>
/// <see href="../../../../../docs/architecture/12-localization.md">12-localization
/// § Pattern B</see> publishes the DDL for exactly this table, keyed
/// <c>(tenant_id, taxonomy_key, key)</c>, and a child table is what lets an item
/// key be unique in the database, lets <c>sort</c> and <c>metadata</c> be real
/// columns, and gives <see href="../../../../../docs/roadmap/phase-05-education-learning-content.md">Phase 05</see>
/// a composite foreign-key target. The published examples in ADR-0018 show the
/// items inline; that is the shape of the JSON a reader sees, not the shape of the
/// storage.
/// </para>
/// <para>
/// <b>The version is part of the item key.</b> Items belong to a revision, not to
/// a concept: <c>(tenant_id, taxonomy_key, schema_version, key)</c>. Two revisions
/// of <c>cefr</c> may disagree about which bands exist, which is the whole point of
/// versioning a taxonomy — removing a band is a change a stored row referencing it
/// could fail.
/// </para>
/// </remarks>
[TenantOwned]
public sealed class TenantLevelTaxonomy
    : CustomizationDefinition<TenantLevelTaxonomyId>, IAggregateRoot<TenantLevelTaxonomyId>
{
    /// <summary>
    /// Beyond this a taxonomy is a catalogue, and the picker that renders it is
    /// unusable. Generous by design: CEFR has six, kyu/dan has twenty.
    /// </summary>
    public const int MaxItems = 100;

    private readonly List<TenantLevelTaxonomyItem> _items = [];

    private TenantLevelTaxonomy(
        TenantLevelTaxonomyId id,
        TenantId tenantId,
        string key,
        int schemaVersion,
        LocalizedText displayName)
        : base(id, tenantId, key, schemaVersion, displayName)
    {
    }

    // EF materialization.
    private TenantLevelTaxonomy()
    {
    }

    /// <summary>The bands, in authored order.</summary>
    public IReadOnlyList<TenantLevelTaxonomyItem> Items =>
        _items.OrderBy(item => item.Sort).ToList();

    public static TenantLevelTaxonomy Create(
        TenantLevelTaxonomyId id,
        TenantId tenantId,
        string key,
        int schemaVersion,
        LocalizedText displayName,
        IClock clock,
        UserId createdBy)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (!id.IsInitialized() || id.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "The identifier was never assigned; construct it through its factory.",
                nameof(id));
        }

        var taxonomy = new TenantLevelTaxonomy(id, tenantId, key, schemaVersion, displayName);
        taxonomy.MarkCreated(clock.UtcNow, createdBy);
        return taxonomy;
    }

    /// <summary>
    /// Adds one band.
    /// </summary>
    /// <remarks>
    /// The two refusals are the ones a database constraint states in a language a
    /// tenant admin cannot read. A duplicate item key is a <c>23505</c> naming an
    /// index; a duplicate <c>sort</c> is not a constraint violation at all — it is
    /// a render whose order changes between two requests, which is the harder bug
    /// and the reason the aggregate refuses it rather than the schema.
    /// </remarks>
    public void AddItem(
        string itemKey,
        LocalizedText displayName,
        short sort,
        string? metadata,
        IClock clock,
        UserId updatedBy)
    {
        ArgumentNullException.ThrowIfNull(clock);

        // The lifecycle guard first, for the reason ReviseSchema gives: a published
        // taxonomy refuses the item whatever is wrong with it, and naming the item's
        // fault sends the author to fix something that would still be refused.
        EnsureBodyMutable();

        // Then the item, validated before anything mutates, so a refused call
        // leaves the aggregate exactly as it was.
        var item = TenantLevelTaxonomyItem.Create(
            TenantId, Key, SchemaVersion, itemKey, displayName, sort, metadata);

        if (_items.Count >= MaxItems)
        {
            throw new InvalidOperationException(
                $"A taxonomy holds at most {MaxItems} items; this one already has {_items.Count}.");
        }

        if (_items.Any(existing => string.Equals(existing.Key, item.Key, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"'{item.Key}' is already an item of '{Key}' at version {SchemaVersion}.");
        }

        if (_items.Any(existing => existing.Sort == item.Sort))
        {
            throw new InvalidOperationException(
                $"Sort {item.Sort} is already taken in '{Key}'. Two items sharing a sort "
                + "order render in whichever order the database happened to return them.");
        }

        MarkUpdated(clock.UtcNow, updatedBy);
        _items.Add(item);
        RaiseRevision();
    }

    /// <summary>
    /// Removes a band from a draft.
    /// </summary>
    /// <remarks>
    /// Only from a draft, and that is <see cref="CustomizationDefinition{TId}.EnsureBodyMutable"/>'s
    /// doing rather than a special case: a content row carrying
    /// <c>levelKey: "b2"</c> resolves through this table, so dropping <c>b2</c>
    /// from a live taxonomy is a change a stored row fails. It raises
    /// <c>schema_version</c>, like any other breaking change.
    /// </remarks>
    public void RemoveItem(string itemKey, IClock clock, UserId updatedBy)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemKey);
        EnsureBodyMutable();

        var item = _items.SingleOrDefault(existing =>
            string.Equals(existing.Key, itemKey, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"'{itemKey}' is not an item of '{Key}' at version {SchemaVersion}.");

        MarkUpdated(clock.UtcNow, updatedBy);
        _items.Remove(item);
        RaiseRevision();
    }

    /// <inheritdoc />
    protected override void EnsurePublishable()
    {
        if (_items.Count == 0)
        {
            throw new InvalidOperationException(
                $"'{Key}' has no items. A taxonomy with no levels resolves every "
                + "reference to nothing, and a content row pointing at it renders a "
                + "placeholder on a page nobody was warned about.");
        }
    }
}

/// <summary>
/// One band inside a taxonomy revision.
/// </summary>
/// <remarks>
/// <para>
/// <b>Composite natural key, no surrogate id, no audit columns</b> — the same
/// shape and the same argument as <c>TenantLocale</c>. A second row for the same
/// <c>(tenant, taxonomy, version, key)</c> is not a second band, it is a
/// duplicate, so a surrogate id would invent an identity the domain does not have.
/// The items are a small wholly-replaced set whose changes the parent's audit
/// covers as one change rather than six.
/// </para>
/// <para>
/// It is <see cref="ITenantOwned"/> and carries <c>[TenantOwned]</c> in its own
/// right: Row Level Security is per table and is not inherited through a parent's
/// policy. A child table without its own policy is unprotected while holding the
/// label a page renders.
/// </para>
/// </remarks>
[TenantOwned]
public sealed class TenantLevelTaxonomyItem : ITenantOwned
{
    private TenantLevelTaxonomyItem()
    {
        TaxonomyKey = null!;
        Key = null!;
        DisplayName = null!;
    }

    public TenantId TenantId { get; private set; }

    /// <summary>The owning taxonomy's key — half of the composite foreign key.</summary>
    public string TaxonomyKey { get; private set; }

    /// <summary>The owning revision. Items belong to a revision, not a concept.</summary>
    public int SchemaVersion { get; private set; }

    /// <summary>The band, e.g. <c>a1</c> or <c>beginner</c>.</summary>
    public string Key { get; private set; }

    /// <summary>The band's label, per authored locale.</summary>
    public LocalizedText DisplayName { get; private set; }

    /// <summary>Display order. Unique within the revision.</summary>
    public short Sort { get; private set; }

    /// <summary>
    /// Free-form presentation data — a colour, an icon — as JSON, or null.
    /// </summary>
    /// <remarks>
    /// Opaque to LearnStack by design: the corpus's worked example carries
    /// <c>{"color": "#e74c3c"}</c>, and fixing a schema for it would make the
    /// second thing a tenant wants to attach a LearnStack release.
    /// </remarks>
    public string? Metadata { get; private set; }

    internal static TenantLevelTaxonomyItem Create(
        TenantId tenantId,
        string taxonomyKey,
        int schemaVersion,
        string key,
        LocalizedText displayName,
        short sort,
        string? metadata)
    {
        // `tenantId` and `taxonomyKey` are the parent's own TenantId and Key, both
        // validated in CustomizationDefinition's constructor and immutable after it,
        // and AddItem is this internal factory's only caller as of 2026-09-04. They
        // are not re-validated here: a guard no caller can reach is a guard no test
        // can kill. A second caller owes its own validation.
        //
        // The same guard as a concept key, not a second copy of it: an item key is
        // referenced from a tenant-authored JSON Schema (`levelKey: "b2"`), reaches
        // a URL filter, and is half of this row's primary key. Two constants and
        // two orderings would be two answers to one question.
        CustomizationKey.EnsureValid(key, nameof(key));

        ArgumentNullException.ThrowIfNull(displayName);

        if (sort < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sort), sort, "Sort order is not negative.");
        }

        if (metadata is not null)
        {
            JsonValue.EnsureWellFormed(metadata, nameof(metadata));
        }

        return new TenantLevelTaxonomyItem
        {
            TenantId = tenantId,
            TaxonomyKey = taxonomyKey,
            SchemaVersion = schemaVersion,
            Key = key,
            DisplayName = displayName,
            Sort = sort,
            Metadata = metadata,
        };
    }
}
