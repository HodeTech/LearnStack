using System.Xml.Linq;
using FluentAssertions;
using LearnStack.Api.Composition;
using LearnStack.Application.Pipeline;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// The persistence rules
/// <see href="../../../docs/decisions/0039-optimistic-concurrency-token.md">ADR-0039</see>
/// and
/// <see href="../../../docs/decisions/0040-ambient-unit-of-work.md">ADR-0040</see>
/// assign to Packet 6, catalogued in
/// <see href="../../../docs/standards/21-architecture-tests-catalogue.md">Standards 21
/// § Persistence: concurrency and the unit of work</see>.
/// </summary>
/// <remarks>
/// Model inspection rather than a source scan. The mistake these rules exist to
/// catch is not a forbidden call site — it is EF metadata that looks right at the
/// call site and is wrong in the model, which is exactly what a scan cannot see.
/// </remarks>
public sealed class PersistenceConventionTests
{
    [Fact]
    public void Aggregates_With_Optimistic_Concurrency_Map_RowVersion()
    {
        // Three properties of the metadata, not one, because the two ways of
        // getting this wrong fail different ones:
        //
        //   IsRowVersion() / ValueGeneratedOnAddOrUpdate() leave both save
        //   behaviours at Ignore, and EF then omits row_version from the UPDATE
        //   entirely — the token stays 0 for the life of the row and every lost
        //   update succeeds while reporting success (ADR-0039 Amendment 1).
        //
        //   HasDefaultValue(0L) — needed for the DDL template's DEFAULT 0 — leaves
        //   ValueGenerated at OnAdd on its own. That one is benign today and is
        //   still rejected: it is a store-generated declaration on a column the
        //   aggregate increments, and the next reader of the model has to work out
        //   which of the two mistakes it is. `.ValueGeneratedNever()` states it
        //   (ADR-0039 Amendment 2).
        // Every module's model, not one: read against Tenancy alone this rule said
        // nothing about the four aggregates the second module shipped, while
        // Standards 21 and ADR-0039 both describe it as covering every entity that
        // implements IOptimisticConcurrency. Measured — the forbidden IsRowVersion()
        // form passed on both new aggregates.
        var contexts = Modules.Scoped.Select(module => module.Context()).ToList();

        try
        {
            AssertRowVersionMapping(contexts);
        }
        finally
        {
            contexts.ForEach(context => context.Dispose());
        }
    }

    private static void AssertRowVersionMapping(IReadOnlyCollection<DbContext> contexts)
    {
        var offenders = new List<string>();

        foreach (var entity in contexts.SelectMany(context => context.Model.GetEntityTypes()))
        {
            if (!typeof(IOptimisticConcurrency).IsAssignableFrom(entity.ClrType))
            {
                continue;
            }

            var version = entity.FindProperty(nameof(IOptimisticConcurrency.Version));

            if (version is null
                || version.GetColumnName() != "row_version"
                || !version.IsConcurrencyToken
                || version.ValueGenerated != ValueGenerated.Never
                || version.GetBeforeSaveBehavior() != PropertySaveBehavior.Save
                || version.GetAfterSaveBehavior() != PropertySaveBehavior.Save)
            {
                offenders.Add(
                    $"{entity.ClrType.Name}: column={version?.GetColumnName() ?? "<unmapped>"} "
                    + $"token={version?.IsConcurrencyToken} valueGenerated={version?.ValueGenerated} "
                    + $"before={version?.GetBeforeSaveBehavior()} after={version?.GetAfterSaveBehavior()}");
            }
        }

        offenders.Should().BeEmpty(
            "row_version is IsConcurrencyToken() + ValueGeneratedNever(), and nothing "
            + "that tells EF the database generates it (ADR-0039; Standards 05 § Concurrency)");

        // A model with no IOptimisticConcurrency entity would pass the loop above
        // without inspecting anything, which is the same defect as an inclusion
        // list that matches nothing — and it is asserted per model, because one
        // model carrying aggregates satisfies a total however many carry none.
        foreach (var context in contexts)
        {
            context.Model.GetEntityTypes()
                .Count(e => typeof(IOptimisticConcurrency).IsAssignableFrom(e.ClrType))
                .Should().BeGreaterThan(
                    0,
                    $"{context.GetType().Name} must be a model that has aggregates in it");
        }
    }

    [Fact]
    public void Migration_Startup_Project_References_EntityFrameworkCore_Design()
    {
        // `dotnet ef` resolves the design package from the STARTUP project, and
        // `make migrate` names LearnStack.Api. Without the reference the tool
        // refuses before it opens a connection — "Your startup project
        // 'LearnStack.Api' doesn't reference Microsoft.EntityFrameworkCore.Design"
        // — and Packet 6 shipped a migration in exactly that state: green under
        // Testcontainers, which calls Database.MigrateAsync() directly, and
        // inapplicable by the one path Standards 05 § Database roles documents.
        var startupProject = Path.Combine(
            RepositoryPaths.BackendSrc(), "LearnStack.Api", "LearnStack.Api.csproj");

        var references = XDocument.Load(startupProject)
            .Descendants("PackageReference")
            .Select(element => element.Attribute("Include")?.Value)
            .ToList();

        references.Should().Contain("Microsoft.EntityFrameworkCore.Design",
            "`make migrate` passes --startup-project backend/src/LearnStack.Api, and "
            + "dotnet ef resolves the design-time package from there");
    }

    [Fact]
    public void Module_DbContexts_Enlist_In_The_Ambient_UnitOfWork()
    {
        // Two halves, because either alone leaves the hole open.
        //
        // The registration half: the composition root's own persistence
        // registration is run, and every DbContext service in it must be one the
        // shared helper registered. A context registered any other way is absent
        // from this collection's marker — read off the collection, so the answer
        // is about the container built here and not about anything another test
        // in this process happened to register first.
        var services = new ServiceCollection();
        services.AddLearnStackPersistence(new ConfigurationBuilder().Build());

        var registered = services.RegisteredContexts();

        var contexts = services
            .Where(descriptor => typeof(DbContext).IsAssignableFrom(descriptor.ServiceType))
            .ToList();

        contexts.Should().NotBeEmpty("TenancyDbContext is registered and is the first consumer");

        contexts.Should().OnlyContain(
            descriptor => registered.Contains(descriptor.ServiceType),
            "every DbContext registration goes through AddModuleDbContext");

        contexts.Should().OnlyContain(
            descriptor => descriptor.Lifetime == ServiceLifetime.Scoped
                          && descriptor.ImplementationFactory != null,
            "a context is built per scope, from the connection IUnitOfWork owns — "
            + "a type registration would let EF open its own");

        // The call-site half: a context on its own connection never saw
        // SET LOCAL, so every read through it returns zero rows under the
        // corrected policy — silently.
        //
        // Seven files under backend/src may reach for a connection at all: the four
        // design-time factories, where a connection string is the point — one per
        // migration chain, and a module that ships a schema ships one; the shared
        // helper, which passes a connection rather than a string; and the two
        // composition roots — the API's, which builds the one application data
        // source behind its credential guard, and the seeder's, which is the same act
        // for a host with no HTTP surface. An eighth is a new decision.
        //
        // The scan covers the raw constructors as well as `UseNpgsql` and
        // `AddDbContext`, because a call site that opened its own
        // `NpgsqlConnection` would bypass the seam without naming either.
        var callSites = Directory
            .EnumerateFiles(RepositoryPaths.BackendSrc(), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                       StringComparison.Ordinal)
                       && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                       StringComparison.Ordinal))
            .Where(file => ProviderTokens.Any(token =>
                StripComments(File.ReadAllText(file)).Contains(token, StringComparison.Ordinal)))
            // Qualified by the directory above the file, not the bare filename. There
            // are now two `Program.cs` under backend/src — the API's and the seeder's —
            // and a set keyed on filenames alone would let one of them acquire a
            // connection under cover of the other's entry.
            .Select(file => $"{Path.GetFileName(Path.GetDirectoryName(file))}/{Path.GetFileName(file)}")
            .Order(StringComparer.Ordinal)
            .ToList();

        callSites.Should().BeEquivalentTo(
        [
            "Persistence/ModuleDbContextRegistration.cs",
            "Composition/PersistenceCompositionExtensions.cs",
            "Persistence/PlatformDbContextFactory.cs",
            "Persistence/TenancyDbContextFactory.cs",

            // One design-time factory per migration chain. Customization's is the
            // third and Audit's the fourth, and both are here rather than exempted for
            // the same reason the seeder is: the list is what makes the next one a
            // reviewed diff.
            "Persistence/CustomizationDbContextFactory.cs",
            "Persistence/AuditDbContextFactory.cs",

            // The seventh, and a deliberate entry rather than a discovered one: the seeder
            // is a second composition root, and building the one application data source
            // is the same act PersistenceCompositionExtensions performs for the API. It
            // is in the set — not exempted from it — so the next tool that reaches for a
            // connection is still a reviewed diff.
            "LearnStack.Tools.Seeder/Program.cs",
        ]);
    }

    /// <summary>
    /// Every way source under <c>backend/src</c> could configure or open a
    /// PostgreSQL connection outside the seam.
    /// </summary>
    private static readonly string[] ProviderTokens =
    [
        "UseNpgsql",
        "AddDbContext",
        "NpgsqlDataSourceBuilder",
        "NpgsqlDataSource.Create",
        "new NpgsqlConnection(",
    ];

    [Fact]
    public void TransactionBehavior_Does_Not_Reference_A_Module_Assembly()
    {
        // The seam exists so that the behavior owning the commit boundary never
        // has to name a module. Two assertions: the assembly takes no build-time
        // reference to one, and the behavior's own surface names IUnitOfWork and
        // no DbContext.
        // The PROJECT file, not only the emitted assembly-reference table. The
        // compiler elides a reference whose types the IL never touches, so an
        // unused <ProjectReference> to a module would leave a reflection-only
        // check green — a trap this repository has documented twice and moved a
        // rule out of this project over.
        var project = Path.Combine(
            RepositoryPaths.BackendSrc(), "LearnStack.Application", "LearnStack.Application.csproj");

        XDocument.Load(project)
            .Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value ?? string.Empty)
            .Should().NotContain(
                include => include.Contains("LearnStack.Modules.", StringComparison.Ordinal),
                "LearnStack.Application is generic over every module and references none");

        var application = typeof(TransactionBehavior<,>).Assembly;

        var referenced = application.GetReferencedAssemblies()
            .Select(reference => reference.Name!)
            .ToList();

        // The positive control the assembly half needs: if this list were empty
        // the NotContain below would pass against a check that read nothing.
        referenced.Should().Contain("LearnStack.SharedKernel");

        referenced.Should().NotContain(
            name => name.StartsWith("LearnStack.Modules.", StringComparison.Ordinal));

        var constructor = typeof(TransactionBehavior<,>).GetConstructors().Single();

        constructor.GetParameters().Select(parameter => parameter.ParameterType)
            .Should().Contain(typeof(IUnitOfWork))
            .And.NotContain(parameter => typeof(DbContext).IsAssignableFrom(parameter));
    }

    /// <summary>
    /// Source with its comments removed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every file the scan above touches argues in prose about the very call it
    /// is forbidden to make, so scanning raw text would fail on the documentation
    /// that explains the rule.
    /// </para>
    /// <para>
    /// Through the literal-aware <see cref="SourceText.WithoutComments"/>, not a
    /// regex. A regex has no literal state: the <c>//</c> inside
    /// <c>"https://…"</c> opens a line comment and deletes the rest of that line
    /// before the scan reads it, and a <c>/*</c> inside a string blanks an
    /// arbitrary multi-line region up to the next <c>*&#47;</c> anywhere in the
    /// file. This is the guard for the ADR-0040 seam; a hole in it is a hole in
    /// that.
    /// </para>
    /// </remarks>
    private static string StripComments(string source) => SourceText.WithoutComments(source);

    [Fact]
    public void The_registration_marker_does_not_vouch_across_containers()
    {
        // What a process-wide marker set got wrong. The rule's other leg —
        // Scoped + ImplementationFactory — cannot tell this helper's registration
        // from a hand-rolled scoped factory that builds the context on its own
        // connection string, which is precisely the ADR-0040 failure. So for that
        // one shape the marker is the whole guard, and a marker that answers for
        // the process rather than the container answers about a registration that
        // is not the one in front of it.
        var correct = new ServiceCollection();
        correct.AddModuleDbContext<ProbeDbContext>();
        correct.RegisteredContexts().Should().Contain(typeof(ProbeDbContext));

        var foreign = new ServiceCollection();
        foreign.AddScoped(_ => new ProbeDbContext(
            new DbContextOptionsBuilder<ProbeDbContext>().Options));

        foreign.RegisteredContexts().Should().BeEmpty(
            "the marker answers for the collection it was read from, not for "
            + "whatever any other container in this process registered first");
    }

    /// <summary>
    /// A unique index on a soft-deletable table counts only the rows that are not deleted.
    /// </summary>
    [Fact]
    public void Unique_Indexes_On_Soft_Deletable_Tables_Exclude_Deleted_Rows()
    {
        // A soft-deleted row keeps its natural key, so an index that counts it holds the key
        // against its own tenant forever — nothing frees a soft-deleted row. Every
        // soft-deletable table wrote its index partial on `deleted_at IS NULL` except one:
        // audit_config, whose override can only be changed by a soft delete and a fresh
        // Declare, which the unfiltered index refused with 23505 (the fifth review of
        // Packet 9). Per-table schema cases had pinned the others; nothing swept for the
        // shape, so a new table repeated the omission and every case stayed green.
        var contexts = Modules.Scoped.Select(module => module.Context()).ToList();

        try
        {
            var soft = contexts
                .SelectMany(context => context.Model.GetEntityTypes())
                .Where(IsSoftDeletable)
                .ToList();

            soft.Should().NotBeEmpty("a sweep over no soft-deletable table passes vacuously");

            var counting = UniqueIndexesCountingDeletedRows(soft);

            counting.Except(HeldByDecision.Keys).Should().BeEmpty(
                "a unique index on a soft-deletable table is partial on `deleted_at IS NULL`, "
                + "or the deleted row holds its key forever — unless holding it is the point, "
                + "which HeldByDecision states with its owner");

            HeldByDecision.Keys.Except(counting).Should().BeEmpty(
                "an exception whose index is gone or already partial is a stale exception");
        }
        finally
        {
            contexts.ForEach(context => context.Dispose());
        }
    }

    [Fact]
    public void The_Soft_Delete_Index_Sweep_Can_Actually_Fail()
    {
        // Every real index passes, so the rule passes whether its predicate works or not. The
        // probe carries one index of each shape: counting deleted rows, partial, one that
        // contains the primary key, and one on a table with no soft delete at all — only the
        // first is a finding.
        using var context = new SoftDeleteProbeContext();

        UniqueIndexesCountingDeletedRows(context.Model.GetEntityTypes())
            .Should().Equal("SoftProbe.ux_soft_probe_code");
    }

    /// <summary>
    /// Unique indexes that hold a soft-deleted row's key on purpose, each with why.
    /// </summary>
    private static readonly Dictionary<string, string> HeldByDecision = new(StringComparer.Ordinal)
    {
        // Packet 6's, and not an omission: a tenant's slug is its public hostname label,
        // `{slug}.{platform-domain}`. Releasing it would hand a later tenant the links, mail
        // and bookmarks that still point at the first one — a subdomain takeover by
        // deletion. Whether a terminated tenant's slug may ever be reissued is decided
        // with tenant termination, in Phase 02c, not by an index filter.
        ["Tenant.ux_tenants_slug"] =
            "a tenant slug is a hostname; its reissue is Phase 02c's decision",
    };

    private static bool IsSoftDeletable(IReadOnlyEntityType entity) =>
        entity.GetProperties().Any(property =>
            string.Equals(property.GetColumnName(), "deleted_at", StringComparison.Ordinal));

    /// <summary>The unique indexes on soft-deletable tables that do not exclude deleted rows.</summary>
    /// <remarks>
    /// An index that contains the whole primary key is outside the rule: it is unique by
    /// construction, so a deleted row holds nothing a live one could want — and it is how a
    /// composite foreign key reaches the table (<c>ux_organizations_tenant_id_id</c>), which a
    /// partial index cannot serve.
    /// </remarks>
    private static List<string> UniqueIndexesCountingDeletedRows(IEnumerable<IReadOnlyEntityType> entities) =>
        [.. entities
            .Where(IsSoftDeletable)
            .SelectMany(entity => entity.GetIndexes()
                .Where(index => index.IsUnique
                    && !ContainsPrimaryKey(entity, index)
                    && !(index.GetFilter() ?? string.Empty)
                        .Contains("deleted_at IS NULL", StringComparison.OrdinalIgnoreCase))
                .Select(index => $"{entity.ClrType.Name}.{index.GetDatabaseName()}"))
            .Order(StringComparer.Ordinal)];

    private static bool ContainsPrimaryKey(IReadOnlyEntityType entity, IReadOnlyIndex index) =>
        entity.FindPrimaryKey() is { } key && key.Properties.All(index.Properties.Contains);

    private sealed class SoftProbe
    {
        public int Id { get; set; }

        public string Code { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public DateTimeOffset? DeletedAt { get; set; }
    }

    private sealed class HardProbe
    {
        public int Id { get; set; }

        public string Code { get; set; } = string.Empty;
    }

    private sealed class SoftDeleteProbeContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder options) =>
            options.UseNpgsql("Host=model-only;Database=model-only;Username=model-only");

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<SoftProbe>(builder =>
            {
                builder.Property(x => x.DeletedAt).HasColumnName("deleted_at");
                builder.HasIndex(x => x.Code).IsUnique().HasDatabaseName("ux_soft_probe_code");
                builder.HasIndex(x => x.Name).IsUnique().HasFilter("deleted_at IS NULL")
                    .HasDatabaseName("ux_soft_probe_name");
                builder.HasIndex(x => new { x.Code, x.Id }).IsUnique()
                    .HasDatabaseName("ux_soft_probe_code_id");
            });

            modelBuilder.Entity<HardProbe>(builder =>
                builder.HasIndex(x => x.Code).IsUnique().HasDatabaseName("ux_hard_probe_code"));
        }
    }

    /// <summary>A context type that exists only to be registered two ways.</summary>
    private sealed class ProbeDbContext(DbContextOptions<ProbeDbContext> options)
        : DbContext(options);

    [Fact]
    public void Every_Database_Test_Carries_The_Docker_Trait()
    {
        // CI splits the integration assembly by `[Trait("Requires","Docker")]`, and
        // the two filters are exact complements — so a class that forgets the
        // attribute does not fail, it runs in the `backend` job. Both jobs are on
        // ubuntu-latest, which carries a Docker socket natively, so it starts its
        // container and passes: nothing goes red, and the Docker suite quietly
        // stops being where the Docker tests live.
        //
        // The whole project, not only Database/: a test beside the HTTP suites can take
        // the shared schema too — ApiHandlerCompositionTests does — and a sweep of one
        // folder could not see it forget the trait (the fifth review of Packet 9).
        //
        // Source-scanned rather than reflected, because this assembly does not
        // reference LearnStack.Tests.Integration — and should not: a test project
        // referencing another test project is a dependency nothing else in the
        // repository has.
        var project = Path.Combine(
            RepositoryPaths.RepoRoot(), "backend", "tests", "LearnStack.Tests.Integration");

        var files = Directory
            .EnumerateFiles(project, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(file => (Path.GetRelativePath(project, file).Replace('\\', '/'), File.ReadAllText(file)))
            .ToList();

        files.Should().Contain(file => file.Item1.StartsWith("Database/", StringComparison.Ordinal),
            "the Docker-bound suite lives there, and a rule that scans nothing passes");

        UntraitedDockerTests(files).Should().BeEmpty(
            "every test class under Database/, and every one that takes a database fixture, "
            + "needs a real Docker socket, and the trait is how CI routes it to the job that "
            + "declares one");
    }

    [Fact]
    public void The_Docker_Trait_Sweep_Can_Actually_Fail()
    {
        // Every file in the repository carries the trait it needs, so the sweep above
        // passes on a project where it is doing its job and on one where it is not. These
        // are the four shapes it has to tell apart.
        const string Trait = "[Trait(RequiresDocker.Key, RequiresDocker.Value)]";
        const string Qualified = "[Trait(Database.RequiresDocker.Key, Database.RequiresDocker.Value)]";

        UntraitedDockerTests(
        [
            ("Database/ForgotTests.cs", "public class ForgotTests { [Fact] public void A() { } }"),
            ("Database/TraitedTests.cs", $"{Trait} public class TraitedTests {{ [Fact] public void A() {{ }} }}"),
            ("Database/SchemaQueries.cs", "public static class SchemaQueries { }"),
            ("RootForgotTests.cs", "public class RootForgotTests(Database.SchemaFixture schema) { [Fact] public void A() { } }"),
            ("RootTraitedTests.cs", $"{Qualified} public class RootTraitedTests(Database.SchemaFixture schema) {{ [Theory] public void A() {{ }} }}"),
            ("HttpOnlyTests.cs", "public class HttpOnlyTests { [Fact] public void A() { } }"),
        ]).Should().Equal("Database/ForgotTests.cs", "RootForgotTests.cs");
    }

    /// <summary>
    /// The test files that need Docker and do not say so: under <c>Database/</c>, or naming
    /// a fixture that starts a container, and carrying no <c>RequiresDocker</c> trait.
    /// </summary>
    private static List<string> UntraitedDockerTests(IEnumerable<(string Path, string Source)> files) =>
        files
            .Where(file =>
            {
                // A file declaring no test method has nothing to trait — the fixtures and
                // the shared query helpers are the case.
                var declaresTests = file.Source.Contains("[Fact]", StringComparison.Ordinal)
                                    || file.Source.Contains("[Theory]", StringComparison.Ordinal);

                var needsDocker = file.Path.StartsWith("Database/", StringComparison.Ordinal)
                                  || DockerFixtures.Any(fixture => file.Source.Contains(fixture, StringComparison.Ordinal));

                var traited = file.Source.Contains("[Trait(RequiresDocker.Key, RequiresDocker.Value)]", StringComparison.Ordinal)
                              || file.Source.Contains("[Trait(Database.RequiresDocker.Key, Database.RequiresDocker.Value)]", StringComparison.Ordinal);

                return declaresTests && needsDocker && !traited;
            })
            .Select(file => file.Path)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>The fixtures that start a PostgreSQL container, by the names a test takes them under.</summary>
    private static readonly string[] DockerFixtures = ["SchemaFixture", "PostgresFixture", "SharedSchema"];

    [Fact]
    public void Migrate_Target_Covers_Every_Migration_Chain()
    {
        // `make migrate` is the only documented path that applies a migration, and
        // its project loop is a glob. The first version globbed `src/Modules` only,
        // which left the platform chain — outbox_messages and idempotency_keys —
        // unapplied by every documented path while the Testcontainers fixtures,
        // which call Database.MigrateAsync() directly, stayed green.
        //
        // Scanned rather than listed: the assertion is that every directory under
        // backend/src carrying a Persistence/Migrations folder is reachable from
        // the recipe, so adding a chain and forgetting the Makefile fails here
        // rather than in a deployment.
        var recipe = ReadMigrateRecipe();

        var chains = MigrationChains();

        chains.Should().NotBeEmpty("the tenancy and platform chains both exist");

        var uncovered = chains
            .Where(chain => !RecipeReaches(recipe, chain))
            .ToList();

        uncovered.Should().BeEmpty(
            "`make migrate` applies every chain, or the ones it misses are "
            + "unmigrated on the only path Standards 05 § Database roles documents");
    }

    /// <summary>
    /// <c>make migrate</c> applies the Tenancy chain before the Audit chain.
    /// </summary>
    [Fact]
    public void Migrate_Target_Applies_The_Tenancy_Chain_First()
    {
        // From Phase 02a Packet 9 the chains are no longer independent: audit_config
        // carries the schema's only foreign key crossing two chains, to `tenants`,
        // which the Tenancy chain creates (ADR-0044 § 9). A run that reaches the Audit
        // chain first fails on a clean database with `relation "tenants" does not
        // exist` — and alphabetical order produces exactly that run, because
        // `Modules/Audit` sorts before `Modules/Tenancy` and the recipe's project list
        // is a glob.
        //
        // Coverage is not order, and this rule exists because the difference is
        // invisible on a database that already has the schema.
        // Migrate_Target_Covers_Every_Migration_Chain stays green when the Tenancy
        // prefix is deleted from the recipe — the glob still reaches Tenancy — while
        // every fresh deployment breaks from that commit onward. So does every suite
        // whose fixture applies the chains in its own order.
        var order = MigrateChainOrder();

        order.Should().Contain(TenancyChain).And.Contain(AuditChain);

        order.IndexOf(TenancyChain).Should().BeLessThan(
            order.IndexOf(AuditChain),
            "`make migrate` names the Tenancy chain ahead of the glob that finds the "
            + "rest, because audit_config references tenants and the glob expands "
            + "alphabetically (Standards 05 § Migrations)");
    }

    private const string TenancyChain =
        "backend/src/Modules/Tenancy/LearnStack.Modules.Tenancy.Infrastructure";

    private const string AuditChain =
        "backend/src/Modules/Audit/LearnStack.Modules.Audit.Infrastructure";

    /// <summary>
    /// The chains <c>make migrate</c> visits, in the order it visits them.
    /// </summary>
    /// <remarks>
    /// A faithful replay of the recipe rather than a reading of it: each
    /// <c>backend/src</c> token is expanded against the chains that actually exist —
    /// ordinal-sorted, which is what a shell does with a glob — and a chain already
    /// visited is skipped, which is what the recipe's <c>applied</c> guard does.
    /// <b>Expanding the glob is the whole difference from a text search</b>, and it is
    /// what catches the mutation that actually happened: deleting the explicit Tenancy
    /// prefix leaves a recipe whose only token is the <c>Modules/*</c> glob, in which the
    /// literal <c>Modules/Tenancy</c> does not appear at all — so a search for it finds
    /// nothing to compare, while the replay expands the glob and reports Audit first.
    /// </remarks>
    private static List<string> MigrateChainOrder()
    {
        var chains = MigrationChains();
        var visited = new List<string>();

        var tokens = ReadMigrateRecipe()
            .Split([' ', '\n', '\t', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.StartsWith("backend/src", StringComparison.Ordinal));

        foreach (var token in tokens)
        {
            var pattern = "^" + string.Join(
                "[^/]*",
                token.Split('*').Select(System.Text.RegularExpressions.Regex.Escape)) + "$";

            foreach (var chain in chains.Where(chain =>
                System.Text.RegularExpressions.Regex.IsMatch(chain, pattern)))
            {
                if (!visited.Contains(chain, StringComparer.Ordinal))
                {
                    visited.Add(chain);
                }
            }
        }

        return visited;
    }

    /// <summary>Every project under <c>backend/src</c> carrying a migration chain.</summary>
    private static List<string> MigrationChains() =>
        Directory
            .EnumerateDirectories(RepositoryPaths.BackendSrc(), "Migrations", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(Path.GetDirectoryName(path)) == "Persistence")
            .Select(path => Path.GetDirectoryName(Path.GetDirectoryName(path))!)
            .Select(project => Path.GetRelativePath(RepositoryPaths.RepoRoot(), project)
                .Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

    [Theory]
    // Npgsql parses every one of these into Username / Password — measured
    // against Npgsql 10, not read off a page. The recipe recognised the first
    // pair only, so the other three read the role as empty and printed the
    // password unredacted.
    [InlineData("Username", "Password")]
    [InlineData("UID", "PWD")]
    [InlineData("User ID", "PSW")]
    [InlineData("USERID", "Pwd")]
    public void Migrate_Target_Refuses_An_Aliased_Runtime_Credential(string userKey, string secretKey)
    {
        // Executed, not scanned. A test that asserted the recipe's text contained
        // "uid" would pass against a recipe that matched the alias and then did
        // nothing with it — and the defect being fixed here is precisely a keyword
        // table that existed and was incomplete.
        var value = $"Host=localhost;Database=learnstack;{userKey}=learnstack_app;{secretKey}=hunter2"; // leakwatch:ignore

        var (exitCode, output) = RunMigrateTarget(value);

        exitCode.Should().NotBe(0, "learnstack_app does not own the tables: {0}", output);
        output.Should().Contain("learnstack_app", "the operator has to be told which role they named");
        output.Should().NotContain(
            "hunter2",
            "the one target whose purpose is keeping the migration credential in one "
            + "place must not echo it into a terminal or a CI log");
    }

    [Theory]
    // Npgsql accepts a semicolon inside a quoted value — measured against
    // Npgsql 10, both quote characters, with a doubled quote as the escape. The
    // split-on-";" version cut such a value in half: the first half matched the
    // keyword table and was redacted, the second half matched nothing and was
    // printed, so `make migrate` echoed a "redacted" string that still carried
    // the password.
    //
    // Every literal is an INPUT to the redaction under test — the file cannot be
    // written without one. They name localhost and a password no service has
    // ever had; leakwatch:ignore applies per line.
    [InlineData("Host=localhost;Password=\";hunter2\";UID=learnstack_app")] // leakwatch:ignore
    [InlineData("Host=localhost;Password=';hunter2';UID=learnstack_app")] // leakwatch:ignore
    [InlineData("Host=localhost;PWD=\"a;hunter2\";Username=learnstack_app")] // leakwatch:ignore
    [InlineData("Host=localhost;Password=\"said \"\"hi\"\";hunter2\";Username=learnstack_app")] // leakwatch:ignore
    public void Migrate_Target_Redacts_A_Quoted_Value_Whole(string value)
    {
        var (exitCode, output) = RunMigrateTarget(value);

        exitCode.Should().NotBe(0, "{0}", output);
        output.Should().Contain("learnstack_app", "the role is still read correctly");
        output.Should().NotContain(
            "hunter2",
            "a semicolon inside a quoted value is not a field boundary, so the "
            + "redaction covers the value whole or it covers nothing");
    }

    [Fact]
    public void Migrate_Target_Reads_The_Role_Through_A_Quoted_Value()
    {
        // The other half: a quoted password containing a semicolon must not
        // shift the fields that follow it out of alignment, or the role check
        // reads the wrong token and the recipe refuses a correct credential.
        var (exitCode, output) = RunMigrateTarget(
            "Host=localhost;Password=\";hunter2\";Username=learnstack_app"); // leakwatch:ignore

        exitCode.Should().NotBe(0, "{0}", output);
        output.Should().Contain(
            "Username='learnstack_app'",
            "the field after the quoted value is still parsed as its own field");
    }

    [Fact]
    public void Migrate_Target_Refuses_A_Uri_Without_Echoing_Its_Userinfo()
    {
        // The form DATABASE_URL carries on several hosts, and the form that has no
        // `password=` in it for a keyword pass to find.
        var (exitCode, output) = RunMigrateTarget(
            "postgres://learnstack_app:hunter2@localhost:5432/learnstack"); // leakwatch:ignore

        exitCode.Should().NotBe(0, "{0}", output);
        output.Should().NotContain("hunter2");
        output.Should().Contain("key/value", "the message names the form that would work");
    }

    /// <summary>
    /// Runs the repo-root <c>migrate</c> target with a given migration credential
    /// and returns its exit code and combined output.
    /// </summary>
    /// <remarks>
    /// Every value the callers pass is refused by the role check, which runs before
    /// the recipe restores a tool or opens a socket — so this touches no database
    /// and needs no Docker.
    /// </remarks>
    private static (int ExitCode, string Output) RunMigrateTarget(string migrationConnectionString)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("make")
        {
            WorkingDirectory = RepositoryPaths.RepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add("migrate");
        startInfo.Environment["ConnectionStrings__Migration"] = migrationConnectionString;

        // Not skipped when `make` is absent — a skipped architecture test is a
        // bug by policy, and a machine without make cannot run `make install`,
        // `make test` or `make migrate` either, so the documented workflow is
        // already broken there. What is worth fixing is the message: the raw
        // Win32Exception says "No such file or directory" and names nothing.
        System.Diagnostics.Process process;

        try
        {
            process = System.Diagnostics.Process.Start(startInfo)
                ?? throw new InvalidOperationException("`make` did not start.");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new InvalidOperationException(
                "`make` is not on PATH. The repo-root Makefile is the documented entry "
                + "point for restoring, testing and migrating (see .github/CONTRIBUTING.md "
                + "§ Local checks before pushing), and this rule executes its `migrate` "
                + "target to prove the credential guard refuses what it claims to.",
                exception);
        }

        using var _ = process;

        // Both pipes drained concurrently, then the wait. Reading one to the end and only
        // then the other deadlocks whenever the child fills the second pipe's buffer while
        // this side is blocked on the first — the classic Process pitfall. The role check
        // is terse enough that it has never happened here, which is exactly why it would
        // land on whoever makes the target chattier.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        process.WaitForExit(milliseconds: 60_000).Should().BeTrue("the role check exits immediately");

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();

        return (process.ExitCode, stdout + stderr);
    }

    /// <summary>
    /// The body of the repo-root Makefile's <c>migrate</c> target.
    /// </summary>
    private static string ReadMigrateRecipe()
    {
        var lines = File.ReadAllLines(Path.Combine(RepositoryPaths.RepoRoot(), "Makefile"));
        var start = Array.FindIndex(lines, line => line.StartsWith("migrate:", StringComparison.Ordinal));

        start.Should().BeGreaterThanOrEqualTo(0, "the Makefile carries a `migrate` target");

        var body = lines.Skip(start + 1).TakeWhile(line => line.StartsWith('\t'));
        return string.Join('\n', body);
    }

    /// <summary>
    /// True when the recipe names the project directly or through a glob that
    /// covers it.
    /// </summary>
    /// <remarks>
    /// A glob segment is matched by translating <c>*</c> to "anything but a
    /// separator", which is what the shell does. Comparing the literal string
    /// would fail on the module loop, which is a glob by design — one entry per
    /// module would be the maintenance burden this rule exists to remove.
    /// </remarks>
    private static bool RecipeReaches(string recipe, string projectPath) =>
        recipe
            .Split([' ', '\n', '\t', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.StartsWith("backend/src", StringComparison.Ordinal))
            .Any(token => System.Text.RegularExpressions.Regex.IsMatch(
                projectPath,
                "^" + string.Join(
                    "[^/]*",
                    token.Split('*').Select(System.Text.RegularExpressions.Regex.Escape)) + "$"));
}
