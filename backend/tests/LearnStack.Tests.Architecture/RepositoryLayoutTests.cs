using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// File-system rules that survive any reflection-based check.
/// These walk the working copy at test time, so no compiled assembly is needed.
/// </summary>
public sealed class RepositoryLayoutTests
{
    /// <summary>Cached `["web"]` so the `BeEquivalentTo` call below does not
    /// allocate a fresh array on every test invocation (CA1861).</summary>
    private static readonly string[] AllowedFrontendApps = ["web"];

    /// <summary>
    /// ADR-0018: domain-specific shapes live as tenant customization data, not code.
    /// A `Verticals/` source folder at any level under `backend/src` is forbidden.
    /// </summary>
    [Fact]
    public void No_Source_Folder_Named_Verticals()
    {
        var srcRoot = RepositoryPaths.BackendSrc();

        var offenders = Directory
            .EnumerateDirectories(srcRoot, "Verticals", SearchOption.AllDirectories)
            .ToArray();

        offenders.Should().BeEmpty(
            "ADR-0018 supersedes ADR-0011; tenant-specific shapes belong to the Customization module's data, " +
            "not to a `Verticals/` source folder. See docs/decisions/0018-tenant-driven-customization-model.md.");
    }

    /// <summary>
    /// ADR-0009: the tenant-facing frontend ships as a single Next.js application
    /// under `frontend/apps/web`. A peer `frontend/apps/studio` or `frontend/apps/portal`
    /// is deferred until the triggers in ADR-0009 fire — adding one without an ADR
    /// is a structural deviation.
    /// </summary>
    [Fact]
    public void Frontend_Has_Only_The_Web_App()
    {
        var appsRoot = RepositoryPaths.FrontendApps();

        Directory.Exists(appsRoot).Should().BeTrue(
            $"`{appsRoot}` must exist — Phase 01 ships the frontend monorepo with `apps/web`. " +
            "If you intentionally removed it, update this test and ADR-0009 together.");

        // Filter dotted directories (.tmp, .cache, .turbo, etc.) — they're tool
        // byproducts, not peer Next apps; ADR-0009 cares about the latter only.
        var appNames = Directory
            .EnumerateDirectories(appsRoot)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Where(name => !name!.StartsWith('.'))
            .ToArray();

        appNames.Should().BeEquivalentTo(
            AllowedFrontendApps,
            "ADR-0009 keeps the tenant-facing frontend as one Next.js app. " +
            "Add a new ADR before splitting (studio / portal extraction is mechanical, " +
            "but the decision must be recorded).");
    }

    /// <summary>
    /// Standards 14 § Commits: the commit-subject grammar is one rule, written in the
    /// `commit-msg` hook and in CI's commit-hygiene step, and the standard's type table
    /// names the same types.
    /// </summary>
    [Fact]
    public void Commit_Subject_Grammar_Is_Stated_Once()
    {
        // Three copies of one rule drifted before anything compared them: the standard
        // listed nine types, the hook admitted eleven, and CI ten — so a `style:` commit,
        // or a `/` in a scope, passed locally and failed the pull request, and a `:` in a
        // scope did the opposite. Both files claim to enforce exactly what the other
        // does; this is what makes that true.
        var hook = CommitGrammar(Path.Combine(RepositoryPaths.RepoRoot(), ".githooks", "commit-msg"));
        var ci = CommitGrammar(Path.Combine(RepositoryPaths.RepoRoot(), ".github", "workflows", "ci.yml"));

        ci.Should().Be(hook, "the hook exists to fail locally on exactly what CI fails on");

        GrammarTypes(hook).Should().BeEquivalentTo(
            StandardTypes(),
            "the standard's table is the list a reader chooses from, so it names what the "
            + "grammar admits — no more and no fewer");

        // What the grammar means, not only that its copies agree: two identical copies of
        // a pattern that admits nothing would pass the comparison above.
        var grammar = new Regex(hook);

        string[] admitted =
        [
            "feat(tenancy): add the host map", "ci: pin the SDK", "style: format",
            "fix(kernel, audit)!: close the leak", "docs(api/tenancy): name the header",
        ];
        string[] refused =
        [
            "Feat: add", "feat(API): add", "feature: add", "feat: ", "feat(a:b): add",
            "feat add", "Update README",
        ];

        admitted.Should().OnlyContain(subject => grammar.IsMatch(subject));
        refused.Should().NotContain(subject => grammar.IsMatch(subject));
    }

    /// <summary>
    /// The one extended regular expression a file passes to <c>grep -qE</c> for a commit
    /// subject — the one anchored on the type list.
    /// </summary>
    private static string CommitGrammar(string path)
    {
        var grammars = Regex
            .Matches(File.ReadAllText(path), @"grep -qE '(\^\(feat\|[^']+)'")
            .Select(match => match.Groups[1].Value)
            .ToList();

        grammars.Should().ContainSingle(
            "{0} states the commit-subject grammar exactly once, and a rule this test "
            + "cannot find is a rule it cannot compare", path);

        return grammars[0];
    }

    /// <summary>The types a grammar's leading alternation admits.</summary>
    private static List<string> GrammarTypes(string grammar) =>
        [.. Regex.Match(grammar, @"^\^\(([a-z|]+)\)")
            .Groups[1].Value
            .Split('|', StringSplitOptions.RemoveEmptyEntries)];

    /// <summary>The types Standards 14's table under § Commits names.</summary>
    private static List<string> StandardTypes()
    {
        var standard = File.ReadAllText(
            Path.Combine(RepositoryPaths.RepoRoot(), "docs", "standards", "14-git-workflow.md"));

        var start = standard.IndexOf("\n## Commits", StringComparison.Ordinal);
        start.Should().BePositive("Standards 14 keeps its commit rules under § Commits");

        var end = standard.IndexOf("\n### ", start, StringComparison.Ordinal);
        var section = standard[start..(end < 0 ? standard.Length : end)];

        var types = Regex
            .Matches(section, @"^\| `([a-z]+)` \|", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .ToList();

        types.Should().NotBeEmpty("a table this test cannot read is a table it cannot compare");

        return types;
    }
}
