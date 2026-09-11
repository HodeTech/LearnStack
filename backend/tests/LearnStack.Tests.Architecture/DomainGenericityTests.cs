using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using LearnStack.Modules.Customization.Domain;
using LearnStack.SharedKernel.Entitlements;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// The mechanical guarantee behind the platform's premise: one binary serves a language
/// school and a yoga studio because no name it ships belongs to either
/// (<see href="../../../docs/decisions/0018-tenant-driven-customization-model.md">ADR-0018</see>;
/// <see href="../../../docs/standards/00-principles.md">Principles § 1</see>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The terms come from the catalogue, not from here.</b>
/// <see href="../../../docs/standards/21-architecture-tests-catalogue.md">Standards 21</see>
/// § <c>Core_Modules_HaveNo_DomainSpecific_Names</c> carries a <b>Forbidden terms</b> line and
/// this test reads it, so adding a term is a one-line change in the document that owns the
/// list rather than an edit in two places that can disagree.
/// </para>
/// <para>
/// <b>Names, not prose.</b> Comments and free text are outside the rule — a comment that
/// explains the genericity boundary has to be able to say "CEFR" — and so are the seeder's
/// seed tenants, which are two demo domains on purpose. What is inside is everything a
/// release ships as an identifier: types, members, namespaces and file names, the tables and
/// columns the models map, the audit slugs, the entitlement and renderer keys, and the
/// frontend's files and exports.
/// </para>
/// </remarks>
public sealed partial class DomainGenericityTests
{
    [Fact]
    public void Core_Modules_HaveNo_DomainSpecific_Names()
    {
        var terms = ForbiddenTerms();

        terms.Should().HaveCountGreaterThan(5,
            "the premise: the Forbidden terms line in Standards 21 parsed, and a scan with no "
            + "terms reports nothing whatever the platform is called");

        var named = new List<(string Subject, string Name)>();

        foreach (var (subject, names) in Subjects())
        {
            names.Should().NotBeEmpty($"the premise: there are {subject} to read");
            named.AddRange(names
                .Where(name => NamesADomainTerm(name, terms))
                .Select(name => (subject, name)));
        }

        named.Select(entry => $"{entry.Subject}: {entry.Name}").Distinct(StringComparer.Ordinal)
            .Should().BeEmpty(
                "a domain shape is tenant customization data, never a name the platform ships "
                + "(ADR-0018) — the boundary is Platform Vision § Genericity boundary");
    }

    [Fact]
    public void The_Domain_Term_Scan_Can_Actually_Fail()
    {
        // Nothing the platform ships carries one of these terms, so the rule above passes
        // whether its matching works or not.
        var terms = ForbiddenTerms();

        terms.Should().Contain("CEFR").And.Contain("CodeChallenge").And.Contain("Yoga");

        NamesADomainTerm("CefrLevel", terms).Should().BeTrue("PascalCase splits into segments");
        NamesADomainTerm("asana-pose", terms).Should().BeTrue("so do hyphens");
        NamesADomainTerm("Yogas", terms).Should().BeTrue("a plural counts as its singular");
        NamesADomainTerm("IELTSScore", terms).Should().BeTrue("an acronym boundary is a boundary");
        NamesADomainTerm("code_challenge_runner", terms).Should().BeTrue("a two-word term spans two segments");
        NamesADomainTerm("CodeChallenges", terms).Should().BeTrue("and its last segment may be plural");
        NamesADomainTerm("tenancy.belt.write", terms).Should().BeTrue("a slug segments on dots");

        NamesADomainTerm("ClassName", terms).Should().BeFalse();
        NamesADomainTerm("Grade", terms).Should().BeFalse();
        NamesADomainTerm("Danger", terms).Should().BeFalse("a term is a whole segment, not a prefix");
        NamesADomainTerm("Standard", terms).Should().BeFalse("nor a substring in the middle");
        NamesADomainTerm("ChordateSpecies", terms).Should().BeFalse();
        NamesADomainTerm("ai.pronunciation_feedback", terms).Should().BeFalse(
            "a capability that serves every domain names what the platform does, not whom for");

        // And the collectors: each must actually see what it claims to read. The probes are in
        // this assembly and in this test's own model, which the rule does not scan.
        NamesIn(typeof(DomainGenericityTests).Assembly)
            .Should().Contain(nameof(Probes.YogaAsanaProbe))
            .And.Contain(nameof(Probes.YogaAsanaProbe.CefrLevel));

        using var probe = new GenericityProbeContext();
        ModelNames(probe).Should().Contain("belt_ranks").And.Contain("kyu_level");

        ExportedIdentifiers("export const KATA_SEQUENCE = 1;\nexport { Foo as BeltRank };")
            .Should().BeEquivalentTo(["KATA_SEQUENCE", "BeltRank"]);
    }

    /// <summary>Every name the platform ships, by the subject it belongs to.</summary>
    private static IEnumerable<(string Subject, IReadOnlyList<string> Names)> Subjects()
    {
        yield return ("a backend name", [.. ProductionAssemblies.All().SelectMany(NamesIn)]);
        yield return ("a backend file", [.. BackendFileNames()]);
        yield return ("a mapped table or column", [.. Modules.Scoped.SelectMany(module =>
        {
            using var context = module.Context();
            return ModelNames(context);
        })]);
        yield return ("an audit slug", [.. AuditCatalogDiscovery.Catalogue().All.Select(entry => entry.Operation)]);
        yield return ("an entitlement key", [.. EntitlementKeys()]);
        yield return ("a renderer key", [.. PrimitiveRendererKey.All.Concat(CompositeRendererKey.All)]);
        yield return ("a frontend file", [.. FrontendFiles().Select(Path.GetFileName).OfType<string>()]);
        yield return ("a frontend export", [.. FrontendFiles()
            .SelectMany(file => ExportedIdentifiers(File.ReadAllText(file)))]);
    }

    /// <summary>The forbidden terms, read from the catalogue entry that owns them.</summary>
    private static IReadOnlyList<string> ForbiddenTerms()
    {
        var catalogue = File.ReadAllText(Path.Combine(
            RepositoryPaths.RepoRoot(), "docs", "standards", "21-architecture-tests-catalogue.md"));

        var bullet = TermsBullet().Match(catalogue);

        bullet.Success.Should().BeTrue(
            "Standards 21 carries the list in a `- **Forbidden terms:**` bullet, and this test "
            + "reads it — a renamed or reformatted bullet empties the rule rather than changing it");

        return [.. Regex.Matches(bullet.Groups["terms"].Value, "`(?<term>[A-Za-z]+)`")
            .Select(match => match.Groups["term"].Value)];
    }

    /// <summary>
    /// Whether a name carries a forbidden term as a whole word segment, singular or plural.
    /// </summary>
    private static bool NamesADomainTerm(string name, IReadOnlyList<string> terms)
    {
        var segments = Segments(name);

        return terms.Select(Segments).Any(term => Contains(segments, term));
    }

    /// <summary>
    /// A name's word segments: separators, and the PascalCase, acronym and digit boundaries
    /// inside each part.
    /// </summary>
    private static List<string> Segments(string name) =>
        [.. Separators().Split(name)
            .SelectMany(part => WordBoundary().Split(part))
            .Where(segment => segment.Length > 0)
            .Select(segment => segment.ToLowerInvariant())];

    /// <summary>Whether a name's segments carry a term's segments, the last one pluralizable.</summary>
    private static bool Contains(List<string> name, List<string> term)
    {
        for (var start = 0; start + term.Count <= name.Count; start++)
        {
            var matched = true;

            for (var index = 0; index < term.Count && matched; index++)
            {
                var segment = name[start + index];
                var expected = term[index];

                matched = index == term.Count - 1
                    ? segment == expected || segment == expected + "s" || segment == expected + "es"
                    : segment == expected;
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Every name an assembly declares: its own, its namespaces, types and members.</summary>
    private static IEnumerable<string> NamesIn(Assembly assembly)
    {
        yield return assembly.GetName().Name!;

        foreach (var type in assembly.GetTypes())
        {
            if (ExemptSeedData(type))
            {
                continue;
            }

            if (type.Namespace is { } space)
            {
                yield return space;
            }

            yield return type.Name;

            foreach (var member in type.GetMembers(
                BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static
                | BindingFlags.Public | BindingFlags.NonPublic))
            {
                yield return member.Name;
            }
        }
    }

    /// <summary>
    /// The seeder's seed data, which is two demo tenants in unrelated domains on purpose —
    /// a language school and a yoga studio are what make the genericity claim checkable.
    /// </summary>
    private static bool ExemptSeedData(Type type) =>
        type.FullName?.StartsWith("LearnStack.Tools.Seeder.SeedData", StringComparison.Ordinal) == true;

    private static IEnumerable<string> BackendFileNames() =>
        Directory.EnumerateFiles(RepositoryPaths.BackendSrc(), "*", SearchOption.AllDirectories)
            .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(segment => segment is "bin" or "obj"))
            .Select(Path.GetFileName)
            .OfType<string>();

    /// <summary>Every table and column a model maps.</summary>
    private static List<string> ModelNames(DbContext context)
    {
        var names = new List<string>();

        foreach (var entity in context.Model.GetEntityTypes())
        {
            if (entity.GetTableName() is not { } table)
            {
                continue;
            }

            names.Add(table);

            var store = StoreObjectIdentifier.Table(table, entity.GetSchema());
            names.AddRange(entity.GetProperties()
                .Select(property => property.GetColumnName(store))
                .OfType<string>());
        }

        return names;
    }

    /// <summary>Every feature, limit and killswitch key the registries declare.</summary>
    private static IEnumerable<string> EntitlementKeys() =>
        new[] { typeof(FeatureKeys), typeof(LimitKeys), typeof(KillswitchKeys) }
            .SelectMany(registry => registry.GetFields(BindingFlags.Public | BindingFlags.Static))
            .Select(field => field.GetValue(null))
            .Select(value => value switch
            {
                FeatureKey feature => feature.Value,
                LimitKey limit => limit.Value,
                KillswitchKey killswitch => killswitch.Value,
                _ => null,
            })
            .OfType<string>();

    /// <summary>The web app's own sources — not its dependencies, its build output or its tests.</summary>
    private static IEnumerable<string> FrontendFiles() =>
        Directory.EnumerateFiles(
                Path.Combine(RepositoryPaths.FrontendApps(), "web"), "*", SearchOption.AllDirectories)
            .Where(file => !file.Split(Path.DirectorySeparatorChar)
                .Any(segment => segment is "node_modules" or ".next" or "coverage" or "test" or "__tests__"))
            .Where(file => !TestFile().IsMatch(Path.GetFileName(file)));

    /// <summary>The identifiers a TypeScript module exports.</summary>
    private static List<string> ExportedIdentifiers(string source) =>
        [.. ExportedDeclaration().Matches(source)
            .Select(match => match.Groups["name"].Value),
          .. ExportedList().Matches(source)
            .SelectMany(match => match.Groups["names"].Value.Split(','))
            .Select(entry => entry.Trim().Split(" as ")[^1].Trim())
            .Select(entry => entry.Replace("type ", string.Empty, StringComparison.Ordinal).Trim())
            .Where(entry => entry.Length > 0)];

    [GeneratedRegex(@"- \*\*Forbidden terms:\*\*(?<terms>.*?)(?=\n- \*\*)", RegexOptions.Singleline)]
    private static partial Regex TermsBullet();

    [GeneratedRegex(@"[^A-Za-z0-9]+")]
    private static partial Regex Separators();

    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|(?<=[A-Za-z])(?=[0-9])|(?<=[0-9])(?=[A-Za-z])")]
    private static partial Regex WordBoundary();

    [GeneratedRegex(@"\.(test|spec)\.[jt]sx?$|^setup\.ts$")]
    private static partial Regex TestFile();

    [GeneratedRegex(@"export\s+(?:default\s+)?(?:declare\s+)?(?:async\s+)?(?:abstract\s+)?(?:function\*?|class|const|let|var|type|interface|enum|namespace)\s+(?<name>[A-Za-z_$][A-Za-z0-9_$]*)")]
    private static partial Regex ExportedDeclaration();

    [GeneratedRegex(@"export\s*\{(?<names>[^}]*)\}")]
    private static partial Regex ExportedList();

    /// <summary>
    /// A model that maps a domain-specific table and column, for
    /// <c>The_Domain_Term_Scan_Can_Actually_Fail</c>. Never connected to.
    /// </summary>
    private sealed class GenericityProbeContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder options) =>
            options.UseNpgsql("Host=model-only;Database=model-only;Username=model-only");

        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.Entity<ProbeRank>().ToTable("belt_ranks");
            builder.Entity<ProbeRank>().Property(rank => rank.Level).HasColumnName("kyu_level");
        }

        internal sealed class ProbeRank
        {
            public int Id { get; set; }

            public int Level { get; set; }
        }
    }
}
