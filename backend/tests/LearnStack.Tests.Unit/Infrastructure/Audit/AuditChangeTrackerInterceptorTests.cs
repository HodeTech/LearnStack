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
            "/Person/Id", "/Person/DisplayName", "/Person/Password", "/Person/Notes",
            "/Person/Nickname", "/Person/Inherited");
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
        change.Fields.Select(field => field.Path).Should().Equal("/Person/DisplayName");
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
        field.Path.Should().Be("/Person/Nickname");
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
        change.Fields.Single(field => field.Path == "/Person/Password")
            .AfterJson.Should().Be(Quoted(SensitiveTokenCatalog.RedactedValue));

        // And an ordinary property beside it is untouched, so the gate is not simply
        // redacting everything.
        change.Fields.Single(field => field.Path == "/Person/DisplayName")
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
        change.Fields.Single(field => field.Path == "/Person/Inherited")
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
            RowVersion = 3,
            DeletedAt = DateTimeOffset.UnixEpoch,
            Payload = "p",
        });

        new AuditChangeTrackerInterceptor(capture).Capture(context);

        capture.Changes.Single().Fields.Select(field => field.Path).Should().BeEquivalentTo(
            "/Bookkept/Id", "/Bookkept/DeletedAt", "/Bookkept/Payload");
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
        fields.Single(field => field.Path == "/Documented/Document")
            .AfterJson.Should().Be("{\"a\":1}", "a jsonb column is emitted verbatim");
        fields.Single(field => field.Path == "/Documented/Text")
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
            .Single(field => field.Path == "/Documented/Status")
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

        fields.Single(field => field.Path == "/Converted/Label")
            .AfterJson.Should().Be(
                """{"text":"Vocabulary Card"}""",
                "the jsonb column holds the converter's output, not the CLR object's shape");

        fields.Single(field => field.Path == "/Converted/Grade")
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
            builder.Entity<Bookkept>();
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

        public long RowVersion { get; set; }

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

    private sealed class Band
    {
        public string TenantId { get; set; } = string.Empty;

        public string Taxonomy { get; set; } = string.Empty;

        public string Key { get; set; } = string.Empty;
    }

    // The four excluded names, as the interceptor matches them: by CLR type name, so a
    // stand-in with the same name is exactly what it will see.
    private sealed class OutboxMessage { public int Id { get; set; } }

    private sealed class IdempotencyKey { public int Id { get; set; } }

    private sealed class AuditEntry { public int Id { get; set; } }

    private sealed class AuditConfig { public int Id { get; set; } }
}
