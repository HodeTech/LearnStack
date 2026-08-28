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
        using var context = BuildTenancyContext();

        var offenders = new List<string>();

        foreach (var entity in context.Model.GetEntityTypes())
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
        // list that matches nothing.
        context.Model.GetEntityTypes()
            .Count(e => typeof(IOptimisticConcurrency).IsAssignableFrom(e.ClrType))
            .Should().BeGreaterThan(0, "the rule must be reading a model that has aggregates in it");
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
        // from ModuleDbContextRegistration.RegisteredContexts.
        var services = new ServiceCollection();
        services.AddLearnStackPersistence(new ConfigurationBuilder().Build());

        var contexts = services
            .Where(descriptor => typeof(DbContext).IsAssignableFrom(descriptor.ServiceType))
            .ToList();

        contexts.Should().NotBeEmpty("TenancyDbContext is registered and is the first consumer");

        contexts.Should().OnlyContain(
            descriptor => ModuleDbContextRegistration.RegisteredContexts.Contains(descriptor.ServiceType),
            "every DbContext registration goes through AddModuleDbContext");

        contexts.Should().OnlyContain(
            descriptor => descriptor.Lifetime == ServiceLifetime.Scoped
                          && descriptor.ImplementationFactory != null,
            "a context is built per scope, from the connection IUnitOfWork owns — "
            + "a type registration would let EF open its own");

        // The call-site half: a context on its own connection never saw
        // SET LOCAL, so every read through it returns zero rows under the
        // corrected policy — silently. `UseNpgsql` with a connection string is how
        // that happens, and there are exactly three files under backend/src that
        // may configure a provider at all: the two design-time factories, where a
        // connection string is the point, and the shared helper, which passes a
        // connection rather than a string. A fourth is a new decision.
        var callSites = Directory
            .EnumerateFiles(RepositoryPaths.BackendSrc(), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                       StringComparison.Ordinal)
                       && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                       StringComparison.Ordinal))
            .Where(file => StripComments(File.ReadAllText(file))
                .Contains("UseNpgsql", StringComparison.Ordinal)
                || StripComments(File.ReadAllText(file))
                .Contains("AddDbContext", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToList();

        callSites.Should().BeEquivalentTo(
        [
            "ModuleDbContextRegistration.cs",
            "PlatformDbContextFactory.cs",
            "TenancyDbContextFactory.cs",
        ]);
    }

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
    /// Every file the scan above touches argues in prose about the very call it
    /// is forbidden to make, so scanning raw text would fail on the documentation
    /// that explains the rule.
    /// </remarks>
    private static string StripComments(string source) =>
        System.Text.RegularExpressions.Regex.Replace(
            source, @"//[^\n]*|/\*.*?\*/", string.Empty,
            System.Text.RegularExpressions.RegexOptions.Singleline);

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

        var chains = Directory
            .EnumerateDirectories(RepositoryPaths.BackendSrc(), "Migrations", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(Path.GetDirectoryName(path)) == "Persistence")
            .Select(path => Path.GetDirectoryName(Path.GetDirectoryName(path))!)
            .Select(project => Path.GetRelativePath(RepositoryPaths.RepoRoot(), project)
                .Replace(Path.DirectorySeparatorChar, '/'))
            .ToList();

        chains.Should().NotBeEmpty("the tenancy and platform chains both exist");

        var uncovered = chains
            .Where(chain => !RecipeReaches(recipe, chain))
            .ToList();

        uncovered.Should().BeEmpty(
            "`make migrate` applies every chain, or the ones it misses are "
            + "unmigrated on the only path Standards 05 § Database roles documents");
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

    /// <summary>
    /// Builds the Tenancy model without a database.
    /// </summary>
    /// <remarks>
    /// A connection string is required to configure the provider and is never
    /// opened: <c>DbContext.Model</c> is built from the configurations alone. The
    /// value is deliberately not a real credential.
    /// </remarks>
    private static TenancyDbContext BuildTenancyContext() =>
        new(new DbContextOptionsBuilder<TenancyDbContext>()
            .UseNpgsql("Host=model-only;Database=model-only;Username=model-only")
            .Options);
}
