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

        // And the tables no model maps, which the migration leg reads instead.
        MigratedNames().Should().Contain("outbox_messages")
            .And.Contain("idempotency_keys", "a table created in raw SQL belongs to no DbContext");

        // A schema qualifier used to take the table AND its columns out of the scan: the name
        // group captured the schema, and the pattern then failed at the dot.
        var qualified = CreateTable().Match("""
            CREATE TABLE public.yoga_levels (
                id uuid NOT NULL,
                asana_name text NOT NULL
            );
            """);
        qualified.Success.Should().BeTrue();
        qualified.Groups["table"].Value.Should().Be("yoga_levels");
        ColumnName().Matches(qualified.Groups["columns"].Value)
            .Select(match => match.Groups["column"].Value)
            .Should().BeEquivalentTo(["id", "asana_name"]);

        var created = CreateTable().Match("""
            CREATE TABLE kata_sequences (
                id uuid NOT NULL,
                belt_rank text NOT NULL,
                "YogaLevel" TEXT NOT NULL,
                sequence_no UUID NOT NULL
            );
            """);
        created.Success.Should().BeTrue();
        created.Groups["table"].Value.Should().Be("kata_sequences");
        ColumnName().Matches(created.Groups["columns"].Value)
            .Select(match => match.Groups["column"].Value)
            .Should().BeEquivalentTo(["id", "belt_rank", "YogaLevel", "sequence_no"],
                "a quoted mixed-case column is a legal column and the likeliest shape of a "
                + "domain-specific name, and an upper-case type is the same type");

        SourceText.WithoutComments("const url = `https://example/{id}`;\nexport const Belt = 1;")
            .Should().Contain("export const Belt",
                "a template literal carries // as text, and truncating there hides what follows");

        ExportedIdentifiers(
                "export const KATA_SEQUENCE = 1;\n"
                + "export { Foo as BeltRank };\n"
                + "const AsanaCard = () => null;\n"
                + "export default AsanaCard;\n"
                + "// export const CefrLevel = 1; — prose, not an export\n")
            .Should().BeEquivalentTo(["KATA_SEQUENCE", "BeltRank", "AsanaCard"],
                "a default export of an identifier is an export, and a commented-out one is not");

        // The shapes it used to miss, each an ordinary spelling rather than an evasion.
        ExportedIdentifiers("export const Generic = 1, KataSequence = 2;\n")
            .Should().Contain("KataSequence", "a second declarator binds a name too");
        ExportedIdentifiers("export const Options = { a: 1 }, CefrLevel = 2;\n")
            .Should().Contain("CefrLevel", "and one that follows a nested initializer still does");
        ExportedIdentifiers("export const Config = { a: 1, YogaLevel: 2 };\n")
            .Should().BeEquivalentTo(["Config"],
                "an object's properties are not bindings, and reading them as exports refused "
                + "a file that was clean");
        ExportedIdentifiers("export const Pair = compute(1, KataSequence, 2);\n")
            .Should().BeEquivalentTo(["Pair"], "neither are a call's arguments");
        ExportedIdentifiers("export const { BeltRank } = data;\n")
            .Should().Contain("BeltRank", "so does a destructured one");
        ExportedIdentifiers("export const { rank: BeltRank } = data;\n")
            .Should().Contain("BeltRank", "and a renamed destructured one binds the new name");
        ExportedIdentifiers("export * as AsanaCatalog from './generic';\n")
            .Should().Contain("AsanaCatalog", "a namespace re-export is a binding");
        ExportedIdentifiers("export type { CefrLevel } from './models';\n")
            .Should().Contain("CefrLevel", "a type re-export names the type it exports");
        ExportedIdentifiers("const KataForm = 1;\nexport default KataForm\n")
            .Should().Contain("KataForm", "TypeScript's semicolon is optional and so is this one");

        // And the other direction: a declaration quoted inside a string exports nothing.
        ExportedIdentifiers("export const prose = \"export const CefrLevel = 1;\";\n")
            .Should().BeEquivalentTo(["prose"],
                "a string is text, and reading it as code rejected files that were clean");
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
        yield return ("a migrated table or column", [.. MigratedNames()]);
        yield return ("an audit slug", [.. AuditCatalogDiscovery.Catalogue().All.Select(entry => entry.Operation)]);
        yield return ("an entitlement key", [.. EntitlementKeys()]);
        yield return ("a renderer key", [.. PrimitiveRendererKey.All.Concat(CompositeRendererKey.All)]);
        yield return ("a frontend file", [.. FrontendFiles().Select(Path.GetFileName).OfType<string>()]);
        yield return ("a frontend export", [.. FrontendFiles()
            .SelectMany(file => ExportedIdentifiers(File.ReadAllText(file)))]);
    }

    /// <summary>The forbidden terms, read from the catalogue entry that owns them.</summary>
    private static List<string> ForbiddenTerms()
    {
        var catalogue = File.ReadAllText(Path.Combine(
            RepositoryPaths.RepoRoot(), "docs", "standards", "21-architecture-tests-catalogue.md"));

        var bullet = TermsBullet().Match(catalogue);

        bullet.Success.Should().BeTrue(
            "Standards 21 carries the list in a `- **Forbidden terms:**` bullet, and this test "
            + "reads it — a renamed or reformatted bullet empties the rule rather than changing it");

        var quoted = Regex.Matches(bullet.Groups["terms"].Value, "`(?<term>[^`]+)`")
            .Select(match => match.Groups["term"].Value)
            .ToList();

        quoted.Should().OnlyContain(term => Regex.IsMatch(term, "^[A-Za-z]+$"),
            "every forbidden term is a word this scan can segment — one written with a digit, a "
            + "space or a hyphen would be dropped silently, and a count that only checks how many "
            + "survived cannot see that");

        return quoted;
    }

    /// <summary>
    /// Whether a name carries a forbidden term as a whole word segment, singular or plural.
    /// </summary>
    private static bool NamesADomainTerm(string name, List<string> terms)
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
    private static bool ExemptSeedData(Type type)
    {
        var declaring = type;
        while (declaring.DeclaringType is { } outer)
        {
            declaring = outer;
        }

        return declaring.FullName == SeedDataType;
    }

    /// <summary>The seeder's seed-data type, exempt by name rather than by prefix.</summary>
    private const string SeedDataType = "LearnStack.Tools.Seeder.SeedData";

    private static IEnumerable<string> BackendFileNames() =>
        Directory.EnumerateFiles(RepositoryPaths.BackendSrc(), "*", SearchOption.AllDirectories)
            .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(segment => segment is "bin" or "obj"))
            .Select(Path.GetFileName)
            .OfType<string>()
            // The seeder's seed data is exempt as a type, and its own file for the same reason:
            // two demo domains are what make the claim checkable. Exact, like the type
            // exemption — `SeedDataExtensions.cs` is code and is a subject.
            .Where(name => name != "SeedData.cs");

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

    /// <summary>
    /// Every table and column a migration creates in raw SQL, which no model maps.
    /// </summary>
    /// <remarks>
    /// <c>outbox_messages</c> and <c>idempotency_keys</c> are created by `CREATE TABLE` and
    /// belong to no <c>DbContext</c>, so the model leg above never sees them — and a column
    /// named for one domain would ship in the same migration as everything else.
    /// </remarks>
    private static IEnumerable<string> MigratedNames()
    {
        foreach (var file in Directory.EnumerateFiles(RepositoryPaths.BackendSrc(), "*.cs", SearchOption.AllDirectories)
            .Where(file => file.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            foreach (Match table in CreateTable().Matches(File.ReadAllText(file)))
            {
                yield return table.Groups["table"].Value;

                foreach (Match column in ColumnName().Matches(table.Groups["columns"].Value))
                {
                    yield return column.Groups["column"].Value;
                }
            }
        }
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

    /// <summary>
    /// The frontend's own sources — every app and every package, not their dependencies, their
    /// build output or their tests.
    /// </summary>
    /// <remarks>
    /// The packages are in scope as much as the app: `packages/ui` is where a component extracted
    /// out of `apps/web` lands, and a domain-named one there ships in exactly the same release.
    /// </remarks>
    private static IEnumerable<string> FrontendFiles() =>
        Directory.EnumerateFiles(
                Path.GetDirectoryName(RepositoryPaths.FrontendApps())!, "*", SearchOption.AllDirectories)
            .Where(file => !file.Split(Path.DirectorySeparatorChar)
                .Any(segment => segment is "node_modules" or ".next" or "coverage" or "dist" or "out"
                    or "test" or "tests" or "__tests__" or ".turbo"))
            .Where(file => !TestFile().IsMatch(Path.GetFileName(file)));

    /// <summary>The identifiers a TypeScript module exports.</summary>
    /// <remarks>
    /// <para>
    /// Comments <b>and literal contents</b> are stripped first — prose is not a subject of this
    /// rule. A file writing <c>export const prose = "export const Yoga = 1;"</c> exports one
    /// binding, and reading the raw text reported two.
    /// </para>
    /// <para>
    /// Every shape TypeScript actually uses, because the ones it missed were ordinary rather
    /// than exotic: a second declarator in <c>export const A = 1, B = 2;</c>, a destructured
    /// <c>export const { B } = data;</c>, a namespace re-export <c>export * as B from …</c>, and
    /// a default export written without its semicolon. Each was measured against this helper
    /// before the pattern for it was added.
    /// </para>
    /// </remarks>
    private static List<string> ExportedIdentifiers(string source)
    {
        var code = SourceText.WithoutCommentsOrLiterals(source);

        return
        [
            .. ExportedDeclaration().Matches(code).SelectMany(match =>
                // `export const A = 1, B = 2;` binds both. The declaration pattern reads the
                // first; the rest of the statement is split on its commas.
                new[] { match.Groups["name"].Value }
                    .Concat(Declarators(code, match))),
            .. ExportedDefault().Matches(code).Select(match => match.Groups["name"].Value),
            .. ExportedNamespace().Matches(code).Select(match => match.Groups["name"].Value),
            .. ExportedBinding().Matches(code)
                .SelectMany(match => Names(match.Groups["names"].Value)),
            .. ExportedList().Matches(code)
                .SelectMany(match => Names(match.Groups["names"].Value)),
        ];
    }

    /// <summary>The binding names in an export list or a destructuring pattern.</summary>
    private static IEnumerable<string> Names(string names) =>
        names.Split(',')
            .Select(entry => entry.Split(':')[^1])
            .Select(entry => entry.Trim().Split(" as ")[^1].Trim())
            .Select(entry => entry.Replace("type ", string.Empty, StringComparison.Ordinal).Trim())
            .Select(entry => entry.TrimStart('.', ' ').Trim())
            .Where(entry => entry.Length > 0 && char.IsLetter(entry[0]) || entry.StartsWith('_'));

    /// <summary>The further declarators of one <c>export const A = 1, B = 2;</c> statement.</summary>
    /// <remarks>
    /// Only commas at the statement's own level. Matching every comma up to the semicolon read
    /// an object's properties and a call's arguments as bindings, so
    /// <c>export const Config = { a: 1, YogaLevel: 2 };</c> reported <c>YogaLevel</c> as an
    /// export and the rule refused a clean file. Stopping at the first bracket instead would
    /// lose the real declarator in <c>export const Options = { a: 1 }, CefrLevel = 2;</c>, so
    /// the depth is counted rather than avoided.
    /// </remarks>
    private static List<string> Declarators(string code, Match declaration)
    {
        var names = new List<string>();
        var depth = 0;

        for (var i = declaration.Index + declaration.Length; i < code.Length; i++)
        {
            var character = code[i];

            if (character is '{' or '[' or '(')
            {
                depth++;
                continue;
            }

            if (character is '}' or ']' or ')')
            {
                depth--;

                // Past the statement's own scope — a file whose last declaration has no
                // semicolon would otherwise be read to its end.
                if (depth < 0)
                {
                    break;
                }

                continue;
            }

            if (depth > 0)
            {
                continue;
            }

            if (character == ';')
            {
                break;
            }

            if (character != ',')
            {
                continue;
            }

            var match = FurtherDeclarator().Match(code[i..]);

            if (match.Success && match.Index == 0)
            {
                names.Add(match.Groups["name"].Value);
            }
        }

        return names;
    }

    [GeneratedRegex(@"- \*\*Forbidden terms:\*\*(?<terms>.*?)(?=\n- \*\*)", RegexOptions.Singleline)]
    private static partial Regex TermsBullet();

    [GeneratedRegex(@"[^A-Za-z0-9]+")]
    private static partial Regex Separators();

    /// <summary>A <c>CREATE TABLE</c> statement and the body that declares its columns.</summary>
    [GeneratedRegex(
        @"CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:""?[A-Za-z_][A-Za-z0-9_]*""?\s*\.\s*)?"
        + @"""?(?<table>[A-Za-z_][A-Za-z0-9_]*)""?\s*\((?<columns>.*?)\n\s*\)\s*;",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex CreateTable();

    /// <summary>A column declaration: an identifier at the start of a line inside the body.</summary>
    /// <remarks>
    /// Both tokens in either case. The type may be written <c>text</c> or <c>TEXT</c>, and the
    /// column name may be quoted — <c>"YogaLevel" text</c> is a legal PostgreSQL column and
    /// precisely the kind of name this rule exists to refuse, yet a lower-case-only pattern
    /// read straight past it.
    /// </remarks>
    [GeneratedRegex(@"^\s{2,}""?(?<column>[A-Za-z_][A-Za-z0-9_]*)""?\s+[A-Za-z]", RegexOptions.Multiline)]
    private static partial Regex ColumnName();

    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|(?<=[A-Za-z])(?=[0-9])|(?<=[0-9])(?=[A-Za-z])")]
    private static partial Regex WordBoundary();

    [GeneratedRegex(@"\.(test|spec)\.[jt]sx?$")]
    private static partial Regex TestFile();

    [GeneratedRegex(@"export\s+(?:default\s+)?(?:declare\s+)?(?:async\s+)?(?:abstract\s+)?(?:function\*?|class|const|let|var|type|interface|enum|namespace)\s+(?<name>[A-Za-z_$][A-Za-z0-9_$]*)")]
    private static partial Regex ExportedDeclaration();

    /// <remarks><c>export type { A }</c> is a list like any other; only the names matter.</remarks>
    [GeneratedRegex(@"export\s*(?:type\s+)?\{(?<names>[^}]*)\}")]
    private static partial Regex ExportedList();

    /// <summary>A default export of an identifier declared elsewhere in the file.</summary>
    /// <remarks>The semicolon is optional, because TypeScript's is.</remarks>
    [GeneratedRegex(@"export\s+default\s+(?<name>[A-Za-z_$][A-Za-z0-9_$]*)\s*(?:;|$)", RegexOptions.Multiline)]
    private static partial Regex ExportedDefault();

    /// <summary><c>export * as Name from './module';</c> — a binding like any other.</summary>
    [GeneratedRegex(@"export\s*\*\s*as\s+(?<name>[A-Za-z_$][A-Za-z0-9_$]*)")]
    private static partial Regex ExportedNamespace();

    /// <summary><c>export const { A, B: C } = data;</c> and its array form.</summary>
    [GeneratedRegex(@"export\s+(?:declare\s+)?(?:const|let|var)\s*[{\[](?<names>[^}\]]*)[}\]]\s*=")]
    private static partial Regex ExportedBinding();

    /// <summary>A declarator after the first in one statement: <c>, B = 2</c>.</summary>
    [GeneratedRegex(@",\s*(?<name>[A-Za-z_$][A-Za-z0-9_$]*)\s*(?:[:=]|,|$)")]
    private static partial Regex FurtherDeclarator();

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
