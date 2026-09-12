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
/// <c>Status</c> header, the catalogue's claim that a test exists under a given name, and the
/// promise that no architecture test is skippable. Each of the three has been wrong before —
/// twenty-two headers said <c>Active</c> while the index said fourteen, and the catalogue has
/// carried a rule at <b>Implemented</b> whose name no test method spelled.
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

        index.Should().HaveCount(22, "the corpus carries twenty-two standards, 00 through 21");

        foreach (var (number, status) in index)
        {
            var document = Standards().Single(path =>
                Path.GetFileName(path).StartsWith($"{number}-", StringComparison.Ordinal));

            HeaderStatus(document).Should().Be(status,
                $"{Path.GetFileName(document)}'s header and the index in README.md are the same "
                + "claim, and a promotion lands in both or in neither");
        }

        // And the sentence that counts them, because a reader takes the summary at its word.
        var readme = File.ReadAllText(Path.Combine(StandardsRoot, "README.md"));
        var counted = StatusCount().Match(readme);

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

        implemented.Should().HaveCountGreaterThan(90,
            "the premise: the catalogue reports ninety-five rules Implemented in this assembly, "
            + "and a reader of an entry that names its class rather than its file is owed the "
            + "same check as a reader of one that names the file");

        var missing = implemented
            .Where(rule => !methods.Contains(rule))
            .ToList();

        missing.Should().BeEmpty(
            "every rule the catalogue reports Implemented in this assembly is a test method of "
            + "that exact name (Standards 21 § Canonical names and superseded spellings)");
    }

    [Fact]
    public void No_Architecture_Test_Is_Skippable()
    {
        // "Architecture tests are non-skippable" is a policy the corpus states in three places
        // and nothing enforced. A `Skip = "…"` is one edit, it goes green, and the suite reports
        // the same number of passing files as before.
        var skips = Directory
            .EnumerateFiles(SuiteRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(segment => segment is "bin" or "obj"))
            // This file is the one exemption, and it is not a loophole: the companion below
            // feeds the pattern the very shapes it must catch, as string literals.
            .Where(file => Path.GetFileName(file) != "CorpusConsistencyTests.cs")
            .Where(file => SkipAttribute().IsMatch(SourceText.WithoutComments(File.ReadAllText(file))))
            .Select(Path.GetFileName)
            .ToList();

        skips.Should().BeEmpty(
            "an architecture test is non-skippable (Testing Standards § Architecture tests): a "
            + "rule that can be turned off for a release is a rule nobody has to satisfy");
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

        SkipAttribute().IsMatch("""[Fact(Skip = "flaky")]""").Should().BeTrue();
        SkipAttribute().IsMatch("""[Theory(Skip="later")]""").Should().BeTrue();
        SkipAttribute().IsMatch("""[Fact(DisplayName = "(meta) NetArchTest detects a planted dependency")]""")
            .Should().BeFalse("a display name is not a skip");

        // And the catalogue reader: an Implemented entry naming this assembly is one this rule
        // must check, a Registered one is not, and the class spelling counts exactly as the file
        // spelling does — the reading that was wrong for fifty-seven of the entries.
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
    }

    /// <summary>The standards directory, which is the subject of two of the rules above.</summary>
    private static string StandardsRoot => Path.Combine(RepositoryPaths.RepoRoot(), "docs", "standards");

    /// <summary>This test project's own directory.</summary>
    private static string SuiteRoot => Path.Combine(
        RepositoryPaths.BackendSrc(), "..", "tests", "LearnStack.Tests.Architecture");

    private static IEnumerable<string> Standards() =>
        Directory.EnumerateFiles(StandardsRoot, "*.md")
            .Where(path => Path.GetFileName(path) != "README.md");

    /// <summary>The status each standard declares in its own header.</summary>
    private static string HeaderStatus(string path) => HeaderStatusIn(File.ReadAllText(path));

    private static string HeaderStatusIn(string document)
    {
        var header = StatusHeader().Match(document);

        header.Success.Should().BeTrue("every standard declares its state at the top");

        return header.Groups["status"].Value;
    }

    /// <summary>The index's own classification, by standard number.</summary>
    private static List<(string Number, string Status)> StatusIndex() =>
        StatusRows(File.ReadAllText(Path.Combine(StandardsRoot, "README.md")));

    private static List<(string Number, string Status)> StatusRows(string readme) =>
        [.. StatusRow().Matches(readme)
            .Select(match => (match.Groups["number"].Value, match.Groups["status"].Value))];

    /// <summary>Every rule the catalogue reports Implemented in this assembly.</summary>
    private static List<string> ImplementedRules() =>
        ImplementedIn(
            File.ReadAllText(Path.Combine(StandardsRoot, "21-architecture-tests-catalogue.md")),
            SuiteClasses());

    /// <summary>The test classes of this project, by the name an entry would spell.</summary>
    private static List<string> SuiteClasses() =>
        [.. Directory.EnumerateFiles(SuiteRoot, "*Tests.cs")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()];

    /// <remarks>
    /// <para>
    /// Only the entries whose Status line names a file <b>of this project</b>, matched against the
    /// files on disk rather than against the shape of a name: a rule implemented in the unit or
    /// integration suites is no less binding, and it is not this assembly's to find.
    /// </para>
    /// <para>
    /// Both spellings the catalogue actually uses count — <c>`PersistenceConventionTests.cs`</c>
    /// and the bare <c>`PersistenceConventionTests`</c>. Matching only the first was measured at
    /// <b>38</b> of the 95 entries this rule is supposed to cover, because most entries name the
    /// class rather than the file. A guard that silently checks two fifths of its subject is the
    /// shape of defect this whole file exists to catch.
    /// </para>
    /// </remarks>
    private static List<string> ImplementedIn(string catalogue, IReadOnlyCollection<string>? classes = null) =>
        [.. CatalogueEntry().Matches(catalogue)
            .Where(entry => entry.Groups["status"].Value.Contains("**Implemented**", StringComparison.Ordinal))
            .Where(entry => (classes ?? ["SomeTests"]).Any(name =>
                NamesTheClass(entry.Groups["status"].Value, name)))
            .Select(entry => entry.Groups["rule"].Value)];

    /// <summary>Whether a Status line names this test class, with or without its extension.</summary>
    private static bool NamesTheClass(string status, string name) =>
        status.Contains($"`{name}`", StringComparison.Ordinal)
        || status.Contains($"`{name}.cs`", StringComparison.Ordinal);

    /// <summary>Every test method this assembly declares.</summary>
    private static HashSet<string> TestMethods() =>
        [.. Directory
            .EnumerateFiles(SuiteRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(segment => segment is "bin" or "obj"))
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

    [GeneratedRegex(@"public\s+(?:async\s+Task|void)\s+(?<name>[A-Za-z0-9_]+)\s*\(")]
    private static partial Regex TestMethod();

    [GeneratedRegex(@"\[(?:Fact|Theory)\s*\([^)]*\bSkip\s*=")]
    private static partial Regex SkipAttribute();
}
