using System.Text.Json;
using FluentAssertions;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.Audit.Capture;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.DataProtection;
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
            Secret = "s",
        });

        new AuditChangeTrackerInterceptor(capture).Capture(context);

        var change = capture.Changes.Should().ContainSingle().Subject;
        change.EntityType.Should().Be(nameof(Person));
        change.EntityId.Should().Be("1");
        change.BeforeJson.Should().BeNull("an insert has no prior state");
        change.AfterJson.Should().NotBeNull();

        change.Fields.Select(field => field.Path).Should().BeEquivalentTo(
            "/Person/Id", "/Person/DisplayName", "/Person/Password", "/Person/Notes",
            "/Person/Secret");
    }

    [Fact]
    public void A_modify_captures_only_the_properties_that_changed()
    {
        // The diff is what a reviewer reads. Listing every property on a modify would
        // bury the one that moved, and on a wide entity it is the difference between a
        // legible record and a wall.
        using var context = new ProbeContext();
        var capture = new AuditStateCapture();

        var person = new Person { Id = 7, DisplayName = "Ada", Password = "p", Notes = "n", Secret = "s" };
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

        var person = new Person { Id = 3, DisplayName = "Ada", Password = "p", Notes = "n", Secret = "s" };
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

        context.Attach(new Person { Id = 9, DisplayName = "Ada", Password = "p", Notes = "n", Secret = "s" });

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

        var person = new Person { Id = 1, DisplayName = "Ada", Password = "old", Notes = "n", Secret = "old" };
        context.Attach(person);
        person.Secret = "new";

        new AuditChangeTrackerInterceptor(capture).Capture(context);

        var field = capture.Changes.Single().Fields.Should().ContainSingle().Subject;
        field.Path.Should().Be("/Person/Secret");
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
            Secret = "s",
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
            Secret = "s",
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

        context.People.Add(new Person { Id = 1, DisplayName = "A", Password = "p", Notes = "n", Secret = "s" });
        interceptor.Capture(context);

        context.People.Add(new Person { Id = 2, DisplayName = "B", Password = "p", Notes = "n", Secret = "s" });
        interceptor.Capture(context);

        capture.Changes.Should().HaveCount(3,
            "the first entity is still Added on the second pass, so it is captured again "
            + "— the store merges by entity type, earliest before and latest after");
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
            builder.Entity<OutboxMessage>();
            builder.Entity<IdempotencyKey>();
            builder.Entity<AuditEntry>();
            builder.Entity<AuditConfig>();
        }
    }

    private sealed class Person
    {
        public int Id { get; set; }

        public string DisplayName { get; set; } = string.Empty;

        /// <summary>Named for a <see cref="SensitiveTokenCatalog"/> token, unmarked.</summary>
        public string Password { get; set; } = string.Empty;

        public string Notes { get; set; } = string.Empty;

        /// <summary>Marked, and named for no token.</summary>
        [PiiSensitive]
        public string Secret { get; set; } = string.Empty;
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
