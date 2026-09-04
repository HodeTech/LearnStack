using FluentAssertions;
using LearnStack.Modules.Customization.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Time;
using Xunit;

namespace LearnStack.Tests.Unit.Modules.Customization;

/// <summary>
/// The two customization aggregates' factories, their lifecycle, and the
/// invariants they refuse to let a caller past.
/// </summary>
/// <remarks>
/// The schema carries the key shape as constraints and that is the second layer,
/// not the first. Two rules here have <b>no</b> database counterpart and exist
/// only in these objects: that a live revision's body is frozen, and that two
/// items may not share a sort order. The first is a contract with every stored
/// instance; the second is a render whose order changes between two identical
/// requests, which no constraint expresses.
/// </remarks>
public sealed class CustomizationAggregateTests
{
    private static readonly FixedClock Clock =
        new(new DateTimeOffset(2026, 9, 4, 9, 0, 0, TimeSpan.Zero));

    private static readonly UserId Actor =
        UserId.From(Guid.Parse("00000000-0000-7000-8000-000000000001"));

    private static readonly TenantId Tenant =
        TenantId.From(Guid.Parse("11111111-1111-7111-8111-111111111111"));

    private static readonly TenantContentTypeId ContentTypeId =
        TenantContentTypeId.From(Guid.Parse("cccccccc-1111-7111-8111-111111111111"));

    private static readonly TenantLevelTaxonomyId TaxonomyId =
        TenantLevelTaxonomyId.From(Guid.Parse("aaaaaaaa-1111-7111-8111-111111111111"));

    private const string Schema =
        """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","properties":{"word":{"type":"string"}}}""";

    private static LocalizedText Label(string english = "Vocabulary Card") =>
        LocalizedText.From(("en", english), ("tr", "Kelime Kartı"));

    private static TenantContentType NewContentType(string key = "vocabulary-card", int version = 1) =>
        TenantContentType.Create(
            ContentTypeId, Tenant, key, version, Label(), Schema, "default-card", Clock, Actor);

    private static TenantLevelTaxonomy NewTaxonomy(string key = "cefr", int version = 1) =>
        TenantLevelTaxonomy.Create(TaxonomyId, Tenant, key, version, Label("CEFR"), Clock, Actor);

    // ── Keys ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("VocabularyCard")]   // the shape the superseded skill documented
    [InlineData("vocabulary_card")]
    [InlineData("vocabulary card")]
    [InlineData("vocabulary--card")]
    [InlineData("-vocabulary")]
    [InlineData("vocabulary:card")]  // a `:` would collide two cache-key tuples
    public void A_key_that_is_not_kebab_case_is_refused(string key)
    {
        var act = () => NewContentType(key);

        act.Should().Throw<ArgumentException>().WithMessage("*URL-safe slug*");
    }

    [Fact]
    public void A_key_past_the_mapped_width_is_refused()
    {
        var act = () => NewContentType(new string('a', CustomizationKey.MaxLength + 1));

        act.Should().Throw<ArgumentException>().WithMessage("*the column holds*");
    }

    [Fact]
    public void A_schema_version_below_one_is_refused()
    {
        var act = () => NewContentType(version: 0);

        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*starts at 1*");
    }

    [Fact]
    public void An_unassigned_identifier_is_refused_before_anything_is_assigned()
    {
        var act = () => TenantContentType.Create(
            Unassigned<TenantContentTypeId>(), Tenant, "vocabulary-card", 1, Label(),
            Schema, "default-card", Clock, Actor);

        act.Should().Throw<ArgumentException>().WithMessage("*never assigned*");
    }

    [Fact]
    public void A_nil_tenant_is_refused_even_though_it_reports_initialized()
    {
        var act = () => TenantContentType.Create(
            ContentTypeId, TenantId.From(Guid.Empty), "vocabulary-card", 1, Label(),
            Schema, "default-card", Clock, Actor);

        act.Should().Throw<ArgumentException>().WithMessage("*belongs to a tenant*");
    }

    // ── Content type ────────────────────────────────────────────────────────

    [Fact]
    public void A_new_content_type_is_a_draft_at_revision_zero()
    {
        var contentType = NewContentType();

        contentType.Status.Should().Be(CustomizationStatus.Draft);
        contentType.SchemaVersion.Should().Be(1);
        contentType.SchemaRevision.Should().Be(0);
        contentType.CreatedAt.Should().Be(Clock.UtcNow);
        contentType.DisplayName.Resolve("tr").Should().Be("Kelime Kartı");
    }

    [Fact]
    public void A_schema_that_is_not_well_formed_json_is_refused_at_the_factory()
    {
        // The column is jsonb: PostgreSQL raises 22P02 three layers from here and
        // names neither the property nor the aggregate.
        var act = () => TenantContentType.Create(
            ContentTypeId, Tenant, "vocabulary-card", 1, Label(), "{", "default-card", Clock, Actor);

        act.Should().Throw<ArgumentException>().WithMessage("*not well-formed JSON*");
    }

    [Fact]
    public void A_renderer_key_outside_the_closed_set_is_refused()
    {
        var act = () => TenantContentType.Create(
            ContentTypeId, Tenant, "vocabulary-card", 1, Label(), Schema, "cefr-card", Clock, Actor);

        act.Should().Throw<ArgumentException>().WithMessage("*not a composite renderer*");
    }

    [Fact]
    public void Every_declared_composite_renderer_is_accepted()
    {
        // The backend's copy of the registry has to admit the whole documented set,
        // not only the four the frontend registers today — a key that is declared
        // and not yet registered renders UnknownBlock rather than failing the save.
        foreach (var key in CompositeRendererKey.All)
        {
            var act = () => TenantContentType.Create(
                ContentTypeId, Tenant, "vocabulary-card", 1, Label(), Schema, key, Clock, Actor);

            act.Should().NotThrow($"'{key}' is in the documented closed set");
        }
    }

    [Fact]
    public void Revising_a_draft_schema_raises_the_revision_and_not_the_version()
    {
        var contentType = NewContentType();

        contentType.ReviseSchema(
            """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object"}""",
            Clock,
            Actor);

        contentType.SchemaRevision.Should().Be(1);
        contentType.SchemaVersion.Should().Be(1, "instances pin the version, never the revision");
    }

    [Fact]
    public void A_published_schema_is_frozen()
    {
        var contentType = NewContentType();
        contentType.Publish(Clock, Actor);

        var act = () => contentType.ReviseSchema(Schema, Clock, Actor);

        act.Should().Throw<InvalidOperationException>().WithMessage("*frozen*");
    }

    [Fact]
    public void Presentation_metadata_stays_mutable_after_publish()
    {
        var contentType = NewContentType();
        contentType.Publish(Clock, Actor);

        // Phase 04: "after first publish, status and presentation metadata are the
        // only mutable columns". A renderer decides how an instance is drawn, never
        // whether it is valid, so changing it cannot invalidate a stored row.
        contentType.Rename(Label("Vocab Card"), Clock, Actor);
        contentType.SetRendererKey("content-list", Clock, Actor);

        contentType.DisplayName.Resolve("en").Should().Be("Vocab Card");
        contentType.RendererKey.Should().Be("content-list");
    }

    // ── Lifecycle, shared by both aggregates ────────────────────────────────

    [Fact]
    public void Publishing_advances_the_status_and_the_concurrency_token()
    {
        var contentType = NewContentType();
        var before = contentType.Version;

        contentType.Publish(Clock, Actor);

        contentType.Status.Should().Be(CustomizationStatus.Active);
        contentType.UpdatedBy.Should().Be(Actor);
        contentType.Version.Should().BeGreaterThan(before, "an audited mutation is a versioned one");
    }

    [Fact]
    public void An_active_revision_cannot_be_published_again()
    {
        var contentType = NewContentType();
        contentType.Publish(Clock, Actor);

        var act = () => contentType.Publish(Clock, Actor);

        act.Should().Throw<InvalidOperationException>().WithMessage("*already live*");
    }

    [Fact]
    public void A_draft_cannot_be_deprecated()
    {
        var act = () => NewContentType().Deprecate(Clock, Actor);

        act.Should().Throw<InvalidOperationException>().WithMessage("*deleted, not deprecated*");
    }

    [Fact]
    public void A_deprecated_revision_cannot_return_to_life()
    {
        var contentType = NewContentType();
        contentType.Publish(Clock, Actor);
        contentType.Deprecate(Clock, Actor);

        var republish = () => contentType.Publish(Clock, Actor);
        var redeprecate = () => contentType.Deprecate(Clock, Actor);

        republish.Should().Throw<InvalidOperationException>();
        redeprecate.Should().Throw<InvalidOperationException>();
    }

    // ── Level taxonomy ──────────────────────────────────────────────────────

    [Fact]
    public void A_taxonomy_with_no_items_cannot_be_published()
    {
        var act = () => NewTaxonomy().Publish(Clock, Actor);

        act.Should().Throw<InvalidOperationException>().WithMessage("*has no items*");
    }

    [Fact]
    public void Items_are_returned_in_sort_order_regardless_of_authoring_order()
    {
        var taxonomy = NewTaxonomy();
        taxonomy.AddItem("c1", LocalizedText.From(("en", "Advanced")), 5, null, Clock, Actor);
        taxonomy.AddItem("a1", LocalizedText.From(("en", "Beginner")), 1, null, Clock, Actor);
        taxonomy.AddItem("b1", LocalizedText.From(("en", "Intermediate")), 3, null, Clock, Actor);

        taxonomy.Items.Select(item => item.Key).Should().ContainInOrder("a1", "b1", "c1");
    }

    [Fact]
    public void A_duplicate_item_key_is_refused()
    {
        var taxonomy = NewTaxonomy();
        taxonomy.AddItem("a1", LocalizedText.From(("en", "Beginner")), 1, null, Clock, Actor);

        var act = () => taxonomy.AddItem("a1", LocalizedText.From(("en", "Novice")), 2, null, Clock, Actor);

        act.Should().Throw<InvalidOperationException>().WithMessage("*already an item*");
    }

    [Fact]
    public void A_duplicate_sort_order_is_refused_because_no_constraint_expresses_it()
    {
        var taxonomy = NewTaxonomy();
        taxonomy.AddItem("a1", LocalizedText.From(("en", "Beginner")), 1, null, Clock, Actor);

        var act = () => taxonomy.AddItem("a2", LocalizedText.From(("en", "Elementary")), 1, null, Clock, Actor);

        act.Should().Throw<InvalidOperationException>().WithMessage("*already taken*");
    }

    [Fact]
    public void A_refused_item_leaves_the_aggregate_exactly_as_it_was()
    {
        var taxonomy = NewTaxonomy();
        taxonomy.AddItem("a1", LocalizedText.From(("en", "Beginner")), 1, null, Clock, Actor);
        var revision = taxonomy.SchemaRevision;

        var act = () => taxonomy.AddItem("a1", LocalizedText.From(("en", "Novice")), 9, null, Clock, Actor);

        act.Should().Throw<InvalidOperationException>();
        taxonomy.Items.Should().ContainSingle();
        taxonomy.SchemaRevision.Should().Be(revision, "a refused edit is not an edit");
    }

    [Fact]
    public void Item_metadata_must_be_well_formed_json_when_present()
    {
        var taxonomy = NewTaxonomy();

        var bad = () => taxonomy.AddItem("a1", LocalizedText.From(("en", "B")), 1, "{", Clock, Actor);
        var good = () => taxonomy.AddItem("a2", LocalizedText.From(("en", "E")), 2, """{"color":"#e74c3c"}""", Clock, Actor);
        var absent = () => taxonomy.AddItem("b1", LocalizedText.From(("en", "I")), 3, null, Clock, Actor);

        bad.Should().Throw<ArgumentException>().WithMessage("*not well-formed JSON*");
        good.Should().NotThrow();
        absent.Should().NotThrow();
    }

    [Fact]
    public void Items_cannot_be_added_to_or_removed_from_a_published_taxonomy()
    {
        var taxonomy = NewTaxonomy();
        taxonomy.AddItem("a1", LocalizedText.From(("en", "Beginner")), 1, null, Clock, Actor);
        taxonomy.Publish(Clock, Actor);

        // Dropping a band a content row references is a change that row fails, so
        // it raises schema_version like any other breaking change.
        var add = () => taxonomy.AddItem("a2", LocalizedText.From(("en", "Elementary")), 2, null, Clock, Actor);
        var remove = () => taxonomy.RemoveItem("a1", Clock, Actor);

        add.Should().Throw<InvalidOperationException>().WithMessage("*frozen*");
        remove.Should().Throw<InvalidOperationException>().WithMessage("*frozen*");
    }

    [Fact]
    public void Removing_an_item_that_is_not_there_is_refused()
    {
        var act = () => NewTaxonomy().RemoveItem("a1", Clock, Actor);

        act.Should().Throw<InvalidOperationException>().WithMessage("*is not an item*");
    }

    [Fact]
    public void A_taxonomy_holds_at_most_the_declared_number_of_items()
    {
        var taxonomy = NewTaxonomy();

        for (var i = 0; i < TenantLevelTaxonomy.MaxItems; i++)
        {
            taxonomy.AddItem($"l{i}", LocalizedText.From(("en", $"Level {i}")), (short)i, null, Clock, Actor);
        }

        var act = () => taxonomy.AddItem("extra", LocalizedText.From(("en", "Extra")), 999, null, Clock, Actor);

        act.Should().Throw<InvalidOperationException>().WithMessage("*at most*");
    }

    [Fact]
    public void An_item_key_carries_the_owning_revision_not_only_the_concept()
    {
        var taxonomy = NewTaxonomy(version: 2);
        taxonomy.AddItem("a1", LocalizedText.From(("en", "Beginner")), 1, null, Clock, Actor);

        var item = taxonomy.Items.Single();

        // Two revisions of `cefr` may disagree about which bands exist, so the item
        // belongs to (tenant, taxonomy, version, key) — not to (tenant, taxonomy, key).
        item.SchemaVersion.Should().Be(2);
        item.TaxonomyKey.Should().Be("cefr");
        item.TenantId.Should().Be(Tenant);
    }

    [Theory]
    [InlineData("A1")]
    [InlineData("level_one")]
    [InlineData("level one")]
    public void An_item_key_that_is_not_kebab_case_is_refused(string itemKey)
    {
        // Item keys are referenced from tenant-authored schemas as `levelKey: "b2"`
        // and reach URL filters, so they carry the same shape as a concept key.
        var act = () => NewTaxonomy().AddItem(itemKey, LocalizedText.From(("en", "X")), 1, null, Clock, Actor);

        act.Should().Throw<ArgumentException>().WithMessage("*URL-safe slug*");
    }

    [Fact]
    public void A_negative_sort_order_is_refused()
    {
        var act = () => NewTaxonomy().AddItem("a1", LocalizedText.From(("en", "B")), -1, null, Clock, Actor);

        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*not negative*");
    }

    /// <summary>
    /// A Vogen id that was never assigned. <c>default(T)</c> written inline is a
    /// compile error (VOG009); through a generic it is the state a caller reaches
    /// by declaring a field and forgetting to fill it, which is exactly what the
    /// factory guard is for.
    /// </summary>
    private static T Unassigned<T>()
        where T : struct => default;
}
