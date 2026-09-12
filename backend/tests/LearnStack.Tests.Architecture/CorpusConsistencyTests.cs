using System.Globalization;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// The corpus says what it enforces, and this is what holds it to that.
/// </summary>
/// <remarks>
/// <para>
/// Every rule here guards a statement a reader trusts without checking: a standard's
/// <c>Status</c> header, the catalogue's claim that a test exists under a given name, the
/// counts it publishes about itself, and the promise that no architecture test is skippable.
/// Each has been wrong before — twenty-two headers said <c>Active</c> while the index said
/// fourteen, the catalogue has carried a rule at <b>Implemented</b> whose name no test method
/// spelled, and its headline counts were stale in the commit that wrote them.
/// </para>
/// <para>
/// The rules in this file are not exempt from each other. An earlier version excused this file
/// from the no-skip scan, because the companion feeds that scan the very shapes it must catch —
/// and the exemption meant the four corpus guards were the only tests in the assembly that one
/// edit could switch off. Stripping literal contents before scanning removes the reason for the
/// exemption, so there is no longer one.
/// </para>
/// </remarks>
public sealed partial class CorpusConsistencyTests
{
    [Fact]
    public void Standard_Status_Headers_Match_The_Index()
    {
        // Two views of "what is actually enforced", and they disagreed for a month: the
        // individual documents all declared Active while the index's table classified eight of
        // them Adopted. The index is the one a reviewer reads; the header is the one an author
        // sees. This is what keeps a promotion from landing in one and not the other.
        var index = StatusIndex();
        var documents = Standards().ToDictionary(
            path => Path.GetFileName(path).Split('-')[0], path => path);

        // Both directions. Checking only the index's rows meant a standard added without a row
        // was never looked at — and forgetting the row is the easier half to forget.
        index.Select(entry => entry.Number).Should().BeEquivalentTo(documents.Keys,
            "every standard on disk has a row in README.md's table and every row has a document");

        foreach (var (number, status) in index)
        {
            HeaderStatus(documents[number]).Should().Be(status,
                $"{Path.GetFileName(documents[number])}'s header and the index in README.md are "
                + "the same claim, and a promotion lands in both or in neither");
        }

        // And the sentence that counts them, because a reader takes the summary at its word.
        var counted = StatusCount().Match(Readme());

        counted.Success.Should().BeTrue("README.md states the split in words as well as in a table");

        Words[counted.Groups["active"].Value].Should().Be(index.Count(entry => entry.Status == "Active"));
        Words[counted.Groups["adopted"].Value].Should().Be(index.Count(entry => entry.Status == "Adopted"));
    }

    [Fact]
    public void Every_Implemented_Rule_Names_A_Test_That_Exists()
    {
        // The catalogue's Status line is the authority on whether a rule runs, and it is prose:
        // "Implemented — SomeTests.cs" is a claim nothing checked. A renamed method, a moved
        // file or a rule quietly deleted leaves the entry saying the opposite of the truth,
        // which is worse than a rule that was never written — a reader stops looking.
        var methods = TestMethods();

        methods.Should().HaveCountGreaterThan(100, "the premise: this scan reads the suite");

        var implemented = ImplementedRules();

        // The reach, asserted rather than assumed. An entry is checked only if its Status names
        // a class this suite has, so anything that stops resolving leaves the subject silently
        // — which is the very failure this rule exists to catch. Pinning the count to what the
        // catalogue publishes about itself is what makes a disappearance visible.
        implemented.Should().HaveCount(Published("assembly"),
            "the subject of this rule is every rule the catalogue reports Implemented in this "
            + "assembly, and § Implemented today publishes that number");

        var missing = implemented
            .Where(rule => !methods.Contains(rule))
            .ToList();

        missing.Should().BeEmpty(
            "every rule the catalogue reports Implemented in this assembly is a test method of "
            + "that exact name (Standards 21 § Canonical names and superseded spellings)");

        // And the other direction of the same failure: an entry naming a test class that exists
        // in no suite at all. Without this a renamed or deleted file drops its entries out of
        // `implemented` rather than failing, and the count above is the only thing that notices.
        var classes = TestClassesEverywhere();

        var orphaned = ImplementedEntries()
            .SelectMany(entry => NamedClasses(entry.Status).Select(name => (entry.Rule, Name: name)))
            .Where(named => !classes.Contains(named.Name))
            .ToList();

        orphaned.Should().BeEmpty(
            "a catalogue entry names a test class that exists somewhere in the repository; one "
            + "that names nothing is a rule that moved, was renamed, or was deleted");
    }

    [Fact]
    public void No_Architecture_Test_Is_Skippable()
    {
        // "Architecture tests are non-skippable" is a policy the corpus states in three places
        // and nothing enforced. A `Skip = "…"` is one edit, it goes green, and the suite reports
        // the same number of passing files as before.
        var skips = SuiteFiles()
            .Where(file => SkipsATest(File.ReadAllText(file)))
            .Select(Path.GetFileName)
            .ToList();

        skips.Should().BeEmpty(
            "an architecture test is non-skippable (Testing Standards § Architecture tests): a "
            + "rule that can be turned off for a release is a rule nobody has to satisfy");
    }

    [Fact]
    public void The_Catalogue_Counts_Its_Own_Rules()
    {
        // § Implemented today deleted a hand-written rule → file table because a second copy
        // goes stale, and kept four numbers, which are a second copy too. They were wrong on
        // arrival: the commit that wrote "ninety-five in that assembly" added three entries to
        // the assembly in the same diff. Numbers a test recomputes cannot drift.
        Published("methods").Should().Be(DeclaredTestMethods(),
            "the count of `[Fact]` and `[Theory]` declarations in the suite");

        var implemented = ImplementedEntries();

        Published("implemented").Should().Be(implemented.Count,
            "the count of entries whose Status reports Implemented");

        Published("assembly").Should().Be(ImplementedRules().Count,
            "of those, the ones whose Status names a class of this assembly");

        Published("outside").Should().Be(implemented.Count - ImplementedRules().Count,
            "and the rest, which run in the unit, integration and frontend suites");
    }

    [Fact]
    public void The_Corpus_Guards_Can_Actually_Fail()
    {
        // Every guard above passes today, so each is fed the shape it must refuse.
        HeaderStatusIn("# 15 — Performance\n\n**Status:** Adopted\n").Should().Be("Adopted");
        HeaderStatusIn("# 15 — Performance\n\n**Status:** Active\n").Should().Be("Active");

        StatusRows("| 15 | [Performance](15-performance.md) | **Adopted** | nothing measures a budget |\n")
            .Should().Equal([("15", "Adopted")]);
        StatusRows("| 21 | [Catalogue](21-architecture-tests-catalogue.md) | **Active** | it runs |\n")
            .Should().Equal([("21", "Active")]);

        // The skip scan, in the shapes that have actually defeated it. The first pattern used
        // `[^)]*` between the paren and `Skip`, which cannot cross a `)` — so a Skip written
        // AFTER a display name containing parentheses was invisible, and the assembly ships
        // exactly one such attribute, on the meta-test that certifies the rest is not vacuous.
        SkipsATest("""[Fact(Skip = "flaky")] public void A() { }""").Should().BeTrue();
        SkipsATest("""[Theory(Skip="later")] public void B() { }""").Should().BeTrue();
        SkipsATest("""[Fact(DisplayName = "(meta) it detects a plant", Skip = "flaky")]""")
            .Should().BeTrue("a Skip after a parenthesised display name is still a Skip");
        SkipsATest("""[Fact(Skip = nameof(Reason), DisplayName = "x")]""")
            .Should().BeTrue("and so is one whose value is a nameof");
        SkipsATest("""[Theory(DisplayName = "a (b) c")] [MemberData(nameof(Cases))]""")
            .Should().BeFalse("a display name is not a skip, and neither is a member-data source");
        SkipsATest("""var attribute = "[Fact(Skip = \"x\")]";""")
            .Should().BeFalse("a skip written inside a string literal is a fixture, not a skip");

        // And the catalogue reader: an Implemented entry naming this assembly is one this rule
        // must check, a Registered one is not, and the class spelling counts exactly as the file
        // spelling does — the reading that was wrong for most of the entries.
        ImplementedIn("""
            #### `A_Rule_That_Runs`

            - **Status:** **Implemented** — `SomeTests.cs`.

            #### `A_Rule_Whose_Entry_Names_The_Class`

            - **Status:** **Implemented** (Packet 6 step 4, `SomeTests`).

            #### `A_Rule_That_Does_Not`

            - **Status:** **Registered.**

            #### `A_Rule_Implemented_Somewhere_Else`

            - **Status:** **Implemented** (`LearnStack.Tests.Integration`, `OtherTests`).
            """).Should().Equal(["A_Rule_That_Runs", "A_Rule_Whose_Entry_Names_The_Class"]);

        // The class reader the orphan check rests on: a `…Tests` token in backticks, with or
        // without its extension, and nothing else.
        NamedClasses("**Implemented** (Packet 6 step 4, `PersistenceConventionTests`)")
            .Should().Equal(["PersistenceConventionTests"]);
        NamedClasses("**Implemented** — `AuditPipelineTests.cs`, Packet 9.")
            .Should().Equal(["AuditPipelineTests"]);
        NamedClasses("**Implemented** — analyzer + unit tests.")
            .Should().BeEmpty("an entry that names no class has nothing to resolve");

        // And the published counts, read from the sentences that carry them.
        PublishedIn("**7 test methods run in", "methods").Should().Be(7);
        PublishedIn("**9 rules in this catalogue are Implemented, and 4 of them are in that assembly.**", "implemented")
            .Should().Be(9);
        PublishedIn("**9 rules in this catalogue are Implemented, and 4 of them are in that assembly.**", "assembly")
            .Should().Be(4);
        PublishedIn("The other 5 are no less binding", "outside").Should().Be(5);
    }

    /// <summary>The standards directory, which is the subject of two of the rules above.</summary>
    private static string StandardsRoot => Path.Combine(RepositoryPaths.RepoRoot(), "docs", "standards");

    /// <summary>This test project's own directory.</summary>
    private static string SuiteRoot => Path.Combine(
        RepositoryPaths.BackendSrc(), "..", "tests", "LearnStack.Tests.Architecture");

    /// <summary>The repository's test projects, for resolving a class an entry names.</summary>
    private static string TestsRoot => Path.Combine(RepositoryPaths.BackendSrc(), "..", "tests");

    private static string Readme() => File.ReadAllText(Path.Combine(StandardsRoot, "README.md"));

    private static string Catalogue() =>
        File.ReadAllText(Path.Combine(StandardsRoot, "21-architecture-tests-catalogue.md"));

    private static IEnumerable<string> Standards() =>
        Directory.EnumerateFiles(StandardsRoot, "*.md")
            .Where(path => Path.GetFileName(path) != "README.md");

    /// <summary>Every source file of this suite, excluding build output.</summary>
    private static IEnumerable<string> SuiteFiles() =>
        Directory.EnumerateFiles(SuiteRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(segment => segment is "bin" or "obj"));

    /// <summary>The status each standard declares in its own header.</summary>
    private static string HeaderStatus(string path) => HeaderStatusIn(File.ReadAllText(path));

    private static string HeaderStatusIn(string document)
    {
        var header = StatusHeader().Match(document);

        header.Success.Should().BeTrue("every standard declares its state at the top");

        return header.Groups["status"].Value;
    }

    /// <summary>The index's own classification, by standard number.</summary>
    private static List<(string Number, string Status)> StatusIndex() => StatusRows(Readme());

    private static List<(string Number, string Status)> StatusRows(string readme) =>
        [.. StatusRow().Matches(readme)
            .Select(match => (match.Groups["number"].Value, match.Groups["status"].Value))];

    /// <summary>Whether any <c>[Fact]</c> or <c>[Theory]</c> in this source carries a Skip.</summary>
    /// <remarks>
    /// Not a regex over the raw text, twice over. Comments and literal contents go first, so a
    /// fixture written as a string is not read as code. Then each attribute's argument list is
    /// walked with a depth counter rather than matched, because a pattern that stops at the
    /// first <c>)</c> cannot see past <c>DisplayName = "(meta) …"</c> or <c>nameof(X)</c> —
    /// measured: appending a Skip after the meta-test's own display name switched it off and
    /// left this rule green.
    /// </remarks>
    private static bool SkipsATest(string source)
    {
        var text = SourceText.WithoutCommentsOrLiterals(source);

        return TestAttribute().Matches(text)
            .Select(match => ArgumentList(text, match.Index + match.Length - 1))
            .Any(arguments => arguments is not null && SkipArgument().IsMatch(arguments));
    }

    /// <summary>The text between a <c>(</c> and its matching <c>)</c>, or null if unbalanced.</summary>
    private static string? ArgumentList(string text, int open)
    {
        var depth = 0;

        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                depth++;
            }
            else if (text[i] == ')')
            {
                depth--;

                if (depth == 0)
                {
                    return text[(open + 1)..i];
                }
            }
        }

        return null;
    }

    /// <summary>Every rule the catalogue reports Implemented in this assembly.</summary>
    private static List<string> ImplementedRules() => ImplementedIn(Catalogue(), SuiteClasses());

    /// <summary>Every catalogue entry whose Status reports Implemented, wherever it runs.</summary>
    private static List<(string Rule, string Status)> ImplementedEntries() =>
        [.. CatalogueEntry().Matches(Catalogue())
            .Where(entry => entry.Groups["status"].Value.Contains("**Implemented**", StringComparison.Ordinal))
            .Select(entry => (entry.Groups["rule"].Value, entry.Groups["status"].Value))];

    /// <summary>The test classes of this project, by the name an entry would spell.</summary>
    /// <remarks>
    /// All directories, not the top one. Globbing the top level only meant that moving a class
    /// into a subfolder removed its entries from the checked set instead of failing — the guard
    /// saw a rename only for files nobody had moved.
    /// </remarks>
    private static List<string> SuiteClasses() =>
        [.. Directory.EnumerateFiles(SuiteRoot, "*Tests.cs", SearchOption.AllDirectories)
            .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(segment => segment is "bin" or "obj"))
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()];

    /// <summary>Every test class in the repository, for the orphan check.</summary>
    private static HashSet<string> TestClassesEverywhere() =>
        [.. Directory.EnumerateFiles(TestsRoot, "*Tests.cs", SearchOption.AllDirectories)
            .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(segment => segment is "bin" or "obj"))
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()];

    /// <remarks>
    /// <para>
    /// Only the entries whose Status line names a class <b>of this project</b>, matched against
    /// the files on disk rather than against the shape of a name: a rule implemented in the unit
    /// or integration suites is no less binding, and it is not this assembly's to find.
    /// </para>
    /// <para>
    /// Both spellings the catalogue actually uses count — <c>`ManyTests.cs`</c> and the bare
    /// <c>`ManyTests`</c>. Matching only the first was measured at <b>38</b> of the 95 entries
    /// this rule is supposed to cover, because most entries name the class rather than the file.
    /// A guard that silently checks two fifths of its subject is the shape of defect this whole
    /// file exists to catch.
    /// </para>
    /// </remarks>
    private static List<string> ImplementedIn(string catalogue, IReadOnlyCollection<string>? classes = null) =>
        [.. CatalogueEntry().Matches(catalogue)
            .Where(entry => entry.Groups["status"].Value.Contains("**Implemented**", StringComparison.Ordinal))
            .Where(entry => NamedClasses(entry.Groups["status"].Value)
                .Any(name => (classes ?? ["SomeTests"]).Contains(name)))
            .Select(entry => entry.Groups["rule"].Value)];

    /// <summary>The test classes a Status line names, with or without the file extension.</summary>
    private static List<string> NamedClasses(string status) =>
        [.. ClassReference().Matches(status).Select(match => match.Groups["name"].Value).Distinct()];

    /// <summary>A count § Implemented today publishes about the suite.</summary>
    private static int Published(string which) => PublishedIn(Catalogue(), which);

    private static int PublishedIn(string catalogue, string which)
    {
        var pattern = which switch
        {
            "methods" => PublishedMethods(),
            "implemented" => PublishedImplemented(),
            "assembly" => PublishedAssembly(),
            "outside" => PublishedOutside(),
            _ => throw new ArgumentOutOfRangeException(nameof(which), which, "no such published count"),
        };

        var match = pattern.Match(catalogue);

        match.Success.Should().BeTrue(
            $"§ Implemented today publishes the '{which}' count in a sentence this reads");

        return int.Parse(match.Groups["count"].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>Every <c>[Fact]</c> / <c>[Theory]</c> declaration in the suite.</summary>
    /// <remarks>
    /// One per line, over text with literals removed, because
    /// <c>Every_Database_Test_Carries_The_Docker_Trait</c> and its companion carry those very
    /// attributes as strings — counting them is the mistake § Implemented today warns about.
    /// </remarks>
    private static int DeclaredTestMethods() =>
        SuiteFiles().Sum(file =>
            Declaration().Count(SourceText.WithoutCommentsOrLiterals(File.ReadAllText(file))));

    /// <summary>Every test method this assembly declares.</summary>
    private static HashSet<string> TestMethods() =>
        [.. SuiteFiles()
            .SelectMany(file => TestMethod().Matches(File.ReadAllText(file)))
            .Select(match => match.Groups["name"].Value)];

    /// <summary>The number words the index's summary sentence uses.</summary>
    private static readonly Dictionary<string, int> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["one"] = 1,
        ["two"] = 2,
        ["three"] = 3,
        ["four"] = 4,
        ["five"] = 5,
        ["six"] = 6,
        ["seven"] = 7,
        ["eight"] = 8,
        ["nine"] = 9,
        ["ten"] = 10,
        ["eleven"] = 11,
        ["twelve"] = 12,
        ["thirteen"] = 13,
        ["fourteen"] = 14,
        ["fifteen"] = 15,
        ["sixteen"] = 16,
        ["seventeen"] = 17,
        ["eighteen"] = 18,
        ["nineteen"] = 19,
        ["twenty"] = 20,
        ["twenty-one"] = 21,
        ["twenty-two"] = 22,
    };

    [GeneratedRegex(@"^\*\*Status:\*\*\s*(?<status>Active|Adopted|Draft)\s*$", RegexOptions.Multiline)]
    private static partial Regex StatusHeader();

    [GeneratedRegex(@"^\|\s*(?<number>\d{2})\s*\|[^|]*\|\s*\*\*(?<status>Active|Adopted|Draft)\*\*\s*\|", RegexOptions.Multiline)]
    private static partial Regex StatusRow();

    [GeneratedRegex(@"(?<active>[A-Za-z-]+)\s+`Active`,\s*(?<adopted>[A-Za-z-]+)\s+`Adopted`")]
    private static partial Regex StatusCount();

    [GeneratedRegex(@"#### `(?<rule>[A-Za-z0-9_]+)`(?<status>.*?)(?=\n#{2,4} |\z)", RegexOptions.Singleline)]
    private static partial Regex CatalogueEntry();

    [GeneratedRegex(@"`(?<name>[A-Za-z0-9_]+Tests)(?:\.cs)?`")]
    private static partial Regex ClassReference();

    [GeneratedRegex(@"public\s+(?:async\s+Task|void)\s+(?<name>[A-Za-z0-9_]+)\s*\(")]
    private static partial Regex TestMethod();

    [GeneratedRegex(@"^\s*\[(?:Fact|Theory)\b", RegexOptions.Multiline)]
    private static partial Regex Declaration();

    [GeneratedRegex(@"\[(?:Fact|Theory)\s*\(")]
    private static partial Regex TestAttribute();

    [GeneratedRegex(@"\bSkip\s*=")]
    private static partial Regex SkipArgument();

    [GeneratedRegex(@"\*\*(?<count>\d+) test methods run in")]
    private static partial Regex PublishedMethods();

    [GeneratedRegex(@"\*\*(?<count>\d+) rules in this catalogue are Implemented")]
    private static partial Regex PublishedImplemented();

    [GeneratedRegex(@"are Implemented, and (?<count>\d+) of them are in that assembly")]
    private static partial Regex PublishedAssembly();

    [GeneratedRegex(@"The other (?<count>\d+) are no less binding")]
    private static partial Regex PublishedOutside();
}
