using System.Text.Json;
using FluentAssertions;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.Audit.Capture;
using LearnStack.SharedKernel.Audit;
using LearnStack.Modules.Customization.Domain;
using LearnStack.Modules.Customization.Infrastructure.Persistence;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using LearnStack.SharedKernel.Secrets;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Audit;

/// <summary>
/// What the interceptor puts in the buffer, and what it refuses to put there.
/// </summary>
/// <remarks>
/// <para>
/// No database. A <c>ChangeTracker</c> needs no connection, and the capture reads
/// nothing else — so these cases drive the real interceptor over real EF entries and
/// measure exactly what a save would have given it.
/// </para>
/// <para>
/// The entities are the suite's own, and that is not laziness: neither redaction gate has
/// a shipped consumer yet. No property in Tenancy or Customization carries
/// <see cref="PiiSensitiveAttribute"/> and none is named for a
/// <see cref="SensitiveTokenCatalog"/> token — the first personal data lands with Identity
/// in Phase 03. A gate with no test until its first consumer arrives is a gate that ships
/// wrong and is discovered by the consumer.
/// </para>
/// </remarks>
public sealed class AuditChangeTrackerInterceptorTests
{
    [Fact]
    public void An_insert_captures_every_property_as_the_after_state_and_no_before_state()
    {
        using var context = new ProbeContext();
        var capture = new AuditStateCapture();

        context.People.Add(new Person
        {
            Id = 1,
            DisplayName = "Ada",
            Password = "hunter2",
            Notes = "first",
            Nickname = "n",
        });

        new AuditChangeTrackerInterceptor(capture).Capture(context);

        var change = capture.Changes.Should().ContainSingle().Subject;
        change.EntityType.Should().Be(nameof(Person));
        change.EntityId.Should().Be("1");
        change.BeforeJson.Should().BeNull("an insert has no prior state");
        change.AfterJson.Should().NotBeNull();

        change.Fields.Select(field => field.Path).Should().BeEquivalentTo(
            "/Person/1/Id", "/Person/1/DisplayName", "/Person/1/Password", "/Person/1/Notes",
            "/Person/1/Nickname", "/Person/1/Inherited");
    }

    [Fact]
    public void A_modify_captures_only_the_properties_that_changed()
    {
        // The diff is what a reviewer reads. Listing every property on a modify would
        // bury the one that moved, and on a wide entity it is the difference between a
        // legible record and a wall.
        using var context = new ProbeContext();
        var capture = new AuditStateCapture();

        var person = new Person { Id = 7, DisplayName = "Ada", Password = "p", Notes = "n", Nickname = "n" };
        context.Attach(person);
        person.DisplayName = "Ada Lovelace";

        new AuditChangeTrackerInterceptor(capture).Capture(context);

        var change = capture.Changes.Should().ContainSingle().Subject;
        change.Fields.Select(field => field.Path).Should().Equal("/Person/7/DisplayName");
        change.Fields[0].BeforeJson.Should().Be("\"Ada\"");
        change.Fields[0].AfterJson.Should().Be("\"Ada Lovelace\"");

        change.BeforeJson.Should().NotBeNull("a modify has both states");
        change.AfterJson.Should().NotBeNull();
    }

    [Fact]
    public void A_delete_captures_the_before_state_and_no_after_state()
    {
        using var context = new ProbeContext();
        var capture = new AuditStateCapture();

        var person = new Person { Id = 3, DisplayName = "Ada", Password = "p", Notes = "n", Nickname = "n" };
        context.Attach(person);
        context.Remove(person);

        new AuditChangeTrackerInterceptor(capture).Capture(context);

        var change = capture.Changes.Should().ContainSingle().Subject;
        change.BeforeJson.Should().NotBeNull();
        change.AfterJson.Should().BeNull();
        change.Fields.Should().NotBeEmpty("a delete is a change to every property");
    }

    [Fact]
    public void An_unchanged_entity_is_not_captured()
    {
        using var context = new ProbeContext();
        var capture = new AuditStateCapture();

        context.Attach(new Person { Id = 9, DisplayName = "Ada", Password = "p", Notes = "n", Nickname = "n" });

        new AuditChangeTrackerInterceptor(capture).Capture(context);

        capture.Changes.Should().BeEmpty();
    }

    [Fact]
    public void A_marked_property_is_replaced_rather_than_dropped()
    {
        // The property is NOT removed: the diff still records THAT it changed, which is
        // the fact a reviewer is usually asking about. Dropping it would make a password
        // rotation indistinguishable from a request that touched nothing.
        using var context = new ProbeContext();
        var capture = new AuditStateCapture();

        var person = new Person { Id = 1, DisplayName = "Ada", Password = "old", Notes = "n", Nickname = "old" };
        context.Attach(person);
        person.Nickname = "new";

        new AuditChangeTrackerInterceptor(capture).Capture(context);

        var field = capture.Changes.Single().Fields.Should().ContainSingle().Subject;
        field.Path.Should().Be("/Person/1/Nickname");
        field.BeforeJson.Should().Be(Quoted(SensitiveTokenCatalog.RedactedValue));
        field.AfterJson.Should().Be(Quoted(SensitiveTokenCatalog.RedactedValue));

        capture.Changes.Single().AfterJson.Should().NotContain("new",
            "the marked value never reaches the buffer, so there is nothing to scrub later");
    }

    [Fact]
    public void A_property_the_token_list_names_is_redacted_without_a_marker()
    {
        // The list runs beside the marker rather than instead of it: the list catches
        // what nobody marked, the marker catches what the list's tokens do not name.
        // `Password` carries no attribute on the probe entity.
        using var context = new ProbeContext();
        var capture = new AuditStateCapture();

        context.People.Add(new Person
        {
            Id = 2,
            DisplayName = "Ada",
            Password = "hunter2",
            Notes = "n",
            Nickname = "n",
        });

        new AuditChangeTrackerInterceptor(capture).Capture(context);

        var change = capture.Changes.Single();
        change.AfterJson.Should().NotContain("hunter2");
        change.Fields.Single(field => field.Path == "/Person/2/Password")
            .AfterJson.Should().Be(Quoted(SensitiveTokenCatalog.RedactedValue));

        // And an ordinary property beside it is untouched, so the gate is not simply
        // redacting everything.
        change.Fields.Single(field => field.Path == "/Person/2/DisplayName")
            .AfterJson.Should().Be("\"Ada\"");
    }

    [Fact]
    public void A_marker_declared_on_a_base_class_is_honoured()
    {
        // Every audit column in this repository is declared on AuditableEntity<TId> rather
        // than on the aggregate, so a lookup that stopped at the entity type would miss
        // every marker that matters and write the value it was asked to hide.
        using var context = new ProbeContext();
        var capture = new AuditStateCapture();

        context.People.Add(new Person
        {
            Id = 5,
            DisplayName = "Ada",
            Password = "p",
            Notes = "n",
            Nickname = "n",
            Inherited = "personal",
        });

        new AuditChangeTrackerInterceptor(capture).Capture(context);

        var change = capture.Changes.Single();
        change.AfterJson.Should().NotContain("personal");
        change.Fields.Single(field => field.Path == "/Person/5/Inherited")
            .AfterJson.Should().Be(Quoted(SensitiveTokenCatalog.RedactedValue));
    }

    [Fact]
    public void The_bookkeeping_columns_the_row_already_carries_are_not_snapshotted()
    {
        // TenantId is the row's own tenant_id, so repeating it says nothing; CreatedAt,
        // UpdatedAt and RowVersion move on every write, so a diff carrying them buries the
        // property that changed under three that always do (Audit Subsystem § 3). The
        // soft-delete pair is deliberately NOT excluded — DeletedAt moving is the whole
        // content of a soft delete.
        using var context = new ProbeContext();
        var capture = new AuditStateCapture();

        context.Add(new Bookkept
        {
            Id = 1,
            TenantId = Guid.Empty,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
            Version = 3,
            DeletedAt = DateTimeOffset.UnixEpoch,
            Payload = "p",
        });

        new AuditChangeTrackerInterceptor(capture).Capture(context);

        capture.Changes.Single().Fields.Select(field => field.Path).Should().BeEquivalentTo(
            "/Bookkept/1/Id", "/Bookkept/1/DeletedAt", "/Bookkept/1/Payload");
    }

    [Fact]
    public void A_jsonb_column_passes_through_and_a_text_column_that_looks_like_JSON_does_not()
    {
        // The passthrough is decided by the COLUMN, never by the value. Deciding from the
        // value retyped ordinary text whose content happened to parse — contradicting the
        // rule that 42 and "42" are different values — and emitted escapes PostgreSQL's
        // jsonb refuses, failing the INSERT inside the business transaction.
        using var context = new ProbeContext();
        var capture = new AuditStateCapture();

        context.Add(new Documented { Id = 1, Document = "{\"a\":1}", Text = "[1,2,3]" });

        new AuditChangeTrackerInterceptor(capture).Capture(context);

        var fields = capture.Changes.Single().Fields;
        fields.Single(field => field.Path == "/Documented/1/Document")
            .AfterJson.Should().Be("{\"a\":1}", "a jsonb column is emitted verbatim");
        fields.Single(field => field.Path == "/Documented/1/Text")
            .AfterJson.Should().Be("\"[1,2,3]\"", "a text column stays the string it is");
    }

    [Fact]
    public void An_enum_is_captured_by_name_and_not_by_ordinal()
    {
        // The three closed-set columns beside these store the name. A snapshot storing 1
        // where the row stores Denied would make the two halves of one record disagree —
        // and change meaning silently the day a member is inserted into the enum.
        using var context = new ProbeContext();
        var capture = new AuditStateCapture();

        context.Add(new Documented { Id = 2, Document = "{}", Text = "t", Status = Grade.Second });

        new AuditChangeTrackerInterceptor(capture).Capture(context);

        capture.Changes.Single().Fields
            .Single(field => field.Path == "/Documented/2/Status")
            .AfterJson.Should().Be("\"Second\"");
    }

    [Fact]
    public void A_converted_property_is_captured_as_the_column_holds_it()
    {
        // The defect this case exists for was measured, not imagined: `CurrentValue` is the
        // MODEL value, and for a converted property the model and the column are different
        // objects with different shapes. A LocalizedText display name stores
        // {"en":"Vocabulary Card","tr":"Kelime Kartı"} and serialises from its CLR side as
        // {"Locales":["en","tr"]} — which records which languages exist and none of the
        // text. Every display name in Customization is one of these, they are the
        // tenant-authored values that module exists for, and audit_log is append-only, so
        // nothing could recover the words afterwards.
        using var context = new ProbeContext();
        var capture = new AuditStateCapture();

        context.Add(new Converted
        {
            Id = 1,
            Label = Label.From("Vocabulary Card"),
            Grade = Grade.Second,
        });

        new AuditChangeTrackerInterceptor(capture).Capture(context);

        var fields = capture.Changes.Single().Fields;

        fields.Single(field => field.Path == "/Converted/1/Label")
            .AfterJson.Should().Be(
                """{"text":"Vocabulary Card"}""",
                "the jsonb column holds the converter's output, not the CLR object's shape");

        fields.Single(field => field.Path == "/Converted/1/Grade")
            .AfterJson.Should().Be(
                "\"Second\"",
                "an enum mapped as text is stored by name, and the snapshot must agree "
                + "with the column it describes");
    }

    [Theory]
    // By name and with a reason each (ADR-0044 § 7). The audit tables cannot arise
    // anyway — the store writes parameterised SQL and never a DbContext — and they are
    // excluded regardless, because "cannot arise" is a property of today's store while
    // the exclusion is a property of the design.
    [InlineData(typeof(OutboxMessage))]
    [InlineData(typeof(IdempotencyKey))]
    [InlineData(typeof(AuditEntry))]
    [InlineData(typeof(AuditConfig))]
    public void The_excluded_types_are_never_captured(Type excluded)
    {
        using var context = new ProbeContext();
        var capture = new AuditStateCapture();

        context.Add(Activator.CreateInstance(excluded)!);

        new AuditChangeTrackerInterceptor(capture).Capture(context);

        capture.Changes.Should().BeEmpty("{0} is excluded by name", excluded.Name);
    }

    [Fact]
    public void A_composite_key_renders_in_key_order()
    {
        // tenant_level_taxonomy_items is the shipped four-column case. The join is `/`,
        // the same separator the pointer uses for a path segment, so a reader that can
        // read one can read the other.
        using var context = new ProbeContext();
        var capture = new AuditStateCapture();

        context.Add(new Band { TenantId = "t", Taxonomy = "proficiency", Key = "beginner" });

        new AuditChangeTrackerInterceptor(capture).Capture(context);

        capture.Changes.Single().EntityId.Should().Be("t/proficiency/beginner");

        // And the key is ONE pointer segment, so its `/` is escaped the way RFC 6901 says.
        // Unescaped, "/Band/t/proficiency/beginner/Key" reads as four levels of nesting.
        capture.Changes.Single().Fields.Should().Contain(
            field => field.Path == "/Band/t~1proficiency~1beginner/Key");
    }

    [Fact]
    public void A_snapshot_over_the_cap_becomes_an_elision_record()
    {
        using var context = new ProbeContext();
        var capture = new AuditStateCapture();

        context.People.Add(new Person
        {
            Id = 4,
            DisplayName = "Ada",
            Password = "p",
            Notes = new string('x', AuditJson.MaxBytes + 1024),
            Nickname = "n",
        });

        new AuditChangeTrackerInterceptor(capture).Capture(context);

        using var after = JsonDocument.Parse(capture.Changes.Single().AfterJson!);
        after.RootElement.ValueKind.Should().Be(JsonValueKind.Object,
            "after_state is an object on both sides of the cap");
        after.RootElement.GetProperty("_elided").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Several_flushes_accumulate_rather_than_replace()
    {
        // ProvisionTenantCommand saves three times inside one transaction, which is
        // exactly why the row is composed later, once, by the frame that owns the commit.
        using var context = new ProbeContext();
        var capture = new AuditStateCapture();
        var interceptor = new AuditChangeTrackerInterceptor(capture);

        context.People.Add(new Person { Id = 1, DisplayName = "A", Password = "p", Notes = "n", Nickname = "n" });
        interceptor.Capture(context);

        context.People.Add(new Person { Id = 2, DisplayName = "B", Password = "p", Notes = "n", Nickname = "n" });
        interceptor.Capture(context);

        capture.Changes.Should().HaveCount(3,
            "the first entity is still Added on the second pass, so it is captured again "
            + "— the store merges by entity type, earliest before and latest after");
    }

    [Fact]
    public void Every_shipped_localized_display_name_is_captured_as_its_stored_document()
    {
        // The stand-in above kills the mutation; this one keeps the stand-in honest. It
        // runs the real interceptor over the REAL Customization model, so a change to
        // CustomizationMapping.HasLocalizedText() — the mapping every display name in the
        // schema shares — is caught here rather than by a reader of audit_log noticing the
        // words are gone.
        var options = new DbContextOptionsBuilder<CustomizationDbContext>()
            .UseNpgsql("Host=model-only;Database=model-only;Username=model-only")
            .Options;

        using var context = new CustomizationDbContext(options, StaticTenantContextAccessor.Unresolved);
        var capture = new AuditStateCapture();

        var label = LocalizedText.From(("en", "Vocabulary Card"), ("tr", "Kelime Kartı"));

        context.Add(TenantContentType.Create(
            TenantContentTypeId.From(Guid.CreateVersion7()),
            TenantId.From(Guid.CreateVersion7()),
            "vocabulary-card",
            1,
            label,
            """{"type":"object"}""",
            "default-card",
            new FixedClock(DateTimeOffset.UnixEpoch),
            UserId.From(Guid.CreateVersion7())));

        new AuditChangeTrackerInterceptor(capture).Capture(context);

        capture.Changes.Single().Fields
            .Single(field => field.Path.EndsWith("/DisplayName", StringComparison.Ordinal))
            .AfterJson.Should().Be(
                label.ToJson(),
                "the snapshot records what the jsonb column holds — the words, not the "
                + "list of locales the CLR object exposes");
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    [Fact]
    public void The_real_concurrency_token_is_left_out_of_the_diff()
    {
        // Against AuditableEntity<TId> itself, not a stand-in that happens to share a
        // name. The exclusion set read "RowVersion" — which is the COLUMN — while the EF
        // model matches on the PROPERTY, and AuditableEntity declares `Version`. The entry
        // therefore excluded nothing on any shipped aggregate, and every diff carried the
        // token that moves on every write. A stand-in with the wrong name would have
        // passed either way, which is why this one uses the real base class.
        var options = new DbContextOptionsBuilder<CustomizationDbContext>()
            .UseNpgsql("Host=model-only;Database=model-only;Username=model-only")
            .Options;

        using var context = new CustomizationDbContext(options, StaticTenantContextAccessor.Unresolved);
        var capture = new AuditStateCapture();

        var contentType = TenantContentType.Create(
            TenantContentTypeId.From(Guid.CreateVersion7()),
            TenantId.From(Guid.CreateVersion7()),
            "vocabulary-card",
            1,
            LocalizedText.From(("en", "Vocabulary Card")),
            """{"type":"object"}""",
            "default-card",
            new FixedClock(DateTimeOffset.UnixEpoch),
            UserId.From(Guid.CreateVersion7()));

        context.Add(contentType);

        // The property really is there and really is called Version, or this case would be
        // asserting the absence of something that never existed — the shape of vacuous
        // assertion this suite has already caught once.
        context.Entry(contentType).Properties.Select(property => property.Metadata.Name)
            .Should().Contain("Version");

        new AuditChangeTrackerInterceptor(capture).Capture(context);

        var paths = capture.Changes.Single().Fields.Select(field => field.Path).ToList();
        var at = $"/TenantContentType/{contentType.Id.Value}/";

        // The positive assertion FIRST, and on the same prefix the negatives use. Pointers
        // are instance-qualified now; left spelled "/TenantContentType/Version", the three
        // negatives below would pass whatever the exclusion set held — the vacuous shape
        // this case was written against in the first place.
        paths.Should().Contain(at + "Key", "the record itself stays");
        paths.Should().NotContain(at + "Version");
        paths.Should().NotContain(at + "CreatedAt");
        paths.Should().NotContain(at + "TenantId");
    }

    [Fact]
    public void A_marker_on_a_private_base_property_is_honoured()
    {
        // The shape BindingFlags.NonPublic alone cannot see: Type.GetProperty searches the
        // GIVEN type only, so a private property declared on a base class is invisible to
        // it whatever flags are passed. The walk is what finds it, and a marker the lookup
        // misses is a marker that reports success and writes the value.
        using var context = new ProbeContext();
        var capture = new AuditStateCapture();

        var hidden = new Hidden { Id = 1, Open = "public" };
        hidden.SetBirthplace("private-personal-data");

        // The name is no token, or the marker is not what this case measures.
        SensitiveTokenCatalog.IsSensitive("Birthplace").Should().BeFalse();

        context.Add(hidden);

        new AuditChangeTrackerInterceptor(capture).Capture(context);

        var change = capture.Changes.Single();
        change.AfterJson.Should().NotContain("private-personal-data");
        change.Fields.Single(field => field.Path == "/Hidden/1/Birthplace")
            .AfterJson.Should().Be(Quoted(SensitiveTokenCatalog.RedactedValue));
    }

    [Fact]
    public void The_entity_id_is_rendered_the_way_the_key_column_stores_it()
    {
        // entity_id is what a reader joins on, so it must not be able to disagree with the
        // row's own key column. Every key shipped today renders identically whether or not
        // the converter runs — a Vogen id's ToString() equals its Guid's — which is exactly
        // why the inconsistency would have gone unnoticed. This key uses a converter whose
        // provider text DIFFERS from the model value's ToString(), which is the only shape
        // that can tell the two mechanisms apart.
        using var context = new ProbeContext();
        var capture = new AuditStateCapture();

        context.Add(new Keyed { Code = new Code("abc"), Payload = "p" });

        new AuditChangeTrackerInterceptor(capture).Capture(context);

        capture.Changes.Single().EntityId.Should().Be(
            "CODE:ABC",
            "the column stores the converter's output, and entity_id points at the column");
    }

    [Fact]
    public void A_contained_entity_is_captured_inside_its_root_and_never_on_its_own()
    {
        // The shape ADR-0044 Amendment 6 § 4 decides. Captured on its own, a member carried
        // a type name no catalogue entry declares, so the composer dropped it: a taxonomy's
        // band labels — the content the tenant authored — never reached the taxonomy's row.
        using var context = new ProbeContext();
        var capture = new AuditStateCapture();

        var folder = new Folder { Id = 1, Title = "Levels" };
        folder.Leaves.Add(new Leaf { FolderId = 1, Key = "b", Label = "Upper" });
        folder.Leaves.Add(new Leaf { FolderId = 1, Key = "a", Label = "Lower" });
        context.Add(folder);

        new AuditChangeTrackerInterceptor(capture).Capture(context);

        var change = capture.Changes.Should().ContainSingle(
            "the members are part of their root's capture, not captures of their own").Subject;
        change.EntityType.Should().Be(nameof(Folder));

        using var after = JsonDocument.Parse(change.AfterJson!);
        var leaves = after.RootElement.GetProperty("Leaves");

        // Keyed by each member's own key, in key order — an index would name a different
        // member the moment one ahead of it was removed.
        leaves.EnumerateObject().Select(member => member.Name).Should().Equal("a", "b");
        leaves.GetProperty("b").GetProperty("Label").GetString().Should().Be("Upper");
        leaves.GetProperty("a").TryGetProperty("FolderId", out _).Should().BeFalse(
            "the owner's key is the path the member sits under; repeating it says nothing");

        change.Fields.Should().Contain(field =>
            field.Path == "/Folder/1/Leaves/b/Label" && field.AfterJson == "\"Upper\"");
    }

    [Fact]
    public void A_removed_member_is_in_the_diff_and_a_loaded_collection_is_not_claimed_complete()
    {
        // A removed band strands every row that referenced it, so its removal is the fact the
        // row most needs to carry — and the diff carries it, before value and all.
        //
        // The snapshots do NOT carry the membership, although EF says the collection is
        // loaded. IsLoaded is true after a filtered Include too, over a partial collection —
        // measured against real PostgreSQL by the second review of Packet 9 — so it proves
        // nothing, and a loaded owner's membership is left out as unknown rather than written
        // down as "these are all the bands" (ADR-0044 Amendment 6 § 4).
        using var context = new ProbeContext();
        var capture = new AuditStateCapture();

        var folder = new Folder { Id = 2, Title = "Levels" };
        folder.Leaves.Add(new Leaf { FolderId = 2, Key = "a", Label = "Lower" });
        folder.Leaves.Add(new Leaf { FolderId = 2, Key = "b", Label = "Upper" });
        context.Attach(folder);

        // What an Include — filtered or not — leaves behind; Attach alone does not set it.
        context.Entry(folder).Collection(owner => owner.Leaves).IsLoaded = true;

        folder.Leaves.RemoveAt(1);

        new AuditChangeTrackerInterceptor(capture).Capture(context);

        var change = capture.Changes.Should().ContainSingle().Subject;
        change.EntityType.Should().Be(nameof(Folder), "an unchanged root is still what the row is about");

        change.Fields.Should().Contain(field =>
            field.Path == "/Folder/2/Leaves/b/Label"
            && field.BeforeJson == "\"Upper\"" && field.AfterJson == null);
        change.Fields.Should().NotContain(field => field.Path.StartsWith("/Folder/2/Leaves/a/",
            StringComparison.Ordinal), "an unchanged member is not a change");

        using var before = JsonDocument.Parse(change.BeforeJson!);
        using var after = JsonDocument.Parse(change.AfterJson!);
        before.RootElement.TryGetProperty("Leaves", out _).Should().BeFalse(
            "a loaded owner's membership is not known to be complete");
        after.RootElement.TryGetProperty("Leaves", out _).Should().BeFalse();
    }

    [Fact]
    public void An_unloaded_collection_is_left_out_rather_than_recorded_empty()
    {
        // Not loaded is not known. An owner read without its collection and then modified
        // would otherwise be written down as owning nothing — a claim that every member was
        // removed, on a table nothing can correct.
        using var context = new ProbeContext();
        var capture = new AuditStateCapture();

        var folder = new Folder { Id = 3, Title = "Levels" };
        context.Attach(folder);
        folder.Title = "Grades";

        new AuditChangeTrackerInterceptor(capture).Capture(context);

        using var after = JsonDocument.Parse(capture.Changes.Single().AfterJson!);
        after.RootElement.GetProperty("Title").GetString().Should().Be("Grades");
        after.RootElement.TryGetProperty("Leaves", out _).Should().BeFalse();
    }

    [Fact]
    public void An_owner_created_earlier_in_the_request_keeps_its_members()
    {
        // Provisioning's shape: the tenant is created in the first flush and modified in the
        // third, and the row's after_state is the LATEST capture. EF reports IsLoaded=false
        // for a new entity and keeps reporting it after the save — measured — so a rule that
        // trusted IsLoaded alone would drop every member from the capture that matters.
        using var context = new ProbeContext();
        var capture = new AuditStateCapture();
        var interceptor = new AuditChangeTrackerInterceptor(capture);

        var folder = new Folder { Id = 4, Title = "Levels" };
        folder.Leaves.Add(new Leaf { FolderId = 4, Key = "a", Label = "Lower" });
        context.Add(folder);

        interceptor.Capture(context);
        context.ChangeTracker.AcceptAllChanges();

        context.Entry(folder).Collection(owner => owner.Leaves).IsLoaded.Should().BeFalse(
            "the premise: EF does not call a new entity's collection loaded");

        folder.Title = "Grades";
        interceptor.Capture(context);

        using var after = JsonDocument.Parse(capture.Changes[^1].AfterJson!);
        after.RootElement.GetProperty("Leaves").GetProperty("a").GetProperty("Label")
            .GetString().Should().Be("Lower");
    }

    [Fact]
    public void A_member_whose_owner_is_not_tracked_is_captured_on_its_own()
    {
        // Folded when its owner is there to fold it into; never dropped when it is not.
        using var context = new ProbeContext();
        var capture = new AuditStateCapture();

        context.Add(new Leaf { FolderId = 9, Key = "a", Label = "Lower" });

        new AuditChangeTrackerInterceptor(capture).Capture(context);

        var change = capture.Changes.Should().ContainSingle().Subject;
        change.EntityType.Should().Be(nameof(Leaf));
        change.EntityId.Should().Be("9/a");
    }

    [Fact]
    public void An_aggregate_root_is_never_folded_into_another()
    {
        // A navigation from one aggregate to another is a reference, not containment. Each
        // root is its own row's subject, and folding one into another would attribute its
        // change to the wrong aggregate.
        using var context = new ProbeContext();
        var capture = new AuditStateCapture();

        var holder = new Holder { Id = 1 };
        holder.Children.Add(new Rooted { Id = new ProbeId(Guid.CreateVersion7()), HolderId = 1, Name = "n" });
        context.Add(holder);

        new AuditChangeTrackerInterceptor(capture).Capture(context);

        capture.Changes.Select(change => change.EntityType).Should().BeEquivalentTo(
            [nameof(Holder), nameof(Rooted)]);
        capture.Changes.Single(change => change.EntityType == nameof(Holder)).AfterJson
            .Should().NotContain("Children");
    }

    [Fact]
    public void The_bands_a_tenant_authored_reach_the_taxonomy_capture()
    {
        // The shipped case, on the REAL Customization model: the stand-ins above prove the
        // mechanism, and this proves the mapping every taxonomy uses reaches it — the
        // containment is read from CustomizationDbContext's own HasMany(Items).
        using var context = RealCustomizationContext();
        var capture = new AuditStateCapture();
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var actor = UserId.From(Guid.CreateVersion7());

        var taxonomy = TenantLevelTaxonomy.Create(
            TenantLevelTaxonomyId.From(Guid.CreateVersion7()),
            TenantId.From(Guid.CreateVersion7()),
            "cefr",
            1,
            LocalizedText.From(("en", "CEFR")),
            clock,
            actor);
        taxonomy.AddItem("a1", LocalizedText.From(("en", "Breakthrough")), 1, """{"color":"#e74c3c"}""", clock, actor);
        taxonomy.AddItem("b2", LocalizedText.From(("en", "Vantage")), 2, null, clock, actor);
        context.Add(taxonomy);

        new AuditChangeTrackerInterceptor(capture).Capture(context);

        var change = capture.Changes.Should().ContainSingle().Subject;
        change.EntityType.Should().Be(nameof(TenantLevelTaxonomy));
        change.AfterJson.Should().Contain("Breakthrough").And.Contain("#e74c3c").And.Contain("Vantage");

        change.Fields.Should().Contain(field =>
            field.Path == $"/TenantLevelTaxonomy/{taxonomy.Id.Value}/Items/b2/DisplayName");

        using var after = JsonDocument.Parse(change.AfterJson!);
        var band = after.RootElement.GetProperty("Items").GetProperty("a1");
        band.TryGetProperty("TaxonomyKey", out _).Should().BeFalse();
        band.TryGetProperty("SchemaVersion", out _).Should().BeFalse();
        band.GetProperty("Metadata").GetProperty("color").GetString().Should().Be("#e74c3c",
            "metadata is a jsonb column, so it lands as the document it is");
    }

    [Fact]
    public void Designation_and_capture_render_one_key()
    {
        // The subject a handler designates and the entity_id the interceptor captures are
        // compared as TEXT, by the composer, so they must be spelled one way. Two renderings
        // of one Guid that differed — a format specifier, a converter — would leave every
        // publication's row with an entity_id and no state, silently.
        using var context = RealCustomizationContext();
        var capture = new AuditStateCapture();

        capture.DeclareIntent(new AuditIntent(
            AuditEntryId.From(Guid.CreateVersion7()),
            TenantId.From(Guid.CreateVersion7()),
            OrganizationId: null,
            ActorUserId: null,
            CorrelationId: null,
            "customization",
            "customization.content_type.publish",
            OperationType.Update,
            OperationClass.Must,
            typeof(TenantContentType),
            DateTimeOffset.UnixEpoch));

        var contentType = TenantContentType.Create(
            TenantContentTypeId.From(Guid.CreateVersion7()),
            TenantId.From(Guid.CreateVersion7()),
            "vocabulary-card",
            1,
            LocalizedText.From(("en", "Vocabulary Card")),
            """{"type":"object"}""",
            "default-card",
            new FixedClock(DateTimeOffset.UnixEpoch),
            UserId.From(Guid.CreateVersion7()));
        context.Add(contentType);

        new AuditChangeTrackerInterceptor(capture).Capture(context);
        capture.Designate(contentType);

        capture.Intents.Single().SubjectId.Should().Be(capture.Changes.Single().EntityId);
    }

    private static CustomizationDbContext RealCustomizationContext() =>
        new(
            new DbContextOptionsBuilder<CustomizationDbContext>()
                .UseNpgsql("Host=model-only;Database=model-only;Username=model-only")
                .Options,
            StaticTenantContextAccessor.Unresolved);

    private static string Quoted(string value) => JsonSerializer.Serialize(value);

    private sealed class ProbeContext : DbContext
    {
        public DbSet<Person> People => Set<Person>();

        protected override void OnConfiguring(DbContextOptionsBuilder options) =>
            // Never opened: a ChangeTracker needs no connection, and nothing here saves.
            options.UseNpgsql("Host=model-only;Database=model-only;Username=model-only");

        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.Entity<Person>();
            builder.Entity<Band>().HasKey(band => new { band.TenantId, band.Taxonomy, band.Key });
            builder.Entity<Folder>()
                .HasMany(folder => folder.Leaves)
                .WithOne()
                .HasForeignKey(leaf => leaf.FolderId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.Entity<Leaf>().HasKey(leaf => new { leaf.FolderId, leaf.Key });
            builder.Entity<Holder>()
                .HasMany(holder => holder.Children)
                .WithOne()
                .HasForeignKey(child => child.HolderId);
            builder.Entity<Rooted>().Property(rooted => rooted.Id)
                .HasConversion(id => id.Value, value => new ProbeId(value));
            builder.Entity<Bookkept>();
            builder.Entity<Hidden>(entity => entity.Property("Birthplace"));
            builder.Entity<Keyed>(entity =>
            {
                entity.HasKey(keyed => keyed.Code);
                entity.Property(keyed => keyed.Code).HasConversion(
                    code => "CODE:" + code.Value.ToUpperInvariant(),
                    stored => new Code(stored.Substring(5).ToLowerInvariant()));
            });
            builder.Entity<Converted>(entity =>
            {
                // Exactly the shape CustomizationMapping.HasLocalizedText() gives every
                // display name: a value converter onto a string, in a jsonb column.
                entity.Property(converted => converted.Label)
                    .HasConversion(
                        label => label.ToJson(),
                        json => Label.FromJson(json))
                    .HasColumnType("jsonb");

                // And the shape HasEnumAsText() gives every closed-set column.
                entity.Property(converted => converted.Grade)
                    .HasConversion(grade => grade.ToString(), text => Enum.Parse<Grade>(text))
                    .HasColumnType("text");
            });
            builder.Entity<Documented>(entity =>
            {
                entity.Property(documented => documented.Document).HasColumnType("jsonb");
                entity.Property(documented => documented.Text).HasColumnType("text");
            });
            builder.Entity<OutboxMessage>();
            builder.Entity<IdempotencyKey>();
            builder.Entity<AuditEntry>();
            builder.Entity<AuditConfig>();
        }
    }

    /// <summary>
    /// Carries the marker on a property that is private AND declared on a base class —
    /// mapped explicitly, the way EF maps a non-public member.
    /// </summary>
    private abstract class Concealing
    {
        /// <summary>
        /// Marked, private, declared on a base class — and named for no
        /// <see cref="SensitiveTokenCatalog"/> token.
        /// </summary>
        /// <remarks>
        /// It was called <c>Secret</c>, and <c>secret</c> is a token: the name redactor
        /// masked it first, so the case passed with the marker deleted and asserted nothing
        /// about the hierarchy walk it is named for. The <c>Nickname</c> probe below had
        /// already been renamed for exactly that reason; this one had not.
        /// </remarks>
        [PiiSensitive]
        private string Birthplace { get; set; } = string.Empty;

        public void SetBirthplace(string value) => Birthplace = value;
    }

    private sealed class Hidden : Concealing
    {
        public int Id { get; set; }

        public string Open { get; set; } = string.Empty;
    }

    private abstract class Traced
    {
        /// <summary>Marked, and declared on a BASE class.</summary>
        /// <remarks>
        /// The shape this repository's aggregates have — every audit column is declared on
        /// <c>AuditableEntity&lt;TId&gt;</c>, not on the aggregate. The lookup walks the
        /// hierarchy with <c>DeclaredOnly</c> at each level, so a lookup that stopped at
        /// the entity type would miss this one and write the value.
        /// </remarks>
        [PiiSensitive]
        public string Inherited { get; set; } = string.Empty;
    }

    private sealed class Person : Traced
    {
        public int Id { get; set; }

        public string DisplayName { get; set; } = string.Empty;

        /// <summary>Named for a <see cref="SensitiveTokenCatalog"/> token, unmarked.</summary>
        public string Password { get; set; } = string.Empty;

        public string Notes { get; set; } = string.Empty;

        /// <summary>
        /// Marked, and named for no token — which is the whole point of the case.
        /// </summary>
        /// <remarks>
        /// It used to be called <c>Secret</c>, and <c>secret</c> is in
        /// <see cref="SensitiveTokenCatalog"/>. The marker branch was therefore dead: the
        /// token list redacted the value first and the case passed with
        /// <see cref="PiiSensitiveAttribute"/> deleted. A name no token matches is what
        /// makes the assertion about the marker.
        /// </remarks>
        [PiiSensitive]
        public string Nickname { get; set; } = string.Empty;


    }

    private sealed class Bookkept
    {
        public int Id { get; set; }

        public Guid TenantId { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset? UpdatedAt { get; set; }

        public DateTimeOffset? DeletedAt { get; set; }

        /// <summary>
        /// <c>Version</c>, matching <c>AuditableEntity&lt;TId&gt;</c>. It was called
        /// <c>RowVersion</c> — the COLUMN name — and so agreed with an exclusion entry
        /// that was spelled the same wrong way and excluded nothing on any real aggregate.
        /// </summary>
        public long Version { get; set; }

        public string Payload { get; set; } = string.Empty;
    }

    /// <summary>A stand-in for <c>LocalizedText</c>: a value object stored as JSON.</summary>
    private sealed record Label(string Text)
    {
        public static Label From(string text) => new(text);

        public string ToJson() => $$"""{"text":{{JsonSerializer.Serialize(Text)}}}""";

        public static Label FromJson(string json) =>
            new(JsonDocument.Parse(json).RootElement.GetProperty("text").GetString()!);
    }

    private sealed class Converted
    {
        public int Id { get; set; }

        public Label Label { get; set; } = Label.From(string.Empty);

        public Grade Grade { get; set; }
    }

    private enum Grade
    {
        First,
        Second,
    }

    private sealed class Documented
    {
        public int Id { get; set; }

        public string Document { get; set; } = string.Empty;

        public string Text { get; set; } = string.Empty;

        public Grade Status { get; set; }
    }

    /// <summary>A key whose stored text differs from the model value's ToString().</summary>
    private sealed record Code(string Value)
    {
        public override string ToString() => Value;
    }

    private sealed class Keyed
    {
        public Code Code { get; set; } = new(string.Empty);

        public string Payload { get; set; } = string.Empty;
    }

    private sealed class Band
    {
        public string TenantId { get; set; } = string.Empty;

        public string Taxonomy { get; set; } = string.Empty;

        public string Key { get; set; } = string.Empty;
    }

    /// <summary>An owner and the members it contains — the shape of a taxonomy and its bands.</summary>
    private sealed class Folder
    {
        public int Id { get; set; }

        public string Title { get; set; } = string.Empty;

        public List<Leaf> Leaves { get; } = [];
    }

    private sealed class Leaf
    {
        public int FolderId { get; set; }

        public string Key { get; set; } = string.Empty;

        public string Label { get; set; } = string.Empty;
    }

    /// <summary>A navigation to another aggregate root, which is a reference and not containment.</summary>
    private sealed class Holder
    {
        public int Id { get; set; }

        public List<Rooted> Children { get; } = [];
    }

    private sealed class Rooted : IAggregateRoot<ProbeId>
    {
        public ProbeId Id { get; set; }

        public int HolderId { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    private readonly record struct ProbeId(Guid Value) : IStronglyTypedId<Guid>
    {
        public bool IsInitialized() => Value != Guid.Empty;
    }

    // The four excluded names, as the interceptor matches them: by CLR type name, so a
    // stand-in with the same name is exactly what it will see.
    private sealed class OutboxMessage { public int Id { get; set; } }

    private sealed class IdempotencyKey { public int Id { get; set; } }

    private sealed class AuditEntry { public int Id { get; set; } }

    private sealed class AuditConfig { public int Id { get; set; } }
}
